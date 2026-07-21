import { afterEach, describe, expect, it, vi } from "vitest";

// The eager Mermaid batch in `renderMermaidNodes` (renderer.ts) used to carry a
// 15s wall-clock budget (`MERMAID_WATCHDOG_MS`). These tests pin the two
// properties that made it inadmissible, so it cannot come back:
//
//  1. Every eager diagram is rendered. A budget could only ever stop the loop
//     from STARTING the tail -- and the tail is in no other live path, because
//     `installLazyMermaidObserver` receives ONLY the lazy complement of the
//     eager set (renderer.ts, `lazyNodes`). An unstarted eager node is
//     therefore observed by nothing and stays a raw <pre> for the rest of the
//     viewing session.
//  2. The batch arms no timer at all (repo no-timers law), matching the
//     sibling guarantee already pinned for a single render in
//     mermaidRender.test.ts ("arms no timer at all while a render is in flight").
//
// The durations here are not arbitrary: live CDP measurement of mermaid.render
// on real input recorded 3792ms for 250 nodes, 6637ms for 300, and 10517ms for
// a 340-node / 451-edge diagram -- all settling normally. Two ordinary diagrams
// of that size exceed any 15s budget, which is what made the abandonment
// reachable on everyday content rather than on a pathological corner case.

type MermaidApiForTesting = {
  render: (id: string, source: string) => Promise<{ svg: string }>;
};

type LoadedRenderer = {
  renderMermaidNodes: (
    nodes: HTMLElement[],
    mermaid: MermaidApiForTesting,
    perfMarkName?: string
  ) => Promise<void>;
  nodes: HTMLElement[];
  observedNodes: () => Set<Element>;
};

async function loadRendererWithEagerNodes(count: number): Promise<LoadedRenderer> {
  vi.resetModules();
  document.documentElement.innerHTML = `<body><main class="mm-document"></main></body>`;

  vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
    void callback;
    return 0;
  });

  // Track every node handed to an IntersectionObserver, so "nothing will ever
  // render this node" is proven by positive evidence (it is in no observer and
  // in no render call) rather than by an absent grep.
  const observed = new Set<Element>();
  class FakeIntersectionObserver {
    constructor(callback: IntersectionObserverCallback) {
      void callback;
    }
    observe(element: Element): void {
      observed.add(element);
    }
    unobserve(element: Element): void {
      observed.delete(element);
    }
    disconnect(): void {
      observed.clear();
    }
  }
  vi.stubGlobal("IntersectionObserver", FakeIntersectionObserver as unknown as typeof IntersectionObserver);

  (window as unknown as {
    chrome: { webview: { postMessage: (message: unknown) => void } };
  }).chrome = { webview: { postMessage: () => undefined } };

  const rendererModule = await import("../src/renderer") as unknown as {
    renderMermaidNodes: LoadedRenderer["renderMermaidNodes"];
  };

  const main = document.querySelector<HTMLElement>("main.mm-document");
  if (!main) throw new Error("document root missing");

  const nodes: HTMLElement[] = [];
  for (let index = 0; index < count; index++) {
    const pre = document.createElement("pre");
    pre.className = "mm-mermaid";
    const code = document.createElement("code");
    code.className = "language-mermaid";
    code.dataset.mmMermaid = "";
    code.textContent = `graph TD; A${index} --> B${index}`;
    pre.appendChild(code);
    main.appendChild(pre);
    // Pin every node inside the eager band explicitly (MERMAID_EAGER_VIEWPORT_MARGIN_PX
    // is 700px, so the band is viewportHeight + 1400px and comfortably holds several
    // diagrams). Stubbing the rect keeps the eager/lazy split deterministic instead of
    // resting on the DOM implementation's default zero rect.
    pre.getBoundingClientRect = () => ({
      x: 0, y: 0, top: 0, bottom: 100, left: 0, right: 100,
      width: 100, height: 100, toJSON: () => ({})
    }) as DOMRect;
    nodes.push(pre);
  }

  return {
    renderMermaidNodes: rendererModule.renderMermaidNodes,
    nodes,
    observedNodes: () => observed,
  };
}

describe("eager Mermaid batch", () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("renders every eager diagram even when the batch outlives any fixed budget", async () => {
    vi.useFakeTimers();
    const { renderMermaidNodes, nodes, observedNodes } = await loadRendererWithEagerNodes(3);

    // Each diagram costs 10s -- the measured cost of one ordinary 340-node
    // diagram. Three of them run well past any 15s budget.
    const RENDER_COST_MS = 10_000;
    const started: string[] = [];
    const mermaid: MermaidApiForTesting = {
      render: (_id, source) => {
        started.push(source);
        return new Promise(resolve => {
          setTimeout(() => resolve({ svg: `<svg>${source}</svg>` }), RENDER_COST_MS);
        });
      }
    };

    const pending = renderMermaidNodes(nodes, mermaid);
    await vi.advanceTimersByTimeAsync(RENDER_COST_MS * nodes.length + 5_000);
    await pending;

    // Every eager diagram was started and rendered. A budget that stopped the
    // loop early would leave the tail here permanently unrendered.
    expect(started).toHaveLength(nodes.length);
    for (const node of nodes) {
      expect(node.classList.contains("is-rendered")).toBe(true);
    }

    // The invariant behind the rule: no diagram is left both unrendered AND
    // unobserved, i.e. in no live path that could ever render it.
    const stranded = nodes.filter(node =>
      !node.classList.contains("is-rendered") && !observedNodes().has(node));
    expect(stranded).toHaveLength(0);
  });

  it("arms no wall-clock timer for the eager batch", async () => {
    vi.useFakeTimers();
    const { renderMermaidNodes, nodes } = await loadRendererWithEagerNodes(2);

    const finishers: Array<() => void> = [];
    const mermaid: MermaidApiForTesting = {
      render: (_id, source) => new Promise(resolve => {
        finishers.push(() => resolve({ svg: `<svg>${source}</svg>` }));
      })
    };

    const pending = renderMermaidNodes(nodes, mermaid);
    await vi.advanceTimersByTimeAsync(0);

    // No clock may participate in the batch: the renders are gated by their own
    // settle, never by elapsed time. A budget timer would show up here.
    expect(vi.getTimerCount()).toBe(0);

    for (let index = 0; index < nodes.length; index++) {
      finishers[index]?.();
      await vi.advanceTimersByTimeAsync(0);
    }
    await pending;

    expect(vi.getTimerCount()).toBe(0);
    for (const node of nodes) {
      expect(node.classList.contains("is-rendered")).toBe(true);
    }
  });
});
