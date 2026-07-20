// PDF / print scale-to-fit for over-wide blocks (Applicate fork, 2026-07).
//
// Problem: display math (`.math-display`), wide tables (`.mm-table-scroll`) and
// code blocks (`pre`) use `overflow-x: auto` so they scroll horizontally on
// screen. In a printed page or an exported PDF a scrollbar is meaningless and
// the block is simply clipped at the container width.
//
// Fix: `renderer.css` (inside `@media print`) first breaks these blocks out of
// their scroll container so the whole block is laid out at natural width. This
// module then measures any block that is STILL wider than the printable page
// and scales it down (CSS `transform: scale`, origin top-left) so it is fully
// visible with no clip and no scrollbar. The scale and a corrected bottom
// margin (so the reserved layout height matches the scaled visual height) are
// written as inline CSS custom properties; the transform rule that consumes
// them lives ONLY inside `@media print`, so the on-screen reading view is never
// transformed.
//
// The measurement is media-independent (it reads natural widths on the live,
// screen-media DOM), so it works whether it is driven from the host right
// before `CoreWebView2.PrintToPdfAsync` (programmatic export never fires
// `beforeprint`) or from the `beforeprint` event (interactive OS / Ctrl+P
// printing). No timers are involved — the scale is applied synchronously from
// measured widths.

/** Attribute flag set on a block that has been scaled for print. */
export const PRINT_FIT_ATTR = "data-mm-print-fit";
/** Inline custom property holding the computed scale factor (0, 1). */
export const PRINT_FIT_SCALE_VAR = "--mm-print-fit-scale";
/** Inline custom property holding the reserved (corrected) bottom margin, px. */
export const PRINT_FIT_MARGIN_VAR = "--mm-print-fit-margin-bottom";
/**
 * Inline custom property holding the whole-document print scale (the reading
 * column → printable width `zoom` factor). Consumed ONLY by the `@media print`
 * rule on `body > main.mm-document`, so it is inert on screen.
 */
export const PRINT_DOC_SCALE_VAR = "--mm-print-doc-scale";

/**
 * Whole-document print scale: shrink the reading column so its border-box maps
 * onto the printable page width. Returns `min(1, printableWidth /
 * readingColumnWidth)` — never upscales (a reading column narrower than the
 * page prints at 1:1), and fails safe to `1` (no scaling) on any unusable
 * input. This is the GLOBAL `zoom` factor; it composes with the per-block
 * `computePrintFit` scale (block→column, then column→page) rather than
 * double-shrinking, because the per-block pass fits blocks to the reading
 * COLUMN width and this pass then scales the whole column (blocks included).
 */
export function computeGlobalPrintScale(
  printableWidthPx: number,
  readingColumnWidthPx: number
): number {
  if (!Number.isFinite(printableWidthPx) || !Number.isFinite(readingColumnWidthPx)) {
    return 1;
  }
  if (printableWidthPx <= 0 || readingColumnWidthPx <= 0) {
    return 1;
  }
  return Math.min(1, printableWidthPx / readingColumnWidthPx);
}

// Blocks whose horizontal overflow must be scaled to fit the page. Mermaid
// diagrams are intentionally excluded: their `<svg>` already carries
// `max-width: 100%`, so it scales natively to the page width (vector, no
// quality loss) — a transform on top would shrink it twice.
const CANDIDATE_SELECTOR = ".math-display, .mm-table-scroll, pre";

// Default printable page content width, in CSS px, used when the host does not
// pass an exact value. Derived from the print settings the export uses:
//   A4 portrait width 8.27in − 0.4in margin each side = 7.47in of content.
// Print CSS lays out at 96 CSS px per inch when the print ScaleFactor is 1.0,
// so 7.47in × 96 = ~717px. The host can override this with the exact value
// computed from its own CoreWebView2PrintSettings (see window.__mmApplyPrintFit).
const A4_PORTRAIT_WIDTH_IN = 8.27;
const PRINT_MARGIN_IN_PER_SIDE = 0.4;
const CSS_PX_PER_IN = 96;
export const DEFAULT_PRINT_PAGE_CONTENT_WIDTH_PX = Math.floor(
  (A4_PORTRAIT_WIDTH_IN - 2 * PRINT_MARGIN_IN_PER_SIDE) * CSS_PX_PER_IN
);

export interface PrintFitInput {
  /** Natural (un-clipped) border-box width of the block, px. */
  contentWidth: number;
  /** Printable page content width to fit into, px. */
  availableWidth: number;
  /** Natural border-box height of the block, px (for height reservation). */
  contentHeight: number;
  /** The block's current computed bottom margin, px. */
  marginBottom: number;
}

export interface PrintFitResult {
  /** Scale factor in the open interval (0, 1). */
  scale: number;
  /**
   * Bottom margin to apply while scaled, px. A `transform: scale` does not
   * shrink the box the layout reserves, so scaling down would otherwise leave a
   * gap of `contentHeight * (1 - scale)` below the block. We subtract exactly
   * that from the existing margin so following content sits directly under the
   * scaled block (never overlapping — scale < 1 always under-fills).
   */
  marginBottom: number;
}

/**
 * Pure scale-to-fit computation. Returns `null` when no scaling is needed
 * (the block already fits) or the inputs are not usable, so callers can treat
 * a missing result as "leave the block untouched" (fail-safe).
 */
export function computePrintFit(input: PrintFitInput): PrintFitResult | null {
  const { contentWidth, availableWidth, contentHeight, marginBottom } = input;
  if (!Number.isFinite(contentWidth) || !Number.isFinite(availableWidth)) {
    return null;
  }
  if (contentWidth <= 0 || availableWidth <= 0) {
    return null;
  }
  if (contentWidth <= availableWidth) {
    // Already fits within the page after break-out — nothing to scale.
    return null;
  }
  const scale = availableWidth / contentWidth;
  const reservedShift =
    Number.isFinite(contentHeight) && contentHeight > 0 ? contentHeight * (1 - scale) : 0;
  const correctedMargin = Number.isFinite(marginBottom) ? marginBottom - reservedShift : -reservedShift;
  return { scale, marginBottom: correctedMargin };
}

function parsePx(value: string): number {
  const parsed = Number.parseFloat(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

/**
 * Measure a block's natural (un-capped) border-box size. Temporarily neutralises
 * the block's own width caps (and an inner table's, for `.mm-table-scroll`) so
 * `getBoundingClientRect` reports the content's intrinsic width instead of the
 * on-screen column width. The mutate/read/restore happens synchronously within
 * one task, so no paint occurs between them and the live view never flickers;
 * `try/finally` guarantees the inline styles are restored even on error.
 *
 * `scrollWidth` alone is insufficient here: it never reports less than the
 * element's own client width, so a formula narrower than the (wide) reading
 * column but wider than the (narrow) print page would measure as the column
 * width and be mis-scaled. Natural-width measurement is media-independent.
 */
function measureNaturalSize(el: HTMLElement): { width: number; height: number } {
  const previous = {
    width: el.style.width,
    maxWidth: el.style.maxWidth,
    contentVisibility: el.style.contentVisibility
  };
  const table = el.querySelector<HTMLElement>(":scope > table");
  const previousTable = table ? { width: table.style.width, maxWidth: table.style.maxWidth } : null;
  try {
    el.style.width = "max-content";
    el.style.maxWidth = "none";
    // The candidate blocks are direct children of `main.mm-document`, which the
    // renderer marks `content-visibility: auto` (with a `contain-intrinsic-size`
    // placeholder) to skip off-screen layout. An off-screen or not-yet-"warmed"
    // block therefore reports its intrinsic-size PLACEHOLDER width from
    // getBoundingClientRect, not its real content width — which would make an
    // over-wide block measure as "fits" and never get scaled (it then overflows
    // and clips on the printed page). Force real layout for the measurement, in
    // the same synchronous mutate/read/restore window as the width caps above, so
    // the natural width is always measured regardless of warm-up/scroll state.
    el.style.contentVisibility = "visible";
    if (table) {
      table.style.width = "max-content";
      table.style.maxWidth = "none";
    }
    const rect = el.getBoundingClientRect();
    return { width: rect.width, height: rect.height };
  } finally {
    el.style.width = previous.width;
    el.style.maxWidth = previous.maxWidth;
    el.style.contentVisibility = previous.contentVisibility;
    if (table && previousTable) {
      table.style.width = previousTable.width;
      table.style.maxWidth = previousTable.maxWidth;
    }
  }
}

function clearPrintFitOne(el: HTMLElement): void {
  if (el.hasAttribute(PRINT_FIT_ATTR)) {
    el.removeAttribute(PRINT_FIT_ATTR);
  }
  el.style.removeProperty(PRINT_FIT_SCALE_VAR);
  el.style.removeProperty(PRINT_FIT_MARGIN_VAR);
}

/**
 * Measure every over-wide candidate block under `root` and mark the ones that
 * exceed `availableWidthPx` with a scale + reserved margin (as inline custom
 * properties consumed only by the `@media print` transform rule). Blocks that
 * fit are reconciled back to untouched, so repeated calls are idempotent.
 * Fail-safe: invalid width or a zero measurement leaves the block as-is.
 */
export function applyPrintFit(root: ParentNode, availableWidthPx: number): void {
  if (!Number.isFinite(availableWidthPx) || availableWidthPx <= 0) {
    return;
  }
  const candidates = root.querySelectorAll<HTMLElement>(CANDIDATE_SELECTOR);
  candidates.forEach((el) => {
    const { width, height } = measureNaturalSize(el);
    if (!Number.isFinite(width) || width <= 0) {
      // Hidden (e.g. a rendered `pre.mm-mermaid.is-rendered`) or unmeasurable.
      clearPrintFitOne(el);
      return;
    }
    const marginBottom = parsePx(getComputedStyle(el).marginBottom);
    const fit = computePrintFit({
      contentWidth: width,
      availableWidth: availableWidthPx,
      contentHeight: height,
      marginBottom
    });
    if (fit === null) {
      clearPrintFitOne(el);
      return;
    }
    el.style.setProperty(PRINT_FIT_SCALE_VAR, `${fit.scale}`);
    el.style.setProperty(PRINT_FIT_MARGIN_VAR, `${fit.marginBottom}px`);
    el.setAttribute(PRINT_FIT_ATTR, "");
  });
}

/** Remove every print-fit marking under `root`. */
export function clearPrintFit(root: ParentNode): void {
  root.querySelectorAll<HTMLElement>(`[${PRINT_FIT_ATTR}]`).forEach((el) => {
    clearPrintFitOne(el);
  });
}
