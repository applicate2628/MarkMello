const BLOCK_AXIS_PADDING_BORDER_PROPERTIES = [
  "padding-top",
  "padding-bottom",
  "border-top-width",
  "border-bottom-width",
] as const;

export function elementDocumentTop(element: HTMLElement): number {
  let top = 0;
  let current: HTMLElement | null = element;
  while (current !== null) {
    if (!Number.isFinite(current.offsetTop)) {
      return Number.NaN;
    }
    top += current.offsetTop;
    const parent: Element | null = current.offsetParent;
    current = parent instanceof HTMLElement ? parent : null;
  }
  return top;
}

export function readNextSiblingDocumentTop(element: HTMLElement): number | null {
  let sibling = element.nextElementSibling;
  while (sibling instanceof HTMLElement) {
    const top = elementDocumentTop(sibling);
    if (Number.isFinite(top)) {
      return top;
    }
    sibling = sibling.nextElementSibling;
  }
  return null;
}

export function readOccupiedTopDelta(top: number, nextTop: number | null | undefined): number | null {
  if (!Number.isFinite(top) || nextTop === null || nextTop === undefined || !Number.isFinite(nextTop) || nextTop <= top) {
    return null;
  }
  return nextTop - top;
}

export function readOccupiedBlockHeight(element: HTMLElement): number | null {
  return readOccupiedTopDelta(elementDocumentTop(element), readNextSiblingDocumentTop(element));
}

export function readCssPixelLength(raw: string): number | null {
  const value = raw.trim();
  if (value === "" || value === "0") {
    return 0;
  }
  if (!value.endsWith("px")) {
    return null;
  }

  const parsed = Number.parseFloat(value);
  return Number.isFinite(parsed) ? parsed : null;
}

export function readBlockAxisPaddingBorderHeightPx(element: HTMLElement): number | null {
  const styles = element.ownerDocument.defaultView?.getComputedStyle(element);
  if (!styles) {
    return 0;
  }

  let total = 0;
  for (const propertyName of BLOCK_AXIS_PADDING_BORDER_PROPERTIES) {
    const value = readCssPixelLength(styles.getPropertyValue(propertyName));
    if (value === null) {
      return null;
    }
    total += value;
  }
  return total;
}

export function readContainIntrinsicBlockSizePx(element: HTMLElement): number | null {
  return parseContainIntrinsicBlockSizePx(readCssPropertyWithNonBlankInlinePreference(
    element,
    "contain-intrinsic-size"
  ));
}

export function readCollapsedBorderBoxHeightPx(element: HTMLElement): number | null {
  // The realization tracker historically treats any truthy inline CSSOM value as authoritative,
  // while the document model ignores blank inline text. Keep that distinction explicit here.
  const intrinsicSize = parseContainIntrinsicBlockSizePx(readCssPropertyWithTruthyInlinePreference(
    element,
    "contain-intrinsic-size"
  ));
  const nonContent = readBlockAxisPaddingBorderHeightPx(element);
  if (intrinsicSize === null || nonContent === null) {
    return null;
  }
  return intrinsicSize + nonContent;
}

export function isStrictlyViewportIntersecting(element: HTMLElement): boolean {
  const root = element.ownerDocument.scrollingElement ?? element.ownerDocument.documentElement;
  const top = elementDocumentTop(element);
  const height = element.offsetHeight;
  const viewportTop = Number.isFinite(root.scrollTop) ? root.scrollTop : 0;
  const viewportHeight = root.clientHeight || element.ownerDocument.defaultView?.innerHeight || 0;
  if (!Number.isFinite(top) || !Number.isFinite(height) || !Number.isFinite(viewportHeight) || viewportHeight <= 0) {
    return false;
  }

  return rangesStrictlyIntersect(top, top + height, viewportTop, viewportTop + viewportHeight);
}

export function isOutsideViewportWithTolerance(
  element: HTMLElement,
  top: number,
  height: number,
  tolerancePx: number
): boolean {
  const viewport = readDocumentViewport(element);
  if (viewport === null) {
    return false;
  }

  const bottom = top + height;
  return bottom <= viewport.top + tolerancePx || top >= viewport.bottom - tolerancePx;
}

export function reachesViewportTopInclusive(blockTop: number, blockHeight: number, viewportTop: number): boolean {
  // Top-visible anchor lookup retains the block whose bottom exactly touches the viewport top.
  // Realization tracking uses rangesStrictlyIntersect instead and excludes both touching edges.
  return blockTop + blockHeight >= viewportTop;
}

export function rangesStrictlyIntersect(
  firstTop: number,
  firstBottom: number,
  secondTop: number,
  secondBottom: number
): boolean {
  return firstTop < secondBottom && firstBottom > secondTop;
}

export function readBlockIndex(element: HTMLElement): number | null {
  const raw = element.dataset["mmBlockIndex"];
  const parsed = raw === undefined ? Number.NaN : Number.parseInt(raw, 10);
  return Number.isFinite(parsed) ? parsed : null;
}

function parseContainIntrinsicBlockSizePx(raw: string): number | null {
  const matches = Array.from(raw.matchAll(/(-?\d+(?:\.\d+)?)px/g));
  const lastMatch = matches[matches.length - 1];
  if (!lastMatch) {
    return null;
  }

  const parsed = Number.parseFloat(lastMatch[1]!);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : null;
}

function readCssPropertyWithNonBlankInlinePreference(element: HTMLElement, propertyName: string): string {
  const inlineValue = element.style.getPropertyValue(propertyName);
  if (inlineValue.trim().length > 0) {
    return inlineValue;
  }

  return element.ownerDocument.defaultView?.getComputedStyle(element).getPropertyValue(propertyName) ?? "";
}

function readCssPropertyWithTruthyInlinePreference(element: HTMLElement, propertyName: string): string {
  return element.style.getPropertyValue(propertyName)
    || element.ownerDocument.defaultView?.getComputedStyle(element).getPropertyValue(propertyName)
    || "";
}

function readDocumentViewport(element: HTMLElement): { top: number; bottom: number } | null {
  const doc = element.ownerDocument;
  const view = doc.defaultView;
  const root = doc.scrollingElement ?? doc.documentElement;
  const top = Number.isFinite(root.scrollTop) ? root.scrollTop : view?.scrollY ?? 0;
  const height = root.clientHeight || view?.innerHeight || doc.documentElement.clientHeight;
  if (!Number.isFinite(top) || !Number.isFinite(height) || height <= 0) {
    return null;
  }

  return { bottom: top + height, top };
}
