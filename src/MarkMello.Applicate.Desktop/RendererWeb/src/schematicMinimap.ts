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
