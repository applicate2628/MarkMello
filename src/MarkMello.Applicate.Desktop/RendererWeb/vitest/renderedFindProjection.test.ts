import { beforeEach, describe, expect, it } from "vitest";

type ProjectionModule = typeof import("../src/renderedFindProjection");

async function loadProjectionModule(): Promise<ProjectionModule> {
  return await import("../src/renderedFindProjection");
}

function installDocument(markup: string): HTMLElement {
  document.body.replaceChildren();
  document.body.innerHTML = `<main class="mm-document">${markup}</main>`;
  return document.querySelector<HTMLElement>("main.mm-document")!;
}

function makeSegment(text: string, overrides: Partial<{
  segmentOrdinal: number;
  blockIndex: number;
  blockLocalStart: number;
}> = {}) {
  return {
    blockIndex: overrides.blockIndex ?? 7,
    blockLocalStart: overrides.blockLocalStart ?? 0,
    segmentCodeUnitLength: text.length,
    segmentOrdinal: overrides.segmentOrdinal ?? 0,
    text,
  };
}

async function projectSegments(
  createRenderedFindProjection: ProjectionModule["createRenderedFindProjection"],
  root: Node
) {
  const result = await createRenderedFindProjection(root);
  expect(result.status).toBe("complete");
  return result.segments;
}

async function collectTransfer(
  mod: ProjectionModule,
  segments: ReturnType<typeof makeSegment>[],
  overrides: Partial<Parameters<ProjectionModule["emitRenderedFindProjectionTransfer"]>[1]> = {}
) {
  const messages: unknown[] = [];
  const checkpoints: string[] = [];
  const yields: string[] = [];
  const result = await mod.emitRenderedFindProjectionTransfer(segments, {
    emit: message => {
      messages.push(message);
    },
    projectionRevision: 3,
    renderId: 11,
    shouldCancel: checkpoint => {
      checkpoints.push(checkpoint);
      return false;
    },
    yieldControl: async () => {
      yields.push("yield");
    },
    ...overrides,
  });
  return { checkpoints, messages, result, yields };
}

describe("rendered find visible text projection", () => {
  beforeEach(() => {
    document.body.replaceChildren();
  });

  it("projects KaTeX visible HTML and excludes hidden MathML", async () => {
    const { createRenderedFindProjection } = await loadProjectionModule();
    const main = installDocument(`
      <p data-mm-block-index="4">Alpha <span class="katex"><span class="katex-mathml">hidden TeX</span><span class="katex-html">β+γ</span></span> Omega</p>
    `);

    const projection = await projectSegments(createRenderedFindProjection, main);

    expect(projection.map(segment => segment.text).join("")).toBe("Alpha β+γ Omega");
    expect(projection.map(segment => segment.blockIndex)).toEqual([4, 4, 4]);
    expect(projection.some(segment => segment.text.includes("hidden TeX"))).toBe(false);
  });

  it("does not synthesize table separators, hard-break characters, or image alt text", async () => {
    const { createRenderedFindProjection } = await loadProjectionModule();
    const main = installDocument(
      `<section data-mm-block-index="9"><table><tbody><tr><td>A</td><td>B</td></tr></tbody></table><p>hard<br>break</p><figure><img alt="ALT TEXT"><figcaption>Caption</figcaption></figure></section>`
    );

    const text = (await projectSegments(createRenderedFindProjection, main))
      .map(segment => segment.text).join("");

    expect(text).toBe("ABhardbreakCaption");
    expect(text).not.toContain(" ");
    expect(text).not.toContain("\n");
    expect(text).not.toContain("ALT TEXT");
  });

  it("skips hidden renderer chrome and rendered Mermaid source", async () => {
    const { createRenderedFindProjection } = await loadProjectionModule();
    const main = installDocument(
      `<section data-mm-block-index="12">keep<aside>aside text</aside><span class="mm-minimap">minimap text</span><span class="mm-minimap-viewport">viewport text</span><span class="mm-width-handle">handle text</span><span class="mm-drop-overlay">overlay text</span><span class="mm-find-bar">find chrome text</span><pre class="mm-mermaid is-rendered">graph TD</pre></section>`
    );

    expect((await projectSegments(createRenderedFindProjection, main))
      .map(segment => segment.text).join("")).toBe("keep");
  });

  it("uses the nearest block and block-local UTF-16 offsets", async () => {
    const { createRenderedFindProjection } = await loadProjectionModule();
    const main = installDocument(`
      <section data-mm-block-index="10">A<span>😀B</span><em>C</em></section><section data-mm-block-index="11"><p>X</p></section>
    `);

    expect(await projectSegments(createRenderedFindProjection, main)).toEqual([
      {
        blockIndex: 10,
        blockLocalStart: 0,
        segmentCodeUnitLength: 1,
        segmentOrdinal: 0,
        text: "A",
      },
      {
        blockIndex: 10,
        blockLocalStart: 1,
        segmentCodeUnitLength: 3,
        segmentOrdinal: 1,
        text: "😀B",
      },
      {
        blockIndex: 10,
        blockLocalStart: 4,
        segmentCodeUnitLength: 1,
        segmentOrdinal: 2,
        text: "C",
      },
      {
        blockIndex: 11,
        blockLocalStart: 0,
        segmentCodeUnitLength: 1,
        segmentOrdinal: 3,
        text: "X",
      },
    ]);
  });

  it("keeps each visible text node as a semantic boundary", async () => {
    const { createRenderedFindProjection } = await loadProjectionModule();
    const main = installDocument(`<p data-mm-block-index="20">pre<span>fix</span></p>`);

    expect((await projectSegments(createRenderedFindProjection, main))
      .map(segment => segment.text)).toEqual(["pre", "fix"]);
  });

  it("yields and honors cancellation during DOM projection before walking all text nodes", async () => {
    const mod = await loadProjectionModule();
    const main = installDocument(`<section data-mm-block-index="30"></section>`);
    const section = main.querySelector<HTMLElement>("section")!;
    const events: string[] = [];
    let yielded = false;
    let now = 0;
    for (let index = 0; index < 12; index++) {
      const node = document.createTextNode("");
      Object.defineProperty(node, "nodeValue", {
        configurable: true,
        get() {
          events.push(`read:${index}`);
          if (index === 5 && !yielded) {
            throw new Error("projection read a late text node before yielding");
          }
          return `t${index}`;
        },
      });
      section.appendChild(node);
    }

    await expect(
      mod.createRenderedFindProjection(main, {
        now: () => {
          const current = now;
          now += mod.RENDERED_FIND_PRODUCER_SLICE_BUDGET_MS + 1;
          return current;
        },
        shouldCancel: checkpoint => {
          events.push(`check:${checkpoint}`);
          return checkpoint === "after-yield";
        },
        yieldControl: async () => {
          yielded = true;
          events.push("yield");
        },
      })
    ).resolves.toEqual({ status: "cancelled", segments: [] });

    expect(events).toContain("yield");
    expect(events).toContain("check:after-yield");
    expect(events).not.toContain("read:5");
  });
});

describe("rendered find transfer packing", () => {
  it("emits exact begin, start, chunk, and complete declarations", async () => {
    const mod = await loadProjectionModule();
    const begin = mod.createRenderedFindDomainBeginMessage({ renderId: 11 });
    const { messages, result } = await collectTransfer(mod, [
      makeSegment("alpha", { blockLocalStart: 0, segmentOrdinal: 0 }),
      makeSegment("beta", { blockLocalStart: 5, segmentOrdinal: 1 }),
    ]);

    expect(result).toBe("complete");
    expect(begin).toEqual({
      renderId: 11,
      schemaVersion: 1,
      textDomain: "rendered-dom-v1",
      type: "find-domain-begin",
    });
    expect(messages.map(message => (message as { type: string }).type)).toEqual([
      "find-text-index-start",
      "find-text-index-chunk",
      "find-text-index-complete",
    ]);
    expect(messages[0]).toMatchObject({
      chunkCount: 1,
      partCount: 2,
      projectionRevision: 3,
      renderId: 11,
      semanticSegmentCount: 2,
      textDomain: "rendered-dom-v1",
      totalCodeUnits: 9,
      transferId: "11:3",
      type: "find-text-index-start",
    });
    expect(messages[1]).toMatchObject({
      chunkIndex: 0,
      parts: [
        {
          blockIndex: 7,
          blockLocalStart: 0,
          partOffset: 0,
          segmentCodeUnitLength: 5,
          segmentOrdinal: 0,
          text: "alpha",
        },
        {
          blockIndex: 7,
          blockLocalStart: 5,
          partOffset: 0,
          segmentCodeUnitLength: 4,
          segmentOrdinal: 1,
          text: "beta",
        },
      ],
      type: "find-text-index-chunk",
    });
    expect(messages[2]).toMatchObject({
      chunkCount: 1,
      partCount: 2,
      semanticSegmentCount: 2,
      totalCodeUnits: 9,
      transferId: "11:3",
      type: "find-text-index-complete",
    });
  });

  it("measures exact serialized UTF-16 and UTF-8 message boundaries", async () => {
    const mod = await loadProjectionModule();
    const emptyMeasurement = mod.measureRenderedFindMessage({ text: "" });
    const exactCodeUnitText = "x".repeat(mod.RENDERED_FIND_MAX_MESSAGE_CODE_UNITS - emptyMeasurement.codeUnits);
    const exactCodeUnitMessage = { text: exactCodeUnitText };

    expect(mod.measureRenderedFindMessage(exactCodeUnitMessage).codeUnits).toBe(
      mod.RENDERED_FIND_MAX_MESSAGE_CODE_UNITS
    );
    expect(() => mod.assertRenderedFindMessageWithinLimits(exactCodeUnitMessage)).not.toThrow();

    const oneCodeUnitOver = { text: `${exactCodeUnitText}x` };
    expect(() => mod.assertRenderedFindMessageWithinLimits(oneCodeUnitOver)).toThrow(/UTF-16/);

    const remainingBytes = mod.RENDERED_FIND_MAX_MESSAGE_UTF8_BYTES - emptyMeasurement.utf8Bytes;
    const exactUtf8Text = `${remainingBytes % 2 === 1 ? "x" : ""}${"é".repeat(Math.floor(remainingBytes / 2))}`;
    const exactUtf8Message = { text: exactUtf8Text };
    expect(mod.measureRenderedFindMessage(exactUtf8Message).utf8Bytes).toBe(
      mod.RENDERED_FIND_MAX_MESSAGE_UTF8_BYTES
    );
    expect(() => mod.assertRenderedFindMessageWithinLimits(exactUtf8Message)).not.toThrow();

    const oneUtf8ByteOver = { text: `${exactUtf8Text}x` };
    expect(() => mod.assertRenderedFindMessageWithinLimits(oneUtf8ByteOver)).toThrow(/UTF-8/);
  });

  it("keeps packed messages within byte, code-unit, chunk, and text-part caps", async () => {
    const mod = await loadProjectionModule();
    const longText = `${"a".repeat(mod.RENDERED_FIND_MAX_TEXT_PART_CODE_UNITS)}tail`;
    const { messages } = await collectTransfer(mod, [makeSegment(longText)]);
    const chunks = messages.filter(
      (message): message is Extract<ProjectionModule extends infer _ ? Awaited<ReturnType<ProjectionModule["createRenderedFindTextIndexChunkMessage"]>> : never, unknown> =>
        (message as { type: string }).type === "find-text-index-chunk"
    ) as Array<{ parts: Array<{ partOffset: number; text: string }> }>;

    expect(chunks.length).toBeGreaterThan(0);
    for (const message of messages) {
      const measurement = mod.measureRenderedFindMessage(message);
      expect(measurement.codeUnits).toBeLessThanOrEqual(mod.RENDERED_FIND_MAX_MESSAGE_CODE_UNITS);
      expect(measurement.utf8Bytes).toBeLessThanOrEqual(mod.RENDERED_FIND_MAX_MESSAGE_UTF8_BYTES);
    }
    for (const chunk of chunks) {
      expect(chunk.parts.length).toBeLessThanOrEqual(mod.RENDERED_FIND_MAX_CHUNK_PARTS);
      for (const part of chunk.parts) {
        expect(part.text.length).toBeLessThanOrEqual(mod.RENDERED_FIND_MAX_TEXT_PART_CODE_UNITS);
      }
    }
    expect(chunks.flatMap(chunk => chunk.parts).map(part => part.text).join("")).toBe(longText);
    expect(chunks.flatMap(chunk => chunk.parts).map(part => part.partOffset)).toEqual([0, 65536]);
  });

  it("splits at 4,096 parts per chunk without changing semantic boundaries", async () => {
    const mod = await loadProjectionModule();
    const segments = Array.from({ length: mod.RENDERED_FIND_MAX_CHUNK_PARTS + 1 }, (_, index) =>
      makeSegment(`s${index}`, {
        blockLocalStart: index * 2,
        segmentOrdinal: index,
      })
    );

    const { messages } = await collectTransfer(mod, segments);
    const chunks = messages.filter(message => (message as { type: string }).type === "find-text-index-chunk") as Array<{
      parts: Array<{ segmentOrdinal: number; text: string }>;
    }>;

    expect(chunks.length).toBeGreaterThan(1);
    for (const chunk of chunks) {
      expect(chunk.parts.length).toBeLessThanOrEqual(mod.RENDERED_FIND_MAX_CHUNK_PARTS);
      expect(mod.measureRenderedFindMessage(chunk).codeUnits).toBeLessThanOrEqual(
        mod.RENDERED_FIND_MAX_MESSAGE_CODE_UNITS
      );
      expect(mod.measureRenderedFindMessage(chunk).utf8Bytes).toBeLessThanOrEqual(
        mod.RENDERED_FIND_MAX_MESSAGE_UTF8_BYTES
      );
    }
    expect(chunks.flatMap(chunk => chunk.parts).map(part => part.segmentOrdinal)).toEqual(
      segments.map(segment => segment.segmentOrdinal)
    );
  });

  it("yields after one posted message or a 7 ms packing slice", async () => {
    const mod = await loadProjectionModule();
    let now = 0;
    const events: string[] = [];
    const postedMessages: unknown[] = [];
    const yieldEvents: string[] = [];
    await collectTransfer(mod, [makeSegment("alpha"), makeSegment("beta")], {
      emit: message => {
        events.push(`post:${message.type}`);
        postedMessages.push(message);
      },
      now: () => now,
      shouldCancel: checkpoint => {
        events.push(`check:${checkpoint}`);
        return false;
      },
      yieldControl: async () => {
        events.push("yield");
        yieldEvents.push("yield");
        now += mod.RENDERED_FIND_PRODUCER_SLICE_BUDGET_MS;
      },
    });

    expect(yieldEvents.length).toBeGreaterThanOrEqual(2);
    expect(postedMessages).toHaveLength(3);
    expect(events).toContain("post:find-text-index-start");
    expect(events).toContain("post:find-text-index-chunk");
    expect(events).toContain("post:find-text-index-complete");
    expect(events.indexOf("post:find-text-index-start")).toBeLessThan(events.indexOf("yield"));
  });

  it("yields and honors cancellation during packing before constructing all messages", async () => {
    const mod = await loadProjectionModule();
    const events: string[] = [];
    let yielded = false;
    let now = 0;
    const segments = Array.from({ length: 12 }, (_, index) => ({
      blockIndex: 7,
      blockLocalStart: index,
      get segmentCodeUnitLength() {
        return 1;
      },
      segmentOrdinal: index,
      get text() {
        events.push(`read:${index}`);
        if (index === 5 && !yielded) {
          throw new Error("packing read a late segment before yielding");
        }
        return "x";
      },
    }));

    await expect(
      mod.emitRenderedFindProjectionTransfer(segments, {
        emit: message => {
          events.push(`post:${message.type}`);
        },
        now: () => {
          const current = now;
          now += mod.RENDERED_FIND_PRODUCER_SLICE_BUDGET_MS + 1;
          return current;
        },
        projectionRevision: 3,
        renderId: 11,
        shouldCancel: checkpoint => {
          events.push(`check:${checkpoint}`);
          return checkpoint === "after-yield";
        },
        yieldControl: async () => {
          yielded = true;
          events.push("yield");
        },
      })
    ).resolves.toBe("cancelled");

    expect(events).toContain("yield");
    expect(events).toContain("check:after-yield");
    expect(events).not.toContain("read:5");
    expect(events).toEqual(expect.not.arrayContaining([
      "post:find-text-index-start",
      "post:find-text-index-chunk",
      "post:find-text-index-complete",
    ]));
  });

  it("checks cancellation before every slice, after every yield, and immediately before every post", async () => {
    const mod = await loadProjectionModule();
    const events: string[] = [];
    const cancelledAfterYield = await collectTransfer(mod, [makeSegment("alpha"), makeSegment("beta")], {
      emit: message => {
        events.push(`post:${message.type}`);
      },
      shouldCancel: checkpoint => {
        events.push(`check:${checkpoint}`);
        return checkpoint === "after-yield";
      },
      yieldControl: async () => {
        events.push("yield");
      },
    });

    expect(cancelledAfterYield.result).toBe("cancelled");
    expect(events).toContain("check:before-slice");
    expect(events).toContain("check:before-post");
    expect(events).toContain("yield");
    expect(events).toContain("check:after-yield");
    expect(events).toEqual(expect.not.arrayContaining(["post:find-text-index-complete"]));

    const cancelledBeforePost = await collectTransfer(mod, [makeSegment("alpha")], {
      shouldCancel: checkpoint => checkpoint === "before-post",
    });
    expect(cancelledBeforePost.result).toBe("cancelled");
    expect(cancelledBeforePost.messages).toEqual([]);
  });
});
