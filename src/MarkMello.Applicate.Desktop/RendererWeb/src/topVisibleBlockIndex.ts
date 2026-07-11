import {
  elementDocumentTop,
  readBlockIndex,
  reachesViewportTopInclusive,
} from "./blockGeometryMeasurement";

const LIVE_DOCUMENT_BLOCK_SELECTOR = "body > main.mm-document [data-mm-block-index]";

export function collectLiveDocumentBlockElements(ownerDocument: Document): HTMLElement[] {
  return Array.from(ownerDocument.querySelectorAll<HTMLElement>(LIVE_DOCUMENT_BLOCK_SELECTOR))
    .filter(hasVisibleBlockBox);
}

export function findTopVisibleBlockIndexFromBlocks(
  blocks: readonly HTMLElement[],
  scrollTop: number
): number | null {
  if (blocks.length === 0) {
    return null;
  }

  let lo = 0;
  let hi = blocks.length - 1;
  let firstAtOrBelowViewportTop = -1;

  while (lo <= hi) {
    const mid = lo + ((hi - lo) >> 1);
    const visibleMid = findNearestVisibleBlockBox(blocks, lo, mid, hi);
    if (visibleMid === null) {
      break;
    }

    if (reachesViewportTopInclusive(visibleMid.top, visibleMid.height, scrollTop)) {
      firstAtOrBelowViewportTop = visibleMid.index;
      hi = visibleMid.index - 1;
    } else {
      lo = visibleMid.index + 1;
    }
  }

  const index = firstAtOrBelowViewportTop >= 0
    ? firstAtOrBelowViewportTop
    : findLastVisibleBlockIndex(blocks);
  return index < 0 ? null : readBlockIndex(blocks[index]!);
}

type VisibleBlockBox = {
  index: number;
  top: number;
  height: number;
};

function findNearestVisibleBlockBox(
  blocks: readonly HTMLElement[],
  lo: number,
  mid: number,
  hi: number
): VisibleBlockBox | null {
  for (let index = mid; index >= lo; index--) {
    const box = readVisibleBlockBox(blocks[index]!, index);
    if (box !== null) {
      return box;
    }
  }
  for (let index = mid + 1; index <= hi; index++) {
    const box = readVisibleBlockBox(blocks[index]!, index);
    if (box !== null) {
      return box;
    }
  }
  return null;
}

function findLastVisibleBlockIndex(blocks: readonly HTMLElement[]): number {
  for (let index = blocks.length - 1; index >= 0; index--) {
    if (hasVisibleBlockBox(blocks[index]!)) {
      return index;
    }
  }
  return -1;
}

function hasVisibleBlockBox(block: HTMLElement): boolean {
  return readVisibleBlockBox(block, 0) !== null;
}

function readVisibleBlockBox(block: HTMLElement, index: number): VisibleBlockBox | null {
  const height = block.offsetHeight;
  const top = elementDocumentTop(block);
  if (!Number.isFinite(height) || height < 0 || !Number.isFinite(top) || isDisplayNoneZeroBox(block, height)) {
    return null;
  }
  return { height, index, top };
}

function isDisplayNoneZeroBox(block: HTMLElement, height: number): boolean {
  if (height !== 0) {
    return false;
  }
  if (block.style.display === "none") {
    return true;
  }
  return getComputedStyle(block).display === "none";
}
