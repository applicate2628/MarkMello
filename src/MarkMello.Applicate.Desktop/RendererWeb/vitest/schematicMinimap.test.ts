import { describe, it, expect } from "vitest";
import {
  walkDocumentBlocks,
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
