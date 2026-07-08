import { afterEach, describe, expect, it } from "vitest";
import { DocumentWindowModel, type SectionModelEntry } from "../src/documentWindow";
import {
  createVirtualizationShadowValidator,
  readVirtualizationShadowFlag,
  validateVirtualizationShadowGeometry,
} from "../src/virtualizationShadow";

let localStorageValue: string | null = null;

Object.defineProperty(window, "localStorage", {
  configurable: true,
  value: {
    getItem: () => localStorageValue,
    setItem: (_key: string, value: string) => {
      localStorageValue = value;
    },
    clear: () => {
      localStorageValue = null;
    },
  },
});

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

function block(index: number, top: number, height: number): HTMLElement {
  const element = document.createElement("p");
  element.dataset.mmBlockIndex = String(index);
  element.dataset.mmBlockKind = "paragraph";
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

function blockWithKind(index: number, top: number, height: number, kind: string, text: string): HTMLElement {
  const element = block(index, top, height);
  element.dataset.mmBlockKind = kind;
  element.textContent = text;
  return element;
}

function nestedBlock(index: number, top: number, height: number): HTMLElement {
  const element = block(index, top, height);
  element.textContent = `nested ${index}`;
  return element;
}

function setDocumentScrollRoot(scrollTop: number, scrollHeight: number, clientHeight: number): HTMLElement {
  const root = document.documentElement;
  Object.defineProperty(document, "scrollingElement", {
    configurable: true,
    get: () => root,
  });
  Object.defineProperty(root, "scrollTop", {
    configurable: true,
    get: () => scrollTop,
  });
  Object.defineProperty(root, "scrollHeight", {
    configurable: true,
    get: () => scrollHeight,
  });
  Object.defineProperty(root, "clientHeight", {
    configurable: true,
    get: () => clientHeight,
  });
  return root;
}

describe("virtualization shadow validation", () => {
  afterEach(() => {
    window.localStorage.clear();
    document.documentElement.removeAttribute("data-markmello-virt-shadow");
    delete (window as Window & { MARKMELLO_VIRT_SHADOW?: unknown }).MARKMELLO_VIRT_SHADOW;
  });

  it("keeps MARKMELLO_VIRT_SHADOW default-off unless an explicit true value is present", () => {
    expect(readVirtualizationShadowFlag(window, document)).toBe(false);

    window.localStorage.setItem("MARKMELLO_VIRT_SHADOW", "1");
    expect(readVirtualizationShadowFlag(window, document)).toBe(true);

    window.localStorage.setItem("MARKMELLO_VIRT_SHADOW", "off");
    document.documentElement.dataset.markmelloVirtShadow = "true";
    expect(readVirtualizationShadowFlag(window, document)).toBe(true);

    document.documentElement.dataset.markmelloVirtShadow = "0";
    (window as Window & { MARKMELLO_VIRT_SHADOW?: unknown }).MARKMELLO_VIRT_SHADOW = true;
    expect(readVirtualizationShadowFlag(window, document)).toBe(true);
  });

  it("reports prediction deltas without mutating live geometry", () => {
    const model = new DocumentWindowModel([
      entry(0, 0, 100),
      entry(1, 1, 100),
      entry(2, 2, 100),
    ]);
    const blocks = [
      block(0, 0, 100),
      block(1, 150, 100),
      block(2, 250, 50),
    ];

    const result = validateVirtualizationShadowGeometry({
      blocks,
      config: {
        aboveViewports: 0,
        belowViewports: 0,
        minAbovePx: 0,
        minBelowPx: 0,
      },
      model,
      realScrollHeight: 320,
      realTopBlockIndex: 1,
      scrollTop: 175,
      viewportHeight: 50,
    });

    expect(result).toMatchObject({
      actualWindowEnd: 1,
      actualWindowStart: 1,
      predictedTopBlockIndex: 1,
      predictedTopSectionIndex: 1,
      predictedWindowEnd: 2,
      predictedWindowStart: 1,
      realTopBlockIndex: 1,
      realTopSectionIndex: 1,
      totalHeightDelta: -20,
      windowEndDelta: 1,
      windowStartDelta: 0,
    });
    expect(result.intraOffsetDelta).toBe(50);
    expect(result.maxAbsError).toBe(50);
    expect(blocks[0]!.style.cssText).toBe("");
  });

  it("compares prefix-sum top offsets instead of margin-collapsed intra offsets", () => {
    const model = new DocumentWindowModel([
      { ...entry(0, 10, 100), measuredHeight: 120 },
      { ...entry(1, 11, 100), measuredHeight: 100 },
    ]);
    const estimateModel = new DocumentWindowModel([
      entry(0, 10, 120),
      entry(1, 11, 100),
    ]);
    const blocks = [
      block(10, 0, 100),
      block(11, 120, 100),
    ];

    const result = validateVirtualizationShadowGeometry({
      blocks,
      config: {
        aboveViewports: 0,
        belowViewports: 0,
        minAbovePx: 20,
        minBelowPx: 20,
      },
      estimateModel,
      model,
      realScrollHeight: 220,
      realTopBlockIndex: 11,
      scrollTop: 110,
      viewportHeight: 1,
    });

    expect(result.predictedTopBlockIndex).toBe(10);
    expect(result.realTopBlockIndex).toBe(11);
    expect(result.realIntraOffset).toBe(0);
    expect(result.anchorBlockIndexMatches).toBe(true);
    expect(result.topOffsetDelta).toBe(0);
    expect(result.maxAbsPxError).toBe(0);
    expect(result.maxAbsIndexDelta).toBe(0);
  });

  it("logs nested-aware production anchors separately from the top-level model anchor", () => {
    const model = new DocumentWindowModel([
      { ...entry(0, 40, 100), measuredHeight: 120 },
      { ...entry(1, 41, 100), measuredHeight: 100 },
    ]);
    const topLevelBlocks = [
      block(40, 0, 100),
      block(41, 240, 100),
    ];
    const child = nestedBlock(400, 140, 80);
    topLevelBlocks[0]!.append(child);

    const result = validateVirtualizationShadowGeometry({
      blocks: topLevelBlocks,
      estimateModel: model,
      model,
      productionBlocks: [topLevelBlocks[0]!, child, topLevelBlocks[1]!],
      productionTopBlockIndex: 400,
      realScrollHeight: 340,
      realTopBlockIndex: 41,
      scrollTop: 150,
      viewportHeight: 40,
    });

    expect(result.productionTopBlockIndex).toBe(400);
    expect(result.realTopBlockIndex).toBe(41);
    expect(result.nestedTopVisibleAnchor).toBe(true);
    expect(result.productionTopSectionIndex).toBeNull();
  });

  it("runs the full validator cycle with timing, reuse, adoption, invalidation, and schedule coalescing", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const main = document.querySelector<HTMLElement>("main.mm-document")!;
    Object.defineProperty(main, "clientWidth", {
      configurable: true,
      get: () => 800,
    });
    main.append(
      blockWithKind(50, 0, 100, "heading", "Heading"),
      blockWithKind(51, 140, 160, "math", "x = y"),
      blockWithKind(52, 340, 80, "paragraph", "Body text"),
    );
    const root = setDocumentScrollRoot(150, 460, 120);
    const perfMarks: Array<{ name: string; detail?: Record<string, unknown> }> = [];
    const debugLogs: string[] = [];
    const idleCallbacks: Array<() => void> = [];
    const ownerWindow = {
      ...window,
      requestIdleCallback: (callback: () => void) => {
        idleCallbacks.push(callback);
        return idleCallbacks.length;
      },
    } as Window;

    const validator = createVirtualizationShadowValidator({
      isDocumentFinal: () => true,
      ownerDocument: document,
      ownerWindow,
      postDebugLog: text => debugLogs.push(text),
      postPerfMark: (name, detail) => perfMarks.push({ name, detail }),
    });

    validator.schedule();
    validator.schedule();
    expect(idleCallbacks).toHaveLength(1);
    idleCallbacks.shift()!();

    const firstValidation = perfMarks.find(mark => mark.name === "mm-virt-shadow-validation")!.detail!;
    expect(perfMarks.filter(mark => mark.name === "mm-virt-shadow-model-built")).toHaveLength(1);
    expect(firstValidation.elapsedMs).toEqual(expect.any(Number));
    expect(firstValidation.estimateHeightError).toMatchObject({
      byKind: {
        math: expect.objectContaining({ count: 1 }),
      },
    });
    expect(debugLogs.at(-1)).toContain("elapsedMs=");

    validator.validateNow();
    expect(perfMarks.filter(mark => mark.name === "mm-virt-shadow-model-built")).toHaveLength(1);
    Object.defineProperty(root, "scrollHeight", {
      configurable: true,
      get: () => 520,
    });
    validator.validateNow();
    const secondValidation = perfMarks.filter(mark => mark.name === "mm-virt-shadow-validation").at(-1)!.detail!;
    expect(secondValidation.scrollHeightGrowth).toBe(60);
    expect(debugLogs.at(-1)).toContain("scrollGrowth=60");

    validator.invalidate();
    validator.validateNow();
    expect(perfMarks.filter(mark => mark.name === "mm-virt-shadow-model-built")).toHaveLength(2);
  });

  it("skips validation while a progressive append is not final", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const main = document.querySelector<HTMLElement>("main.mm-document")!;
    main.append(blockWithKind(60, 0, 100, "paragraph", "pending"));
    setDocumentScrollRoot(0, 100, 50);
    const perfMarks: string[] = [];
    const validator = createVirtualizationShadowValidator({
      isDocumentFinal: () => false,
      ownerDocument: document,
      ownerWindow: window,
      postDebugLog: () => undefined,
      postPerfMark: name => perfMarks.push(name),
    });

    expect(validator.validateNow()).toBeNull();
    expect(perfMarks).toEqual(["mm-virt-shadow-validation-skipped"]);
  });

  it("feeds measured rendered block heights into the estimate-only calibration table", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const main = document.querySelector<HTMLElement>("main.mm-document")!;
    Object.defineProperty(main, "clientWidth", {
      configurable: true,
      get: () => 800,
    });
    main.append(
      blockWithKind(70, 0, 80, "paragraph", "short paragraph"),
      blockWithKind(71, 180, 80, "paragraph", "short paragraph"),
      blockWithKind(72, 360, 80, "paragraph", "short paragraph"),
    );
    setDocumentScrollRoot(0, 540, 120);
    const perfMarks: Array<{ name: string; detail?: Record<string, unknown> }> = [];
    const validator = createVirtualizationShadowValidator({
      isDocumentFinal: () => true,
      ownerDocument: document,
      ownerWindow: window,
      postDebugLog: () => undefined,
      postPerfMark: (name, detail) => perfMarks.push({ name, detail }),
    });

    const validation = validator.validateNow()!;

    expect(validation.estimatedTotalHeight).toBe(540);
    expect(validation.estimatedTotalHeightDelta).toBe(0);
    expect(validation.estimateHeightError.byKind.paragraph.meanAbsError).toBe(0);
    expect(validation.estimateCalibration.byKind.paragraph).toMatchObject({
      calibratedBucketCount: 1,
      sampleCount: 3,
    });
    expect(perfMarks.at(-1)?.detail?.estimateCalibration).toEqual(validation.estimateCalibration);
  });
});
