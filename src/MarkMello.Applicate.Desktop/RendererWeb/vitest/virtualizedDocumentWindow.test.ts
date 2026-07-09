import { describe, expect, it, vi } from "vitest";
import {
  DocumentWindowModel,
  buildDocumentWindowModelsFromLiveBlocks,
  type MeasuredHeightUpdate,
  type SectionModelEntry,
} from "../src/documentWindow";
import {
  createFullDocumentFragmentFromWindowModel,
  createVirtualizedDocumentWindowController,
  type VirtualizedDocumentWindowController,
} from "../src/virtualizedDocumentWindow";
import type { IntrinsicSizeMetrics } from "../src/sectionIntrinsicSize";

const metrics: IntrinsicSizeMetrics = {
  charsPerLine: 40,
  fontSizePx: 18,
  lineHeightPx: 30,
};

function entry(sectionIndex: number, blockIndex: number, estimatedHeight: number): SectionModelEntry {
  return {
    blockIndex,
    cumulativeTop: 0,
    estimatedHeight,
    headingLevel: 0,
    html: `<section data-mm-block-index="${blockIndex}" data-mm-block-kind="paragraph">Block ${blockIndex}</section>`,
    kind: "paragraph",
    measuredHeight: undefined,
    sectionIndex,
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
  prepare?: (root: ParentNode) => void;
}): VirtualizedDocumentWindowController {
  const main = document.querySelector<HTMLElement>("main.mm-document")!;
  return createVirtualizedDocumentWindowController({
    main,
    model: input.model,
    ownerWindow: window,
    prepareInsertedContent: input.prepare,
    readMeasuredHeights: input.measure,
    renderAhead: {
      aboveViewports: 0,
      belowViewports: 0,
      minAbovePx: 0,
      minBelowPx: 0,
    },
    root: input.root,
  });
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

  it("stamps full model fragments with model effective heights before cache cloning", () => {
    const measured = entry(0, 10, 120);
    measured.measuredHeight = 210;
    const estimated = entry(1, 11, 140);
    const model = new DocumentWindowModel([measured, estimated]);

    const fragment = createFullDocumentFragmentFromWindowModel(document, model);
    const nodes = Array.from(fragment.children) as HTMLElement[];

    expect(nodes.map(node => node.style.containIntrinsicSize)).toEqual([
      "auto 210px",
      "auto 140px",
    ]);
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

  it("keeps the same offset anchor when a scroll changes the live window", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const { getScrollTop, root, setScrollTop } = setScrollRoot(150, 450, 100);
    const model = new DocumentWindowModel([
      entry(0, 30, 100),
      entry(1, 31, 120),
      entry(2, 32, 80),
      entry(3, 33, 150),
    ]);
    const controller = makeController({ model, root });

    controller.updateWindowForScroll();
    setScrollTop(320);
    controller.updateWindowForScroll();

    const main = document.querySelector<HTMLElement>("main.mm-document")!;
    expect(Array.from(main.querySelectorAll<HTMLElement>("[data-mm-block-index]")).map(node =>
      Number(node.dataset.mmBlockIndex))).toEqual([33]);
    expect(getScrollTop()).toBe(320);
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
    const adopted = controller.adoptRenderedHeights();

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
      const adopted = controller.adoptRenderedHeights();
      const anchorAfter = document.querySelector<HTMLElement>('[data-mm-block-index="81"]')!;

      expect(adopted.updatedCount).toBe(2);
      expect(anchorAfter.getBoundingClientRect().top).toBeCloseTo(anchorTopBefore);
      expect(getScrollTop()).toBe(140);
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
});
