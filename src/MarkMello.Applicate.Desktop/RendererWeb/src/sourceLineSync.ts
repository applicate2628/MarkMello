export type SourceLineAnchor = {
  sourceLine: number;
  endLine: number;
  top: number;
};

const SOURCE_LINE_ANCHOR_SELECTOR = "[data-mm-source-line]";

// VIRT-TODO(integration): source-line sync reads only live virtualized anchors;
// off-window source targets need model-backed anchor lookup.
export function readSourceLineAnchors(root: ParentNode = document, scrollY = window.scrollY): SourceLineAnchor[] {
  const anchors: SourceLineAnchor[] = [];
  for (const element of Array.from(root.querySelectorAll<HTMLElement>(SOURCE_LINE_ANCHOR_SELECTOR))) {
    const sourceLine = parseNonNegativeInt(element.dataset["mmSourceLine"]);
    if (sourceLine === null) {
      continue;
    }

    const endLine = parseNonNegativeInt(element.dataset["mmSourceEndLine"]) ?? sourceLine;
    anchors.push({
      sourceLine,
      endLine: Math.max(sourceLine, endLine),
      top: Math.max(0, element.getBoundingClientRect().top + scrollY),
    });
  }

  anchors.sort((left, right) => {
    const sourceComparison = left.sourceLine - right.sourceLine;
    return sourceComparison !== 0 ? sourceComparison : left.top - right.top;
  });
  return anchors;
}

export function findScrollTopForSourceLine(
  anchors: readonly SourceLineAnchor[],
  sourceLine: number
): number | null {
  if (anchors.length === 0 || !Number.isFinite(sourceLine)) {
    return null;
  }

  const normalizedLine = Math.max(0, Math.floor(sourceLine));
  const selectedIndex = findLastAnchorIndexAtOrBeforeLine(anchors, normalizedLine);
  const selected = anchors[selectedIndex]!;
  const next = anchors[selectedIndex + 1] ?? null;

  if (next && normalizedLine > selected.endLine) {
    const lineSpan = Math.max(1, next.sourceLine - selected.sourceLine);
    const visualSpan = Math.max(0, next.top - selected.top);
    const ratio = clamp01((normalizedLine - selected.sourceLine) / lineSpan);
    return Math.max(0, selected.top + visualSpan * ratio);
  }

  if (next && normalizedLine > selected.sourceLine && normalizedLine <= selected.endLine) {
    const lineSpan = Math.max(1, selected.endLine - selected.sourceLine);
    const visualSpan = Math.max(0, next.top - selected.top);
    const ratio = clamp01((normalizedLine - selected.sourceLine) / lineSpan);
    return Math.max(0, selected.top + visualSpan * ratio);
  }

  return Math.max(0, selected.top);
}

export function findSourceLineAtDocumentY(
  anchors: readonly SourceLineAnchor[],
  documentY: number
): number | null {
  if (anchors.length === 0 || !Number.isFinite(documentY)) {
    return null;
  }

  const normalizedY = Math.max(0, documentY);
  const selectedIndex = findLastAnchorIndexAtOrBeforeTop(anchors, normalizedY);
  const selected = anchors[selectedIndex]!;
  const next = anchors[selectedIndex + 1] ?? null;
  if (!next) {
    return selected.sourceLine;
  }

  const visualSpan = next.top - selected.top;
  if (visualSpan <= 1) {
    return selected.sourceLine;
  }

  const targetLine = selected.endLine > selected.sourceLine
    ? selected.endLine
    : next.sourceLine;
  const lineSpan = Math.max(0, targetLine - selected.sourceLine);
  if (lineSpan <= 0) {
    return selected.sourceLine;
  }

  const ratio = clamp01((normalizedY - selected.top) / visualSpan);
  return selected.sourceLine + Math.round(lineSpan * ratio);
}

function findLastAnchorIndexAtOrBeforeLine(
  anchors: readonly SourceLineAnchor[],
  sourceLine: number
): number {
  let low = 0;
  let high = anchors.length - 1;
  let result = 0;

  while (low <= high) {
    const mid = low + Math.floor((high - low) / 2);
    if (anchors[mid]!.sourceLine <= sourceLine) {
      result = mid;
      low = mid + 1;
    } else {
      high = mid - 1;
    }
  }

  return result;
}

function findLastAnchorIndexAtOrBeforeTop(
  anchors: readonly SourceLineAnchor[],
  documentY: number
): number {
  let low = 0;
  let high = anchors.length - 1;
  let result = 0;

  while (low <= high) {
    const mid = low + Math.floor((high - low) / 2);
    if (anchors[mid]!.top <= documentY) {
      result = mid;
      low = mid + 1;
    } else {
      high = mid - 1;
    }
  }

  return result;
}

function parseNonNegativeInt(value: string | undefined): number | null {
  if (value === undefined || value.trim() === "") {
    return null;
  }

  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : null;
}

function clamp01(value: number): number {
  return Math.max(0, Math.min(1, value));
}
