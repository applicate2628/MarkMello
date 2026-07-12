import { describe, expect, it, vi } from "vitest";
import {
  DocumentWindowModel,
  buildDocumentWindowModelsFromLiveBlocks,
  type MeasuredHeightUpdate,
  type SectionModelEntry,
} from "../src/documentWindow";
import {
  captureReadingAnchor,
  createFullDocumentFragmentFromWindowModel,
  createVirtualizedDocumentWindowController,
  type VirtualizedDocumentWindowController,
} from "../src/virtualizedDocumentWindow";
import type { IntrinsicSizeMetrics, SectionKind } from "../src/sectionIntrinsicSize";
import { renderMermaidNode, type MermaidApiLike } from "../src/mermaidRender";
import { readFileSync } from "node:fs";

const metrics: IntrinsicSizeMetrics = {
  charsPerLine: 40,
  fontSizePx: 18,
  lineHeightPx: 30,
};

function entry(
  sectionIndex: number,
  blockIndex: number,
  estimatedHeight: number,
  options: Partial<SectionModelEntry> = {}
): SectionModelEntry {
  return {
    blockIndex,
    cumulativeTop: 0,
    estimatedHeight,
    headingLevel: 0,
    html: `<section data-mm-block-index="${blockIndex}" data-mm-block-kind="paragraph">Block ${blockIndex}</section>`,
    kind: "paragraph",
    measuredHeight: undefined,
    sectionIndex,
    ...options,
  };
}

function block(index: number, top: number, height: number, text = `Block ${index}`): HTMLElement {
  const element = document.createElement("section");
  element.dataset.mmBlockIndex = String(index);
  element.dataset.mmBlockKind = "paragraph";
  element.textContent = text;
  Object.defineProperty(element, "offsetTop", {
    configurable: true,
    get: () => top,
  });
  Object.defineProperty(element, "offsetHeight", {
    configurable: true,
    get: () => height,
  });
  return element;
}

function setScrollRoot(scrollTop: number, scrollHeight: number, clientHeight: number): {
  root: HTMLElement;
  setScrollTop: (value: number) => void;
  getScrollTop: () => number;
} {
  let mutableScrollTop = scrollTop;
  const root = document.documentElement;
  Object.defineProperty(document, "scrollingElement", {
    configurable: true,
    get: () => root,
  });
  Object.defineProperty(root, "scrollTop", {
    configurable: true,
    get: () => mutableScrollTop,
    set: value => {
      mutableScrollTop = value;
    },
  });
  Object.defineProperty(root, "scrollHeight", {
    configurable: true,
    get: () => scrollHeight,
  });
  Object.defineProperty(root, "clientHeight", {
    configurable: true,
    get: () => clientHeight,
  });
  return {
    getScrollTop: () => mutableScrollTop,
    root,
    setScrollTop: value => {
      mutableScrollTop = value;
    },
  };
}

function makeController(input: {
  model: DocumentWindowModel;
  root: HTMLElement;
  measure?: () => MeasuredHeightUpdate[];
  onRealizationStateChanged?: (mountGeneration: number) => void;
  prepare?: (root: ParentNode) => void;
  realization?: unknown;
  trace?: (event: { id: string; details?: Readonly<Record<string, unknown>> }) => void;
}): VirtualizedDocumentWindowController {
  const main = document.querySelector<HTMLElement>("main.mm-document")!;
  return createVirtualizedDocumentWindowController({
    main,
    model: input.model,
    ownerWindow: window,
    onRealizationStateChanged: input.onRealizationStateChanged,
    prepareInsertedContent: input.prepare,
    readMeasuredHeights: input.measure,
    realization: input.realization,
    renderAhead: {
      aboveViewports: 0,
      belowViewports: 0,
      minAbovePx: 0,
      minBelowPx: 0,
    },
    root: input.root,
    trace: input.trace,
  } as Parameters<typeof createVirtualizedDocumentWindowController>[0]);
}

function installFrameQueue(): { flush: (count?: number) => void; pending: () => number; restore: () => void } {
  const original = window.requestAnimationFrame;
  const callbacks: FrameRequestCallback[] = [];
  window.requestAnimationFrame = ((callback: FrameRequestCallback): number => {
    callbacks.push(callback);
    return callbacks.length;
  }) as typeof window.requestAnimationFrame;
  return {
    flush: (count = 1) => {
      for (let index = 0; index < count; index++) {
        const callback = callbacks.shift();
        if (callback === undefined) {
          throw new Error("Expected a queued requestAnimationFrame callback");
        }
        callback(performance.now());
      }
    },
    pending: () => callbacks.length,
    restore: () => {
      window.requestAnimationFrame = original;
    },
  };
}

function setElementLayout(
  element: HTMLElement,
  input: { top?: () => number; height?: () => number }
): void {
  Object.defineProperty(element, "offsetTop", {
    configurable: true,
    get: () => input.top?.() ?? 0,
  });
  Object.defineProperty(element, "offsetHeight", {
    configurable: true,
    get: () => input.height?.() ?? 0,
  });
}

function dispatchContentVisibilityState(element: HTMLElement, skipped: boolean): void {
  const event = new Event("contentvisibilityautostatechange", { bubbles: false });
  Object.defineProperty(event, "skipped", {
    configurable: true,
    value: skipped,
  });
  element.dispatchEvent(event);
}

function createMermaidPrepareHarness(): {
  flush: () => Promise<void>;
  prepare: ReturnType<typeof vi.fn<(root: ParentNode) => void>>;
} {
  let generation = 0;
  const pending: Promise<void>[] = [];
  const api: MermaidApiLike = {
    render: async () => ({ svg: "<svg>owned</svg>" }),
  };
  const prepare = vi.fn((root: ParentNode) => {
    const source = root.querySelector<HTMLElement>("pre.mm-mermaid");
    if (source === null) {
      return;
    }
    const currentGeneration = ++generation;
    pending.push(renderMermaidNode(
      source,
      currentGeneration,
      () => generation,
      api,
      1000,
      { manageVirtualizedProxyLifecycle: true }
    ));
  });
  return {
    flush: async () => {
      await Promise.all(pending.splice(0));
    },
    prepare,
  };
}

function setReadyMermaidLayout(
  source: HTMLElement,
  input: { proxyHeight?: number; proxyTop?: number; sourceHeight?: number } = {}
): HTMLElement {
  const proxy = source.nextElementSibling as HTMLElement;
  setElementLayout(source, {
    height: () => input.sourceHeight ?? 0,
    top: () => 0,
  });
  setElementLayout(proxy, {
    height: () => input.proxyHeight ?? 182,
    top: () => input.proxyTop ?? 50,
  });
  return proxy;
}

describe("virtualized document window", () => {
  it("captures section HTML while building the window model from the full DOM", () => {
    const models = buildDocumentWindowModelsFromLiveBlocks([
      block(10, 0, 80, "Alpha"),
      block(11, 100, 90, "Beta"),
    ], metrics, 220);

    expect(models.estimateOnlyModel.sections.map(section => section.html)).toEqual([
      '<section data-mm-block-index="10" data-mm-block-kind="paragraph">Alpha</section>',
      '<section data-mm-block-index="11" data-mm-block-kind="paragraph">Beta</section>',
    ]);
  });

  it("stamps full model fragments with model occupied height minus non-content metadata", () => {
    const measured = entry(0, 10, 120, { occupiedNonContentHeight: 16 });
    measured.measuredHeight = 210;
    const estimated = entry(1, 11, 140, { occupiedNonContentHeight: 24 });
    const model = new DocumentWindowModel([measured, estimated]);

    const fragment = createFullDocumentFragmentFromWindowModel(document, model);
    const nodes = Array.from(fragment.children) as HTMLElement[];

    expect(nodes.map(node => node.style.containIntrinsicSize)).toEqual([
      "auto 194px",
      "auto 116px",
    ]);
  });

  it("does not stamp unresolved non-content metadata", () => {
    const model = new DocumentWindowModel([entry(0, 12, 120)]);

    const fragment = createFullDocumentFragmentFromWindowModel(document, model);

    expect((fragment.firstElementChild as HTMLElement).style.containIntrinsicSize).toBe("");
  });

  it("replaces off-window sections with top and bottom spacers sized by the model", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root } = setScrollRoot(150, 450, 100);
    const model = new DocumentWindowModel([
      entry(0, 20, 100),
      entry(1, 21, 120),
      entry(2, 22, 80),
      entry(3, 23, 150),
    ]);
    const prepare = vi.fn();
    const controller = makeController({ model, prepare, root });

    controller.updateWindowForScroll();

    const main = document.querySelector<HTMLElement>("main.mm-document")!;
    expect(Array.from(main.querySelectorAll<HTMLElement>("[data-mm-block-index]")).map(node =>
      Number(node.dataset.mmBlockIndex))).toEqual([21, 22]);
    expect(main.querySelector<HTMLElement>("[data-mm-virtual-spacer='top']")?.style.height).toBe("100px");
    expect(main.querySelector<HTMLElement>("[data-mm-virtual-spacer='bottom']")?.style.height).toBe("150px");
    expect(main.textContent).not.toContain("Block 20");
    expect(main.textContent).not.toContain("Block 23");
    expect(prepare).toHaveBeenCalledTimes(1);
  });

  it("uses E-minus-K stamps while spacers keep the occupied model height", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root } = setScrollRoot(0, 500, 300);
    const model = new DocumentWindowModel([
      entry(0, 24, 165, { occupiedNonContentHeight: 18 }),
      entry(1, 25, 96, { occupiedNonContentHeight: 11 }),
    ]);
    const controller = makeController({ model, root });

    controller.updateWindowForScroll();

    const nodes = Array.from(document.querySelectorAll<HTMLElement>("[data-mm-block-index]"));
    expect(nodes.map(node => node.style.containIntrinsicSize)).toEqual([
      "auto 147px",
      "auto 85px",
    ]);
    expect(document.querySelector<HTMLElement>("[data-mm-virtual-spacer='bottom']")?.style.height)
      .toBe("0px");
    expect(model.getTotalHeight()).toBe(261);
  });

  it.each<SectionKind>(["heading", "paragraph", "quote", "list", "rule", "code", "table", "image", "math", "unknown"])(
    "stamps top-level %s sections through the same occupied metadata path",
    kind => {
      document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
      const { root } = setScrollRoot(0, 500, 100);
      const model = new DocumentWindowModel([
        entry(0, 240, 70, {
          html: `<section data-mm-block-index="240" data-mm-block-kind="${kind}">${kind}</section>`,
          kind,
          occupiedNonContentHeight: 13,
        }),
      ]);
      const controller = makeController({ model, root });

      controller.updateWindowForScroll();

      expect(document.querySelector<HTMLElement>("[data-mm-block-index='240']")?.style.containIntrinsicSize)
        .toBe("auto 57px");
    }
  );

  it("ordinary code pre remains in generic E-minus-K stamp", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root } = setScrollRoot(0, 500, 100);
    const model = new DocumentWindowModel([
      entry(0, 241, 165, {
        html: '<pre data-mm-block-index="241" data-mm-block-kind="code">code</pre>',
        kind: "code",
        occupiedNonContentHeight: 54,
      }),
    ]);
    const controller = makeController({ model, root });

    controller.updateWindowForScroll();

    expect(document.querySelector<HTMLElement>("pre")?.style.containIntrinsicSize).toBe("auto 111px");
  });

  it("top-level block box sizing is compatible with content-box stamping", async () => {
    const css = readFileSync("RendererWeb/assets/renderer.css", "utf8");
    document.documentElement.innerHTML = `<head><style>${css}</style></head><body><main class="mm-document"></main></body>`;
    const { root } = setScrollRoot(0, 2000, 2000);
    const kinds: SectionKind[] = [
      "heading", "paragraph", "quote", "list", "rule", "code", "table", "image", "math", "unknown",
    ];
    const markup: Record<SectionKind, string> = {
      heading: "<h2>heading</h2>",
      paragraph: "<p>paragraph</p>",
      quote: "<blockquote>quote</blockquote>",
      list: "<ul><li>list</li></ul>",
      rule: "<hr>",
      code: "<pre><code>code</code></pre>",
      table: '<div class="mm-table-scroll"><table><tbody><tr><td>table</td></tr></tbody></table></div>',
      image: '<figure><img alt="image"></figure>',
      math: '<div class="math-display">math</div>',
      unknown: "<section>unknown</section>",
    };
    const isCompatible = (element: HTMLElement): boolean => {
      const style = window.getComputedStyle(element);
      const boxSizing = style.boxSizing || "content-box";
      if (boxSizing === "content-box") return true;
      if (boxSizing !== "border-box") return false;
      return [style.paddingTop, style.paddingBottom, style.borderTopWidth, style.borderBottomWidth]
        .every(value => value === "" || value === "0" || value === "0px");
    };
    const model = new DocumentWindowModel(kinds.map((kind, index) => entry(index, 300 + index, 50, {
      html: markup[kind].replace(">", ` data-mm-block-index="${300 + index}" data-mm-block-kind="${kind}">`),
      kind,
      occupiedNonContentHeight: 0,
    })));
    makeController({ model, root }).updateWindowForScroll();
    const generated = Array.from(document.querySelectorAll<HTMLElement>("main.mm-document > [data-mm-block-kind]"));
    expect(generated.map(node => node.dataset.mmBlockKind)).toEqual(kinds);

    const mermaidSource = document.createElement("pre");
    mermaidSource.className = "mm-mermaid";
    mermaidSource.innerHTML = '<code data-mm-mermaid>flowchart LR\nA --&gt; B</code>';
    document.querySelector("main.mm-document")!.append(mermaidSource);
    await renderMermaidNode(
      mermaidSource,
      1,
      () => 1,
      { render: async () => ({ svg: "<svg>owned</svg>" }) },
      1000,
      { manageVirtualizedProxyLifecycle: true }
    );
    const mermaidProxy = mermaidSource.nextElementSibling as HTMLElement;
    expect([...generated, mermaidProxy].every(isCompatible)).toBe(true);

    const invalid = generated.find(node => node.dataset.mmBlockKind === "code")!;
    invalid.classList.add("qa-padded-border-box");
    document.head.insertAdjacentHTML("beforeend", "<style>.qa-padded-border-box{box-sizing:border-box;padding-top:8px}</style>");
    expect(isCompatible(invalid)).toBe(false);
  });

  it("does not re-anchor when a scroll changes only the realized window", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { getScrollTop, root, setScrollTop } = setScrollRoot(150, 450, 100);
    const model = new DocumentWindowModel([
      entry(0, 30, 100),
      entry(1, 31, 120),
      entry(2, 32, 80),
      entry(3, 33, 150),
    ]);
    const controller = makeController({ model, root });
    const requestScrollTop = vi.fn();

    controller.updateWindowForScroll();
    setScrollTop(320);
    controller.updateWindowForScroll({ operation: { requestScrollTop } });

    const main = document.querySelector<HTMLElement>("main.mm-document")!;
    expect(Array.from(main.querySelectorAll<HTMLElement>("[data-mm-block-index]")).map(node =>
      Number(node.dataset.mmBlockIndex))).toEqual([33]);
    expect(getScrollTop()).toBe(320);
    expect(requestScrollTop).not.toHaveBeenCalled();
  });

  it("re-anchors the current scroll by the model shift after a window update", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root, setScrollTop } = setScrollRoot(150, 450, 100);
    const model = new DocumentWindowModel([
      entry(0, 34, 100),
      entry(1, 35, 100),
      entry(2, 36, 100),
      entry(3, 37, 100),
    ]);
    let mutateGeometry = false;
    const controller = createVirtualizedDocumentWindowController({
      main: document.querySelector<HTMLElement>("main.mm-document")!,
      model,
      onWindowMounted: () => {
        if (!mutateGeometry) {
          return;
        }
        model.updateMeasuredHeightsByBlockIndex([{ blockIndex: 34, measuredHeight: 130 }]);
        setScrollTop(335);
      },
      ownerWindow: window,
      renderAhead: {
        aboveViewports: 0,
        belowViewports: 0,
        minAbovePx: 0,
        minBelowPx: 0,
      },
      root,
    });
    const requestScrollTop = vi.fn();

    controller.updateWindowForScroll();
    setScrollTop(320);
    mutateGeometry = true;
    controller.updateWindowForScroll({ operation: { requestScrollTop } });

    expect(model.sectionTop(3)).toBe(330);
    expect(requestScrollTop).toHaveBeenCalledOnce();
    expect(requestScrollTop).toHaveBeenCalledWith(365, "scroll-window-reanchor");
  });

  it("re-anchors after adopting a measured height above the viewport", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { getScrollTop, root } = setScrollRoot(250, 400, 100);
    const model = new DocumentWindowModel([
      entry(0, 40, 100),
      entry(1, 41, 100),
      entry(2, 42, 100),
      entry(3, 43, 100),
    ]);
    const controller = createVirtualizedDocumentWindowController({
      main: document.querySelector<HTMLElement>("main.mm-document")!,
      model,
      ownerWindow: window,
      readMeasuredHeights: () => [{ blockIndex: 41, measuredHeight: 150 }],
      renderAhead: {
        aboveViewports: 1,
        belowViewports: 0,
        minAbovePx: 0,
        minBelowPx: 0,
      },
      root,
    });

    controller.updateWindowForScroll();
    const adopted = controller.adoptRenderedHeights({
      operation: { requestScrollTop: target => { root.scrollTop = target; } },
    });

    expect(adopted.updatedCount).toBe(1);
    expect(getScrollTop()).toBe(300);
    expect(model.sectionTop(2)).toBe(250);
    expect(document.querySelector<HTMLElement>("[data-mm-virtual-spacer='top']")?.style.height).toBe("100px");
  });

  it("preserves the first visible rendered block offset when measured heights change", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { getScrollTop, root } = setScrollRoot(95, 500, 100);
    const model = new DocumentWindowModel([
      entry(0, 80, 100),
      entry(1, 81, 100),
      entry(2, 82, 100),
      entry(3, 83, 100),
      entry(4, 84, 100),
    ]);
    const rectSpy = vi.spyOn(window.HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      const blockIndex = Number.parseInt(this.dataset.mmBlockIndex ?? "", 10);
      const entry = Number.isFinite(blockIndex) ? model.getEntryByBlockIndex(blockIndex) : undefined;
      let top = entry === undefined ? 0 : entry.cumulativeTop - root.scrollTop;
      if (model.sectionTop(1) === 100) {
        if (blockIndex === 80) {
          top = -120;
        } else if (blockIndex === 81) {
          top = 10;
        }
      }
      const height = entry === undefined ? 0 : model.sectionEffectiveHeight(entry.sectionIndex);
      return {
        bottom: top + height,
        height,
        left: 0,
        right: 0,
        top,
        width: 0,
        x: 0,
        y: top,
        toJSON() {
          return this;
        },
      } as DOMRect;
    });
    const controller = createVirtualizedDocumentWindowController({
      main: document.querySelector<HTMLElement>("main.mm-document")!,
      model,
      ownerWindow: window,
      readMeasuredHeights: () => [
        { blockIndex: 80, measuredHeight: 150 },
        { blockIndex: 81, measuredHeight: 80 },
      ],
      renderAhead: {
        aboveViewports: 1,
        belowViewports: 0,
        minAbovePx: 0,
        minBelowPx: 0,
      },
      root,
    });

    try {
      controller.updateWindowForScroll();
      const anchorBefore = document.querySelector<HTMLElement>('[data-mm-block-index="81"]')!;
      const anchorTopBefore = anchorBefore.getBoundingClientRect().top;
      const adopted = controller.adoptRenderedHeights({
        operation: { requestScrollTop: target => { root.scrollTop = target; } },
      });
      const anchorAfter = document.querySelector<HTMLElement>('[data-mm-block-index="81"]')!;

      expect(adopted.updatedCount).toBe(2);
      expect(anchorTopBefore).toBe(10);
      expect(anchorAfter.getBoundingClientRect().top).toBeCloseTo(0);
      expect(getScrollTop()).toBe(150);
    } finally {
      rectSpy.mockRestore();
    }
  });

  it("re-covers the viewport after restore-time calibration changes an unchanged range", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { getScrollTop, root, setScrollTop } = setScrollRoot(250, 400, 50);
    const model = new DocumentWindowModel([
      entry(0, 50, 100),
      entry(1, 51, 100),
      entry(2, 52, 100),
      entry(3, 53, 100),
    ]);
    const controller = makeController({ model, root });

    controller.updateWindowForScroll();
    expect(controller.getCurrentRange()).toEqual({ start: 2, end: 3 });
    expect(document.querySelector<HTMLElement>("[data-mm-virtual-spacer='top']")?.style.height).toBe("200px");

    const restoredAnchor = model.captureAnchor(getScrollTop());
    model.updateMeasuredHeightsByBlockIndex([{ blockIndex: 50, measuredHeight: 150 }]);
    setScrollTop(model.scrollTopForAnchor(restoredAnchor));

    expect(controller.updateWindowForScroll({ force: true })).toBe(true);

    const range = controller.getCurrentRange()!;
    const viewportTop = getScrollTop();
    const viewportBottom = viewportTop + 50;
    const rangeTop = model.sectionTop(range.start);
    const rangeBottom = model.sectionTop(range.end) + model.sectionEffectiveHeight(range.end);

    expect(range).toEqual({ start: 2, end: 3 });
    expect(document.querySelector<HTMLElement>("[data-mm-virtual-spacer='top']")?.style.height).toBe("250px");
    expect(viewportTop).toBe(300);
    expect(rangeTop).toBeLessThanOrEqual(viewportTop);
    expect(rangeBottom).toBeGreaterThanOrEqual(viewportBottom);
  });

  it("measures windowed sections by their own rendered height, not the bottom spacer", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root } = setScrollRoot(0, 1000, 100);
    const model = new DocumentWindowModel([
      entry(0, 60, 100),
      entry(1, 61, 100),
      entry(2, 62, 100),
    ]);
    const previousOffsetHeight = Object.getOwnPropertyDescriptor(window.HTMLElement.prototype, "offsetHeight");
    Object.defineProperty(window.HTMLElement.prototype, "offsetHeight", {
      configurable: true,
      get() {
        const blockIndex = Number.parseInt((this as HTMLElement).dataset.mmBlockIndex ?? "", 10);
        if (blockIndex === 60) return 90;
        if (blockIndex === 61) return 110;
        return 0;
      },
    });

    try {
      const controller = makeController({ model, root });
      controller.updateWindowForScroll();
      const adopted = controller.adoptRenderedHeights();

      expect(adopted.updatedCount).toBe(2);
      expect(model.getEntryByBlockIndex(60)?.measuredHeight).toBe(90);
      expect(model.getEntryByBlockIndex(61)?.measuredHeight).toBe(110);
    } finally {
      if (previousOffsetHeight) {
        Object.defineProperty(window.HTMLElement.prototype, "offsetHeight", previousOffsetHeight);
      } else {
        delete (window.HTMLElement.prototype as HTMLElement & { offsetHeight?: number }).offsetHeight;
      }
    }
  });

  it("promotes visible event-realized geometry from one finite producer sample", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root } = setScrollRoot(0, 400, 180);
    const frames = installFrameQueue();
    let blockHeight = 122;
    let occupied = 144;
    const model = new DocumentWindowModel([
      entry(0, 242, 104, {
        html: '<section data-mm-block-index="242" data-mm-block-kind="paragraph" style="content-visibility:auto">Block 242</section>',
        occupiedNonContentHeight: 22,
      }),
    ]);
    const controller = makeController({
      model,
      realization: { enabled: true },
      root,
    });

    try {
      controller.updateWindowForScroll();
      const blockNode = document.querySelector<HTMLElement>("[data-mm-block-index='242']")!;
      const bottomSpacer = document.querySelector<HTMLElement>("[data-mm-virtual-spacer='bottom']")!;
      setElementLayout(blockNode, { height: () => blockHeight, top: () => 0 });
      setElementLayout(bottomSpacer, { top: () => occupied });

      dispatchContentVisibilityState(blockNode, false);
      frames.flush();
      const adopted = controller.adoptRenderedHeights();

      expect(adopted.updatedCount).toBe(1);
      expect(model.getEntryByBlockIndex(242)?.measuredHeight).toBe(144);
    } finally {
      frames.restore();
    }
  });

  it("keeps event-realized geometry fail-closed when occupied adjacency is unresolved", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root } = setScrollRoot(0, 400, 180);
    const frames = installFrameQueue();
    const model = new DocumentWindowModel([
      entry(0, 250, 104, {
        html: '<section data-mm-block-index="250" data-mm-block-kind="paragraph" style="content-visibility:auto">Block 250</section>',
        occupiedNonContentHeight: 22,
      }),
    ]);
    const controller = makeController({
      model,
      realization: { enabled: true },
      root,
    });

    try {
      controller.updateWindowForScroll();
      const blockNode = document.querySelector<HTMLElement>("[data-mm-block-index='250']")!;
      setElementLayout(blockNode, { height: () => 122, top: () => 0 });

      dispatchContentVisibilityState(blockNode, false);
      frames.flush();

      expect(controller.adoptRenderedHeights().updatedCount).toBe(0);
      expect(model.getEntryByBlockIndex(250)?.measuredHeight).toBeUndefined();
    } finally {
      frames.restore();
    }
  });

  it("matches filtered measured updates to the same current realized node", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root } = setScrollRoot(0, 500, 240);
    const frames = installFrameQueue();
    let firstHeight = 150;
    const model = new DocumentWindowModel([
      entry(0, 251, 104, {
        html: '<section data-mm-block-index="251" data-mm-block-kind="paragraph" style="content-visibility:auto">Block 251</section>',
        occupiedNonContentHeight: 22,
      }),
      entry(1, 252, 104, {
        html: '<section data-mm-block-index="252" data-mm-block-kind="paragraph" style="content-visibility:auto">Block 252</section>',
        occupiedNonContentHeight: 22,
      }),
    ]);
    const controller = makeController({
      model,
      realization: { enabled: true },
      root,
    });

    try {
      controller.updateWindowForScroll();
      const firstNode = document.querySelector<HTMLElement>("[data-mm-block-index='251']")!;
      const secondNode = document.querySelector<HTMLElement>("[data-mm-block-index='252']")!;
      const bottomSpacer = document.querySelector<HTMLElement>("[data-mm-virtual-spacer='bottom']")!;
      setElementLayout(firstNode, { height: () => firstHeight, top: () => 0 });
      setElementLayout(secondNode, { height: () => 133, top: () => 180 });
      setElementLayout(bottomSpacer, { top: () => 333 });

      dispatchContentVisibilityState(firstNode, false);
      frames.flush();
      firstHeight = 0;
      const adopted = controller.adoptRenderedHeights();

      expect(adopted.updatedCount).toBe(0);
      expect(model.getEntryByBlockIndex(252)?.measuredHeight).toBeUndefined();
    } finally {
      frames.restore();
    }
  });

  it("re-adopts late real-ready growth while preserving the off-screen height pin", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root } = setScrollRoot(0, 800, 240);
    const frames = installFrameQueue();
    let blockHeight = 122;
    let blockTop = 0;
    let occupiedBottom = 144;
    const model = new DocumentWindowModel([
      entry(0, 253, 104, {
        html: '<section data-mm-block-index="253" data-mm-block-kind="paragraph" style="content-visibility:auto">Block 253</section>',
        occupiedNonContentHeight: 22,
      }),
    ]);
    const controller = makeController({
      model,
      realization: { enabled: true },
      root,
    });

    try {
      controller.updateWindowForScroll();
      const blockNode = document.querySelector<HTMLElement>("[data-mm-block-index='253']")!;
      let bottomSpacer = document.querySelector<HTMLElement>("[data-mm-virtual-spacer='bottom']")!;
      setElementLayout(blockNode, { height: () => blockHeight, top: () => blockTop });
      setElementLayout(bottomSpacer, { top: () => occupiedBottom });

      dispatchContentVisibilityState(blockNode, false);
      frames.flush();
      expect(controller.adoptRenderedHeights().updatedCount).toBe(1);
      expect(model.getEntryByBlockIndex(253)?.measuredHeight).toBe(144);

      blockHeight = 162;
      occupiedBottom = 184;
      bottomSpacer = document.querySelector<HTMLElement>("[data-mm-virtual-spacer='bottom']")!;
      setElementLayout(bottomSpacer, { top: () => occupiedBottom });
      expect(controller.adoptRenderedHeights().updatedCount).toBe(1);
      expect(model.getEntryByBlockIndex(253)?.measuredHeight).toBe(184);

      blockTop = 500;
      blockHeight = 82;
      occupiedBottom = 104;
      bottomSpacer = document.querySelector<HTMLElement>("[data-mm-virtual-spacer='bottom']")!;
      setElementLayout(bottomSpacer, { top: () => occupiedBottom });
      controller.adoptRenderedHeights();
      expect(model.getEntryByBlockIndex(253)?.measuredHeight).toBe(184);
    } finally {
      frames.restore();
    }
  });

  it("keeps equal-fallback event geometry diagnostic-only", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root } = setScrollRoot(0, 400, 180);
    const frames = installFrameQueue();
    const model = new DocumentWindowModel([
      entry(0, 243, 104, {
        html: '<section data-mm-block-index="243" data-mm-block-kind="paragraph" style="content-visibility:auto">Block 243</section>',
        occupiedNonContentHeight: 22,
      }),
    ]);
    const controller = makeController({
      model,
      realization: { enabled: true },
      root,
    });

    try {
      controller.updateWindowForScroll();
      const blockNode = document.querySelector<HTMLElement>("[data-mm-block-index='243']")!;
      const bottomSpacer = document.querySelector<HTMLElement>("[data-mm-virtual-spacer='bottom']")!;
      setElementLayout(blockNode, { height: () => 82, top: () => 0 });
      setElementLayout(bottomSpacer, { top: () => 104 });

      dispatchContentVisibilityState(blockNode, false);
      frames.flush();

      expect(controller.adoptRenderedHeights().updatedCount).toBe(0);
      expect(model.getEntryByBlockIndex(243)?.measuredHeight).toBeUndefined();
    } finally {
      frames.restore();
    }
  });

  it("does not consume a realization budget until animation frames are delivered", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root } = setScrollRoot(0, 400, 180);
    const frames = installFrameQueue();
    const model = new DocumentWindowModel([
      entry(0, 244, 104, {
        html: '<section data-mm-block-index="244" data-mm-block-kind="paragraph" style="content-visibility:auto">Block 244</section>',
        occupiedNonContentHeight: 22,
      }),
    ]);
    const controller = makeController({
      model,
      realization: { enabled: true },
      root,
    });

    try {
      controller.updateWindowForScroll();
      const blockNode = document.querySelector<HTMLElement>("[data-mm-block-index='244']")!;
      const bottomSpacer = document.querySelector<HTMLElement>("[data-mm-virtual-spacer='bottom']")!;
      setElementLayout(blockNode, { height: () => 122, top: () => 0 });
      setElementLayout(bottomSpacer, { top: () => 144 });

      dispatchContentVisibilityState(blockNode, false);

      expect(controller.adoptRenderedHeights().updatedCount).toBe(0);
      expect(frames.pending()).toBe(1);
      frames.flush();
      expect(controller.adoptRenderedHeights().updatedCount).toBe(1);
    } finally {
      frames.restore();
    }
  });

  it("preserves false-event state when the event precedes strict intersection", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root, setScrollTop } = setScrollRoot(0, 1400, 100);
    const frames = installFrameQueue();
    const model = new DocumentWindowModel([
      entry(0, 245, 104, {
        html: '<section data-mm-block-index="245" data-mm-block-kind="paragraph" style="content-visibility:auto">Block 245</section>',
        occupiedNonContentHeight: 22,
      }),
    ]);
    const controller = makeController({
      model,
      realization: { enabled: true },
      root,
    });

    try {
      controller.updateWindowForScroll();
      const blockNode = document.querySelector<HTMLElement>("[data-mm-block-index='245']")!;
      const bottomSpacer = document.querySelector<HTMLElement>("[data-mm-virtual-spacer='bottom']")!;
      setElementLayout(blockNode, { height: () => 122, top: () => 1000 });
      setElementLayout(bottomSpacer, { top: () => 1144 });

      dispatchContentVisibilityState(blockNode, false);
      setScrollTop(960);
      frames.flush();

      expect(controller.adoptRenderedHeights().updatedCount).toBe(1);
      expect(model.getEntryByBlockIndex(245)?.measuredHeight).toBe(144);
    } finally {
      frames.restore();
    }
  });

  it.each([
    {
      name: "real-ready",
      settle: (blockNode: HTMLElement, bottomSpacer: HTMLElement, admission: number) => {
        setElementLayout(blockNode, { height: () => 122 + admission * 10, top: () => 0 });
        setElementLayout(bottomSpacer, { top: () => 144 + admission * 10 });
      },
      updatedCount: 1,
    },
    {
      name: "equal-fallback noop",
      settle: (blockNode: HTMLElement, bottomSpacer: HTMLElement) => {
        setElementLayout(blockNode, { height: () => 82, top: () => 0 });
        setElementLayout(bottomSpacer, { top: () => 104 });
      },
      updatedCount: 0,
    },
  ])("resets non-convergent realization history after $name convergence", ({ settle, updatedCount }) => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root } = setScrollRoot(0, 400, 180);
    const frames = installFrameQueue();
    let admission = 0;
    const traces: Array<{ id: string; details?: Readonly<Record<string, unknown>> }> = [];
    const model = new DocumentWindowModel([
      entry(0, 254, 104, {
        html: '<section data-mm-block-index="254" data-mm-block-kind="paragraph" style="content-visibility:auto">Block 254</section>',
        occupiedNonContentHeight: 22,
      }),
    ]);
    const controller = makeController({
      model,
      realization: { enabled: true },
      root,
      trace: event => traces.push(event),
    });

    const expireOnce = (blockNode: HTMLElement, expectedCycle: number): void => {
      const bottomSpacer = document.querySelector<HTMLElement>("[data-mm-virtual-spacer='bottom']")!;
      setElementLayout(blockNode, { height: () => 120, top: () => 0 });
      setElementLayout(bottomSpacer, { top: () => Number.NaN });
      dispatchContentVisibilityState(blockNode, false);
      for (let index = 0; index < 120; index++) {
        frames.flush();
      }
      expect(traces.filter(trace => trace.id === "mm-virt-realization-expired").at(-1)?.details)
        .toMatchObject({ blockIndex: 254, cycles: expectedCycle });
    };

    const convergeOnce = (blockNode: HTMLElement): void => {
      const bottomSpacer = document.querySelector<HTMLElement>("[data-mm-virtual-spacer='bottom']")!;
      settle(blockNode, bottomSpacer, admission++);
      dispatchContentVisibilityState(blockNode, false);
      frames.flush();
      expect(controller.adoptRenderedHeights().updatedCount).toBe(updatedCount);
    };

    try {
      controller.updateWindowForScroll();
      const blockNode = document.querySelector<HTMLElement>("[data-mm-block-index='254']")!;

      expireOnce(blockNode, 1);
      expect(controller.recensusRealizationWatches()).toBe(false);
      convergeOnce(blockNode);

      expireOnce(blockNode, 1);
      expect(controller.recensusRealizationWatches()).toBe(false);
      convergeOnce(blockNode);

      expireOnce(blockNode, 1);
      expect(controller.recensusRealizationWatches()).toBe(false);
      expect(traces.filter(trace => trace.id === "mm-virt-realization-quarantined")).toHaveLength(0);
    } finally {
      frames.restore();
    }
  });

  it("quarantines non-convergent realization after three cycles without adopting geometry", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root } = setScrollRoot(0, 400, 180);
    const frames = installFrameQueue();
    const traces: Array<{ id: string; details?: Readonly<Record<string, unknown>> }> = [];
    const realizationStateChanges: number[] = [];
    const model = new DocumentWindowModel([
      entry(0, 246, 104, {
        html: '<section data-mm-block-index="246" data-mm-block-kind="paragraph" style="content-visibility:auto">Block 246</section>',
        occupiedNonContentHeight: 22,
      }),
    ]);
    const controller = makeController({
      model,
      onRealizationStateChanged: mountGeneration => realizationStateChanges.push(mountGeneration),
      realization: { enabled: true },
      root,
      trace: event => traces.push(event),
    });

    try {
      controller.updateWindowForScroll();
      const blockNode = document.querySelector<HTMLElement>("[data-mm-block-index='246']")!;
      const bottomSpacer = document.querySelector<HTMLElement>("[data-mm-virtual-spacer='bottom']")!;
      setElementLayout(blockNode, { height: () => 120, top: () => 0 });
      setElementLayout(bottomSpacer, { top: () => Number.NaN });

      dispatchContentVisibilityState(blockNode, false);
      for (let cycle = 1; cycle <= 3; cycle++) {
        for (let index = 0; index < 120; index++) {
          frames.flush();
        }
        expect(traces.filter(trace => trace.id === "mm-virt-realization-expired").at(-1)?.details)
          .toMatchObject({ blockIndex: 246, cycles: cycle });
        expect(controller.recensusRealizationWatches()).toBe(cycle === 3);
      }

      expect(controller.adoptRenderedHeights().updatedCount).toBe(0);
      expect(model.getEntryByBlockIndex(246)?.measuredHeight).toBeUndefined();
      expect(traces.filter(trace => trace.id === "mm-virt-realization-quarantined")).toEqual([
        expect.objectContaining({
          details: expect.objectContaining({ blockIndex: 246, cycles: 3, mountGeneration: 1 }),
        }),
      ]);
      expect(realizationStateChanges).toEqual([1, 1, 1]);

      dispatchContentVisibilityState(blockNode, false);
      expect(frames.pending()).toBe(0);
      expect(controller.recensusRealizationWatches()).toBe(true);
      expect(traces.filter(trace => trace.id === "mm-virt-realization-quarantined")).toHaveLength(1);
    } finally {
      frames.restore();
    }
  });

  it("creates a fresh realization watch after a quarantined block is remounted", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root, setScrollTop } = setScrollRoot(0, 600, 100);
    const frames = installFrameQueue();
    const model = new DocumentWindowModel([
      entry(0, 246, 100, {
        html: '<section data-mm-block-index="246" data-mm-block-kind="paragraph" style="content-visibility:auto">Block 246</section>',
        occupiedNonContentHeight: 22,
      }),
      entry(1, 247, 100, {
        html: '<section data-mm-block-index="247" data-mm-block-kind="paragraph" style="content-visibility:auto">Block 247</section>',
        occupiedNonContentHeight: 22,
      }),
    ]);
    const controller = makeController({ model, realization: { enabled: true }, root });

    try {
      controller.updateWindowForScroll();
      const quarantinedNode = document.querySelector<HTMLElement>("[data-mm-block-index='246']")!;
      const initialBottomSpacer = document.querySelector<HTMLElement>("[data-mm-virtual-spacer='bottom']")!;
      setElementLayout(quarantinedNode, { height: () => 120, top: () => 0 });
      setElementLayout(initialBottomSpacer, { top: () => Number.NaN });
      dispatchContentVisibilityState(quarantinedNode, false);
      for (let cycle = 1; cycle <= 3; cycle++) {
        for (let index = 0; index < 120; index++) {
          frames.flush();
        }
        expect(controller.recensusRealizationWatches()).toBe(cycle === 3);
      }

      setScrollTop(140);
      controller.updateWindowForScroll({ force: true });
      expect(document.body.contains(quarantinedNode)).toBe(false);
      setScrollTop(0);
      controller.updateWindowForScroll({ force: true });

      const remountedNode = document.querySelector<HTMLElement>("[data-mm-block-index='246']")!;
      const remountedSuccessor = document.querySelector<HTMLElement>("[data-mm-block-index='247']")!;
      expect(remountedNode).not.toBe(quarantinedNode);
      setElementLayout(remountedNode, { height: () => 122, top: () => 0 });
      setElementLayout(remountedSuccessor, { height: () => 100, top: () => 144 });
      dispatchContentVisibilityState(remountedNode, false);
      frames.flush();

      expect(controller.adoptRenderedHeights().updatedCount).toBe(1);
      expect(model.getEntryByBlockIndex(246)?.measuredHeight).toBe(144);
    } finally {
      frames.restore();
    }
  });

  it("cleans realized watches on eviction and controller disposal", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root, setScrollTop } = setScrollRoot(0, 600, 100);
    const main = document.querySelector<HTMLElement>("main.mm-document")!;
    const addSpy = vi.spyOn(main, "addEventListener");
    const removeSpy = vi.spyOn(main, "removeEventListener");
    const model = new DocumentWindowModel([
      entry(0, 247, 100, {
        html: '<section data-mm-block-index="247" data-mm-block-kind="paragraph" style="content-visibility:auto">Block 247</section>',
        occupiedNonContentHeight: 20,
      }),
      entry(1, 248, 100, {
        html: '<section data-mm-block-index="248" data-mm-block-kind="paragraph" style="content-visibility:auto">Block 248</section>',
        occupiedNonContentHeight: 20,
      }),
    ]);
    const controller = makeController({
      model,
      realization: { enabled: true },
      root,
    });

    controller.updateWindowForScroll();
    const firstNode = document.querySelector<HTMLElement>("[data-mm-block-index='247']")!;
    setScrollTop(140);
    controller.updateWindowForScroll({ force: true });
    dispatchContentVisibilityState(firstNode, false);
    controller.dispose();

    expect(model.getEntryByBlockIndex(247)?.measuredHeight).toBeUndefined();
    expect(addSpy).toHaveBeenCalledWith(
      "contentvisibilityautostatechange",
      expect.any(Function),
      expect.objectContaining({ capture: true })
    );
    expect(removeSpy).toHaveBeenCalledWith(
      "contentvisibilityautostatechange",
      expect.any(Function),
      expect.objectContaining({ capture: true })
    );
  });

  it("reaches an occupied fixed point across ten remount cycles", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root } = setScrollRoot(0, 600, 100);
    const model = new DocumentWindowModel([
      entry(0, 249, 165, { occupiedNonContentHeight: 18 }),
    ]);
    const controller = makeController({ model, root });
    const totals: number[] = [];

    for (let index = 0; index < 10; index++) {
      expect(controller.updateWindowForScroll({ force: true })).toBe(true);
      totals.push(model.getTotalHeight());
      expect(document.querySelector<HTMLElement>("[data-mm-block-index='249']")?.style.containIntrinsicSize)
        .toBe("auto 147px");
    }

    expect(new Set(totals)).toEqual(new Set([165]));
  });

  it("rendered mermaid source and proxy remain one adjacency unit on reuse", async () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root, setScrollTop } = setScrollRoot(0, 500, 50);
    const model = new DocumentWindowModel([
      entry(0, 260, 100, {
        hasMermaid: true,
        html: '<pre class="mm-mermaid" data-mm-block-index="260" data-mm-block-kind="code"><code data-mm-mermaid>graph TD</code></pre>',
        kind: "code",
      }),
      entry(1, 261, 100),
    ]);
    const mermaid = createMermaidPrepareHarness();
    const controller = makeController({ model, prepare: mermaid.prepare, root });

    controller.updateWindowForScroll();
    await mermaid.flush();
    const source = document.querySelector<HTMLElement>("[data-mm-block-index='260']")!;
    const proxy = setReadyMermaidLayout(source);

    expect(controller.updateWindowForScroll({ force: true })).toBe(true);
    expect(source.nextElementSibling).toBe(proxy);
    expect(proxy.isConnected).toBe(true);
    expect(mermaid.prepare).toHaveBeenCalledTimes(1);

    setScrollTop(150);
    controller.updateWindowForScroll({ force: true });

    expect(source.isConnected).toBe(false);
    expect(proxy.isConnected).toBe(false);
  });

  it("stale rendered source without proxy is re-rendered before adoption", async () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root } = setScrollRoot(0, 400, 100);
    const model = new DocumentWindowModel([
      entry(0, 262, 180, {
        hasMermaid: true,
        html: '<pre class="mm-mermaid" data-mm-block-index="262" data-mm-block-kind="code" style="content-visibility:auto"><code data-mm-mermaid>graph TD</code></pre>',
        kind: "code",
        occupiedNonContentHeight: 20,
      }),
    ]);
    const mermaid = createMermaidPrepareHarness();
    const controller = makeController({
      measure: blocks => {
        const source = blocks[0];
        return source?.nextElementSibling?.classList.contains("mm-mermaid-svg")
          ? [{ blockIndex: 262, geometryOwner: "mermaid-proxy", measuredHeight: 200 }]
          : [];
      },
      model,
      prepare: mermaid.prepare,
      realization: { enabled: true },
      root,
    });

    controller.updateWindowForScroll();
    await mermaid.flush();
    const source = document.querySelector<HTMLElement>("[data-mm-block-index='262']")!;
    const staleProxy = setReadyMermaidLayout(source);
    staleProxy.remove();

    controller.updateWindowForScroll({ force: true });
    expect(mermaid.prepare).toHaveBeenCalledTimes(2);
    expect(source.classList.contains("is-rendered")).toBe(false);
    expect(source.style.containIntrinsicSize).toBe("auto 160px");
    await mermaid.flush();
    const replacementProxy = setReadyMermaidLayout(source);
    const adopted = controller.adoptRenderedHeights({ reanchor: false });

    expect(source.classList.contains("is-rendered")).toBe(true);
    expect(replacementProxy).not.toBe(staleProxy);
    expect(replacementProxy.classList.contains("mm-mermaid-svg")).toBe(true);
    expect(adopted.updatedCount).toBe(1);
  });

  it.each([
    {
      name: "zero-height proxy",
      mutate: (source: HTMLElement, proxy: HTMLElement) => {
        setElementLayout(proxy, { height: () => 0, top: () => 50 });
      },
    },
    {
      name: "display-none proxy",
      mutate: (source: HTMLElement, proxy: HTMLElement) => {
        proxy.style.display = "none";
      },
    },
    {
      name: "non-hidden positive source",
      mutate: (source: HTMLElement) => {
        setElementLayout(source, { height: () => 24, top: () => 0 });
      },
    },
    {
      name: "immediate duplicate proxy",
      mutate: (source: HTMLElement, proxy: HTMLElement) => {
        const duplicate = document.createElement("div");
        duplicate.className = "mm-mermaid-svg";
        proxy.after(duplicate);
      },
    },
    {
      name: "unmanaged current-shaped proxy",
      mutate: (source: HTMLElement, proxy: HTMLElement) => {
        proxy.remove();
        const unmanaged = document.createElement("div");
        unmanaged.className = "mm-mermaid-svg";
        source.after(unmanaged);
        setElementLayout(unmanaged, { height: () => 182, top: () => 50 });
      },
    },
  ])("repairs $name on zero-insert reuse before adoption", async ({ mutate, name }) => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root } = setScrollRoot(0, 400, 100);
    const model = new DocumentWindowModel([
      entry(0, 263, 180, {
        hasMermaid: true,
        html: '<pre class="mm-mermaid" data-mm-block-index="263" data-mm-block-kind="code" style="content-visibility:auto"><code data-mm-mermaid>graph TD</code></pre>',
        kind: "code",
        occupiedNonContentHeight: 20,
      }),
    ]);
    const mermaid = createMermaidPrepareHarness();
    const controller = makeController({
      measure: blocks => {
        const source = blocks[0];
        return source?.nextElementSibling?.classList.contains("mm-mermaid-svg")
          ? [{ blockIndex: 263, geometryOwner: "mermaid-proxy", measuredHeight: 200 }]
          : [];
      },
      model,
      prepare: mermaid.prepare,
      realization: { enabled: true },
      root,
    });

    controller.updateWindowForScroll();
    await mermaid.flush();
    const source = document.querySelector<HTMLElement>("[data-mm-block-index='263']")!;
    const originalProxy = setReadyMermaidLayout(source);
    mutate(source, originalProxy);
    const invalidProxy = source.nextElementSibling as HTMLElement;

    controller.updateWindowForScroll({ force: true });

    expect(mermaid.prepare, name).toHaveBeenCalledTimes(2);
    expect(source.classList.contains("is-rendered"), name).toBe(false);
    expect(source.style.containIntrinsicSize, name).toBe("auto 160px");
    expect(invalidProxy.isConnected, name).toBe(false);

    await mermaid.flush();
    const replacementProxy = setReadyMermaidLayout(source);
    const adopted = controller.adoptRenderedHeights({ reanchor: false });

    expect(replacementProxy).not.toBe(invalidProxy);
    expect(adopted.updatedCount).toBe(1);
  });

  it("keeps the Task 1 realization watch while a zero-box pair awaits rerender", async () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root } = setScrollRoot(0, 400, 180);
    const frames = installFrameQueue();
    const model = new DocumentWindowModel([
      entry(0, 264, 180, {
        hasMermaid: true,
        html: '<pre class="mm-mermaid" data-mm-block-index="264" data-mm-block-kind="code" style="content-visibility:auto"><code data-mm-mermaid>graph TD</code></pre>',
        kind: "code",
        occupiedNonContentHeight: 20,
      }),
    ]);
    let generation = 0;
    let resolveRepair: ((value: { svg: string }) => void) | undefined;
    let pendingRender = Promise.resolve();
    const prepare = vi.fn((rootNode: ParentNode) => {
      const source = rootNode.querySelector<HTMLElement>("pre.mm-mermaid");
      if (source === null) {
        return;
      }
      const currentGeneration = ++generation;
      const api: MermaidApiLike = currentGeneration === 1
        ? { render: async () => ({ svg: "<svg>initial</svg>" }) }
        : { render: () => new Promise(resolve => { resolveRepair = resolve; }) };
      pendingRender = renderMermaidNode(
        source,
        currentGeneration,
        () => generation,
        api,
        5000,
        { manageVirtualizedProxyLifecycle: true }
      );
    });
    const controller = makeController({
      model,
      prepare,
      realization: { enabled: true },
      root,
    });

    try {
      controller.updateWindowForScroll();
      await pendingRender;
      const source = document.querySelector<HTMLElement>("[data-mm-block-index='264']")!;
      const zeroProxy = setReadyMermaidLayout(source, { proxyHeight: 0 });

      controller.updateWindowForScroll({ force: true });

      expect(prepare).toHaveBeenCalledTimes(2);
      expect(zeroProxy.isConnected).toBe(false);
      expect(source.classList.contains("is-rendered")).toBe(false);
      const bottomSpacer = document.querySelector<HTMLElement>("[data-mm-virtual-spacer='bottom']")!;
      setElementLayout(source, { height: () => 122, top: () => 0 });
      setElementLayout(bottomSpacer, { top: () => 144 });

      dispatchContentVisibilityState(source, false);
      expect(frames.pending()).toBe(1);
      frames.flush();
      const adopted = controller.adoptRenderedHeights({ reanchor: false });

      expect(adopted.updatedCount).toBe(1);
      expect(model.getEntryByBlockIndex(264)?.measuredHeight).toBe(144);
    } finally {
      resolveRepair?.({ svg: "<svg>repaired</svg>" });
      await pendingRender;
      frames.restore();
    }
  });

  it("does no DOM work when scroll stays inside the current model window", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root, setScrollTop } = setScrollRoot(150, 450, 100);
    const model = new DocumentWindowModel([
      entry(0, 50, 100),
      entry(1, 51, 120),
      entry(2, 52, 80),
      entry(3, 53, 150),
    ]);
    const prepare = vi.fn();
    const controller = makeController({ model, prepare, root });

    expect(controller.updateWindowForScroll()).toBe(true);
    setScrollTop(175);
    expect(controller.updateWindowForScroll()).toBe(false);

    expect(prepare).toHaveBeenCalledTimes(1);
  });

  it("renders a target section without synthesizing a scroll event and preserves the current anchor", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { getScrollTop, root } = setScrollRoot(25, 400, 50);
    const model = new DocumentWindowModel([
      entry(0, 70, 100),
      entry(1, 71, 100),
      entry(2, 72, 100),
      entry(3, 73, 100),
    ]);
    const controller = makeController({ model, root });
    controller.updateWindowForScroll();

    expect(controller.isSectionRendered(3)).toBe(false);
    expect(controller.ensureSectionRendered(3, { preserveAnchor: true })).toBe(true);

    const main = document.querySelector<HTMLElement>("main.mm-document")!;
    expect(Array.from(main.querySelectorAll<HTMLElement>("[data-mm-block-index]")).map(node =>
      Number(node.dataset.mmBlockIndex))).toEqual([73]);
    expect(controller.getCurrentRange()).toEqual({ start: 3, end: 3 });
    expect(controller.isSectionRendered(3)).toBe(true);
    expect(getScrollTop()).toBe(25);
  });

  it("renders a requested section range for future multi-section targets", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root } = setScrollRoot(0, 400, 50);
    const model = new DocumentWindowModel([
      entry(0, 80, 100),
      entry(1, 81, 100),
      entry(2, 82, 100),
      entry(3, 83, 100),
    ]);
    const controller = makeController({ model, root });

    expect(controller.ensureSectionRangeRendered(1, 2)).toBe(true);
    expect(controller.getCurrentRange()).toEqual({ start: 1, end: 2 });
    expect(controller.ensureSectionRangeRendered(1, 2)).toBe(false);
  });

  it("captures the strict containing semantic block and clamps its intra offset", () => {
    const boundary = block(90, 0, 100);
    const containing = block(91, 0, 100);
    boundary.getBoundingClientRect = () => ({
      bottom: 0,
      height: 100,
      left: 0,
      right: 0,
      top: -100,
      width: 0,
      x: 0,
      y: -100,
      toJSON: () => ({}),
    } as DOMRect);
    containing.getBoundingClientRect = () => ({
      bottom: 10,
      height: 100,
      left: 0,
      right: 0,
      top: -120,
      width: 0,
      x: 0,
      y: -120,
      toJSON: () => ({}),
    } as DOMRect);

    expect(captureReadingAnchor([boundary, containing])).toEqual({
      blockIndex: 91,
      intraOffsetPx: 99.5,
    });

    const model = new DocumentWindowModel([entry(0, 91, 100)]);
    expect(model.scrollTopForAnchor({
      blockIndex: 91,
      intraOffset: 500,
      sectionIndex: 0,
    })).toBe(99.5);
  });

  it("rejects a mounted block that is wholly below the viewport as a reading anchor", () => {
    const belowViewport = block(90, 0, 100);
    belowViewport.getBoundingClientRect = () => ({
      bottom: 420,
      height: 100,
      left: 0,
      right: 0,
      top: 320,
      width: 0,
      x: 0,
      y: 320,
      toJSON: () => ({}),
    } as DOMRect);
    Object.defineProperty(document.documentElement, "clientHeight", {
      configurable: true,
      value: 200,
    });

    expect(captureReadingAnchor([belowViewport])).toBeNull();
  });

  it("requests one terminal re-anchor after measured adoption without moving the root directly", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { getScrollTop, root } = setScrollRoot(120, 400, 50);
    const model = new DocumentWindowModel([
      entry(0, 100, 100),
      entry(1, 101, 100),
      entry(2, 102, 100),
    ]);
    const controller = makeController({
      measure: () => [{ blockIndex: 100, measuredHeight: 160 }],
      model,
      root,
    });
    const requestScrollTop = vi.fn();
    const operation = { requestScrollTop };
    controller.updateWindowForScroll({ operation });
    requestScrollTop.mockClear();

    const result = controller.adoptRenderedHeights({
      operation,
      preserveSectionIndex: 1,
    });

    expect(result.updatedCount).toBe(1);
    expect(requestScrollTop).toHaveBeenCalledOnce();
    expect(requestScrollTop).toHaveBeenCalledWith(160, "measured-height-adoption");
    expect(getScrollTop()).toBe(120);
  });

  it("returns the navigation model-anchor shift without issuing a re-anchor write", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root } = setScrollRoot(120, 400, 50);
    const model = new DocumentWindowModel([
      entry(0, 120, 100),
      entry(1, 121, 100),
      entry(2, 122, 100),
    ]);
    const controller = makeController({
      measure: () => [
        { blockIndex: 120, measuredHeight: 160 },
        { blockIndex: 122, measuredHeight: 180 },
      ],
      model,
      root,
    });
    const requestScrollTop = vi.fn();
    controller.updateWindowForScroll({ operation: { requestScrollTop } });
    requestScrollTop.mockClear();

    const result = controller.adoptRenderedHeights({
      modelAnchor: { sectionIndex: 2, targetLocalOffset: 17 },
      operation: { requestScrollTop },
      reanchor: false,
    });

    expect(result.anchorShift).toBe(60);
    expect(requestScrollTop).not.toHaveBeenCalled();
  });

  it("recensuses realization watches before a geometry quiet candidate", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root } = setScrollRoot(0, 400, 50);
    const model = new DocumentWindowModel([
      {
        ...entry(0, 110, 100),
        html: '<p data-mm-block-index="110" style="content-visibility:auto">pending</p>',
      },
    ]);
    const controller = makeController({ model, realization: { enabled: true }, root });
    controller.updateWindowForScroll();
    const block = document.querySelector<HTMLElement>("[data-mm-block-index='110']")!;
    Object.defineProperty(block, "offsetHeight", {
      configurable: true,
      value: 80,
    });
    vi.spyOn(block, "getBoundingClientRect").mockReturnValue({
      bottom: 80,
      height: 80,
      left: 0,
      right: 400,
      top: 0,
      width: 400,
      x: 0,
      y: 0,
      toJSON: () => ({}),
    } as DOMRect);
    const recensus = (controller as unknown as {
      recensusRealizationWatches?: () => boolean;
    }).recensusRealizationWatches;

    expect(typeof recensus).toBe("function");
    expect(recensus?.()).toBe(false);
  });

  it("delayed image decode changes occupied delta by at most one pixel", () => {
    const fixture = JSON.parse(readFileSync("RendererWeb/vitest/fixtures/hostImageMarkup.json", "utf8")) as {
      flagOn: string;
      intrinsicHeight: number;
      intrinsicWidth: number;
    };
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { root } = setScrollRoot(0, 400, 50);
    const model = new DocumentWindowModel([
      {
        ...entry(0, 0, 100),
        html: fixture.flagOn,
      },
    ]);
    const controller = makeController({ model, root });
    controller.updateWindowForScroll();
    const figure = document.querySelector<HTMLElement>("figure")!;
    const image = document.querySelector<HTMLImageElement>("img")!;
    let decoded = false;
    const occupiedHeight = () => {
      if (decoded) {
        const renderedWidth = Number(image.getAttribute("width"));
        return renderedWidth / (fixture.intrinsicWidth / fixture.intrinsicHeight);
      }
      const reserved = image.getAttribute("height");
      return reserved === null ? Number.NaN : Number(reserved);
    };
    setElementLayout(figure, { height: occupiedHeight, top: () => 0 });
    const beforeDecode = figure.offsetHeight;
    decoded = true;
    image.dispatchEvent(new Event("load"));
    const afterDecode = figure.offsetHeight;

    expect(image.getAttribute("width")).toBe(String(fixture.intrinsicWidth));
    expect(image.getAttribute("height")).toBe(String(fixture.intrinsicHeight));
    expect(Math.abs(afterDecode - beforeDecode)).toBeLessThanOrEqual(1);

    image.removeAttribute("height");
    decoded = false;
    expect(Number.isFinite(occupiedHeight())).toBe(false);
  });
});
