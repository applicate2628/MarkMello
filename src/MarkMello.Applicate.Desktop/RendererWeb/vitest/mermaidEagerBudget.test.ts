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

// G-M1 fixture. A mixed sweep: the live document and the minimap clone both hold the
// same two diagrams, because the mermaid sweeps are document-wide and the clone is
// mounted inside the document before they run. Only the LIVE nodes may decide.
type SurfaceFixture = {
  renderMermaidNodes: LoadedRenderer["renderMermaidNodes"];
  liveEager: HTMLElement;
  liveLazy: HTMLElement;
  followers: HTMLElement[];
  sweptNodes: HTMLElement[];
  observedNodes: () => Set<Element>;
  followerRectReads: () => number;
  perfMarks: () => Array<{ name: string; detail: Record<string, unknown> }>;
};

function stubRect(node: HTMLElement, top: number, bottom: number, onRead?: () => void): void {
  node.getBoundingClientRect = () => {
    onRead?.();
    return {
      x: 0, y: top, top, bottom, left: 0, right: 100,
      width: 100, height: bottom - top, toJSON: () => ({})
    } as DOMRect;
  };
}

function mermaidPre(blockIndex: string, source: string): HTMLElement {
  const pre = document.createElement("pre");
  pre.className = "mm-mermaid";
  pre.dataset.mmBlockIndex = blockIndex;
  const code = document.createElement("code");
  code.className = "language-mermaid";
  code.dataset.mmMermaid = "";
  code.textContent = source;
  pre.appendChild(code);
  return pre;
}

async function loadRendererWithLiveAndCloneSurfaces(): Promise<SurfaceFixture> {
  vi.resetModules();
  document.documentElement.innerHTML = `<body><main class="mm-document"></main></body>`;

  vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
    void callback;
    return 0;
  });

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

  const marks: Array<{ name: string; detail: Record<string, unknown> }> = [];
  (window as unknown as {
    chrome: { webview: { postMessage: (message: unknown) => void } };
  }).chrome = {
    webview: {
      postMessage: (message: unknown) => {
        const posted = message as { type?: string; name?: string; detail?: string };
        if (posted?.type !== "perf-mark" || posted.name === undefined) return;
        marks.push({
          name: posted.name,
          detail: posted.detail === undefined ? {} : JSON.parse(posted.detail) as Record<string, unknown>
        });
      }
    }
  };

  const rendererModule = await import("../src/renderer") as unknown as {
    renderMermaidNodes: LoadedRenderer["renderMermaidNodes"];
    __testSetMinimapCloneBlockElementsForTesting: (
      clone: HTMLElement,
      elements: readonly HTMLElement[]
    ) => void;
  };

  const main = document.querySelector<HTMLElement>("main.mm-document");
  if (!main) throw new Error("document root missing");

  const liveEager = mermaidPre("2", "graph TD; A0 --> B0");
  const liveLazy = mermaidPre("7", "graph TD; A1 --> B1");
  main.append(liveEager, liveLazy);
  stubRect(liveEager, 0, 100);
  // Far past viewportHeight + MERMAID_EAGER_VIEWPORT_MARGIN_PX for happy-dom's 0-height
  // viewport, so this one is unambiguously lazy.
  stubRect(liveLazy, 90_000, 90_100);

  const minimap = document.createElement("aside");
  minimap.className = "mm-minimap";
  const followerRoot = document.createElement("div");
  followerRoot.className = "mm-minimap-content";
  minimap.appendChild(followerRoot);
  document.body.appendChild(minimap);

  let followerRectReads = 0;
  const followers = [mermaidPre("2", "graph TD; A0 --> B0"), mermaidPre("7", "graph TD; A1 --> B1")];
  for (const follower of followers) {
    followerRoot.appendChild(follower);
    // Deliberately OUT of the eager band: if a follower ever reached the predicate it
    // would be classified lazy and land in the observer, which is exactly the failure
    // this fixture is built to make visible.
    stubRect(follower, 90_000, 90_100, () => { followerRectReads++; });
  }

  rendererModule.__testSetMinimapCloneBlockElementsForTesting(followerRoot, []);

  return {
    renderMermaidNodes: rendererModule.renderMermaidNodes,
    liveEager,
    liveLazy,
    followers,
    // Document order, as every real sweep produces it: live nodes first, clone after.
    sweptNodes: [liveEager, liveLazy, ...followers],
    observedNodes: () => observed,
    followerRectReads: () => followerRectReads,
    perfMarks: () => marks
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

// G-M1. This replaces the source-text assertion that used to live in
// handleHostMessageLoadDocument.test.ts and pin the literal
// `installLazyMermaidObserver(lazyNodes, generation, mermaid);`. That literal was the ONLY
// thing pinning "every lazy node reaches an observer", and it says nothing about WHICH
// nodes `lazyNodes` holds. The assertions below are behavioural and were proven to go red
// on a build where the surface partition is removed: the two clone nodes then appear in
// observedNodes(), and their rects are read.
describe("one render decision per diagram", () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("observes exactly the live lazy complement — no follower observed, no live lazy node unobserved", async () => {
    const fixture = await loadRendererWithLiveAndCloneSurfaces();
    const mermaid: MermaidApiForTesting = {
      render: async (id) => ({ svg: `<svg id="${id}"><g/></svg>` })
    };

    await fixture.renderMermaidNodes(fixture.sweptNodes, mermaid);

    expect(Array.from(fixture.observedNodes())).toEqual([fixture.liveLazy]);
    for (const follower of fixture.followers) {
      expect(fixture.observedNodes().has(follower)).toBe(false);
    }
    // No diagram is left both unrendered AND unobserved on the deciding surface.
    const stranded = [fixture.liveEager, fixture.liveLazy].filter(node =>
      !node.classList.contains("is-rendered") && !fixture.observedNodes().has(node));
    expect(stranded).toHaveLength(0);
  });

  it("never measures a follower: getBoundingClientRect is not called on a clone node", async () => {
    const fixture = await loadRendererWithLiveAndCloneSurfaces();
    const mermaid: MermaidApiForTesting = {
      render: async (id) => ({ svg: `<svg id="${id}"><g/></svg>` })
    };

    await fixture.renderMermaidNodes(fixture.sweptNodes, mermaid);

    expect(fixture.followerRectReads()).toBe(0);
  });

  it("calls mermaid.render once per DIAGRAM, and the clone twin receives the result", async () => {
    const fixture = await loadRendererWithLiveAndCloneSurfaces();
    const renderedSources: string[] = [];
    const mermaid: MermaidApiForTesting = {
      render: async (id, source) => {
        renderedSources.push(source);
        return { svg: `<svg id="${id}"><g/></svg>` };
      }
    };

    await fixture.renderMermaidNodes(fixture.sweptNodes, mermaid);

    // Four swept nodes, two diagrams, one eager decision: one render, not two.
    expect(renderedSources).toEqual(["graph TD; A0 --> B0"]);
    expect(fixture.liveEager.classList.contains("is-rendered")).toBe(true);
    const eagerTwin = fixture.followers[0]!;
    expect(eagerTwin.classList.contains("is-rendered")).toBe(true);
    expect(eagerTwin.nextElementSibling?.className).toBe("mm-mermaid-svg");
    // The lazy diagram rendered on neither surface, so both stay raw source together.
    expect(fixture.liveLazy.classList.contains("is-rendered")).toBe(false);
    expect(fixture.followers[1]!.classList.contains("is-rendered")).toBe(false);
  });

  it("reports decisions and swept nodes as separate fields on mm-mermaid-visible-first", async () => {
    const fixture = await loadRendererWithLiveAndCloneSurfaces();
    const mermaid: MermaidApiForTesting = {
      render: async (id) => ({ svg: `<svg id="${id}"><g/></svg>` })
    };

    await fixture.renderMermaidNodes(fixture.sweptNodes, mermaid);

    const mark = fixture.perfMarks().find(entry => entry.name === "mm-mermaid-visible-first");
    expect(mark).toBeDefined();
    // total/eager/lazy count DECISIONS; `swept` keeps the pre-change quantity under a name
    // that still means the same thing, so a cross-arm gate cannot compare the wrong number.
    expect(mark!.detail).toEqual({ total: 2, eager: 1, lazy: 1, swept: 4, follower: 2, unscoped: 0 });
  });
});
