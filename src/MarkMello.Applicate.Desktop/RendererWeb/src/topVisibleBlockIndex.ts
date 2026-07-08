const LIVE_DOCUMENT_BLOCK_SELECTOR = "body > main.mm-document [data-mm-block-index]";

export function collectLiveDocumentBlockElements(ownerDocument: Document): HTMLElement[] {
  return Array.from(ownerDocument.querySelectorAll<HTMLElement>(LIVE_DOCUMENT_BLOCK_SELECTOR));
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
  let firstAtOrBelowViewportTop = blocks.length;

  while (lo <= hi) {
    const mid = lo + ((hi - lo) >> 1);
    const block = blocks[mid]!;
    if (blockDocumentBottom(block) >= scrollTop) {
      firstAtOrBelowViewportTop = mid;
      hi = mid - 1;
    } else {
      lo = mid + 1;
    }
  }

  const index = firstAtOrBelowViewportTop === blocks.length
    ? blocks.length - 1
    : firstAtOrBelowViewportTop;
  return readBlockIndex(blocks[index]!);
}

function readBlockIndex(block: HTMLElement): number | null {
  const raw = block.dataset["mmBlockIndex"];
  const parsed = raw === undefined ? Number.NaN : Number.parseInt(raw, 10);
  return Number.isFinite(parsed) ? parsed : null;
}

function blockDocumentBottom(block: HTMLElement): number {
  return blockDocumentTop(block) + block.offsetHeight;
}

function blockDocumentTop(block: HTMLElement): number {
  let top = 0;
  let current: HTMLElement | null = block;
  while (current !== null) {
    top += current.offsetTop;
    const nextOffsetParent: Element | null = current.offsetParent;
    current = nextOffsetParent instanceof HTMLElement ? nextOffsetParent : null;
  }
  return top;
}
