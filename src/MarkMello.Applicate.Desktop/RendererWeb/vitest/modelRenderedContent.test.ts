import { afterEach, describe, expect, it, vi } from "vitest";
import { DocumentWindowModel, type SectionModelEntry } from "../src/documentWindow";
import { prepareDocumentWindowModelRenderedContent } from "../src/modelRenderedContent";

function entry(sectionIndex: number, html: string): SectionModelEntry {
  return {
    blockIndex: 200 + sectionIndex,
    cumulativeTop: 0,
    estimatedHeight: 100,
    headingLevel: 0,
    html,
    kind: "math",
    measuredHeight: undefined,
    sectionIndex,
  };
}

function makeKatex() {
  return {
    render: vi.fn((tex: string, node: HTMLElement) => {
      const rendered = node.ownerDocument.createElement("span");
      rendered.className = "katex";
      rendered.textContent = `rendered:${tex}`;
      node.replaceChildren(rendered);
    }),
  };
}

function installTemplateParseCounter(ownerDocument: Document = document): {
  readonly count: number;
  reset: () => void;
  restore: () => void;
} {
  const createElement = ownerDocument.createElement.bind(ownerDocument);
  let count = 0;
  ownerDocument.createElement = ((tagName: string, options?: ElementCreationOptions) => {
    if (tagName.toLowerCase() === "template") {
      count++;
    }
    return createElement(tagName, options);
  }) as typeof ownerDocument.createElement;

  return {
    get count() {
      return count;
    },
    reset() {
      count = 0;
    },
    restore() {
      ownerDocument.createElement = createElement as typeof ownerDocument.createElement;
    },
  };
}

function allPendingEntries(sectionCount: number): SectionModelEntry[] {
  return Array.from({ length: sectionCount }, (_value, index) =>
    entry(index, `<p data-mm-block-index='${200 + index}'><span class='math-inline' data-tex='x_${index}'>x_${index}</span></p>`));
}

function readModelMathStats(model: DocumentWindowModel): {
  failedMathCount: number;
  pendingMathCount: number;
  totalMathCount: number;
} {
  let failedMathCount = 0;
  let pendingMathCount = 0;
  let totalMathCount = 0;
  for (const section of model.sections) {
    const template = document.createElement("template");
    template.innerHTML = section.html ?? "";
    const mathNodes = Array.from(template.content.querySelectorAll<HTMLElement>("[data-tex]"));
    totalMathCount += mathNodes.length;
    for (const node of mathNodes) {
      const state = node.dataset["mmMathRendered"];
      if (state === "failed") {
        failedMathCount++;
      } else if (state !== "true") {
        pendingMathCount++;
      }
    }
  }
  return { failedMathCount, pendingMathCount, totalMathCount };
}

async function measureAllPendingPreparation(sectionCount: number, measureConstruction: boolean): Promise<{
  model: DocumentWindowModel;
  parseCount: number;
  progressEvents: unknown[];
  result: Awaited<ReturnType<typeof prepareDocumentWindowModelRenderedContent>>;
}> {
  const counter = installTemplateParseCounter();
  try {
    const model = new DocumentWindowModel(allPendingEntries(sectionCount));
    if (!measureConstruction) {
      counter.reset();
    }
    const progress = vi.fn();
    const result = await prepareDocumentWindowModelRenderedContent(model, {
      katex: makeKatex(),
      now: () => 0,
      onProgress: progress,
      ownerDocument: document,
      yield: () => Promise.resolve(),
    });
    const parseCount = counter.count;
    return {
      model,
      parseCount,
      progressEvents: progress.mock.calls.map(call => call[0]),
      result,
    };
  } finally {
    counter.restore();
  }
}

afterEach(() => {
  document.body.innerHTML = "";
  vi.restoreAllMocks();
});

describe("model rendered content preparation", () => {
  it("renders a never-mounted section in a detached template", async () => {
    const model = new DocumentWindowModel([
      entry(0, "<p data-mm-block-index='200'><span class='math-inline' data-tex='x'>x</span></p>"),
    ]);
    const katex = makeKatex();

    const result = await prepareDocumentWindowModelRenderedContent(model, {
      katex,
      now: () => 0,
      ownerDocument: document,
      yield: () => Promise.resolve(),
    });

    expect(result).toMatchObject({
      cancelled: false,
      committedSectionCount: 1,
      completed: true,
      failedMathCount: 0,
      renderedMathCount: 1,
      status: "ready",
    });
    expect(katex.render).toHaveBeenCalledTimes(1);
    expect(model.sections[0]?.html).toContain("class=\"katex\"");
    expect(model.sections[0]?.html).toContain("data-mm-math-rendered=\"true\"");
    expect(document.body.children).toHaveLength(0);
  });

  it("commits a multi-formula section only after every formula is terminal", async () => {
    const originalHtml = "<p data-mm-block-index='200'><span class='math-inline' data-tex='a'>a</span><span class='math-inline' data-tex='b'>b</span></p>";
    const model = new DocumentWindowModel([entry(0, originalHtml)]);
    const katex = makeKatex();
    katex.render.mockImplementation((tex: string, node: HTMLElement) => {
      expect(model.sections[0]?.html).toBe(originalHtml);
      const rendered = node.ownerDocument.createElement("span");
      rendered.className = "katex";
      rendered.textContent = tex;
      node.replaceChildren(rendered);
    });
    const progress = vi.fn();

    const result = await prepareDocumentWindowModelRenderedContent(model, {
      katex,
      now: () => 0,
      onProgress: progress,
      ownerDocument: document,
      yield: () => Promise.resolve(),
    });

    expect(katex.render.mock.calls.map(call => call[0])).toEqual(["a", "b"]);
    expect(result).toMatchObject({
      committedSectionCount: 1,
      pendingMathCount: 0,
      renderedMathCount: 2,
      status: "ready",
    });
    expect(model.sections[0]?.html).not.toBe(originalHtml);
    expect(model.sections[0]?.html?.match(/data-mm-math-rendered="true"/g)).toHaveLength(2);
    expect(progress).toHaveBeenCalledWith(expect.objectContaining({
      committed: true,
      failedMathCount: 0,
      pendingMathCount: 0,
      sectionIndex: 0,
      type: "progress",
    }));
  });

  it.each([12, 24])("parses each pending section once while publishing counter-backed progress for %i sections", async sectionCount => {
    // Current parse-on-read baseline:
    // construction+preparation = 2N^2 + 6N, preparation-only = 2N^2 + 5N.
    const fullMeasurement = await measureAllPendingPreparation(sectionCount, true);
    const preparationOnly = await measureAllPendingPreparation(sectionCount, false);
    const fullCeiling = 2 * sectionCount;
    const preparationCeiling = sectionCount;

    expect(fullMeasurement.parseCount).toBeLessThanOrEqual(fullCeiling);
    expect(preparationOnly.parseCount).toBeLessThanOrEqual(preparationCeiling);
    expect(fullMeasurement.result).toMatchObject({
      cancelled: false,
      committedSectionCount: sectionCount,
      completed: true,
      failedMathCount: 0,
      pendingMathCount: 0,
      renderedMathCount: sectionCount,
      status: "ready",
    });
    expect(preparationOnly.result).toMatchObject({
      cancelled: false,
      committedSectionCount: sectionCount,
      completed: true,
      failedMathCount: 0,
      pendingMathCount: 0,
      renderedMathCount: sectionCount,
      status: "ready",
    });
    expect(fullMeasurement.progressEvents).toHaveLength(sectionCount + 1);
    expect(fullMeasurement.progressEvents[0]).toMatchObject({
      pendingMathCount: sectionCount - 1,
      status: "unprepared",
      type: "progress",
    });
    expect(fullMeasurement.progressEvents.at(-2)).toMatchObject({
      pendingMathCount: 0,
      status: "ready",
      type: "progress",
    });
    expect(fullMeasurement.progressEvents.at(-1)).toMatchObject({
      pendingMathCount: 0,
      status: "ready",
      type: "complete",
    });

    expect(readModelMathStats(fullMeasurement.model)).toEqual({
      failedMathCount: 0,
      pendingMathCount: 0,
      totalMathCount: sectionCount,
    });
  });

  it("cancels before commit and leaves the in-progress section unchanged", async () => {
    const originalHtml = "<p data-mm-block-index='200'><span class='math-inline' data-tex='x'>x</span></p>";
    const model = new DocumentWindowModel([entry(0, originalHtml)]);
    const katex = makeKatex();
    const progress = vi.fn();

    const result = await prepareDocumentWindowModelRenderedContent(model, {
      katex,
      now: () => 0,
      onProgress: progress,
      ownerDocument: document,
      shouldContinue: () => false,
      yield: () => Promise.resolve(),
    });

    expect(result).toMatchObject({
      cancelled: true,
      committedSectionCount: 0,
      completed: false,
      status: "cancelled",
    });
    expect(katex.render).not.toHaveBeenCalled();
    expect(model.sections[0]?.html).toBe(originalHtml);
    expect(model.getRenderedContentState()).toBe("unprepared");
    expect(progress).toHaveBeenCalledWith(expect.objectContaining({
      pendingMathCount: 1,
      type: "cancelled",
    }));
  });

  it("resumes from already committed terminal sections after cancellation", async () => {
    const firstHtml = "<p data-mm-block-index='200'><span class='math-inline' data-tex='first'>first</span></p>";
    const secondHtml = "<p data-mm-block-index='201'><span class='math-inline' data-tex='second'>second</span></p>";
    const model = new DocumentWindowModel([
      entry(0, firstHtml),
      entry(1, secondHtml),
    ]);
    const firstKatex = makeKatex();
    let checks = 0;

    const cancelled = await prepareDocumentWindowModelRenderedContent(model, {
      katex: firstKatex,
      now: () => 0,
      ownerDocument: document,
      shouldContinue: () => {
        checks++;
        return checks <= 3;
      },
      yield: () => Promise.resolve(),
    });

    expect(cancelled).toMatchObject({
      cancelled: true,
      committedSectionCount: 1,
      status: "cancelled",
    });
    expect(model.sections[0]?.html).toContain("data-mm-math-rendered=\"true\"");
    expect(model.sections[1]?.html).toBe(secondHtml);

    const secondKatex = makeKatex();
    const resumed = await prepareDocumentWindowModelRenderedContent(model, {
      katex: secondKatex,
      now: () => 0,
      ownerDocument: document,
      yield: () => Promise.resolve(),
    });

    expect(secondKatex.render.mock.calls.map(call => call[0])).toEqual(["second"]);
    expect(resumed).toMatchObject({
      cancelled: false,
      committedSectionCount: 1,
      completed: true,
      renderedMathCount: 1,
      status: "ready",
    });
    expect(model.getRenderedContentState()).toBe("ready");
  });

  it("commits terminal failed KaTeX output and reports failed-count information", async () => {
    const model = new DocumentWindowModel([
      entry(0, "<p data-mm-block-index='200'><span class='math-inline' data-tex='bad'>bad</span></p>"),
    ]);
    const katex = makeKatex();
    katex.render.mockImplementation(() => {
      throw new Error("bad formula");
    });
    const progress = vi.fn();

    const result = await prepareDocumentWindowModelRenderedContent(model, {
      katex,
      now: () => 0,
      onProgress: progress,
      ownerDocument: document,
      yield: () => Promise.resolve(),
    });

    expect(result).toMatchObject({
      completed: true,
      failedMathCount: 1,
      pendingMathCount: 0,
      status: "ready-with-failures",
    });
    expect(model.sections[0]?.html).toContain("data-mm-math-rendered=\"failed\"");
    expect(model.getRenderedContentState()).toBe("ready-with-failures");
    expect(progress).toHaveBeenCalledWith(expect.objectContaining({
      failedMathCount: 1,
      type: "progress",
    }));
    expect(progress).toHaveBeenCalledWith(expect.objectContaining({
      failedMathCount: 1,
      status: "ready-with-failures",
      type: "complete",
    }));
  });

  it("commits ready-with-failures when a section mixes pre-existing failed and pending formulas", async () => {
    const model = new DocumentWindowModel([
      entry(0, [
        "<p data-mm-block-index='200'>",
        "<span class='math-inline' data-tex='bad' data-mm-math-rendered='failed'>bad</span>",
        "<span class='math-inline' data-tex='x'>x</span>",
        "</p>",
      ].join("")),
    ]);
    const katex = makeKatex();
    const progress = vi.fn();

    const result = await prepareDocumentWindowModelRenderedContent(model, {
      katex,
      now: () => 0,
      onProgress: progress,
      ownerDocument: document,
      yield: () => Promise.resolve(),
    });

    expect(katex.render.mock.calls.map(call => call[0])).toEqual(["x"]);
    expect(result).toMatchObject({
      committedSectionCount: 1,
      completed: true,
      failedMathCount: 1,
      pendingMathCount: 0,
      renderedMathCount: 1,
      status: "ready-with-failures",
    });
    expect(model.getRenderedContentState()).toBe("ready-with-failures");
    expect(model.sections[0]?.html).toContain("data-mm-math-rendered=\"failed\"");
    expect(model.sections[0]?.html).toContain("data-mm-math-rendered=\"true\"");
    expect(progress).toHaveBeenCalledWith(expect.objectContaining({
      committed: true,
      failedMathCount: 1,
      pendingMathCount: 0,
      status: "ready-with-failures",
      type: "progress",
    }));
  });

  it("reports skipped-no-katex without mutating pending model content", async () => {
    const originalHtml = "<p data-mm-block-index='200'><span class='math-inline' data-tex='x'>x</span></p>";
    const model = new DocumentWindowModel([entry(0, originalHtml)]);
    const progress = vi.fn();

    const result = await prepareDocumentWindowModelRenderedContent(model, {
      katex: undefined,
      now: () => 0,
      onProgress: progress,
      ownerDocument: document,
      yield: () => Promise.resolve(),
    });

    expect(result).toMatchObject({
      cancelled: false,
      completed: false,
      pendingMathCount: 1,
      skippedNoKatex: true,
      status: "unavailable",
    });
    expect(model.sections[0]?.html).toBe(originalHtml);
    expect(model.getRenderedContentState()).toBe("unprepared");
    expect(progress).toHaveBeenCalledWith(expect.objectContaining({
      pendingMathCount: 1,
      type: "skipped-no-katex",
    }));
  });
});
