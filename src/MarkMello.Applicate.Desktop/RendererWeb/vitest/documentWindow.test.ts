import { describe, expect, it } from "vitest";
import {
  DEFAULT_RENDER_AHEAD,
  DocumentWindowModel,
  buildDocumentWindowModelFromLiveBlocks,
  buildDocumentWindowModelsFromLiveBlocks,
  collectLiveDocumentSectionElements,
  computeLiveBlockWindowRange,
  readLiveBlockMeasuredHeights,
  readLiveBlockOffsetMeasuredHeights,
  summarizeEstimateHeightErrors,
  type SectionModelEntry,
} from "../src/documentWindow";
import {
  createSectionIntrinsicCalibrator,
  type IntrinsicSizeMetrics,
} from "../src/sectionIntrinsicSize";
import { renderMermaidNode, type MermaidApiLike } from "../src/mermaidRender";

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
    kind: "paragraph",
    measuredHeight: undefined,
    sectionIndex,
  };
}

function block(index: number, top: number, height: number, kind = "paragraph", tagName = "p"): HTMLElement {
  const element = document.createElement(tagName);
  element.dataset.mmBlockIndex = String(index);
  element.dataset.mmBlockKind = kind;
  element.textContent = `block ${index}`;
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

function setDocumentScrollRoot(scrollTop: number, clientHeight: number): void {
  const root = document.documentElement;
  Object.defineProperty(document, "scrollingElement", {
    configurable: true,
    get: () => root,
  });
  Object.defineProperty(root, "scrollTop", {
    configurable: true,
    get: () => scrollTop,
  });
  Object.defineProperty(root, "clientHeight", {
    configurable: true,
    get: () => clientHeight,
  });
}

function setComputedBoxStyle(
  element: HTMLElement,
  styles: Partial<Record<"paddingTop" | "paddingBottom" | "borderTopWidth" | "borderBottomWidth", string>>
): void {
  const current = window.getComputedStyle;
  const style = {
    borderBottomWidth: "",
    borderTopWidth: "",
    paddingBottom: "",
    paddingTop: "",
    ...styles,
    getPropertyValue(propertyName: string): string {
      const camelName = propertyName.replace(/-([a-z])/g, (_match, letter: string) => letter.toUpperCase());
      return (this as Record<string, string>)[propertyName]
        ?? (this as Record<string, string>)[camelName]
        ?? "";
    },
  };
  window.getComputedStyle = ((target: Element) =>
    target === element ? style : current.call(window, target)) as typeof window.getComputedStyle;
}

function mermaidBlock(index: number, top: number, height: number): HTMLElement {
  const element = block(index, top, height, "code", "pre");
  element.className = "mm-mermaid";
  const code = document.createElement("code");
  code.dataset.mmMermaid = "";
  code.textContent = "flowchart LR\nA --> B";
  element.append(code);
  return element;
}

function mermaidProxy(top: number, height: number): HTMLElement {
  const element = document.createElement("div");
  element.className = "mm-mermaid-svg";
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

async function renderOwnedMermaidProxy(
  source: HTMLElement,
  top: number,
  height: number,
  generation = 1
): Promise<HTMLElement> {
  const api: MermaidApiLike = {
    render: async () => ({ svg: "<svg>owned</svg>" }),
  };
  await renderMermaidNode(
    source,
    generation,
    () => generation,
    api,
    1000,
    { manageVirtualizedProxyLifecycle: true }
  );
  const proxy = source.nextElementSibling as HTMLElement;
  Object.defineProperty(proxy, "offsetTop", {
    configurable: true,
    get: () => top,
  });
  Object.defineProperty(proxy, "offsetHeight", {
    configurable: true,
    get: () => height,
  });
  return proxy;
}

describe("document window model", () => {
  it("maintains measured-height prefix sums and total height", () => {
    const model = new DocumentWindowModel([
      entry(0, 10, 100),
      entry(1, 11, 120),
      entry(2, 12, 80),
    ]);

    expect(model.getTotalHeight()).toBe(300);
    expect(model.sectionTop(2)).toBe(220);

    const update = model.updateMeasuredHeightsByBlockIndex([
      { blockIndex: 11, measuredHeight: 150 },
    ]);

    expect(update.updatedCount).toBe(1);
    expect(update.maxAbsDelta).toBe(30);
    expect(model.getTotalHeight()).toBe(330);
    expect(model.sectionTop(2)).toBe(250);
  });

  it("computes windows, anchors, and spacer math from the same height model", () => {
    const model = new DocumentWindowModel([
      entry(0, 20, 100),
      entry(1, 21, 150),
      entry(2, 22, 200),
      entry(3, 23, 120),
    ]);

    const range = model.computeWindowRange(260, 100, {
      aboveViewports: 0,
      belowViewports: 0,
      minAbovePx: 0,
      minBelowPx: 0,
    });
    expect(range).toEqual({ start: 2, end: 2 });

    const anchor = model.captureAnchor(275);
    expect(anchor).toEqual({ blockIndex: 22, intraOffset: 25, sectionIndex: 2 });

    model.updateMeasuredHeightsByBlockIndex([
      { blockIndex: 21, measuredHeight: 180 },
    ]);
    expect(model.scrollTopForAnchor(anchor)).toBe(305);

    expect(model.computeSpacerHeights({ start: 1, end: 2 })).toEqual({
      bottomSpacer: 120,
      topSpacer: 100,
      totalHeight: 600,
      windowHeight: 380,
    });
  });

  it("builds a shadow model from live block geometry including margin gaps", () => {
    const blocks = [
      block(30, 24, 40, "heading"),
      block(31, 84, 50),
      block(32, 164, 60),
    ];

    const model = buildDocumentWindowModelFromLiveBlocks(blocks, metrics, 260);

    expect(model.getSectionCount()).toBe(3);
    expect(model.sectionTop(0)).toBe(24);
    expect(model.sectionTop(1)).toBe(84);
    expect(model.sectionTop(2)).toBe(164);
    expect(model.sectionEffectiveHeight(0)).toBe(60);
    expect(model.sectionEffectiveHeight(1)).toBe(80);
    expect(model.sectionEffectiveHeight(2)).toBe(96);
    expect(model.getTotalHeight()).toBe(260);
    expect(model.captureAnchor(100)).toEqual({ blockIndex: 31, intraOffset: 16, sectionIndex: 1 });
  });

  it("adopts live rendered heights with the same gap-inclusive footprint convention", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const main = document.querySelector<HTMLElement>("main.mm-document")!;
    const first = block(33, 120, 40, "heading");
    const second = block(34, 180, 50);
    const third = block(35, 260, 60);
    const bottomSpacer = document.createElement("div");
    bottomSpacer.dataset.mmVirtualSpacer = "bottom";
    Object.defineProperty(bottomSpacer, "offsetTop", {
      configurable: true,
      get: () => 350,
    });
    main.append(first, second, third, bottomSpacer);

    expect(readLiveBlockOffsetMeasuredHeights([first, second, third])).toEqual([
      { blockIndex: 33, measuredHeight: 60, occupiedNonContentHeight: 20 },
      { blockIndex: 34, measuredHeight: 80, occupiedNonContentHeight: 30 },
      { blockIndex: 35, measuredHeight: 90, occupiedNonContentHeight: 30 },
    ]);
  });

  it("rendered mermaid uses adjacent svg proxy without duplicating block index", async () => {
    const main = document.createElement("main");
    const source = mermaidBlock(90, 0, 0);
    const bottomSpacer = block(-1, 250, 0);
    bottomSpacer.removeAttribute("data-mm-block-index");
    main.append(source, bottomSpacer);
    document.body.append(main);
    const proxy = await renderOwnedMermaidProxy(source, 50, 182);

    expect(collectLiveDocumentSectionElements(main)).toEqual([source]);
    expect(proxy.hasAttribute("data-mm-block-index")).toBe(false);
    expect(readLiveBlockOffsetMeasuredHeights([source])).toEqual([
      {
        blockIndex: 90,
        geometryOwner: "mermaid-proxy",
        measuredHeight: 200,
      },
    ]);
  });

  it("rendered mermaid height is not absorbed by previous section", async () => {
    const main = document.createElement("main");
    const previous = block(91, 0, 40);
    const source = mermaidBlock(92, 0, 0);
    const next = block(93, 279, 56);
    main.append(previous, source, next);
    document.body.append(main);
    await renderOwnedMermaidProxy(source, 79, 182);

    const updates = readLiveBlockOffsetMeasuredHeights([previous, source, next]);

    expect(updates[0]).toMatchObject({ blockIndex: 91, measuredHeight: 79 });
    expect(updates[1]).toEqual({
      blockIndex: 92,
      geometryOwner: "mermaid-proxy",
      measuredHeight: 200,
    });
  });

  it("mermaid proxy never enters generic intrinsic calibration", async () => {
    const main = document.createElement("main");
    const source = mermaidBlock(94, 0, 0);
    const bottomSpacer = block(-1, 240, 0);
    bottomSpacer.removeAttribute("data-mm-block-index");
    main.append(source, bottomSpacer);
    document.body.append(main);
    await renderOwnedMermaidProxy(source, 40, 160);

    const model = buildDocumentWindowModelFromLiveBlocks([source], metrics, 240);
    const calibrator = createSectionIntrinsicCalibrator({ minSamplesPerBucket: 1 });

    expect(model.sections).toHaveLength(1);
    expect(model.sections[0]).toMatchObject({
      blockIndex: 94,
      geometryOwner: "mermaid-proxy",
      measuredHeight: 200,
    });
    expect(model.recordIntrinsicSizeCalibrationSamples(calibrator)).toBe(0);
    expect(calibrator.getSummary().sampleCount).toBe(0);
  });

  it("rejects an unmanaged adjacent Mermaid proxy without predecessor absorption", () => {
    const main = document.createElement("main");
    const previous = block(95, 0, 40);
    const source = mermaidBlock(96, 0, 0);
    source.classList.add("is-rendered");
    const unmanagedProxy = mermaidProxy(79, 182);
    const next = block(97, 279, 56);
    main.append(previous, source, unmanagedProxy, next);
    document.body.append(main);

    const updates = readLiveBlockOffsetMeasuredHeights([previous, source, next]);

    expect(updates[0]).toMatchObject({ blockIndex: 95, measuredHeight: 40 });
    expect(updates.some(update => update.blockIndex === 96)).toBe(false);
  });

  it("rejects a current pending Mermaid claim until its exact proxy is terminal ready", async () => {
    const main = document.createElement("main");
    const source = mermaidBlock(98, 0, 0);
    const bottomSpacer = block(-1, 250, 0);
    bottomSpacer.removeAttribute("data-mm-block-index");
    main.append(source, bottomSpacer);
    document.body.append(main);
    const proxy = await renderOwnedMermaidProxy(source, 50, 182);
    let resolveRender!: (value: { svg: string }) => void;
    const pendingApi: MermaidApiLike = {
      render: () => new Promise(resolve => { resolveRender = resolve; }),
    };
    const pending = renderMermaidNode(
      source,
      2,
      () => 2,
      pendingApi,
      5000,
      { manageVirtualizedProxyLifecycle: true }
    );

    expect(readLiveBlockOffsetMeasuredHeights([source])).toEqual([]);

    resolveRender({ svg: "<svg>ready</svg>" });
    await pending;

    expect(source.nextElementSibling).toBe(proxy);
    expect(readLiveBlockOffsetMeasuredHeights([source])).toEqual([
      { blockIndex: 98, geometryOwner: "mermaid-proxy", measuredHeight: 200 },
    ]);
  });

  it.each([
    {
      name: "zero-height proxy",
      mutate: (source: HTMLElement, proxy: HTMLElement) => {
        Object.defineProperty(proxy, "offsetHeight", { configurable: true, get: () => 0 });
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
        Object.defineProperty(source, "offsetHeight", { configurable: true, get: () => 24 });
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
  ])("rejects a terminal claim with $name", async ({ mutate }) => {
    const main = document.createElement("main");
    const source = mermaidBlock(99, 0, 0);
    const bottomSpacer = block(-1, 250, 0);
    bottomSpacer.removeAttribute("data-mm-block-index");
    main.append(source, bottomSpacer);
    document.body.append(main);
    const proxy = await renderOwnedMermaidProxy(source, 50, 182);

    mutate(source, proxy);

    expect(readLiveBlockOffsetMeasuredHeights([source])).toEqual([]);
  });

  it("derives occupied non-content metadata as occupied minus content box", () => {
    const padded = block(36, 20, 90, "code");
    const next = block(37, 155, 40);
    setComputedBoxStyle(padded, {
      borderBottomWidth: "2px",
      borderTopWidth: "3px",
      paddingBottom: "12px",
      paddingTop: "18px",
    });

    const models = buildDocumentWindowModelsFromLiveBlocks([padded, next], metrics, 220);

    expect(models.measuredModel.sections[0]).toMatchObject({
      blockIndex: 36,
      measuredHeight: 135,
      occupiedNonContentHeight: 80,
    });
    expect(readLiveBlockOffsetMeasuredHeights([padded, next])[0]).toMatchObject({
      blockIndex: 36,
      measuredHeight: 135,
      occupiedNonContentHeight: 80,
    });
  });

  it("placeholder-flagged height contributes to layout but not calibration", () => {
    setDocumentScrollRoot(0, 240);
    const placeholder = block(78, 1000, 126, "code", "pre");
    placeholder.style.setProperty("content-visibility", "auto");
    placeholder.style.setProperty("contain-intrinsic-size", "auto 80px");
    setComputedBoxStyle(placeholder, {
      borderBottomWidth: "4px",
      borderTopWidth: "2px",
      paddingBottom: "18px",
      paddingTop: "22px",
    });
    const next = block(79, 1165, 90, "paragraph");

    const models = buildDocumentWindowModelsFromLiveBlocks([placeholder, next], metrics, 1300);
    const placeholderEntry = models.measuredModel.sections[0]!;

    expect(placeholderEntry).toMatchObject({
      blockIndex: 78,
      measuredHeight: undefined,
      measuredHeightPlaceholder: true,
      occupiedNonContentHeight: 85,
    });
    expect(models.measuredModel.computeSpacerHeights({ start: 0, end: 0 }).windowHeight)
      .toBe(placeholderEntry.estimatedHeight);

    const calibrator = createSectionIntrinsicCalibrator({ minSamplesPerBucket: 1 });
    expect(models.measuredModel.recordIntrinsicSizeCalibrationSamples(calibrator)).toBe(1);
    expect(models.measuredModel.getEntryByBlockIndex(78)?.measuredHeight).toBeUndefined();
    expect(calibrator.getSummary().sampleCount).toBe(1);
  });

  it("fails closed for non-px box terms when deriving occupied non-content metadata", () => {
    const nonPx = block(92, 10, 80, "paragraph");
    const next = block(93, 130, 60, "paragraph");
    setComputedBoxStyle(nonPx, { paddingTop: "1.5em" });

    expect(readLiveBlockOffsetMeasuredHeights([nonPx, next])[0]).toEqual({
      blockIndex: 92,
      measuredHeight: 120,
      measuredHeightPlaceholder: true,
    });
    expect(buildDocumentWindowModelFromLiveBlocks([nonPx, next], metrics, 210)
      .getEntryByBlockIndex(92)).toMatchObject({
        measuredHeight: undefined,
        measuredHeightPlaceholder: true,
      });
  });

  it("image and rule kinds retain explicit stamp and realization classification", () => {
    const image = block(94, 0, 80, "image", "figure");
    const rule = block(95, 80, 2, "rule", "hr");
    const next = block(96, 82, 40, "paragraph");

    const model = buildDocumentWindowModelFromLiveBlocks([image, rule, next], metrics, 122);

    expect(model.getEntryByBlockIndex(94)).toMatchObject({ kind: "image", measuredHeight: 80 });
    expect(model.getEntryByBlockIndex(95)).toMatchObject({ kind: "rule", measuredHeight: 2 });
  });

  it("keeps the document leading offset out of virtual DOM spacer heights", () => {
    const model = new DocumentWindowModel([
      entry(0, 36, 50),
      entry(1, 37, 60),
      entry(2, 38, 70),
    ], { leadingOffset: 120 });

    expect(model.sectionTop(1)).toBe(170);
    expect(model.computeSpacerHeights({ start: 1, end: 1 })).toEqual({
      bottomSpacer: 70,
      topSpacer: 50,
      totalHeight: 300,
      windowHeight: 60,
    });
  });

  it("keeps a fresh top anchor at the document top before the leading offset", () => {
    const model = new DocumentWindowModel([
      entry(0, 39, 50),
      entry(1, 40, 60),
    ], { leadingOffset: 120 });

    const topAnchor = model.captureAnchor(0);

    expect(topAnchor).toEqual({ blockIndex: -1, intraOffset: 0, sectionIndex: -1 });
    expect(model.scrollTopForAnchor(topAnchor)).toBe(0);
    expect(model.scrollTopForAnchor(model.captureAnchor(120))).toBe(120);
  });

  it("builds estimate-only and measured twin models with per-kind estimate error stats", () => {
    const blocks = [
      block(70, 0, 40, "heading"),
      block(71, 64, 110, "math"),
      mermaidBlock(72, 200, 140),
      block(73, 360, 40, "paragraph"),
    ];

    const models = buildDocumentWindowModelsFromLiveBlocks(blocks, metrics, 440);

    expect(models.measuredModel.getTotalHeight()).toBe(440);
    expect(models.estimateOnlyModel.getTotalHeight()).not.toBe(440);
    expect(models.estimateOnlyModel.sections.every(section => section.measuredHeight === undefined)).toBe(true);
    expect(models.measuredModel.sections.every(section => section.measuredHeight !== undefined)).toBe(true);
    expect(models.estimateHeightError.byKind.math).toMatchObject({
      count: 1,
      maxAbsError: 28,
      meanAbsError: 28,
    });
    expect(models.estimateHeightError.byKind.mermaid.count).toBe(1);
    expect(models.estimateHeightError.byKind.mermaid.worstOffenders[0]).toMatchObject({
      blockIndex: 72,
      measuredHeight: 160,
    });
  });

  it("flags content-visibility placeholder measurements and excludes them from estimate-error stats", () => {
    setDocumentScrollRoot(0, 240);
    const placeholder = block(74, 1000, 120, "paragraph");
    placeholder.style.setProperty("content-visibility", "auto");
    placeholder.style.setProperty("contain-intrinsic-size", "auto 120px");
    const rememberedReal = block(75, 1164, 110, "paragraph");

    const models = buildDocumentWindowModelsFromLiveBlocks([
      placeholder,
      rememberedReal,
    ], metrics, 1300);

    expect(models.measuredModel.sections[0]).toMatchObject({
      blockIndex: 74,
      measuredHeight: undefined,
      measuredHeightPlaceholder: true,
    });
    expect(models.estimateHeightError.placeholderCount).toBe(1);
    expect(models.estimateHeightError.byKind.paragraph.placeholderCount).toBe(1);
    expect(models.estimateHeightError.byKind.paragraph.count).toBe(1);
    expect(models.estimateHeightError.worstOffenders.every(offender => offender.blockIndex !== 74)).toBe(true);
  });

  it("summarizes remembered-real measurements while skipping placeholder entries", () => {
    const estimateModel = new DocumentWindowModel([
      { ...entry(0, 76, 600), kind: "paragraph" },
      { ...entry(1, 77, 150), kind: "paragraph" },
    ]);
    const measuredModel = new DocumentWindowModel([
      { ...entry(0, 76, 600), kind: "paragraph", measuredHeight: 120, measuredHeightPlaceholder: true },
      { ...entry(1, 77, 150), kind: "paragraph", measuredHeight: 164 },
    ]);

    const summary = summarizeEstimateHeightErrors(estimateModel, measuredModel);

    expect(summary.placeholderCount).toBe(1);
    expect(summary.count).toBe(1);
    expect(summary.byKind.paragraph).toMatchObject({
      count: 1,
      maxAbsError: 14,
      meanAbsError: 14,
      placeholderCount: 1,
    });
    expect(summary.worstOffenders).toEqual([
      expect.objectContaining({ blockIndex: 77, measuredHeight: 164 }),
    ]);
  });

  it("skips zero-box live blocks before deriving measurements and windows", () => {
    const blocks = [
      block(80, 0, 100),
      block(81, 0, 0, "code"),
      block(82, 260, 80),
    ];

    const models = buildDocumentWindowModelsFromLiveBlocks(blocks, metrics, 360);
    const updates = readLiveBlockMeasuredHeights(blocks, 360);

    expect(models.measuredModel.sections.map(section => section.blockIndex)).toEqual([80, 82]);
    expect(models.measuredModel.sectionEffectiveHeight(0)).toBe(260);
    expect(models.measuredModel.getTotalHeight()).toBe(360);
    expect(updates).toEqual([
      { blockIndex: 80, measuredHeight: 260, occupiedNonContentHeight: 160 },
      { blockIndex: 82, measuredHeight: 100, occupiedNonContentHeight: 20 },
    ]);
    expect(computeLiveBlockWindowRange(blocks, 280, 40, {
      ...DEFAULT_RENDER_AHEAD,
      aboveViewports: 0,
      belowViewports: 0,
      minAbovePx: 0,
      minBelowPx: 0,
    })).toEqual({ start: 1, end: 1 });
  });

  it("computes the live block window range with the same render-ahead contract", () => {
    const blocks = [
      block(40, 0, 50),
      block(41, 100, 50),
      block(42, 200, 50),
      block(43, 300, 50),
      block(44, 400, 50),
    ];

    expect(computeLiveBlockWindowRange(blocks, 225, 50, {
      ...DEFAULT_RENDER_AHEAD,
      aboveViewports: 0,
      belowViewports: 0,
      minAbovePx: 75,
      minBelowPx: 75,
    })).toEqual({ start: 2, end: 3 });
  });

  it("collects only top-level live document sections for the shadow model", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const main = document.querySelector<HTMLElement>("main.mm-document")!;
    const topLevel = block(50, 0, 100);
    const nested = block(999, 0, 25);
    topLevel.append(nested);
    const unindexed = document.createElement("div");
    main.append(topLevel, unindexed);

    expect(collectLiveDocumentSectionElements(main)).toEqual([topLevel]);
  });

  it("resolves nested block indexes to the containing top-level section", () => {
    const owner = block(110, 0, 120, "quote");
    const nested = block(111, 0, 40, "paragraph");
    owner.append(nested);
    const model = buildDocumentWindowModelFromLiveBlocks([owner], metrics, 120);

    expect(model.getEntryContainingBlockIndex(111)).toMatchObject({
      blockIndex: 110,
      sectionIndex: 0,
    });
  });

  it("resolves heading anchors and source lines from the full section HTML", () => {
    const first = block(120, 0, 100, "heading");
    first.innerHTML = '<h2 id="alpha-heading">Alpha</h2>';
    const second = block(121, 100, 140, "quote");
    second.innerHTML = '<blockquote data-mm-block-index="122" data-mm-source-line="24" data-mm-source-end-line="27">Nested</blockquote>';
    const model = buildDocumentWindowModelFromLiveBlocks([first, second], metrics, 240);

    expect(model.getEntryByHeadingAnchor("alpha-heading")).toMatchObject({
      blockIndex: 120,
      sectionIndex: 0,
    });
    expect(model.getEntryBySourceLine(26)).toMatchObject({
      blockIndex: 121,
      sectionIndex: 1,
    });
    expect(model.getSourceLineAnchors()).toEqual([
      expect.objectContaining({
        endLine: 27,
        sectionIndex: 1,
        sourceLine: 24,
        top: 100,
      }),
    ]);
  });

  it("projects every model section to minimap block geometry without reading live DOM", () => {
    const model = new DocumentWindowModel([
      { ...entry(0, 130, 100), headingLevel: 2, kind: "heading" },
      { ...entry(1, 131, 150), kind: "quote" },
    ]);

    expect(model.getMinimapBlockProjection()).toEqual([
      { headingLevel: 2, height: 100, kind: "heading", top: 0 },
      { headingLevel: 0, height: 150, kind: "quote", top: 100 },
    ]);
  });

  it("ignores non-finite, negative, and unknown measured-height updates", () => {
    const model = new DocumentWindowModel([
      entry(0, 90, 100),
      entry(1, 91, 120),
    ]);

    const update = model.updateMeasuredHeightsByBlockIndex([
      { blockIndex: 999, measuredHeight: 300 },
      { blockIndex: 90, measuredHeight: Number.NaN },
      { blockIndex: 91, measuredHeight: -1 },
    ]);

    expect(update).toEqual({ maxAbsDelta: 0, totalDelta: 0, updatedCount: 0 });
    expect(model.getTotalHeight()).toBe(220);
  });
});
