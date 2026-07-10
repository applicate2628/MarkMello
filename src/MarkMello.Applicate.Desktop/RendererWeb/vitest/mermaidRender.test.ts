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

  it("mermaid terminal lifecycle removes stale or failed proxy without orphaning source", async () => {
    const node = makeNode("graph TD");
    node.style.containIntrinsicSize = "auto 140px";
    const existingProxy = document.createElement("div");
    existingProxy.className = "mm-mermaid-svg";
    node.after(existingProxy);
    node.classList.add("is-rendered");
    let resolveRender!: (value: { svg: string }) => void;
    const staleApi: MermaidApiLike = {
      render: () => new Promise(resolve => { resolveRender = resolve; })
    };
    let currentGeneration = 1;
    const staleRender = renderMermaidNode(
      node,
      1,
      () => currentGeneration,
      staleApi,
      5000,
      { manageVirtualizedProxyLifecycle: true }
    );

    currentGeneration = 2;
    resolveRender!({ svg: "<svg>STALE</svg>" });
    await staleRender;

    expect(node.classList.contains("is-rendered")).toBe(false);
    expect(node.nextElementSibling).toBeNull();
    expect(node.style.containIntrinsicSize).toBe("auto 140px");

    const currentApi: MermaidApiLike = {
      render: async () => { throw new Error("syntax"); }
    };
    const failedProxy = document.createElement("div");
    failedProxy.className = "mm-mermaid-svg";
    node.after(failedProxy);
    node.classList.add("is-rendered");

    await renderMermaidNode(
      node,
      2,
      () => 2,
      currentApi,
      5000,
      { manageVirtualizedProxyLifecycle: true }
    );

    expect(node.classList.contains("is-rendered")).toBe(false);
    expect(node.nextElementSibling).toBeNull();
    expect(node.style.containIntrinsicSize).toBe("auto 140px");
  });

  it("newer lifecycle claim supersedes older completion without overwrite or cleanup", async () => {
    const node = makeNode("graph TD");
    let resolveOlder!: (value: { svg: string }) => void;
    let resolveNewer!: (value: { svg: string }) => void;
    const olderApi: MermaidApiLike = {
      render: () => new Promise(resolve => { resolveOlder = resolve; }),
    };
    const newerApi: MermaidApiLike = {
      render: () => new Promise(resolve => { resolveNewer = resolve; }),
    };
    const older = renderMermaidNode(
      node,
      1,
      () => 1,
      olderApi,
      5000,
      { manageVirtualizedProxyLifecycle: true }
    );
    const newer = renderMermaidNode(
      node,
      1,
      () => 1,
      newerApi,
      5000,
      { manageVirtualizedProxyLifecycle: true }
    );

    resolveNewer({ svg: "<svg>NEW</svg>" });
    await newer;
    const currentProxy = node.nextElementSibling;
    resolveOlder({ svg: "<svg>OLD</svg>" });
    await older;

    expect(node.classList.contains("is-rendered")).toBe(true);
    expect(node.nextElementSibling).toBe(currentProxy);
    expect(currentProxy?.innerHTML).toBe("<svg>NEW</svg>");

    let rejectSuperseded!: (reason?: unknown) => void;
    const supersededFailureApi: MermaidApiLike = {
      render: () => new Promise((_resolve, reject) => { rejectSuperseded = reject; }),
    };
    const supersededFailure = renderMermaidNode(
      node,
      1,
      () => 1,
      supersededFailureApi,
      5000,
      { manageVirtualizedProxyLifecycle: true }
    );
    const latest = renderMermaidNode(
      node,
      1,
      () => 1,
      { render: async () => ({ svg: "<svg>LATEST</svg>" }) },
      5000,
      { manageVirtualizedProxyLifecycle: true }
    );
    await latest;
    rejectSuperseded(new Error("superseded"));
    await supersededFailure;

    expect(node.classList.contains("is-rendered")).toBe(true);
    expect(node.nextElementSibling?.innerHTML).toBe("<svg>LATEST</svg>");
  });

  it("detached lifecycle completion cannot create or retain a proxy", async () => {
    const node = makeNode("graph TD");
    let resolveRender!: (value: { svg: string }) => void;
    const api: MermaidApiLike = {
      render: () => new Promise(resolve => { resolveRender = resolve; }),
    };
    const completion = renderMermaidNode(
      node,
      1,
      () => 1,
      api,
      5000,
      { manageVirtualizedProxyLifecycle: true }
    );

    node.remove();
    resolveRender({ svg: "<svg>DETACHED</svg>" });
    await completion;

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
