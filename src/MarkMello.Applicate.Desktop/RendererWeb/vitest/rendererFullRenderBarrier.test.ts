import { afterEach, describe, expect, it, vi } from "vitest";
import { readFileSync } from "node:fs";

type HostBridge = (message: unknown) => void;
type RendererMessage = { type?: string; requestId?: string; mermaidErrorCount?: number; reason?: string };

type FakeObserverRecord = {
  elements: Set<Element>;
  callback: IntersectionObserverCallback;
};

type MermaidApiForTesting = {
  initialize?: (config: unknown) => void;
  render: (id: string, source: string) => Promise<{ svg: string }>;
};

type MermaidLifecycleSnapshot = {
  documentIdentity: number;
  owner: "normal" | "barrier" | "terminal";
  retainedErrorCount: number | null;
  activeRenderCount: number;
  generation: number;
  observerPresent: boolean;
  cacheResumeScheduled: boolean;
  themeRefreshScheduled: boolean;
};

type RendererInternals = {
  initMermaidWithTheme: (theme: "light" | "dark" | "classic-white") => void;
  renderMermaid: () => Promise<void>;
  renderMermaidNodes: (nodes: HTMLElement[], mermaid: MermaidApiForTesting, perfMarkName?: string) => Promise<void>;
  scheduleCachedMermaidResume: (hasMermaid?: boolean) => void;
  scheduleThemeMermaidRefresh: (theme: "light" | "dark" | "classic-white") => void;
  installLazyMermaidObserver: (nodes: HTMLElement[], generation: number, mermaid: MermaidApiForTesting) => void;
  enqueueLazyMermaidRender: (node: HTMLElement, generation: number, mermaid: MermaidApiForTesting) => void;
  handlePrepareForExport: (message: { type: "prepare-for-export"; requestId: string }) => Promise<void>;
  getMermaidLifecycleSnapshotForTesting: () => MermaidLifecycleSnapshot;
  setFullRenderBarrierFailureForTesting: (reason: unknown | null) => void;
};

async function loadRenderer(body = '<main class="mm-document"></main>') {
  vi.resetModules();
  document.documentElement.innerHTML = `<body>${body}</body>`;

  const animationFrames: FrameRequestCallback[] = [];
  vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
    animationFrames.push(callback);
    return animationFrames.length;
  });

  const idleCallbacks: Array<() => void> = [];
  vi.stubGlobal("requestIdleCallback", (callback: () => void) => {
    idleCallbacks.push(callback);
    return idleCallbacks.length;
  });

  const observers: FakeObserverRecord[] = [];
  class FakeIntersectionObserver {
    private readonly record = { elements: new Set<Element>() } as FakeObserverRecord;

    constructor(callback: IntersectionObserverCallback) {
      this.record.callback = callback;
      observers.push(this.record);
    }

    observe(element: Element): void {
      this.record.elements.add(element);
    }

    unobserve(element: Element): void {
      this.record.elements.delete(element);
    }

    disconnect(): void {
      this.record.elements.clear();
    }
  }
  vi.stubGlobal("IntersectionObserver", FakeIntersectionObserver as unknown as typeof IntersectionObserver);

  const messages: RendererMessage[] = [];
  (window as unknown as {
    chrome: { webview: { postMessage: (message: RendererMessage) => void } };
  }).chrome = {
    webview: { postMessage: (message) => messages.push(message) },
  };

  const rendererModule = await import("../src/renderer");
  const send = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;

  async function drainAnimationFrames(maxFrames = 200): Promise<void> {
    let idleTurns = 0;
    for (let index = 0; index < maxFrames; index++) {
      await Promise.resolve();
      const callback = animationFrames.shift();
      if (!callback) {
        idleTurns++;
        if (idleTurns >= 20) return;
        continue;
      }
      idleTurns = 0;
      callback(index * 16);
    }
    throw new Error("renderer animation-frame queue did not settle");
  }

  return { drainAnimationFrames, idleCallbacks, messages, observers, rendererModule, send };
}

function requireRendererInternals(rendererModule: object): RendererInternals {
  const candidate = rendererModule as Partial<RendererInternals>;
  const names: Array<keyof RendererInternals> = [
    "initMermaidWithTheme",
    "renderMermaid",
    "renderMermaidNodes",
    "scheduleCachedMermaidResume",
    "scheduleThemeMermaidRefresh",
    "installLazyMermaidObserver",
    "enqueueLazyMermaidRender",
    "handlePrepareForExport",
    "getMermaidLifecycleSnapshotForTesting",
    "setFullRenderBarrierFailureForTesting",
  ];
  for (const name of names) {
    expect(candidate[name], `actual production export ${name}`).toBeTypeOf("function");
  }
  return candidate as RendererInternals;
}

function createGate<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(next => {
    resolve = next;
  });
  return { promise, resolve };
}

async function flushMicrotasks(turns = 20): Promise<void> {
  for (let index = 0; index < turns; index++) {
    await Promise.resolve();
  }
}

afterEach(() => {
  delete (window as unknown as { chrome?: unknown }).chrome;
  delete (window as unknown as { katex?: unknown }).katex;
  delete (window as unknown as { mermaid?: unknown }).mermaid;
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("prepare-for-export full-render barrier", () => {
  it("waits for append-final, warms every block, and drains appended math before completion", async () => {
    const katexRender = vi.fn((_tex: string, node: Element) => {
      node.textContent = "rendered math";
    });
    (window as unknown as { katex: { render: typeof katexRender } }).katex = { render: katexRender };
    const { drainAnimationFrames, idleCallbacks, messages, send } = await loadRenderer();

    send({
      type: "load-document",
      html: "<h1>Intro</h1>",
      renderId: 41,
      cacheKey: null,
      hasMermaid: false,
      hasHljs: false,
    });
    send({ type: "prepare-for-export", requestId: "export-41" });
    await drainAnimationFrames();

    expect(messages.some(message => message.type === "full-render-complete")).toBe(false);
    expect(messages.some(message => message.type === "full-render-failed")).toBe(false);

    const ordinaryBlocks = Array.from({ length: 65 }, (_, index) => `<p>block ${index}</p>`).join("");
    send({
      type: "append-document",
      html: `${ordinaryBlocks}<p data-tex="x^2"></p>`,
      renderId: 41,
      isFinal: true,
      cacheKey: "full-cache",
      hasMermaid: false,
      hasHljs: false,
    });
    await drainAnimationFrames();

    expect(idleCallbacks.length).toBeGreaterThan(0);
    expect(document.querySelectorAll("main.mm-document > *:not(.mm-warmed)")).toHaveLength(0);
    expect(document.querySelector<HTMLElement>("[data-tex]")?.dataset.mmMathRendered).toBe("true");
    expect(katexRender).toHaveBeenCalled();
    expect(messages).toContainEqual({
      type: "full-render-complete",
      requestId: "export-41",
      mermaidErrorCount: 0,
    });
  });

  it("ignores stale append-final messages and accepts an empty matching final append", async () => {
    const { drainAnimationFrames, messages, rendererModule, send } = await loadRenderer();

    send({
      type: "load-document",
      html: "<p>progressive start</p>",
      renderId: 61,
      cacheKey: null,
      hasMermaid: false,
      hasHljs: false,
    });
    send({ type: "prepare-for-export", requestId: "export-61" });
    send({
      type: "append-document",
      html: "",
      renderId: 60,
      isFinal: true,
      cacheKey: "stale-cache",
      hasMermaid: false,
      hasHljs: false,
    });
    await drainAnimationFrames();

    expect(messages.some(message => message.type === "full-render-complete")).toBe(false);
    expect(messages.some(message => message.type === "full-render-failed")).toBe(false);

    send({
      type: "append-document",
      html: "",
      renderId: 61,
      isFinal: true,
      cacheKey: "matching-cache",
      hasMermaid: false,
      hasHljs: false,
    });
    await drainAnimationFrames();

    expect(messages).toContainEqual({
      type: "full-render-complete",
      requestId: "export-61",
      mermaidErrorCount: 0,
    });
  });

  it("renders only pending Mermaid nodes directly and counts per-node failures", async () => {
    const mermaidRender = vi.fn(async (_id: string, source: string) => {
      if (source === "bad") throw new Error("bad diagram");
      return { svg: `<svg>${source}</svg>` };
    });
    (window as unknown as {
      mermaid: { initialize: (config: unknown) => void; render: typeof mermaidRender };
    }).mermaid = { initialize: vi.fn(), render: mermaidRender };

    const { drainAnimationFrames, messages, observers, send } = await loadRenderer();
    send({
      type: "load-document",
      html: [
        '<pre class="mm-mermaid is-rendered"><code data-mm-mermaid>already</code></pre>',
        '<pre class="mm-mermaid"><code data-mm-mermaid>good</code></pre>',
        '<pre class="mm-mermaid"><code data-mm-mermaid>bad</code></pre>',
      ].join(""),
      renderId: 52,
      hasMermaid: false,
      hasHljs: false,
    });
    await drainAnimationFrames();

    send({ type: "prepare-for-export", requestId: "export-52" });
    await drainAnimationFrames();

    expect(mermaidRender.mock.calls.map(call => call[1])).toEqual(["good", "bad"]);
    expect(messages).toContainEqual({
      type: "full-render-complete",
      requestId: "export-52",
      mermaidErrorCount: 1,
    });
    const alreadyRendered = document.querySelectorAll<HTMLElement>("pre.mm-mermaid")[0]!;
    expect(observers.every(observer => !observer.elements.has(alreadyRendered))).toBe(true);
  });

  it("exports a slow diagram that outlives every former deadline while still counting a broken one", async () => {
    vi.useFakeTimers();
    try {
      let finishSlowRender!: (value: { svg: string }) => void;
      const mermaidRender = vi.fn(async (_id: string, source: string) => {
        if (source === "bad") throw new Error("bad diagram");
        return await new Promise<{ svg: string }>(resolve => { finishSlowRender = resolve; });
      });
      (window as unknown as {
        mermaid: { initialize: (config: unknown) => void; render: typeof mermaidRender };
      }).mermaid = { initialize: vi.fn(), render: mermaidRender };

      const { drainAnimationFrames, messages, send } = await loadRenderer();
      send({
        type: "load-document",
        html: [
          '<pre class="mm-mermaid"><code data-mm-mermaid>slow</code></pre>',
          '<pre class="mm-mermaid"><code data-mm-mermaid>bad</code></pre>',
        ].join(""),
        renderId: 53,
        hasMermaid: false,
        hasHljs: false,
      });
      await drainAnimationFrames();

      send({ type: "prepare-for-export", requestId: "export-53" });
      await drainAnimationFrames();

      // The slow diagram is mid-render. Push the clock far past the 3000 ms budget
      // this path used to race against - and past any "more generous" number a
      // future session might be tempted to reach for. A clock must not be able to
      // decide the export's outcome, so the barrier still owes an answer here.
      await vi.advanceTimersByTimeAsync(120_000);
      await drainAnimationFrames();
      expect(messages.some(message => message.requestId === "export-53")).toBe(false);
      expect(mermaidRender.mock.calls.map(call => call[1])).toEqual(["slow"]);

      finishSlowRender({ svg: "<svg>slow</svg>" });
      await drainAnimationFrames();

      // Forward direction: the slow-but-valid diagram exported instead of being
      // rejected. Opposite direction: the genuinely broken one is still an error,
      // so the host still refuses a document that truly did not render.
      expect(mermaidRender.mock.calls.map(call => call[1])).toEqual(["slow", "bad"]);
      expect(messages).toContainEqual({
        type: "full-render-complete",
        requestId: "export-53",
        mermaidErrorCount: 1,
      });
      const nodes = document.querySelectorAll<HTMLElement>("pre.mm-mermaid");
      expect(nodes[0]!.classList.contains("is-rendered")).toBe(true);
      expect(nodes[0]!.nextElementSibling?.innerHTML).toBe("<svg>slow</svg>");
      expect(nodes[1]!.classList.contains("is-rendered")).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  it("posts a correlated failure when the barrier driver cannot find the document root", async () => {
    const { drainAnimationFrames, messages, send } = await loadRenderer("");

    send({ type: "prepare-for-export", requestId: "export-fatal" });
    await drainAnimationFrames();

    expect(messages).toContainEqual({
      type: "full-render-failed",
      requestId: "export-fatal",
      reason: expect.stringContaining("document root"),
    });
    expect(messages.some(message => message.type === "full-render-complete")).toBe(false);
  });

  it("keeps the barrier event-driven and the processed cache progressively reloadable", () => {
    const source = readFileSync("RendererWeb/src/renderer.ts", "utf8");
    const barrierStart = source.indexOf("async function driveFullRenderBarrier(");
    const barrierEnd = source.indexOf("async function handlePrepareForExport(", barrierStart);
    const barrier = source.slice(barrierStart, barrierEnd);
    const cacheStart = source.indexOf("function preserveCurrentProcessedDocument()");
    const cacheEnd = source.indexOf("function applyViewerChromeState", cacheStart);
    const cacheOwner = source.slice(cacheStart, cacheEnd);

    expect(barrierStart).toBeGreaterThanOrEqual(0);
    expect(barrierEnd).toBeGreaterThan(barrierStart);
    expect(barrier).toContain("await waitForProgressiveAppendFinal()");
    expect(barrier).toContain("await waitForDocumentWarmup()");
    expect(barrier).toContain("await trackMermaidRenderCall(");
    expect(barrier).not.toContain("setTimeout");
    expect(barrier).not.toContain("setInterval");
    expect(cacheOwner).toContain('node.classList.remove("mm-warmed")');

    // Asserting on the barrier's own body alone is what let a timer sit on this
    // path unnoticed: the barrier called trackMermaidRenderCall, which handed a
    // 3000 ms budget to renderMermaidNode, which raced it against the render. Walk
    // the callees the barrier actually reaches, not just its own text.
    const trackerStart = source.indexOf("function trackMermaidRenderCall(");
    const trackerEnd = source.indexOf("async function drainActiveMermaidRenderCalls(", trackerStart);
    const tracker = source.slice(trackerStart, trackerEnd);
    expect(trackerStart).toBeGreaterThanOrEqual(0);
    expect(trackerEnd).toBeGreaterThan(trackerStart);
    expect(tracker).toContain("renderMermaidNode(");
    expect(tracker).not.toContain("setTimeout");
    expect(tracker).not.toContain("TIMEOUT");

    const mermaidHelper = readFileSync("RendererWeb/src/mermaidRender.ts", "utf8");
    expect(mermaidHelper).toContain("await mermaid.render(id, source)");
    expect(mermaidHelper).not.toContain("setTimeout");
    expect(mermaidHelper).not.toContain("setInterval");
    expect(mermaidHelper).not.toContain("Promise.race");
  });

  it("MermaidSequentialPrepareReplay retains the terminal count and starts zero new work", async () => {
    const mermaidRender = vi.fn(async (_id: string, source: string) => {
      if (source === "bad") throw new Error("bad diagram");
      return { svg: `<svg>${source}</svg>` };
    });
    const initialize = vi.fn();
    (window as unknown as { mermaid: MermaidApiForTesting }).mermaid = { initialize, render: mermaidRender };
    const { drainAnimationFrames, messages, send } = await loadRenderer();

    send({
      type: "load-document",
      html: '<pre class="mm-mermaid"><code data-mm-mermaid>bad</code></pre>',
      renderId: 71,
      hasMermaid: false,
      hasHljs: false,
    });
    await drainAnimationFrames();
    send({ type: "prepare-for-export", requestId: "sequential-1" });
    await drainAnimationFrames();
    const callsAfterTerminal = mermaidRender.mock.calls.length;
    send({ type: "prepare-for-export", requestId: "sequential-2" });
    await drainAnimationFrames();

    expect(messages).toContainEqual({
      type: "full-render-complete",
      requestId: "sequential-1",
      mermaidErrorCount: 1,
    });
    expect(messages).toContainEqual({
      type: "full-render-complete",
      requestId: "sequential-2",
      mermaidErrorCount: 1,
    });
    expect(mermaidRender).toHaveBeenCalledTimes(callsAfterTerminal);
    expect(initialize).not.toHaveBeenCalled();
  });

  it("MermaidMissingApiReplay retains the count and terminally suppresses later API work", async () => {
    const { drainAnimationFrames, messages, rendererModule, send } = await loadRenderer();
    send({
      type: "load-document",
      html: [
        '<pre class="mm-mermaid"><code data-mm-mermaid>one</code></pre>',
        '<pre class="mm-mermaid"><code data-mm-mermaid>two</code></pre>',
      ].join(""),
      renderId: 72,
      hasMermaid: false,
      hasHljs: false,
    });
    await drainAnimationFrames();
    send({ type: "prepare-for-export", requestId: "missing-api-1" });
    await drainAnimationFrames();
    send({ type: "prepare-for-export", requestId: "missing-api-2" });
    await drainAnimationFrames();

    const initialize = vi.fn();
    const render = vi.fn(async () => ({ svg: "<svg></svg>" }));
    (window as unknown as { mermaid: MermaidApiForTesting }).mermaid = { initialize, render };
    const internals = requireRendererInternals(rendererModule);
    internals.initMermaidWithTheme("dark");
    await flushMicrotasks();

    expect(messages).toContainEqual({
      type: "full-render-complete",
      requestId: "missing-api-1",
      mermaidErrorCount: 2,
    });
    expect(messages).toContainEqual({
      type: "full-render-complete",
      requestId: "missing-api-2",
      mermaidErrorCount: 2,
    });
    expect(initialize).not.toHaveBeenCalled();
    expect(render).not.toHaveBeenCalled();
  });

  it("MermaidBarrierOwnsEveryApiStarter and drains an already active render", async () => {
    const firstRender = createGate<{ svg: string }>();
    const initialize = vi.fn();
    const render = vi.fn()
      .mockImplementationOnce(() => firstRender.promise)
      .mockResolvedValue({ svg: "<svg>barrier</svg>" });
    const mermaid = { initialize, render } satisfies MermaidApiForTesting;
    (window as unknown as { mermaid: MermaidApiForTesting }).mermaid = mermaid;
    const { drainAnimationFrames, messages, observers, rendererModule } = await loadRenderer(
      '<main class="mm-document"><pre class="mm-mermaid"><code data-mm-mermaid>active</code></pre></main>'
    );
    const internals = requireRendererInternals(rendererModule);
    const node = document.querySelector<HTMLElement>("pre.mm-mermaid")!;
    const normalRender = internals.renderMermaidNodes([node], mermaid);
    await flushMicrotasks();
    expect(render).toHaveBeenCalledTimes(1);

    const lazy = document.createElement("pre");
    lazy.className = "mm-mermaid";
    lazy.innerHTML = "<code data-mm-mermaid>lazy</code>";
    document.querySelector("main.mm-document")!.append(lazy);
    const normalSnapshot = internals.getMermaidLifecycleSnapshotForTesting();
    internals.installLazyMermaidObserver([lazy], normalSnapshot.generation, mermaid);
    const staleObserver = observers.at(-1)!;

    const barrier = internals.handlePrepareForExport({ type: "prepare-for-export", requestId: "active-drain" });
    await flushMicrotasks();
    const barrierSnapshot = internals.getMermaidLifecycleSnapshotForTesting();
    expect(barrierSnapshot.owner).toBe("barrier");
    expect(barrierSnapshot.activeRenderCount).toBe(1);
    expect(messages.some(message => message.requestId === "active-drain")).toBe(false);

    const callsBeforeSuppressedStarters = render.mock.calls.length;
    const generationBeforeSuppressedStarters = barrierSnapshot.generation;
    internals.initMermaidWithTheme("dark");
    void internals.renderMermaid();
    void internals.renderMermaidNodes([lazy], mermaid);
    internals.scheduleCachedMermaidResume(true);
    internals.scheduleThemeMermaidRefresh("dark");
    internals.installLazyMermaidObserver([lazy], generationBeforeSuppressedStarters, mermaid);
    internals.enqueueLazyMermaidRender(lazy, generationBeforeSuppressedStarters, mermaid);
    staleObserver.callback([
      { isIntersecting: true, target: lazy } as Pick<IntersectionObserverEntry, "target" | "isIntersecting"> as IntersectionObserverEntry,
    ], {} as IntersectionObserver);
    await flushMicrotasks();
    const suppressedSnapshot = internals.getMermaidLifecycleSnapshotForTesting();
    expect(initialize).not.toHaveBeenCalled();
    expect(render).toHaveBeenCalledTimes(callsBeforeSuppressedStarters);
    expect(suppressedSnapshot.generation).toBe(generationBeforeSuppressedStarters);
    expect(suppressedSnapshot.observerPresent).toBe(false);
    expect(suppressedSnapshot.cacheResumeScheduled).toBe(false);
    expect(suppressedSnapshot.themeRefreshScheduled).toBe(false);

    firstRender.resolve({ svg: "<svg>stale</svg>" });
    await normalRender;
    await drainAnimationFrames();
    await barrier;
    const terminalSnapshot = internals.getMermaidLifecycleSnapshotForTesting();
    expect(terminalSnapshot.owner).toBe("terminal");
    expect(terminalSnapshot.activeRenderCount).toBe(0);
    expect(messages).toContainEqual({
      type: "full-render-complete",
      requestId: "active-drain",
      mermaidErrorCount: 0,
    });

    const terminalCalls = render.mock.calls.length;
    internals.initMermaidWithTheme("light");
    void internals.renderMermaid();
    void internals.renderMermaidNodes([lazy], mermaid);
    internals.scheduleCachedMermaidResume(true);
    internals.scheduleThemeMermaidRefresh("light");
    internals.installLazyMermaidObserver([lazy], terminalSnapshot.generation, mermaid);
    internals.enqueueLazyMermaidRender(lazy, terminalSnapshot.generation, mermaid);
    staleObserver.callback([
      { isIntersecting: true, target: lazy } as Pick<IntersectionObserverEntry, "target" | "isIntersecting"> as IntersectionObserverEntry,
    ], {} as IntersectionObserver);
    await flushMicrotasks();
    expect(initialize).not.toHaveBeenCalled();
    expect(render).toHaveBeenCalledTimes(terminalCalls);
    expect(internals.getMermaidLifecycleSnapshotForTesting()).toEqual(terminalSnapshot);
  });

  it("MermaidFailureRecoverySameIdentity drains before restoring exactly one owner", async () => {
    const firstRender = createGate<{ svg: string }>();
    const render = vi.fn()
      .mockImplementationOnce(() => firstRender.promise)
      .mockResolvedValue({ svg: "<svg>retry</svg>" });
    const mermaid = { initialize: vi.fn(), render } satisfies MermaidApiForTesting;
    (window as unknown as { mermaid: MermaidApiForTesting }).mermaid = mermaid;
    const { drainAnimationFrames, messages, observers, rendererModule } = await loadRenderer(
      '<main class="mm-document"><pre class="mm-mermaid"><code data-mm-mermaid>recover</code></pre></main>'
    );
    const internals = requireRendererInternals(rendererModule);
    const node = document.querySelector<HTMLElement>("pre.mm-mermaid")!;
    const normalRender = internals.renderMermaidNodes([node], mermaid);
    await flushMicrotasks();
    internals.setFullRenderBarrierFailureForTesting(new Error("injected barrier failure"));
    const barrier = internals.handlePrepareForExport({ type: "prepare-for-export", requestId: "recover-same" });
    await flushMicrotasks();
    expect(internals.getMermaidLifecycleSnapshotForTesting().owner).toBe("barrier");
    expect(messages.some(message => message.requestId === "recover-same")).toBe(false);

    firstRender.resolve({ svg: "<svg>stale</svg>" });
    await normalRender;
    await drainAnimationFrames();
    await barrier;
    const recovered = internals.getMermaidLifecycleSnapshotForTesting();
    expect(recovered.owner).toBe("normal");
    expect(recovered.activeRenderCount).toBe(0);
    expect(recovered.observerPresent).toBe(true);
    expect(messages).toContainEqual({
      type: "full-render-failed",
      requestId: "recover-same",
      reason: "injected barrier failure",
    });

    const recoveryObserver = observers.at(-1)!;
    recoveryObserver.callback([
      { isIntersecting: true, target: node } as Pick<IntersectionObserverEntry, "target" | "isIntersecting"> as IntersectionObserverEntry,
    ], {} as IntersectionObserver);
    await flushMicrotasks();
    expect(render).toHaveBeenCalledTimes(2);
    expect(recoveryObserver.elements.has(node)).toBe(false);
  });

  it("MermaidFailureRecoveryStaleIdentity restores no owner and performs no stale DOM work", async () => {
    const firstRender = createGate<{ svg: string }>();
    const render = vi.fn()
      .mockImplementationOnce(() => firstRender.promise)
      .mockResolvedValue({ svg: "<svg>unexpected</svg>" });
    const mermaid = { initialize: vi.fn(), render } satisfies MermaidApiForTesting;
    (window as unknown as { mermaid: MermaidApiForTesting }).mermaid = mermaid;
    const { drainAnimationFrames, messages, observers, rendererModule, send } = await loadRenderer(
      '<main class="mm-document"><pre class="mm-mermaid"><code data-mm-mermaid>stale</code></pre></main>'
    );
    const internals = requireRendererInternals(rendererModule);
    const node = document.querySelector<HTMLElement>("pre.mm-mermaid")!;
    const normalRender = internals.renderMermaidNodes([node], mermaid);
    await flushMicrotasks();
    const oldIdentity = internals.getMermaidLifecycleSnapshotForTesting().documentIdentity;
    internals.setFullRenderBarrierFailureForTesting(new Error("stale barrier failure"));
    const barrier = internals.handlePrepareForExport({ type: "prepare-for-export", requestId: "recover-stale" });
    await flushMicrotasks();

    send({
      type: "load-document",
      html: '<p id="fresh-document">fresh</p>',
      renderId: 73,
      hasMermaid: false,
      hasHljs: false,
    });
    await drainAnimationFrames();
    const observerCountAfterMutation = observers.length;
    firstRender.resolve({ svg: "<svg>stale</svg>" });
    await normalRender;
    await barrier;
    await flushMicrotasks();

    const recovered = internals.getMermaidLifecycleSnapshotForTesting();
    expect(recovered.documentIdentity).toBe(oldIdentity + 1);
    expect(recovered.owner).toBe("normal");
    expect(recovered.observerPresent).toBe(false);
    expect(observers).toHaveLength(observerCountAfterMutation);
    expect(render).toHaveBeenCalledTimes(1);
    expect(document.querySelector("#fresh-document")?.textContent).toBe("fresh");
    expect(messages).toContainEqual({
      type: "full-render-failed",
      requestId: "recover-stale",
      reason: "stale barrier failure",
    });
  });

  it("CancelledExportUnwindsTheRendererSoALaterDocumentStillExports", async () => {
    // A render that never settles. Not "slow" - there is no value to deliver and
    // no rejection to catch, which is exactly what makes .finally cleanup and
    // Promise.allSettled useless against it.
    const neverSettles = new Promise<{ svg: string }>(() => undefined);
    const render = vi.fn(() => neverSettles);
    (window as unknown as { mermaid: MermaidApiForTesting }).mermaid = { initialize: vi.fn(), render };
    const { drainAnimationFrames, messages, rendererModule, send } = await loadRenderer();
    const internals = requireRendererInternals(rendererModule);

    send({
      type: "load-document",
      html: '<pre class="mm-mermaid"><code data-mm-mermaid>stuck</code></pre>',
      renderId: 81,
      hasMermaid: false,
      hasHljs: false,
    });
    await drainAnimationFrames();
    send({ type: "prepare-for-export", requestId: "export-stuck" });
    await drainAnimationFrames();

    const parked = internals.getMermaidLifecycleSnapshotForTesting();
    expect(parked.owner).toBe("barrier");
    expect(parked.activeRenderCount).toBe(1);
    expect(messages.some(message => message.requestId === "export-stuck")).toBe(false);

    send({ type: "cancel-full-render", requestId: "export-stuck" });
    await drainAnimationFrames();

    // The barrier answered instead of holding its requester forever, and gave the
    // Mermaid lifecycle back to the normal owner, so renderMermaid /
    // initMermaidWithTheme / scheduleCachedMermaidResume stop being no-ops.
    const recovered = internals.getMermaidLifecycleSnapshotForTesting();
    expect(recovered.owner).toBe("normal");
    expect(recovered.activeRenderCount).toBe(0);
    expect(messages).toContainEqual({
      type: "full-render-failed",
      requestId: "export-stuck",
      reason: "export cancelled",
    });

    // The decisive assertion, and the filed symptom: the abandoned promise used to
    // stay in activeMermaidRenderCalls, so the NEXT barrier - even one built for a
    // different document, after a document change - re-parked on it and export was
    // dead for the whole WebView lifetime. A later export must now complete.
    send({
      type: "load-document",
      html: "<p>a different document, no diagrams</p>",
      renderId: 82,
      hasMermaid: false,
      hasHljs: false,
    });
    await drainAnimationFrames();
    send({ type: "prepare-for-export", requestId: "export-after-cancel" });
    await drainAnimationFrames();

    expect(messages).toContainEqual({
      type: "full-render-complete",
      requestId: "export-after-cancel",
      mermaidErrorCount: 0,
    });
  });

  it("CancelIsExactlyCorrelatedAndLeavesAnUnnamedBarrierRunning", async () => {
    const neverSettles = new Promise<{ svg: string }>(() => undefined);
    const render = vi.fn(() => neverSettles);
    (window as unknown as { mermaid: MermaidApiForTesting }).mermaid = { initialize: vi.fn(), render };
    const { drainAnimationFrames, messages, rendererModule, send } = await loadRenderer();
    const internals = requireRendererInternals(rendererModule);

    send({
      type: "load-document",
      html: '<pre class="mm-mermaid"><code data-mm-mermaid>stuck</code></pre>',
      renderId: 83,
      hasMermaid: false,
      hasHljs: false,
    });
    await drainAnimationFrames();
    send({ type: "prepare-for-export", requestId: "export-live" });
    await drainAnimationFrames();
    expect(internals.getMermaidLifecycleSnapshotForTesting().owner).toBe("barrier");

    // A cancel is a destructive unwind, so its gate is exact membership in the
    // live barrier's attached requests. A stale generation, a request from another
    // WebView, or an empty stamp must cancel NOTHING - the failure mode a gate that
    // fell back to "no id means cancel whatever is running" would introduce is
    // worse than not being able to cancel at all.
    send({ type: "cancel-full-render", requestId: "export-somebody-else" });
    send({ type: "cancel-full-render", requestId: "" });
    await drainAnimationFrames();

    const untouched = internals.getMermaidLifecycleSnapshotForTesting();
    expect(untouched.owner).toBe("barrier");
    expect(untouched.activeRenderCount).toBe(1);
    expect(messages.some(message => message.requestId === "export-live")).toBe(false);

    send({ type: "cancel-full-render", requestId: "export-live" });
    await drainAnimationFrames();

    expect(internals.getMermaidLifecycleSnapshotForTesting().owner).toBe("normal");
    expect(messages).toContainEqual({
      type: "full-render-failed",
      requestId: "export-live",
      reason: "export cancelled",
    });
  });

  it("keeps the cancel path event-driven with no clock of its own", () => {
    const source = readFileSync("RendererWeb/src/renderer.ts", "utf8");
    const cancelStart = source.indexOf("function cancelFullRenderBarrier(");
    const cancelEnd = source.indexOf("\n}", cancelStart);
    const cancel = source.slice(cancelStart, cancelEnd);
    const factoryStart = source.indexOf("function createFullRenderBarrier(");
    const factoryEnd = source.indexOf("\n}", factoryStart);
    const factory = source.slice(factoryStart, factoryEnd);
    const releaseStart = source.indexOf("function releaseAbandonedMermaidRenderCalls(");
    const releaseEnd = source.indexOf("\n}", releaseStart);
    const release = source.slice(releaseStart, releaseEnd);

    expect(cancelStart).toBeGreaterThanOrEqual(0);
    expect(factoryStart).toBeGreaterThanOrEqual(0);
    expect(releaseStart).toBeGreaterThanOrEqual(0);
    for (const body of [cancel, factory, release]) {
      expect(body).not.toContain("setTimeout");
      expect(body).not.toContain("setInterval");
      expect(body).not.toContain("Date.now");
      expect(body).not.toContain("performance.now");
    }

    // Order is load-bearing: recoverMermaidBarrierFailure - which the rejection
    // routes through - itself awaits the lazy queue and drains
    // activeMermaidRenderCalls. Rejecting before releasing them parks the recovery
    // on the very promise the cancel exists to escape.
    expect(cancel.indexOf("releaseAbandonedMermaidRenderCalls()"))
      .toBeLessThan(cancel.indexOf("barrier.cancel("));
    expect(release).toContain("activeMermaidRenderCalls.clear()");
    expect(release).toContain("mermaidLazyRenderQueue = Promise.resolve()");
  });

  it("MermaidTerminalResetOnPostMutationLoadAndClear advances once only after successful writes", async () => {
    const { applyLoadDocument, clearDocumentState } = await import("../src/loadDocument");
    let mutationCount = 0;
    const seenBodies: string[] = [];
    const deps = {
      runInitialRenderPipeline: async () => undefined,
      cancelCurrentMathController: vi.fn(),
      resetModuleGlobals: vi.fn(),
      scrollWindowToTop: vi.fn(),
      emitMark: vi.fn(),
      ensureChromeNodes: vi.fn(),
      applyTheme: vi.fn(),
      debugLog: vi.fn(),
      onDocumentBodyMutated: () => {
        mutationCount++;
        seenBodies.push(document.querySelector("main.mm-document")?.innerHTML ?? "<missing>");
      },
    };

    document.body.innerHTML = '<main class="mm-document"><p>old</p></main>';
    applyLoadDocument({ html: "<p>cold</p>", hasMermaid: false }, deps);
    expect(mutationCount).toBe(1);
    expect(seenBodies.at(-1)).toBe("<p>cold</p>");

    const fragment = document.createDocumentFragment();
    const cached = document.createElement("p");
    cached.textContent = "cached";
    fragment.append(cached);
    applyLoadDocument({ cacheKey: "cached" }, {
      ...deps,
      getCachedDocumentFragment: () => fragment,
    });
    expect(mutationCount).toBe(2);
    expect(seenBodies.at(-1)).toBe("<p>cached</p>");

    clearDocumentState(deps);
    expect(mutationCount).toBe(3);
    expect(seenBodies.at(-1)).toBe("");

    document.body.innerHTML = "";
    applyLoadDocument({ html: "<p>missing root</p>" }, deps);
    expect(mutationCount).toBe(3);

    document.body.innerHTML = '<main class="mm-document"></main>';
    applyLoadDocument({ cacheKey: "missing" }, {
      ...deps,
      getCachedDocumentFragment: () => undefined,
    });
    expect(mutationCount).toBe(3);

    const main = document.querySelector<HTMLElement>("main.mm-document")!;
    Object.defineProperty(main, "innerHTML", {
      configurable: true,
      get: () => "",
      set: () => { throw new Error("pre-write failure"); },
    });
    expect(() => applyLoadDocument({ html: "<p>never written</p>" }, deps)).toThrow("pre-write failure");
    expect(mutationCount).toBe(3);
  });
});
