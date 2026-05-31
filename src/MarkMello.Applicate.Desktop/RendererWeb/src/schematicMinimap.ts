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
    const kind = classify(child);
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

function classify(el: HTMLElement): DocumentBlockKind | null {
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

// Tolerance threshold for Phase B re-clone. With content-visibility:auto on
// document blocks (renderer.css), off-screen math stays at intrinsic size
// until intersected → total scrollHeight is stable between Phase A
// (visible-first math done) and allMathRendered. Small drifts (rounding,
// scrollbar appearance, sub-pixel layout) used to trigger a 70ms+ full
// cloneNode of a 138-formula doc for no visible gain.
//
// 100px = roughly 1 paragraph or 1 math block worth of "real height shift".
// Below this, Phase A's clone is "close enough"; above this, content
// genuinely changed and re-cloning the minimap matters.
const PHASE_B_HEIGHT_DELTA_THRESHOLD_PX = 100;

export function shouldTriggerPhaseB(currentHeight: number, cachedHeight: number): boolean {
  if (cachedHeight <= 0) return false;
  return Math.abs(currentHeight - cachedHeight) >= PHASE_B_HEIGHT_DELTA_THRESHOLD_PX;
}

export type PhaseBRebuildDeps = {
  allMathRendered: Promise<void>;
  getCurrentDocumentHeight: () => number;
  getCachedDocumentHeight: () => number;
  refresh: (phase: "B") => void;
};

export function schedulePhaseBRebuild(deps: PhaseBRebuildDeps): Promise<void> {
  return deps.allMathRendered.then(() => {
    if (!shouldTriggerPhaseB(deps.getCurrentDocumentHeight(), deps.getCachedDocumentHeight())) {
      return;
    }
    // Defer the actual rebuild to browser idle time. Phase B re-clones a
    // 138-formula doc (~47ms long task in Phase A's window). The user has
    // already seen the initial render — they're not waiting for minimap
    // refinement. Running it during idle lets the cold-render budget
    // breathe and keeps the minimap update OUT of any user-visible long
    // task. requestIdleCallback timeout backstop (500ms) ensures it
    // eventually runs even if main thread stays busy.
    const win = window as typeof window & {
      requestIdleCallback?: (cb: () => void, opts?: { timeout: number }) => number;
    };
    return new Promise<void>(resolve => {
      const refresh = () => {
        deps.refresh("B");
        resolve();
      };
      if (typeof win.requestIdleCallback === "function") {
        win.requestIdleCallback(refresh, { timeout: 500 });
      } else {
        window.setTimeout(refresh, 50);
      }
    });
  });
}
