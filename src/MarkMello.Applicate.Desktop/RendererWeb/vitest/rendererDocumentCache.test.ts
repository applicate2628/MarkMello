import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

type HostBridge = (msg: unknown) => void;

async function loadRendererWithMessages() {
  vi.resetModules();
  document.documentElement.innerHTML = `<body><main class="mm-document"></main></body>`;
  const messages: unknown[] = [];
  (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
    webview: { postMessage: (m: unknown) => messages.push(m) }
  };
  await import("../src/renderer");
  const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;
  return { load, messages };
}

async function letPipelineSettle(): Promise<void> {
  // Cache entries are published only after post-ready enhancements complete.
  // The live renderer now gives edit-preview mode-settle messages a short
  // head start before post-ready Mermaid/hljs work, so fixed sleeps in these
  // cache tests need to cover that debounce plus Phase B minimap settle.
  await new Promise(resolve => setTimeout(resolve, 700));
}

beforeEach(() => {
  delete (window as unknown as { chrome?: unknown }).chrome;
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("renderer document cache", () => {
  it("reports cached geometry immediately and refreshes current geometry after cached reveal", async () => {
    const root = document.documentElement;
    Object.defineProperty(root, "scrollHeight", { configurable: true, value: 2000 });
    Object.defineProperty(root, "clientHeight", { configurable: true, value: 800 });
    vi.spyOn(window, "scrollTo").mockImplementation((options?: ScrollToOptions | number, y?: number) => {
      root.scrollTop = typeof options === "number"
        ? (y ?? 0)
        : (options?.top ?? 0);
    });

    const { load, messages } = await loadRendererWithMessages();
    const firstHtml = "<h1 id='first'>First</h1><p>cached document</p>";
    const secondHtml = "<h1 id='second'>Second</h1><p>other document</p>";

    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();
    root.scrollTop = 240;
    document.dispatchEvent(new Event("scroll"));
    await letPipelineSettle();

    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    Object.defineProperty(root, "scrollHeight", { configurable: true, value: 2600 });
    Object.defineProperty(root, "clientHeight", { configurable: true, value: 900 });
    messages.length = 0;
    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 3 });
    await letPipelineSettle();

    const cachedLayoutReady = messages.find((message): message is { type: "layout-ready"; cached?: boolean; scrollTop: number; scrollHeight: number; clientHeight: number } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "layout-ready");

    expect(cachedLayoutReady).toMatchObject({
      type: "layout-ready",
      cached: true,
      scrollTop: 240,
      scrollHeight: 2000,
      clientHeight: 800,
    });

    const currentGeometryScroll = messages.find((message): message is { type: "scroll"; scrollTop: number; scrollHeight: number; clientHeight: number } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "scroll"
      && (message as { scrollHeight?: unknown }).scrollHeight === 2600);

    expect(currentGeometryScroll).toMatchObject({
      type: "scroll",
      scrollTop: 240,
      scrollHeight: 2600,
      clientHeight: 900,
    });
  });

  it("marks cached layout-ready messages so the host does not restore by progress twice", async () => {
    const root = document.documentElement;
    Object.defineProperty(root, "scrollHeight", { configurable: true, value: 2000 });
    Object.defineProperty(root, "clientHeight", { configurable: true, value: 800 });
    vi.spyOn(window, "scrollTo").mockImplementation((options?: ScrollToOptions | number, y?: number) => {
      root.scrollTop = typeof options === "number"
        ? (y ?? 0)
        : (options?.top ?? 0);
    });

    const { load, messages } = await loadRendererWithMessages();
    const firstHtml = "<h1 id='first'>First</h1><p>cached document</p>";
    const secondHtml = "<h1 id='second'>Second</h1><p>other document</p>";

    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();
    root.scrollTop = 240;
    document.dispatchEvent(new Event("scroll"));
    await letPipelineSettle();

    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    messages.length = 0;
    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 3 });
    await letPipelineSettle();

    const cachedLayoutReady = messages.find((message): message is { type: "layout-ready"; cached?: boolean; scrollTop: number } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "layout-ready");

    expect(cachedLayoutReady).toMatchObject({
      type: "layout-ready",
      cached: true,
      scrollTop: 240,
    });
  });

  it("reuses the cached top block index without a cached layout-ready DOM scan", async () => {
    const root = document.documentElement;
    Object.defineProperty(root, "scrollHeight", { configurable: true, value: 2000 });
    Object.defineProperty(root, "clientHeight", { configurable: true, value: 800 });
    vi.spyOn(window, "scrollTo").mockImplementation((options?: ScrollToOptions | number, y?: number) => {
      root.scrollTop = typeof options === "number"
        ? (y ?? 0)
        : (options?.top ?? 0);
    });

    const { load, messages } = await loadRendererWithMessages();
    const firstHtml = "<h1 id='first'>First</h1><p data-mm-block-index='42'>cached document</p>";
    const secondHtml = "<h1 id='second'>Second</h1><p>other document</p>";

    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();
    root.scrollTop = 240;
    document.dispatchEvent(new Event("scroll"));
    await letPipelineSettle();

    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    const rectSpy = vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      if (this.hasAttribute("data-mm-block-index")) {
        throw new Error("cached layout-ready measured block geometry");
      }
      return {
        x: 0,
        y: 0,
        width: 1,
        height: 1,
        top: 0,
        right: 1,
        bottom: 1,
        left: 0,
        toJSON: () => ({}),
      };
    });

    messages.length = 0;
    expect(() => load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 3 })).not.toThrow();
    await letPipelineSettle();
    rectSpy.mockRestore();

    const cachedScroll = messages.find((message): message is { type: "scroll"; topBlockIndex: number | null } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "scroll");

    expect(cachedScroll).toMatchObject({
      type: "scroll",
      topBlockIndex: 42,
    });
  });

  it("restores cached minimap content on document cache hits without synchronously refreshing the minimap", async () => {
    const root = document.documentElement;
    Object.defineProperty(root, "scrollHeight", { configurable: true, value: 2000 });
    Object.defineProperty(root, "clientHeight", { configurable: true, value: 800 });
    const { load, messages } = await loadRendererWithMessages();
    const firstHtml = "<h1 id='first'>First</h1><p>cached document</p>";
    const secondHtml = "<h1 id='second'>Second</h1><p>other document</p>";

    load({
      type: "reading-preferences",
      fontFamily: "serif",
      fontSize: 16,
      lineHeight: 1.6,
      maxWidth: 820,
      minMaxWidth: 320,
      minimapMode: "on",
      viewerChromeEnabled: true,
      documentScrollEnabled: true,
      wheelProxyEnabled: true,
      widthResizerVisibility: "on-hover",
      viewportWidth: 1200,
      viewportHeight: 800,
    });
    load({
      type: "minimap-policy",
      minimapPolicy: {
        minHostWidth: 0,
        minScrollableViewportRatio: 1,
        maxDetailedDocumentHeight: 10000,
      },
    });

    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();
    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    messages.length = 0;
    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 3 });
    await letPipelineSettle();

    const perfMarks = messages
      .filter((message): message is { type: "perf-mark"; name: string } =>
        typeof message === "object"
        && message !== null
        && (message as { type?: unknown }).type === "perf-mark")
      .map(message => message.name);

    expect(perfMarks).toContain("mm-load-document-cache-hit");
    expect(perfMarks).toContain("mm-minimap-cache-hit");
    expect(perfMarks).not.toContain("mm-minimap-refresh-start");
  });

  it("reuses post-ready mermaid documents from the processed document cache", async () => {
    const { load, messages } = await loadRendererWithMessages();
    const firstHtml = "<h1 id='first'>First</h1><pre class='mm-mermaid'>graph TD; A-->B;</pre>";
    const secondHtml = "<h1 id='second'>Second</h1><p>other document</p>";

    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "dark", hasMermaid: true, renderId: 1 });
    await letPipelineSettle();
    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "dark", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    messages.length = 0;
    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "dark", hasMermaid: true, renderId: 3 });
    await letPipelineSettle();

    const perfMarks = messages
      .filter((message): message is { type: "perf-mark"; name: string } =>
        typeof message === "object"
        && message !== null
        && (message as { type?: unknown }).type === "perf-mark")
      .map(message => message.name);
    const postReadyComplete = messages.find((message): message is { type: "post-ready-enhancements-complete"; renderId?: number } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "post-ready-enhancements-complete");

    expect(perfMarks).toContain("mm-load-document-cache-hit");
    expect(postReadyComplete).toMatchObject({ renderId: 3 });
  });

  it("resumes lazy mermaid rendering for missing diagrams after a processed document cache hit", async () => {
    type FakeObserverRecord = {
      callback: IntersectionObserverCallback;
      elements: Set<Element>;
    };
    const observers: FakeObserverRecord[] = [];
    class FakeIntersectionObserver {
      private readonly record: FakeObserverRecord;

      constructor(callback: IntersectionObserverCallback) {
        this.record = { callback, elements: new Set<Element>() };
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
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      if (this.classList.contains("mm-mermaid")) {
        return {
          x: 0,
          y: 5000,
          width: 600,
          height: 120,
          top: 5000,
          right: 600,
          bottom: 5120,
          left: 0,
          toJSON: () => ({}),
        } as DOMRect;
      }

      return {
        x: 0,
        y: 0,
        width: 600,
        height: 40,
        top: 0,
        right: 600,
        bottom: 40,
        left: 0,
        toJSON: () => ({}),
      } as DOMRect;
    });

    const root = document.documentElement;
    Object.defineProperty(root, "scrollHeight", { configurable: true, value: 6000 });
    Object.defineProperty(root, "clientHeight", { configurable: true, value: 800 });
    const mermaidRender = vi.fn(async (_id: string, source: string) => ({ svg: `<svg>${source}</svg>` }));
    (window as unknown as {
      mermaid: {
        initialize: (config: unknown) => void;
        render: (id: string, source: string) => Promise<{ svg: string }>;
      };
    }).mermaid = {
      initialize: vi.fn(),
      render: mermaidRender,
    };

    const { load, messages } = await loadRendererWithMessages();
    const firstHtml = "<h1 id='first'>First</h1><pre class='mm-mermaid'><code data-mm-mermaid>graph TD; A-->B;</code></pre>";
    const secondHtml = "<h1 id='second'>Second</h1><p>other document</p>";

    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: true, renderId: 1 });
    await letPipelineSettle();
    expect(mermaidRender).not.toHaveBeenCalled();

    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    messages.length = 0;
    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: true, renderId: 3 });
    await letPipelineSettle();

    const restoredMermaid = document.querySelector<HTMLElement>("pre.mm-mermaid");
    expect(restoredMermaid).not.toBeNull();
    expect(restoredMermaid!.classList.contains("is-rendered")).toBe(false);
    expect(messages).toContainEqual(expect.objectContaining({
      type: "perf-mark",
      name: "mm-mermaid-cache-resume",
    }));
    expect(observers.at(-1)?.elements.has(restoredMermaid!)).toBe(true);

    observers.at(-1)?.callback([
      { target: restoredMermaid!, isIntersecting: true } as IntersectionObserverEntry
    ], {} as IntersectionObserver);
    await letPipelineSettle();

    expect(mermaidRender).toHaveBeenCalledWith(expect.any(String), "graph TD; A-->B;");
    expect(restoredMermaid!.classList.contains("is-rendered")).toBe(true);
    expect(restoredMermaid!.nextElementSibling?.classList.contains("mm-mermaid-svg")).toBe(true);
  });
});
