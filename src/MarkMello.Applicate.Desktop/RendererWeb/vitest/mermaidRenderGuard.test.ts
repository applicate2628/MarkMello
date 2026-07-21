import { describe, it, expect, vi } from "vitest";
import { renderMermaidNode, type MermaidApiLike } from "../src/mermaidRender";

function makeNode(source: string): HTMLElement {
  const pre = document.createElement("pre");
  pre.className = "mm-mermaid";
  const code = document.createElement("code");
  code.className = "language-mermaid";
  code.dataset.mmMermaid = "";
  code.textContent = source;
  pre.appendChild(code);
  document.body.appendChild(pre);
  return pre;
}

/** A classDiagram with `edges` relation lines — the shape that freezes the thread. */
function bigClassDiagram(edges: number): string {
  const s = ["classDiagram"];
  for (let i = 0; i < edges; i++) s.push(`  C${i} --> D${i}`);
  return s.join("\n");
}

describe("renderMermaidNode — pre-render cost guard", () => {
  // FAILS AT HEAD: without the guard, renderMermaidNode calls mermaid.render on this
  // 600-edge classDiagram (which the runtime probe drove into a multi-second thread
  // freeze). After the guard it must be refused BEFORE render is ever invoked.
  it("refuses an over-cap diagram before calling mermaid.render and shows a placeholder", async () => {
    const source = bigClassDiagram(600);
    const node = makeNode(source);
    const onLayoutBoxChange = vi.fn();
    const render = vi.fn(async () => ({ svg: "<svg>SHOULD NOT RUN</svg>" }));
    const api: MermaidApiLike = { render };

    await renderMermaidNode(node, 1, () => 1, api, onLayoutBoxChange);

    // The expensive call never happened.
    expect(render).not.toHaveBeenCalled();

    // The slot carries a transparent, specific placeholder (failure-transparency law).
    const host = node.nextElementSibling as HTMLElement | null;
    expect(host?.classList.contains("mm-mermaid-svg")).toBe(true);
    expect(host?.classList.contains("mm-mermaid-oversize")).toBe(true);
    const note = host?.querySelector(".mm-mermaid-oversize-note");
    expect(note?.textContent).toContain("600"); // the element count
    expect(note?.textContent).toContain("500"); // the limit
    expect(note?.getAttribute("role")).toBe("note");

    // The raw source stays visible/copyable inside the placeholder.
    const shownSource = host?.querySelector(".mm-mermaid-oversize-source code")?.textContent;
    expect(shownSource).toBe(source);

    // Export-safe: no external resource sinks in the placeholder.
    expect(host?.querySelector("img, script, iframe, object, embed")).toBeNull();

    // Marked rendered + one layout-box notification, exactly like a real diagram.
    expect(node.classList.contains("is-rendered")).toBe(true);
    expect(onLayoutBoxChange).toHaveBeenCalledTimes(1);
  });

  it("a refused diagram is is-rendered, so the export/lazy `:not(.is-rendered)` sweeps skip it", async () => {
    const node = makeNode(bigClassDiagram(600));
    const render = vi.fn(async () => ({ svg: "<svg/>" }));
    await renderMermaidNode(node, 1, () => 1, { render });

    expect(document.querySelectorAll("pre.mm-mermaid:not(.is-rendered)").length).toBe(0);
    expect(render).not.toHaveBeenCalled();
    node.remove();
  });

  it("still renders an ordinary diagram normally (no regression)", async () => {
    const node = makeNode("classDiagram\n  Animal <|-- Dog\n  Animal <|-- Cat");
    const onLayoutBoxChange = vi.fn();
    const render = vi.fn(async () => ({ svg: "<svg>OK</svg>" }));

    await renderMermaidNode(node, 1, () => 1, { render }, onLayoutBoxChange);

    expect(render).toHaveBeenCalledTimes(1);
    const host = node.nextElementSibling as HTMLElement | null;
    expect(host?.className).toBe("mm-mermaid-svg"); // plain output slot, not oversize
    expect(host?.innerHTML).toBe("<svg>OK</svg>");
    expect(node.classList.contains("is-rendered")).toBe(true);
    expect(onLayoutBoxChange).toHaveBeenCalledTimes(1);
    node.remove();
  });
});
