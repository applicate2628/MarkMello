import { describe, expect, it } from "vitest";
import {
  collectLiveDocumentBlockElements,
  findTopVisibleBlockIndexFromBlocks,
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
});
