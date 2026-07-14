const LIVE_DOCUMENT_BLOCK_SELECTOR = "body > main.mm-document [data-mm-block-index]";

export type BlockElementIndex = {
  readonly elements: readonly HTMLElement[];
  readonly elementsByBlockIndex: ReadonlyMap<number, HTMLElement>;
  readonly positionsByBlockIndex: ReadonlyMap<number, number>;
};

export type DocumentViewportTopCloneYInput = {
  topBlockIndex: number | null;
  documentBlocks: BlockElementIndex;
  cloneBlocks: BlockElementIndex;
  cloneContainer: HTMLElement;
  clientY: number;
};

export function collectLiveDocumentBlockElements(ownerDocument: Document): HTMLElement[] {
  return Array.from(ownerDocument.querySelectorAll<HTMLElement>(LIVE_DOCUMENT_BLOCK_SELECTOR));
}

export function createBlockElementIndex(elements: readonly HTMLElement[]): BlockElementIndex {
  const elementsByBlockIndex = new Map<number, HTMLElement>();
  const positionsByBlockIndex = new Map<number, number>();
  for (let position = 0; position < elements.length; position++) {
    const element = elements[position]!;
    const blockIndex = readBlockIndex(element);
    if (blockIndex === null) {
      continue;
    }
    // Match querySelector's first-match behavior if malformed input ever
    // contains a duplicate index; the normal renderer producer is unique.
    if (!elementsByBlockIndex.has(blockIndex)) {
      elementsByBlockIndex.set(blockIndex, element);
      positionsByBlockIndex.set(blockIndex, position);
    }
  }

  return { elements, elementsByBlockIndex, positionsByBlockIndex };
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

export function getDocumentViewportTopCloneYFromIndex(
  input: DocumentViewportTopCloneYInput
): number | null {
  if (input.topBlockIndex === null) {
    return null;
  }

  const startPosition = input.documentBlocks.positionsByBlockIndex.get(input.topBlockIndex);
  if (startPosition === undefined) {
    return null;
  }

  for (let position = startPosition; position < input.documentBlocks.elements.length; position++) {
    const documentBlock = input.documentBlocks.elements[position]!;
    const rect = documentBlock.getBoundingClientRect();
    // A rendered mermaid's source <pre> and clone twin are display:none. Preserve
    // the established forward skip, but start it at the binary-search result so
    // work depends only on the local hidden run, never the scroll depth.
    if (rect.height <= 0 || rect.bottom < input.clientY) {
      continue;
    }

    const blockIndex = readBlockIndex(documentBlock);
    if (blockIndex === null) {
      continue;
    }
    const cloneBlock = input.cloneBlocks.elementsByBlockIndex.get(blockIndex);
    if (!cloneBlock) {
      continue;
    }
    const cloneTop = elementTopWithinContainer(cloneBlock, input.cloneContainer);
    if (cloneTop === null) {
      continue;
    }

    const offset = input.clientY - rect.top;
    const contribution = offset <= 0
      ? offset
      : (rect.height > 0 ? (offset / rect.height) * cloneBlock.offsetHeight : 0);
    return cloneTop + contribution;
  }

  return null;
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

export function elementTopWithinContainer(element: HTMLElement, container: HTMLElement): number | null {
  let top = 0;
  let current: HTMLElement | null = element;
  while (current !== null && current !== container) {
    top += current.offsetTop;
    const nextOffsetParent: Element | null = current.offsetParent;
    current = nextOffsetParent instanceof HTMLElement ? nextOffsetParent : null;
  }
  return current === container ? top : null;
}
