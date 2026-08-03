// schematicMinimap.ts
//
// Stage 2 schematic-minimap walker. Performs a single one-pass classification
// over the direct children of `.mm-document`, returning a typed DocumentBlock
// array. Pure read-only DOM inspection: never mutates the DOM. Unknown element
// types are silently skipped so the walker is safe over real renderer output
// that contains wrapper divs, anchors, or other unclassified nodes.

export type DocumentBlockKind =
  | "heading-1"
  | "heading-2"
  | "heading-3"
  | "heading-4"
  | "heading-5"
  | "heading-6"
  | "paragraph"
  | "code"
  | "math-display"
  | "mermaid"
  | "table"
  | "list"
  | "quote"
  | "hr";

export type DocumentBlock = {
  kind: DocumentBlockKind;
  top: number;
  height: number;
  textLines?: number;
};

export type SchematicMinimapInput = {
  documentRoot: HTMLElement;
  documentHeight: number;
};

export function walkDocumentBlocks(input: SchematicMinimapInput): DocumentBlock[] {
  const blocks: DocumentBlock[] = [];
  const children = input.documentRoot.children;
  for (let i = 0; i < children.length; i++) {
    const child = children[i];
    if (!(child instanceof HTMLElement)) continue;
    const kind = classifyDocumentBlockElement(child);
    if (!kind) continue;
    const top = child.offsetTop;
    const height = child.offsetHeight;
    const block: DocumentBlock = { kind, top, height };
    if (kind === "paragraph" || kind === "list" || kind === "quote") {
      const lineHeight = parseFloat(getComputedStyle(child).lineHeight) || 16;
      block.textLines = Math.max(1, Math.round(height / lineHeight));
    }
    blocks.push(block);
  }
  return blocks;
}

export function classifyDocumentBlockElement(el: HTMLElement): DocumentBlockKind | null {
  if (el.dataset["mmBlockKind"] === "table") return "table";
  const tag = el.tagName.toLowerCase();
  if (tag === "h1") return "heading-1";
  if (tag === "h2") return "heading-2";
  if (tag === "h3") return "heading-3";
  if (tag === "h4") return "heading-4";
  if (tag === "h5") return "heading-5";
  if (tag === "h6") return "heading-6";
  if (tag === "p") return "paragraph";
  if (tag === "pre") {
    if (el.classList.contains("mm-mermaid")) return "mermaid";
    return "code";
  }
  if (el.classList.contains("math-display")) return "math-display";
  if (tag === "table") return "table";
  if (tag === "ul" || tag === "ol") return "list";
  if (tag === "blockquote") return "quote";
  if (tag === "hr") return "hr";
  return null;
}

const SVG_NS = "http://www.w3.org/2000/svg";

export function renderSchematicSvg(blocks: DocumentBlock[], documentWidth: number, documentHeight: number): SVGSVGElement {
  const svg = document.createElementNS(SVG_NS, "svg");
  svg.setAttribute("viewBox", `0 0 ${documentWidth} ${documentHeight}`);
  svg.setAttribute("preserveAspectRatio", "none");
  svg.style.width = `${documentWidth}px`;
  svg.style.height = `${documentHeight}px`;
  svg.style.display = "block";
  for (const block of blocks) {
    const rect = document.createElementNS(SVG_NS, "rect");
    rect.setAttribute("x", "0");
    rect.setAttribute("y", String(block.top));
    rect.setAttribute("width", String(documentWidth));
    rect.setAttribute("height", String(block.height));
    rect.setAttribute("class", `mm-schematic-${block.kind}`);
    rect.setAttribute("fill", `var(--mm-minimap-${block.kind}, currentColor)`);
    svg.appendChild(rect);
  }
  return svg;
}

// Phase B fires once, after allMathRendered. It schedules a GEOMETRY re-measure of
// the minimap clone — it does NOT re-clone it.
//
// The mounted Phase-A clone is a live render target, not a stale snapshot: it is
// mounted inside the document (loadDocument.ts:171) BEFORE the document-wide math
// and mermaid passes capture their node sets (:191), so those passes render into
// the clone as well as into the live document. Measured over 6 byte-identical runs:
// 2 527 rendered [data-tex] in the live tree and 2 527 in the clone, f_structural
// 0.0000 across 696 blocks. The rebuild this used to schedule therefore replaced a
// correct clone with an equivalent one, at 489.8 ms and 144 840 dirtied layout
// objects, and it destroyed the mermaid the clone had already rendered.
//
// The height-delta staleness gate went with the rebuild. It compared two LIVE
// document heights and never inspected the clone at all, so it was a proxy for a
// staleness that does not occur.
//
// What survives is the clone's own geometry: its math rendered in place, so its
// measured height and clone-space block tops changed, and those caches are
// generation-keyed with no other invalidator on a load-then-idle document. The
// caller owns that re-measure; this seam owns only WHEN it happens. It is
// unconditional because it is cheap — one cached-height write plus one commit.
//
// NO TIMER, idle callback, or deadline. There is no long task left to defer, and
// deferring the re-measure would only move work out of browser idle time into the
// frame right after allMathRendered — backward. Staleness is handled
// deterministically by the caller's document-identity guard inside `refresh`.
export type PhaseBGeometryRefreshDeps = {
  allMathRendered: Promise<void>;
  refresh: (phase: "B") => void;
};

export function schedulePhaseBGeometryRefresh(deps: PhaseBGeometryRefreshDeps): Promise<void> {
  return deps.allMathRendered.then(() => {
    deps.refresh("B");
  });
}
