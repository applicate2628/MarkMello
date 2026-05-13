import { describe, it, expect } from "vitest";
import {
  walkDocumentBlocks,
  renderSchematicSvg,
  type DocumentBlock,
  type DocumentBlockKind,
} from "../src/schematicMinimap";

describe("walkDocumentBlocks", () => {
  it("emits blocks for headings, paragraphs, code, math-display, mermaid, table, list, quote, hr", () => {
    const root = document.createElement("div");
    root.className = "mm-document";
    root.innerHTML = `
      <h1>Title</h1>
      <p>Some paragraph</p>
      <h2>Section</h2>
      <pre><code>code block</code></pre>
      <div class="math-display"></div>
      <pre class="mm-mermaid"></pre>
      <table><tr><td>x</td></tr></table>
      <ul><li>i</li></ul>
      <blockquote>q</blockquote>
      <hr>
    `;
    document.body.appendChild(root);
    const blocks: DocumentBlock[] = walkDocumentBlocks({ documentRoot: root, documentHeight: 1000 });
    const kinds: DocumentBlockKind[] = blocks.map((b: DocumentBlock) => b.kind);
    expect(kinds).toEqual<DocumentBlockKind[]>([
      "heading-1",
      "paragraph",
      "heading-2",
      "code",
      "math-display",
      "mermaid",
      "table",
      "list",
      "quote",
      "hr",
    ]);
    document.body.removeChild(root);
  });

  it("emits empty array for empty .mm-document", () => {
    const root = document.createElement("div");
    root.className = "mm-document";
    document.body.appendChild(root);
    const blocks: DocumentBlock[] = walkDocumentBlocks({
      documentRoot: root,
      documentHeight: 0,
    });
    expect(blocks).toEqual([]);
    document.body.removeChild(root);
  });
});

describe("renderSchematicSvg", () => {
  it("produces SVG in document coordinates with rect per block", () => {
    const blocks: DocumentBlock[] = [
      { kind: "heading-1", top: 0, height: 30 },
      { kind: "paragraph", top: 40, height: 60 },
      { kind: "math-display", top: 110, height: 80 },
    ];
    const svg = renderSchematicSvg(blocks, 800, 200);
    expect(svg.tagName.toLowerCase()).toBe("svg");
    expect(svg.getAttribute("viewBox")).toBe("0 0 800 200");
    expect(svg.getAttribute("preserveAspectRatio")).toBe("none");
    expect(svg.style.width).toBe("800px");
    expect(svg.style.height).toBe("200px");
    const rects = svg.querySelectorAll("rect");
    expect(rects).toHaveLength(3);
    expect(rects[0]!.getAttribute("y")).toBe("0");
    expect(rects[0]!.getAttribute("height")).toBe("30");
  });

  it("empty blocks produces empty SVG (no crash)", () => {
    const svg = renderSchematicSvg([], 800, 0);
    expect(svg.querySelectorAll("rect")).toHaveLength(0);
  });
});
