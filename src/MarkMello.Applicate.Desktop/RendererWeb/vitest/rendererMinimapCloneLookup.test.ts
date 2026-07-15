import { afterEach, describe, expect, it } from "vitest";
import {
  __testCloneBlockAtCloneYForTesting,
  __testSetMinimapCloneBlockElementsForTesting,
} from "../src/renderer";
import * as rendererModule from "../src/renderer";

type CloneHit = { block: HTMLElement; mode: "gap" | "frac" | "tail"; value: number } | null;

type LayoutReadCounts = {
  offsetTop: number;
  offsetHeight: number;
};

type CloneFixture = {
  clone: HTMLElement;
  blocks: HTMLElement[];
  counts: LayoutReadCounts;
  resetCounts: () => void;
};

type MutableGeometry = {
  top: number;
  height: number;
};

type RendererTestModule = typeof rendererModule & {
  __testDocScrollTopForCloneYForTesting?: (root: Element, y: number) => number | null;
  __testInvalidateMinimapCloneGeometryForTesting?: () => void;
};

const rendererForTesting = rendererModule as RendererTestModule;

afterEach(() => {
  document.body.replaceChildren();
});

describe("minimap clone-space lookup", () => {
  it("matches the reference linear scan across boundaries, gaps, hidden twins, zero height, and tail", () => {
    const emptyClone = document.createElement("main");
    __testSetMinimapCloneBlockElementsForTesting(emptyClone, []);
    expect(__testCloneBlockAtCloneYForTesting(emptyClone, 0)).toBeNull();

    const fixture = createCloneFixture();
    __testSetMinimapCloneBlockElementsForTesting(fixture.clone, fixture.blocks);

    const yPositions = [
      0,
      10,
      20,
      30,
      40,
      49,
      50,
      60,
      80,
      100,
      120,
      150,
      155,
      160,
      175,
    ];

    for (const y of yPositions) {
      const expected = referenceCloneBlockAtCloneY(fixture.blocks, fixture.clone, y);
      const actual = __testCloneBlockAtCloneYForTesting(fixture.clone, y);

      expectCloneHit(actual, expected, `y=${y}`);
    }
  });

  it("matches the original full-descendant oracle while searching only top-level clone blocks", () => {
    const clone = document.createElement("main");
    clone.className = "mm-document";
    document.body.append(clone);
    const counts = { offsetTop: 0, offsetHeight: 0 };

    const topLevelList = cloneBlock(10, 20, 80, clone, counts);
    const nestedItem = cloneBlock(11, 35, 10, topLevelList, counts);
    const topLevelParagraph = cloneBlock(12, 140, 20, clone, counts);

    topLevelList.append(nestedItem);
    clone.append(topLevelList, topLevelParagraph);
    const fullDescendantOrder = [topLevelList, nestedItem, topLevelParagraph];

    __testSetMinimapCloneBlockElementsForTesting(clone, fullDescendantOrder);

    for (const y of [5, 40, 50, 70, 120, 145, 180]) {
      const expected = referenceCloneBlockAtCloneY(fullDescendantOrder, clone, y);
      const actual = __testCloneBlockAtCloneYForTesting(clone, y);

      expectCloneHit(actual, expected, `nested y=${y}`);
    }
  });

  it("reuses cached clone geometry for unchanged indexed elements and inline width", () => {
    const fixture = createCloneFixture();
    fixture.clone.style.width = "120px";
    __testSetMinimapCloneBlockElementsForTesting(fixture.clone, fixture.blocks);

    expect(__testCloneBlockAtCloneYForTesting(fixture.clone, 100)?.mode).toBe("frac");
    expect(fixture.counts.offsetTop).toBeGreaterThan(0);
    expect(fixture.counts.offsetHeight).toBeGreaterThan(0);

    fixture.resetCounts();
    for (const y of [0, 20, 49, 80, 120, 175]) {
      expect(__testCloneBlockAtCloneYForTesting(fixture.clone, y)).not.toBeNull();
    }

    expect(fixture.counts.offsetTop).toBe(0);
    expect(fixture.counts.offsetHeight).toBe(0);

    fixture.clone.style.width = "121px";
    expect(__testCloneBlockAtCloneYForTesting(fixture.clone, 100)?.mode).toBe("frac");
    expect(fixture.counts.offsetTop).toBeGreaterThan(0);
    expect(fixture.counts.offsetHeight).toBeGreaterThan(0);
  });

  it("rebuilds same-key clone geometry after the production generation invalidation seam advances", () => {
    const clone = document.createElement("main");
    clone.className = "mm-document";
    clone.style.width = "120px";
    document.body.append(clone);
    const counts = { offsetTop: 0, offsetHeight: 0 };
    const firstGeometry: MutableGeometry = { top: 0, height: 40 };
    const secondGeometry: MutableGeometry = { top: 60, height: 40 };
    const first = mutableCloneBlock(20, firstGeometry, clone, counts);
    const second = mutableCloneBlock(21, secondGeometry, clone, counts);
    const blocks = [first, second];
    clone.append(...blocks);

    __testSetMinimapCloneBlockElementsForTesting(clone, blocks);
    expectCloneHit(
      __testCloneBlockAtCloneYForTesting(clone, 65),
      { block: second, mode: "frac", value: 0.125 },
      "initial geometry",
    );

    firstGeometry.top = 100;
    secondGeometry.top = 160;
    counts.offsetTop = 0;
    counts.offsetHeight = 0;

    expect(typeof rendererForTesting.__testInvalidateMinimapCloneGeometryForTesting).toBe("function");
    rendererForTesting.__testInvalidateMinimapCloneGeometryForTesting!();

    expectCloneHit(
      __testCloneBlockAtCloneYForTesting(clone, 65),
      { block: first, mode: "gap", value: -35 },
      "same key after generation invalidation",
    );
    expect(counts.offsetTop).toBeGreaterThan(0);
    expect(counts.offsetHeight).toBeGreaterThan(0);
  });

  it("uses the original live document selector when the block-index map misses rendered Mermaid", () => {
    const main = document.createElement("main");
    main.className = "mm-document";
    document.body.append(main);
    const renderedMermaid = document.createElement("pre");
    renderedMermaid.className = "mm-mermaid is-rendered";
    renderedMermaid.dataset.mmBlockIndex = "30";
    renderedMermaid.getBoundingClientRect = () => new DOMRect(0, 0, 800, 0);
    main.append(renderedMermaid);

    const clone = document.createElement("main");
    clone.className = "mm-document";
    const counts = { offsetTop: 0, offsetHeight: 0 };
    const cloneBlock = cloneBlockForDocumentIndex(30, 10, 60, clone, counts);
    clone.append(cloneBlock);
    __testSetMinimapCloneBlockElementsForTesting(clone, [cloneBlock]);

    const lookup = rendererForTesting.__testDocScrollTopForCloneYForTesting;
    expect(typeof lookup).toBe("function");
    expect(lookup!(scrollRoot(50, 1000, 200), 40)).toBe(50);
  });

  it("returns null before inverse contribution arithmetic when the live block height is not positive", () => {
    const main = document.createElement("main");
    main.className = "mm-document";
    document.body.append(main);
    const liveBlock = document.createElement("p");
    liveBlock.dataset.mmBlockIndex = "40";
    liveBlock.getBoundingClientRect = () => new DOMRect(0, 25, 800, 0);
    main.append(liveBlock);

    const clone = document.createElement("main");
    clone.className = "mm-document";
    const counts = { offsetTop: 0, offsetHeight: 0 };
    const cloneBlock = cloneBlockForDocumentIndex(40, 10, 60, clone, counts);
    clone.append(cloneBlock);
    __testSetMinimapCloneBlockElementsForTesting(clone, [cloneBlock]);

    const lookup = rendererForTesting.__testDocScrollTopForCloneYForTesting;
    expect(typeof lookup).toBe("function");
    expect(lookup!(scrollRoot(50, 1000, 200), 40)).toBeNull();
  });
});

function createCloneFixture(): CloneFixture {
  const clone = document.createElement("main");
  clone.className = "mm-minimap-content";
  document.body.append(clone);

  const counts = { offsetTop: 0, offsetHeight: 0 };
  const resetCounts = () => {
    counts.offsetTop = 0;
    counts.offsetHeight = 0;
  };

  const blocks = [
    cloneBlock(0, 10, 20, clone, counts),
    cloneBlock(1, 50, 0, clone, counts),
    cloneBlock(2, 55, 9, null, counts, true),
    cloneBlock(3, 80, 40, clone, counts),
    cloneBlock(4, 150, 10, clone, counts),
  ];

  clone.append(...blocks);
  const unindexedPoison = document.createElement("p");
  unindexedPoison.dataset.mmBlockIndex = "999";
  Object.defineProperty(unindexedPoison, "offsetTop", {
    configurable: true,
    get: () => {
      throw new Error("clone querySelectorAll scanned an unindexed block");
    },
  });
  clone.append(unindexedPoison);
  resetCounts();

  return { clone, blocks, counts, resetCounts };
}

function cloneBlock(
  index: number,
  top: number,
  height: number,
  offsetParent: HTMLElement | null,
  counts: LayoutReadCounts,
  hidden = false,
): HTMLElement {
  const element = document.createElement(hidden ? "pre" : "p");
  element.dataset.mmBlockIndex = String(index);
  if (hidden) {
    element.className = "mm-mermaid is-rendered";
    element.style.display = "none";
  }

  Object.defineProperty(element, "offsetTop", {
    configurable: true,
    get: () => {
      counts.offsetTop++;
      return top;
    },
  });
  Object.defineProperty(element, "offsetHeight", {
    configurable: true,
    get: () => {
      counts.offsetHeight++;
      return height;
    },
  });
  Object.defineProperty(element, "offsetParent", {
    configurable: true,
    get: () => offsetParent,
  });

  return element;
}

function mutableCloneBlock(
  index: number,
  geometry: MutableGeometry,
  offsetParent: HTMLElement | null,
  counts: LayoutReadCounts,
): HTMLElement {
  const element = document.createElement("p");
  element.dataset.mmBlockIndex = String(index);
  Object.defineProperty(element, "offsetTop", {
    configurable: true,
    get: () => {
      counts.offsetTop++;
      return geometry.top;
    },
  });
  Object.defineProperty(element, "offsetHeight", {
    configurable: true,
    get: () => {
      counts.offsetHeight++;
      return geometry.height;
    },
  });
  Object.defineProperty(element, "offsetParent", {
    configurable: true,
    get: () => offsetParent,
  });
  return element;
}

function cloneBlockForDocumentIndex(
  index: number,
  top: number,
  height: number,
  offsetParent: HTMLElement | null,
  counts: LayoutReadCounts,
): HTMLElement {
  return cloneBlock(index, top, height, offsetParent, counts);
}

function referenceCloneBlockAtCloneY(
  blocks: readonly HTMLElement[],
  clone: HTMLElement,
  y: number,
): CloneHit {
  let prev: HTMLElement | null = null;
  let prevTop = 0;
  for (const block of blocks) {
    const top = referenceElementTopWithinContainer(block, clone);
    if (top === null) {
      continue;
    }
    const height = block.offsetHeight;
    if (y < top) {
      return { block, mode: "gap", value: y - top };
    }
    if (y < top + height) {
      return { block, mode: "frac", value: height > 0 ? (y - top) / height : 0 };
    }
    prev = block;
    prevTop = top;
  }

  if (prev) {
    return { block: prev, mode: "tail", value: y - (prevTop + prev.offsetHeight) };
  }
  return null;
}

function referenceElementTopWithinContainer(element: HTMLElement, container: HTMLElement): number | null {
  let top = 0;
  let current: HTMLElement | null = element;
  while (current !== null && current !== container) {
    top += current.offsetTop;
    const nextOffsetParent: Element | null = current.offsetParent;
    current = nextOffsetParent instanceof HTMLElement ? nextOffsetParent : null;
  }
  return current === container ? top : null;
}

function scrollRoot(scrollTop: number, scrollHeight: number, clientHeight: number): Element {
  const root = document.createElement("div");
  Object.defineProperty(root, "scrollTop", { configurable: true, value: scrollTop });
  Object.defineProperty(root, "scrollHeight", { configurable: true, value: scrollHeight });
  Object.defineProperty(root, "clientHeight", { configurable: true, value: clientHeight });
  return root;
}

function expectCloneHit(actual: CloneHit, expected: CloneHit, context: string): void {
  if (expected === null) {
    expect(actual, context).toBeNull();
    return;
  }

  expect(actual, context).not.toBeNull();
  expect(actual!.block, context).toBe(expected.block);
  expect(actual!.mode, context).toBe(expected.mode);
  expect(actual!.value, context).toBe(expected.value);
}
