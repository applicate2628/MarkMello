import { describe, it, expect } from "vitest";
import { isMermaidNodeNearViewport, renderMermaidNode, type MermaidApiLike } from "../src/mermaidRender";

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

describe("renderMermaidNode", () => {
  it("on success adds is-rendered class and sibling .mm-mermaid-svg", async () => {
    const node = makeNode("graph TD");
    const api: MermaidApiLike = {
      render: async () => ({ svg: "<svg>OK</svg>" })
    };
    await renderMermaidNode(node, 1, () => 1, api, 1000);

    expect(node.classList.contains("is-rendered")).toBe(true);
    expect(node.nextElementSibling?.className).toBe("mm-mermaid-svg");
    expect(node.nextElementSibling?.innerHTML).toBe("<svg>OK</svg>");
  });

  it("on syntax error leaves pre/code visible without svg sibling", async () => {
    const node = makeNode("bad syntax");
    const api: MermaidApiLike = {
      render: async () => { throw new Error("syntax"); }
    };
    await renderMermaidNode(node, 1, () => 1, api, 1000);

    expect(node.classList.contains("is-rendered")).toBe(false);
    expect(node.nextElementSibling).toBeNull();
  });

  it("on timeout leaves pre/code visible", async () => {
    const node = makeNode("hangs");
    const api: MermaidApiLike = {
      render: () => new Promise(() => { /* never resolves */ })
    };
    await renderMermaidNode(node, 1, () => 1, api, 50);

    expect(node.classList.contains("is-rendered")).toBe(false);
    expect(node.nextElementSibling).toBeNull();
  });

  it("stale generation does not mutate DOM after late resolve", async () => {
    const node = makeNode("graph TD");
    let resolveRender!: (v: { svg: string }) => void;
    const api: MermaidApiLike = {
      render: () => new Promise((resolve) => { resolveRender = resolve; })
    };
    let currentGen = 1;
    const promise = renderMermaidNode(node, 1, () => currentGen, api, 5000);

    currentGen = 2;
    resolveRender!({ svg: "<svg>STALE</svg>" });
    await promise;

    expect(node.classList.contains("is-rendered")).toBe(false);
    expect(node.nextElementSibling).toBeNull();
  });
});

describe("isMermaidNodeNearViewport", () => {
  function makeMeasuredNode(top: number, bottom: number): HTMLElement {
    const node = document.createElement("pre");
    node.getBoundingClientRect = () => ({
      x: 0,
      y: top,
      top,
      bottom,
      left: 0,
      right: 100,
      width: 100,
      height: bottom - top,
      toJSON: () => ({})
    } as DOMRect);
    return node;
  }

  it("treats visible and near-viewport diagrams as eager", () => {
    expect(isMermaidNodeNearViewport(makeMeasuredNode(100, 220), 800, 200)).toBe(true);
    expect(isMermaidNodeNearViewport(makeMeasuredNode(900, 1020), 800, 200)).toBe(true);
    expect(isMermaidNodeNearViewport(makeMeasuredNode(-180, -20), 800, 200)).toBe(true);
  });

  it("keeps distant offscreen diagrams lazy", () => {
    expect(isMermaidNodeNearViewport(makeMeasuredNode(1101, 1220), 800, 200)).toBe(false);
    expect(isMermaidNodeNearViewport(makeMeasuredNode(-421, -301), 800, 200)).toBe(false);
  });
});
