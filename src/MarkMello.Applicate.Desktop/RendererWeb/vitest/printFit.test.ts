import { readFileSync } from "node:fs";
import { afterEach, describe, expect, it } from "vitest";
import {
  applyPrintFit,
  clearPrintFit,
  computeGlobalPrintScale,
  computePrintFit,
  DEFAULT_PRINT_PAGE_CONTENT_WIDTH_PX,
  PRINT_FIT_ATTR,
  PRINT_FIT_MARGIN_VAR,
  PRINT_FIT_SCALE_VAR
} from "../src/printFit";

type Rect = { width: number; height: number };

function stubRect(el: HTMLElement, rect: Rect): void {
  // happy-dom does not lay out, so getBoundingClientRect returns zeros. Stub it
  // to model a block whose natural width exceeds (or fits within) the page.
  el.getBoundingClientRect = () =>
    ({
      width: rect.width,
      height: rect.height,
      top: 0,
      left: 0,
      right: rect.width,
      bottom: rect.height,
      x: 0,
      y: 0,
      toJSON: () => ({})
    }) as DOMRect;
}

afterEach(() => {
  document.body.replaceChildren();
});

describe("computePrintFit", () => {
  it("returns null when the block already fits the page", () => {
    expect(
      computePrintFit({ contentWidth: 600, availableWidth: 717, contentHeight: 40, marginBottom: 24 })
    ).toBeNull();
  });

  it("returns null when width equals the page exactly", () => {
    expect(
      computePrintFit({ contentWidth: 717, availableWidth: 717, contentHeight: 40, marginBottom: 24 })
    ).toBeNull();
  });

  it("scales a block wider than the page by availableWidth / contentWidth", () => {
    const result = computePrintFit({
      contentWidth: 1000,
      availableWidth: 717,
      contentHeight: 40,
      marginBottom: 24
    });
    expect(result).not.toBeNull();
    expect(result?.scale).toBeCloseTo(0.717, 5);
  });

  it("reserves the scaled height by subtracting the over-reservation from the margin", () => {
    // scale = 0.5 → over-reservation = height * (1 - scale) = 100 * 0.5 = 50.
    const result = computePrintFit({
      contentWidth: 1000,
      availableWidth: 500,
      contentHeight: 100,
      marginBottom: 24
    });
    expect(result?.scale).toBeCloseTo(0.5, 5);
    expect(result?.marginBottom).toBeCloseTo(24 - 50, 5); // -26px (negative is valid)
  });

  it("leaves the margin unchanged when the height is unusable", () => {
    const result = computePrintFit({
      contentWidth: 1000,
      availableWidth: 500,
      contentHeight: 0,
      marginBottom: 24
    });
    expect(result?.marginBottom).toBeCloseTo(24, 5);
  });

  it("returns null for non-finite or non-positive inputs (fail-safe)", () => {
    expect(
      computePrintFit({ contentWidth: Number.NaN, availableWidth: 717, contentHeight: 40, marginBottom: 24 })
    ).toBeNull();
    expect(
      computePrintFit({ contentWidth: 0, availableWidth: 717, contentHeight: 40, marginBottom: 24 })
    ).toBeNull();
    expect(
      computePrintFit({ contentWidth: 1000, availableWidth: 0, contentHeight: 40, marginBottom: 24 })
    ).toBeNull();
  });
});

describe("applyPrintFit / clearPrintFit (DOM)", () => {
  it("marks a block wider than the page with the measured scale", () => {
    const math = document.createElement("div");
    math.className = "math-display";
    document.body.appendChild(math);
    stubRect(math, { width: 1000, height: 40 });

    applyPrintFit(document, 717);

    expect(math.hasAttribute(PRINT_FIT_ATTR)).toBe(true);
    const scale = Number.parseFloat(math.style.getPropertyValue(PRINT_FIT_SCALE_VAR));
    expect(scale).toBeCloseTo(0.717, 5);
    expect(math.style.getPropertyValue(PRINT_FIT_MARGIN_VAR)).toMatch(/px$/);
  });

  it("leaves a block that fits untouched", () => {
    const pre = document.createElement("pre");
    document.body.appendChild(pre);
    stubRect(pre, { width: 400, height: 120 });

    applyPrintFit(document, 717);

    expect(pre.hasAttribute(PRINT_FIT_ATTR)).toBe(false);
    expect(pre.style.getPropertyValue(PRINT_FIT_SCALE_VAR)).toBe("");
  });

  it("measures the inner table for a .mm-table-scroll wrapper", () => {
    const wrapper = document.createElement("div");
    wrapper.className = "mm-table-scroll";
    const table = document.createElement("table");
    wrapper.appendChild(table);
    document.body.appendChild(wrapper);
    stubRect(wrapper, { width: 1200, height: 300 });

    applyPrintFit(document, 600);

    expect(wrapper.hasAttribute(PRINT_FIT_ATTR)).toBe(true);
    expect(Number.parseFloat(wrapper.style.getPropertyValue(PRINT_FIT_SCALE_VAR))).toBeCloseTo(0.5, 5);
  });

  it("reconciles a block back to untouched once it fits (idempotent re-run)", () => {
    const math = document.createElement("div");
    math.className = "math-display";
    document.body.appendChild(math);

    stubRect(math, { width: 1000, height: 40 });
    applyPrintFit(document, 717);
    expect(math.hasAttribute(PRINT_FIT_ATTR)).toBe(true);

    // Content later fits (e.g. page width passed by the host is larger).
    stubRect(math, { width: 500, height: 40 });
    applyPrintFit(document, 717);
    expect(math.hasAttribute(PRINT_FIT_ATTR)).toBe(false);
    expect(math.style.getPropertyValue(PRINT_FIT_SCALE_VAR)).toBe("");
  });

  it("clearPrintFit removes every marking", () => {
    const math = document.createElement("div");
    math.className = "math-display";
    document.body.appendChild(math);
    stubRect(math, { width: 1000, height: 40 });

    applyPrintFit(document, 717);
    expect(math.hasAttribute(PRINT_FIT_ATTR)).toBe(true);

    clearPrintFit(document);
    expect(math.hasAttribute(PRINT_FIT_ATTR)).toBe(false);
    expect(math.style.getPropertyValue(PRINT_FIT_SCALE_VAR)).toBe("");
    expect(math.style.getPropertyValue(PRINT_FIT_MARGIN_VAR)).toBe("");
  });

  it("ignores invalid available width (fail-safe)", () => {
    const math = document.createElement("div");
    math.className = "math-display";
    document.body.appendChild(math);
    stubRect(math, { width: 1000, height: 40 });

    applyPrintFit(document, 0);
    expect(math.hasAttribute(PRINT_FIT_ATTR)).toBe(false);
  });

  it("derives a sane A4 default page width", () => {
    // 8.27in − 2×0.4in = 7.47in × 96 = 717 (floored).
    expect(DEFAULT_PRINT_PAGE_CONTENT_WIDTH_PX).toBe(717);
  });
});

describe("computeGlobalPrintScale (whole-document reading→page zoom)", () => {
  it("scales the reading column down to the printable page width", () => {
    // A4 portrait printable 717 / default reading column 820.
    expect(computeGlobalPrintScale(717, 820)).toBeCloseTo(717 / 820, 6);
  });

  it("never upscales a reading column narrower than or equal to the page (clamps to 1)", () => {
    expect(computeGlobalPrintScale(717, 600)).toBe(1);
    expect(computeGlobalPrintScale(717, 717)).toBe(1);
  });

  it("scales a resized (wider) reading column further down", () => {
    expect(computeGlobalPrintScale(717, 1000)).toBeCloseTo(0.717, 6);
  });

  it("fails safe to 1 (no scaling) for unusable input", () => {
    expect(computeGlobalPrintScale(Number.NaN, 820)).toBe(1);
    expect(computeGlobalPrintScale(717, Number.NaN)).toBe(1);
    expect(computeGlobalPrintScale(717, 0)).toBe(1);
    expect(computeGlobalPrintScale(0, 820)).toBe(1);
    expect(computeGlobalPrintScale(-10, 820)).toBe(1);
  });
});

describe("per-block + global-scale composition (no double-shrink, no overflow)", () => {
  // The reading defaults: column border-box 820, symmetric base padding 72 ⇒
  // reading CONTENT column 676; A4 printable page 717.
  const READING_COLUMN = 820;
  const BASE_PADDING = 72;
  const READING_CONTENT = READING_COLUMN - 2 * BASE_PADDING; // 676
  const PRINTABLE = 717;
  const NATURAL_BLOCK = 1500; // a formula/table/code far wider than the column

  it("a wide block ends at readingContent×globalScale on the page and never past the printable width", () => {
    const fit = computePrintFit({
      contentWidth: NATURAL_BLOCK,
      availableWidth: READING_CONTENT,
      contentHeight: 200,
      marginBottom: 24
    });
    const g = computeGlobalPrintScale(PRINTABLE, READING_COLUMN);
    expect(fit).not.toBeNull();
    const blockWidthOnPage = NATURAL_BLOCK * (fit as { scale: number }).scale * g;
    // Per-block pass fits the block to the reading content column; the global
    // zoom then carries the whole column (block included) to the page.
    expect(blockWidthOnPage).toBeCloseTo(READING_CONTENT * g, 4);
    // The content column (676) scaled by g maps within the printable width.
    expect(blockWidthOnPage).toBeLessThanOrEqual(PRINTABLE + 1e-6);
  });

  it("fitting to the COLUMN makes the block align exactly with the text column after zoom (page-target would overflow it)", () => {
    const g = computeGlobalPrintScale(PRINTABLE, READING_COLUMN);
    const textColumnOnPage = READING_CONTENT * g; // where the text column lands
    const toColumn = computePrintFit({
      contentWidth: NATURAL_BLOCK,
      availableWidth: READING_CONTENT,
      contentHeight: 200,
      marginBottom: 24
    }) as { scale: number };
    // Correct: the column-fitted block lands exactly on the text column width —
    // it "fits the reading column then scales with everything else", so it is
    // flush with the text, never spilling into the page margins.
    expect(NATURAL_BLOCK * toColumn.scale * g).toBeCloseTo(textColumnOnPage, 4);
    // The double-shrink mistake (target the block straight to the PAGE width and
    // still zoom) would land it at PRINTABLE*g, WIDER than the text column, i.e.
    // spilling past the text into the margins. This is the falsifiable guard: if
    // applyPrintFit's target were changed back to the page width, the block would
    // no longer be flush with the text column.
    const toPage = computePrintFit({
      contentWidth: NATURAL_BLOCK,
      availableWidth: PRINTABLE,
      contentHeight: 200,
      marginBottom: 24
    }) as { scale: number };
    expect(NATURAL_BLOCK * toPage.scale * g).toBeGreaterThan(textColumnOnPage);
  });
});

describe("renderer.css @media print rules (Change 1 + Change 2 CSS contract)", () => {
  // vitest runs with cwd = the MarkMello.Applicate.Desktop package dir (same
  // pattern the capture suite uses for renderer.ts); import.meta.url is not a
  // file:// URL under vite, so read the shipped stylesheet by repo path.
  const css = readFileSync("RendererWeb/assets/renderer.css", "utf8");
  // Isolate the body > main.mm-document print rule body so the assertions are
  // about that rule, not a coincidental substring elsewhere.
  const printMainRule =
    css.match(/body\s*>\s*main\.mm-document\s*\{[^}]*\}/)?.[0] ?? "";

  it("Change 2: the print document is zoomed by --mm-print-doc-scale with a never-upscale fallback", () => {
    expect(printMainRule).toMatch(/zoom:\s*var\(\s*--mm-print-doc-scale\s*,\s*0?\.\d+\s*\)/);
  });

  it("Change 2: the reading column width is preserved in print (not reflowed full-bleed)", () => {
    expect(printMainRule).toMatch(/width:\s*var\(\s*--mm-document-max-width\s*\)/);
    // Padding pinned to the base value (not the old 0 full-bleed) so the text
    // column matches the reading view and the minimap reservation cannot skew it.
    expect(printMainRule).toMatch(/padding-left:\s*var\(\s*--mm-document-base-padding-x\s*\)/);
    expect(printMainRule).toMatch(/padding-right:\s*var\(\s*--mm-document-base-padding-x\s*\)/);
    // The old full-bleed reflow (padding-left/right: 0) must be gone.
    expect(printMainRule).not.toMatch(/padding-left:\s*0\b/);
  });

  it("Change 1 (generalised): every scaled block grows its border-box to max-content next to the transform", () => {
    const fitRule = css.match(/\[data-mm-print-fit\]\s*\{[^}]*\}/)?.[0] ?? "";
    // The single scaled-block rule must both grow the box to max-content (so the
    // box wraps content before scaling — no clip, no under-cover) AND apply the
    // per-block scale. Applying to `[data-mm-print-fit]` (not `pre[...]`) covers
    // wide display-math and wide tables, which clip without it.
    expect(fitRule).toMatch(/width:\s*max-content/);
    expect(fitRule).toMatch(/transform:\s*scale\(\s*var\(\s*--mm-print-fit-scale/);
  });
});
