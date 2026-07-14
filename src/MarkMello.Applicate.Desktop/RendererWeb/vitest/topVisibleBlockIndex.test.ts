import { describe, expect, it } from "vitest";
import {
  collectLiveDocumentBlockElements,
  createBlockElementIndex,
  findTopVisibleBlockIndexFromBlocks,
  getDocumentViewportTopCloneYFromIndex,
} from "../src/topVisibleBlockIndex";

function setBlockGeometry(element: HTMLElement, top: number, height: number): void {
  Object.defineProperty(element, "offsetTop", {
    configurable: true,
    get: () => top,
  });
  Object.defineProperty(element, "offsetHeight", {
    configurable: true,
    get: () => height,
  });
}

function setBlockOffsetParent(element: HTMLElement, offsetParent: HTMLElement | null): void {
  Object.defineProperty(element, "offsetParent", {
    configurable: true,
    get: () => offsetParent,
  });
}

function block(index: number, top: number, height: number): HTMLElement {
  const element = document.createElement("p");
  element.dataset.mmBlockIndex = String(index);
  setBlockGeometry(element, top, height);
  return element;
}

function referenceTopVisibleBlockIndex(blocks: readonly HTMLElement[], scrollTop: number): number | null {
  if (blocks.length === 0) {
    return null;
  }

  for (const element of blocks) {
    if (element.offsetTop + element.offsetHeight - scrollTop >= 0) {
      const raw = element.dataset.mmBlockIndex;
      return raw === undefined ? null : Number.parseInt(raw, 10);
    }
  }

  const last = blocks[blocks.length - 1]!;
  const raw = last.dataset.mmBlockIndex;
  return raw === undefined ? null : Number.parseInt(raw, 10);
}

type AnchorFixture = {
  allDocumentBlocks: HTMLElement[];
  clone: HTMLElement;
  cloneBlocks: HTMLElement[];
  documentBlocks: HTMLElement[];
  getRectReadCount: () => number;
  resetRectReadCount: () => void;
  setScrollTop: (value: number) => void;
};

function createAnchorFixture(blockCount: number, hiddenMermaidIndex: number): AnchorFixture {
  const documentRoot = document.createElement("main");
  documentRoot.className = "mm-document";
  document.body.replaceChildren(documentRoot);
  const clone = document.createElement("main");
  const allDocumentBlocks: HTMLElement[] = [];
  const cloneBlocks: HTMLElement[] = [];
  let scrollTop = 0;
  let rectReadCount = 0;
  let documentTop = 0;
  let cloneTop = 0;

  for (let index = 0; index < blockCount; index++) {
    const hidden = index === hiddenMermaidIndex;
    const blockDocumentTop = hidden ? 0 : documentTop;
    const documentHeight = hidden ? 0 : 24 + (index % 3);
    const blockCloneTop = hidden ? 0 : cloneTop;
    const cloneHeight = hidden ? 0 : 40 + (index % 7);
    const documentBlock = document.createElement(hidden ? "pre" : "p");
    const cloneBlock = document.createElement(hidden ? "pre" : "p");
    documentBlock.dataset.mmBlockIndex = String(index);
    cloneBlock.dataset.mmBlockIndex = String(index);
    if (hidden) {
      documentBlock.className = "mm-mermaid is-rendered";
      cloneBlock.className = "mm-mermaid is-rendered";
      documentBlock.style.display = "none";
      cloneBlock.style.display = "none";
    }

    setBlockGeometry(documentBlock, blockDocumentTop, documentHeight);
    setBlockOffsetParent(documentBlock, hidden ? null : documentRoot);
    setBlockGeometry(cloneBlock, blockCloneTop, cloneHeight);
    setBlockOffsetParent(cloneBlock, hidden ? null : clone);
    documentBlock.getBoundingClientRect = () => {
      rectReadCount++;
      return hidden
        ? new DOMRect(0, 0, 0, 0)
        : new DOMRect(0, blockDocumentTop - scrollTop, 800, documentHeight);
    };

    allDocumentBlocks.push(documentBlock);
    cloneBlocks.push(cloneBlock);
    documentRoot.append(documentBlock);
    clone.append(cloneBlock);
    if (!hidden) {
      documentTop += 32;
      cloneTop += cloneHeight + 11 + (index % 2);
    }
  }

  return {
    allDocumentBlocks,
    clone,
    cloneBlocks,
    documentBlocks: collectLiveDocumentBlockElements(document),
    getRectReadCount: () => rectReadCount,
    resetRectReadCount: () => { rectReadCount = 0; },
    setScrollTop: (value: number) => { scrollTop = value; },
  };
}

function referenceCloneSpaceTop(element: HTMLElement, container: HTMLElement): number | null {
  let top = 0;
  let current: HTMLElement | null = element;
  while (current !== null && current !== container) {
    top += current.offsetTop;
    const nextOffsetParent: Element | null = current.offsetParent;
    current = nextOffsetParent instanceof HTMLElement ? nextOffsetParent : null;
  }
  return current === container ? top : null;
}

function referenceDocumentViewportTopCloneY(
  documentBlocks: readonly HTMLElement[],
  clone: HTMLElement,
): number | null {
  for (const documentBlock of documentBlocks) {
    const rect = documentBlock.getBoundingClientRect();
    if (rect.height <= 0 || rect.bottom < 0) {
      continue;
    }

    const blockIndex = documentBlock.dataset.mmBlockIndex;
    if (blockIndex === undefined) {
      continue;
    }
    const cloneBlock = clone.querySelector<HTMLElement>(`[data-mm-block-index="${blockIndex}"]`);
    if (!cloneBlock) {
      continue;
    }
    const cloneTop = referenceCloneSpaceTop(cloneBlock, clone);
    if (cloneTop === null) {
      continue;
    }

    const offset = -rect.top;
    const contribution = offset <= 0
      ? offset
      : (rect.height > 0 ? (offset / rect.height) * cloneBlock.offsetHeight : 0);
    return cloneTop + contribution;
  }

  return null;
}

describe("top visible block index lookup", () => {
  it("matches the scoped linear scan contract across scroll positions", () => {
    const blocks = [
      block(10, 0, 90),
      block(11, 120, 80),
      block(12, 240, 120),
      block(13, 420, 180),
      block(14, 720, 90),
    ];

    for (const scrollTop of [0, 89, 90, 91, 200, 201, 359, 360, 601, 900]) {
      expect(findTopVisibleBlockIndexFromBlocks(blocks, scrollTop)).toBe(
        referenceTopVisibleBlockIndex(blocks, scrollTop));
    }
  });

  it("uses a live-document scoped block list so minimap clones are excluded", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main><aside class='mm-minimap'></aside></body>";
    const main = document.querySelector<HTMLElement>("main.mm-document")!;
    const minimap = document.querySelector<HTMLElement>(".mm-minimap")!;
    const liveBlocks = [
      block(0, 0, 100),
      block(1, 120, 100),
      block(2, 240, 100),
    ];
    main.append(...liveBlocks);

    const cloneBlock = block(999, 1, 1);
    Object.defineProperty(cloneBlock, "offsetTop", {
      configurable: true,
      get: () => {
        throw new Error("minimap clone was scanned");
      },
    });
    minimap.append(cloneBlock);

    const collected = collectLiveDocumentBlockElements(document);

    expect(collected).toEqual(liveBlocks);
    expect(findTopVisibleBlockIndexFromBlocks(collected, 121)).toBe(1);
  });

  it("excludes rendered Mermaid <pre> by class WITHOUT reading layout (guards the far-jump freeze)", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const main = document.querySelector<HTMLElement>("main.mm-document")!;
    const visibleA = document.createElement("p");
    visibleA.dataset.mmBlockIndex = "0";
    const hiddenMermaid = document.createElement("pre");
    hiddenMermaid.dataset.mmBlockIndex = "1";
    hiddenMermaid.className = "mm-mermaid is-rendered";
    const visibleB = document.createElement("p");
    visibleB.dataset.mmBlockIndex = "2";
    // Reading offsetParent/offsetHeight on N content-visibility blocks was the
    // ~856ms far-jump freeze (a full layout flush per lazy-Mermaid invalidation).
    // The class-based selector must build the list WITHOUT touching layout, so
    // trip a failure if collect reads either property on any block.
    for (const el of [visibleA, hiddenMermaid, visibleB]) {
      Object.defineProperty(el, "offsetParent", {
        configurable: true,
        get: () => { throw new Error("collectLiveDocumentBlockElements read offsetParent (forced layout)"); },
      });
      Object.defineProperty(el, "offsetHeight", {
        configurable: true,
        get: () => { throw new Error("collectLiveDocumentBlockElements read offsetHeight (forced layout)"); },
      });
    }
    main.append(visibleA, hiddenMermaid, visibleB);

    const collected = collectLiveDocumentBlockElements(document);

    expect(collected).toHaveLength(2);
    expect(collected[0]).toBe(visibleA);
    expect(collected[1]).toBe(visibleB);
  });

  it("matches the old linear scan at every position with a real display:none Mermaid at the median", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const main = document.querySelector<HTMLElement>("main.mm-document")!;
    const blockCount = 2776;
    const hiddenMermaidIndex = Math.floor(blockCount / 2);
    const allBlocks: HTMLElement[] = [];
    let documentTop = 0;

    for (let index = 0; index < blockCount; index++) {
      const hidden = index === hiddenMermaidIndex;
      const element = document.createElement(hidden ? "pre" : "p");
      element.dataset.mmBlockIndex = String(index);
      if (hidden) {
        element.className = "mm-mermaid is-rendered";
        element.style.display = "none";
      }
      setBlockGeometry(element, hidden ? 0 : documentTop, hidden ? 0 : 1);
      setBlockOffsetParent(element, hidden ? null : main);
      main.append(element);
      allBlocks.push(element);
      if (!hidden) {
        documentTop++;
      }
    }

    const searchableBlocks = collectLiveDocumentBlockElements(document);
    expect(searchableBlocks).toHaveLength(blockCount - 1);
    expect(searchableBlocks).not.toContain(allBlocks[hiddenMermaidIndex]);

    for (let scrollTop = 0; scrollTop < blockCount - 1; scrollTop++) {
      expect(
        findTopVisibleBlockIndexFromBlocks(searchableBlocks, scrollTop),
        `scrollTop=${scrollTop}`
      ).toBe(referenceTopVisibleBlockIndex(allBlocks, scrollTop));
    }
  });

  it("reads logarithmically instead of measuring every block", () => {
    let offsetReadCount = 0;
    const blocks = Array.from({ length: 4096 }, (_, index) => {
      const element = document.createElement("p");
      element.dataset.mmBlockIndex = String(index);
      Object.defineProperty(element, "offsetTop", {
        configurable: true,
        get: () => {
          offsetReadCount++;
          return index * 20;
        },
      });
      Object.defineProperty(element, "offsetHeight", {
        configurable: true,
        get: () => {
          offsetReadCount++;
          return 10;
        },
      });
      return element;
    });

    expect(findTopVisibleBlockIndexFromBlocks(blocks, 70_000)).toBe(3500);
    expect(offsetReadCount).toBeLessThan(80);
  });

  it("returns byte-identical clone geometry to the linear anchor scan at every depth", () => {
    const fixture = createAnchorFixture(32, 23);
    const documentIndex = createBlockElementIndex(fixture.documentBlocks);
    const cloneIndex = createBlockElementIndex(fixture.cloneBlocks);
    const scrollDepths = [
      { name: "top", scrollTop: 0 },
      { name: "middle", scrollTop: 14 * 32 + 9 },
      { name: "deep", scrollTop: 22 * 32 + 29 },
      { name: "hidden-mermaid", scrollTop: 23 * 32 },
      { name: "bottom", scrollTop: 31 * 32 - 80 },
    ];

    for (const { name, scrollTop } of scrollDepths) {
      fixture.setScrollTop(scrollTop);
      const expected = referenceDocumentViewportTopCloneY(fixture.allDocumentBlocks, fixture.clone);
      const topBlockIndex = findTopVisibleBlockIndexFromBlocks(fixture.documentBlocks, scrollTop);
      const actual = getDocumentViewportTopCloneYFromIndex({
        topBlockIndex,
        documentBlocks: documentIndex,
        cloneBlocks: cloneIndex,
        cloneContainer: fixture.clone,
        clientY: 0,
      });

      expect(actual, `${name}: scrollTop=${scrollTop}`).toBe(expected);
    }
  });

  it("keeps document rect reads constant across a 4096-block depth sweep", () => {
    const hiddenMermaidIndex = 3072;
    const fixture = createAnchorFixture(4096, hiddenMermaidIndex);
    const documentIndex = createBlockElementIndex(fixture.documentBlocks);
    const cloneIndex = createBlockElementIndex(fixture.cloneBlocks);
    const scrollDepths = [0, 1024 * 32 + 7, 3071 * 32 + 31, hiddenMermaidIndex * 32, 4095 * 32 - 700];

    for (const scrollTop of scrollDepths) {
      fixture.setScrollTop(scrollTop);
      const expected = referenceDocumentViewportTopCloneY(fixture.allDocumentBlocks, fixture.clone);
      const topBlockIndex = findTopVisibleBlockIndexFromBlocks(fixture.documentBlocks, scrollTop);
      fixture.resetRectReadCount();

      const actual = getDocumentViewportTopCloneYFromIndex({
        topBlockIndex,
        documentBlocks: documentIndex,
        cloneBlocks: cloneIndex,
        cloneContainer: fixture.clone,
        clientY: 0,
      });

      expect(actual, `scrollTop=${scrollTop}`).toBe(expected);
      expect(fixture.getRectReadCount(), `scrollTop=${scrollTop}`).toBeLessThanOrEqual(2);
    }
  });
});
