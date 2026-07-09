import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

type HostBridge = (msg: unknown) => void;

type ReadingPreferencesMessage = {
  type: "reading-preferences";
  fontFamily: "serif";
  fontSize: number;
  lineHeight: number;
  maxWidth: number;
  minimapMode: "off" | "auto" | "on";
  viewerChromeEnabled: boolean;
  documentScrollEnabled: boolean;
  wheelProxyEnabled: boolean;
  widthResizerVisibility: "on-hover" | "always";
};

const makePreferences = (
  viewerChromeEnabled: boolean,
  minimapMode: ReadingPreferencesMessage["minimapMode"] = "off"
): ReadingPreferencesMessage => ({
  type: "reading-preferences",
  fontFamily: "serif",
  fontSize: 16,
  lineHeight: 1.6,
  maxWidth: 720,
  minimapMode,
  viewerChromeEnabled,
  documentScrollEnabled: true,
  wheelProxyEnabled: false,
  widthResizerVisibility: "always",
});

describe("renderer chrome race handling", () => {
  let rafCallbacks: FrameRequestCallback[];

  beforeEach(async () => {
    vi.resetModules();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    delete (window as Window & { MARKMELLO_VIRTUALIZATION?: unknown }).MARKMELLO_VIRTUALIZATION;
    document.documentElement.removeAttribute("data-markmello-virtualization");
    if (typeof window.localStorage?.removeItem === "function") {
      window.localStorage.removeItem("MARKMELLO_VIRTUALIZATION");
    }
    document.documentElement.innerHTML =
      `<body><main class="mm-document"><p>Loaded document</p></main></body>`;
    Object.defineProperty(document, "scrollingElement", {
      configurable: true,
      get: () => document.documentElement,
    });
    Object.defineProperty(document.documentElement, "clientHeight", {
      configurable: true,
      value: 600,
    });
    Object.defineProperty(document.documentElement, "scrollHeight", {
      configurable: true,
      value: 2400,
    });
    Object.defineProperty(window, "innerWidth", {
      configurable: true,
      value: 1600,
    });

    rafCallbacks = [];
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
      rafCallbacks.push(callback);
      return rafCallbacks.length;
    });

    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function () {
      if (this instanceof HTMLElement && this.classList.contains("mm-document")) {
        return {
          x: 120,
          y: 0,
          width: 720,
          height: 1600,
          top: 0,
          right: 840,
          bottom: 1600,
          left: 120,
          toJSON: () => ({}),
        } as DOMRect;
      }

      return {
        x: 0,
        y: 0,
        width: 0,
        height: 0,
        top: 0,
        right: 0,
        bottom: 0,
        left: 0,
        toJSON: () => ({}),
      } as DOMRect;
    });

    await import("../src/renderer");
  });

  afterEach(() => {
    (window as unknown as { __mmRendererLoad?: HostBridge }).__mmRendererLoad?.({ type: "clear-document" });
    if (vi.isFakeTimers()) {
      vi.clearAllTimers();
    }
    vi.useRealTimers();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  function load(message: unknown): void {
    (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad(message);
  }

  function flushNextRaf(): void {
    const callback = rafCallbacks.shift();
    if (!callback) {
      throw new Error("Expected a queued requestAnimationFrame callback");
    }
    callback(performance.now());
  }

  function flushQueuedRafs(limit = 20): void {
    for (let frame = 0; frame < limit && rafCallbacks.length > 0; frame++) {
      flushNextRaf();
    }
    if (rafCallbacks.length > 0) {
      throw new Error("requestAnimationFrame queue did not settle");
    }
  }

  function setDocumentMetrics(documentHeight: number, viewportHeight: number): void {
    Object.defineProperty(document.documentElement, "clientHeight", {
      configurable: true,
      value: viewportHeight,
    });
    Object.defineProperty(document.documentElement, "scrollHeight", {
      configurable: true,
      value: documentHeight,
    });
  }

  function loadMinimapPolicy(maxDetailedDocumentHeight: number): void {
    load({
      type: "minimap-policy",
      minimapPolicy: {
        minHostWidth: 500,
        minScrollableViewportRatio: 0.1,
        maxDetailedDocumentHeight,
      },
    });
  }

  function findMessageIndex(type: string, messages: unknown[]): number {
    return messages.findIndex((message: { type?: string } | null) => message?.type === type);
  }

  function countRendererMarks(name: string): number {
    const report = (window as unknown as { __mmPerfReport: () => { marks: Array<{ name: string }> } }).__mmPerfReport();
    return report.marks.filter(mark => mark.name === name).length;
  }

  async function advanceLayoutReadyTimer(): Promise<void> {
    await Promise.resolve();
    await Promise.resolve();
    await vi.advanceTimersByTimeAsync(300);
    await Promise.resolve();
  }

  async function loadDocumentAndFlushMinimap(): Promise<void> {
    load({ type: "load-document", html: "<p>Loaded document</p>", hasMermaid: false });
    await Promise.resolve();
    await Promise.resolve();
    flushQueuedRafs();
  }

  async function loadDocumentWithMinimapPolicy(maxDetailedDocumentHeight: number): Promise<void> {
    loadMinimapPolicy(maxDetailedDocumentHeight);
    await loadDocumentAndFlushMinimap();
  }

  function loadModeSettleProbe(minimapMode: ReadingPreferencesMessage["minimapMode"]): void {
    load({ ...makePreferences(true, minimapMode), type: "mode-settle-probe" });
  }

  function makePointerEvent(type: string, clientX: number): MouseEvent {
    const event = new MouseEvent(type, {
      bubbles: true,
      cancelable: true,
      button: 0,
      clientX,
    });
    Object.defineProperty(event, "pointerId", {
      configurable: true,
      value: 1,
    });
    return event;
  }

  async function settleInitialVisibleLayout(): Promise<void> {
    load({ type: "reading-preferences", ...makePreferences(true) });
    flushNextRaf();

    load({ type: "load-document", html: "<p>Loaded document</p>", hasMermaid: false });
    await Promise.resolve();
    await Promise.resolve();
  }

  it("hides viewer chrome immediately when edit preferences arrive", async () => {
    await settleInitialVisibleLayout();

    const handle = document.querySelector<HTMLElement>(".mm-width-handle");
    expect(handle).toBeTruthy();
    expect(handle!.hidden).toBe(false);
    expect(document.documentElement.dataset.mmChrome).toBe("on");

    load({ type: "reading-preferences", ...makePreferences(false) });

    expect(handle!.hidden).toBe(true);
    expect(document.documentElement.dataset.mmChrome).toBe("off");
  });

  it("suppresses first-preferences bootstrap when the load pipeline owns a complete fresh body", () => {
    load({
      type: "load-document",
      html: "<p><span class='math-inline' data-tex='x'>x</span></p>",
      hasMermaid: false,
      hasHljs: false,
      renderId: 21,
      cacheKey: "fresh-full",
    });
    load({ type: "reading-preferences", ...makePreferences(true) });
    flushQueuedRafs();

    expect(countRendererMarks("mm-render-math-start")).toBe(1);
  });

  it("does not suppress first-preferences bootstrap after a progressive initial load completes", async () => {
    load({
      type: "load-document",
      html: "<p><span class='math-inline' data-tex='x'>x</span></p>",
      hasMermaid: false,
      hasHljs: false,
      renderId: 22,
      cacheKey: null,
    });
    await Promise.resolve();
    await Promise.resolve();

    load({ type: "reading-preferences", ...makePreferences(true) });
    flushQueuedRafs();

    expect(countRendererMarks("mm-render-math-start")).toBe(2);
  });

  it("keeps detailed minimap content visible for heavy scrollable documents when mode is on", async () => {
    const messages: unknown[] = [];
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) }
    };
    document.documentElement.style.setProperty("--mm-minimap-width", "136px");
    document.documentElement.style.setProperty("--mm-minimap-gap", "16px");

    setDocumentMetrics(2400, 600);
    await loadDocumentWithMinimapPolicy(1000);
    const minimap = document.querySelector<HTMLElement>(".mm-minimap");
    const minimapContent = document.querySelector<HTMLElement>(".mm-minimap-content");
    const documentElement = document.querySelector<HTMLElement>(".mm-document");
    expect(minimap).not.toBeNull();
    expect(minimapContent).not.toBeNull();
    expect(documentElement).not.toBeNull();
    Object.defineProperty(minimap!, "clientHeight", {
      configurable: true,
      get: () => {
        throw new Error("heavy minimap transition measured minimap height");
      },
    });
    Object.defineProperty(minimap!, "clientWidth", {
      configurable: true,
      get: () => {
        throw new Error("heavy minimap transition measured minimap width");
      },
    });
    Object.defineProperty(documentElement!, "clientWidth", {
      configurable: true,
      get: () => {
        throw new Error("heavy minimap transition measured source width");
      },
    });
    vi.spyOn(minimap!, "getBoundingClientRect").mockImplementation(() => {
      throw new Error("minimap-state forced minimap layout");
    });
    vi.spyOn(documentElement!, "getBoundingClientRect").mockImplementation(() => {
      throw new Error("heavy minimap transition forced document layout");
    });
    messages.length = 0;

    loadModeSettleProbe("on");
    expect(() => flushQueuedRafs()).not.toThrow();

    const minimapStates = messages.filter((message): message is { type: "minimap-state"; visible: boolean; reservedWidth: number } =>
      typeof message === "object" && message !== null && (message as { type?: string }).type === "minimap-state");
    const latestState = minimapStates.at(-1);

    expect(document.body.classList.contains("mm-has-minimap")).toBe(true);
    expect(document.querySelector(".mm-minimap-content .mm-document")).not.toBeNull();
    expect(document.querySelector(".mm-minimap-content svg")).toBeNull();
    expect(latestState).toEqual({
      type: "minimap-state",
      visible: true,
      reservedWidth: 168,
    });
    expect(Object.keys(latestState ?? {}).sort()).toEqual(["reservedWidth", "type", "visible"]);
  });

  it("keeps heavy documents hidden in automatic minimap mode", async () => {
    setDocumentMetrics(2400, 600);
    await loadDocumentWithMinimapPolicy(1000);

    loadModeSettleProbe("auto");
    flushQueuedRafs();

    expect(document.body.classList.contains("mm-has-minimap")).toBe(false);
  });

  it("keeps auto-hidden heavy minimap empty until explicit on mode requests detail", async () => {
    const messages: unknown[] = [];
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) }
    };
    setDocumentMetrics(2400, 600);

    load({ type: "reading-preferences", ...makePreferences(true, "auto") });
    flushQueuedRafs();
    loadMinimapPolicy(1000);
    messages.length = 0;

    load({ type: "load-document", html: "<p>Heavy document</p>", hasMermaid: false });
    await Promise.resolve();
    await Promise.resolve();
    flushQueuedRafs();

    expect(document.querySelector(".mm-minimap-content .mm-document")).toBeNull();
    const skippedRefreshMark = messages.find((message): message is { type: string; name: string; detail: string } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: string }).type === "perf-mark"
      && (message as { name?: string }).name === "mm-minimap-refresh-skipped"
      && typeof (message as { detail?: unknown }).detail === "string");
    expect(skippedRefreshMark?.detail).toContain('"reason":"auto-heavy"');

    messages.length = 0;
    load({ type: "reading-preferences", ...makePreferences(true, "on") });
    flushQueuedRafs();
    load({ type: "minimap-settle-probe", transactionGeneration: 1 });
    flushQueuedRafs();

    const settled = messages.find((message): message is {
      type: string;
      transactionGeneration: number;
      visible: boolean;
      reservedWidth: number;
    } => typeof message === "object"
      && message !== null
      && (message as { type?: string }).type === "minimap-settled");
    expect(settled).toEqual({
      type: "minimap-settled",
      transactionGeneration: 1,
      visible: true,
      reservedWidth: 168,
    });
    expect(document.body.classList.contains("mm-has-minimap")).toBe(true);
    expect(document.querySelector(".mm-minimap-content .mm-document")).not.toBeNull();
    expect(document.querySelector(".mm-minimap-content svg")).toBeNull();
  });

  it("keeps progressive heavy minimap finalization off the full-DOM clone path", async () => {
    vi.useFakeTimers();
    const messages: unknown[] = [];
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) }
    };
    setDocumentMetrics(2400, 600);

    load({ type: "reading-preferences", ...makePreferences(true, "on") });
    flushQueuedRafs();
    loadMinimapPolicy(1000);
    load({
      type: "load-document",
      html: "<h1>Intro</h1><p>Visible first chunk</p>",
      hasMermaid: false,
      hasHljs: false,
      renderId: 1,
      cacheKey: null,
    });
    await Promise.resolve();
    await Promise.resolve();
    flushQueuedRafs();

    const documentElement = document.querySelector<HTMLElement>("main.mm-document");
    expect(documentElement).not.toBeNull();
    const cloneSpy = vi.spyOn(documentElement!, "cloneNode").mockImplementation(() => {
      throw new Error("progressive final append cloned the full heavy document");
    });
    messages.length = 0;

    expect(() => load({
      type: "append-document",
      html: "<h2>Later</h2><p>Rest of heavy document</p>",
      hasMermaid: false,
      hasHljs: false,
      renderId: 1,
      isFinal: true,
      cacheKey: "heavy-full",
    })).not.toThrow();

    expect(cloneSpy).not.toHaveBeenCalled();
    expect(document.body.classList.contains("mm-has-minimap")).toBe(true);
    expect(document.querySelector(".mm-minimap-content .mm-document")).not.toBeNull();
    expect(document.querySelector(".mm-minimap-content svg")).toBeNull();

    const perfMarks = messages
      .filter((message): message is { type: "perf-mark"; name: string } =>
        typeof message === "object"
        && message !== null
        && (message as { type?: unknown }).type === "perf-mark")
      .map(message => message.name);
    expect(perfMarks).toContain("mm-progressive-append-end");
    expect(perfMarks).not.toContain("mm-minimap-refresh-start");

    cloneSpy.mockRestore();
    await vi.advanceTimersByTimeAsync(160);
    expect(document.querySelector(".mm-minimap-content .mm-document")?.textContent).toContain("Rest of heavy document");
  });

  it("does not remeasure the detailed minimap clone on every width-handle drag frame", async () => {
    const messages: unknown[] = [];
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) }
    };
    setDocumentMetrics(2400, 600);
    await loadDocumentWithMinimapPolicy(10000);
    loadModeSettleProbe("on");
    flushQueuedRafs();

    const handle = document.querySelector<HTMLElement>(".mm-width-handle");
    const minimap = document.querySelector<HTMLElement>(".mm-minimap");
    const minimapContent = document.querySelector<HTMLElement>(".mm-minimap-content");
    const documentElement = document.querySelector<HTMLElement>(".mm-document");
    expect(handle).not.toBeNull();
    expect(minimap).not.toBeNull();
    expect(minimapContent).not.toBeNull();
    expect(documentElement).not.toBeNull();
    Object.defineProperty(handle!, "setPointerCapture", {
      configurable: true,
      value: vi.fn(),
    });
    Object.defineProperty(handle!, "releasePointerCapture", {
      configurable: true,
      value: vi.fn(),
    });

    Object.defineProperty(minimap!, "clientHeight", {
      configurable: true,
      value: 300,
    });
    Object.defineProperty(minimap!, "clientWidth", {
      configurable: true,
      value: 80,
    });
    Object.defineProperty(documentElement!, "clientWidth", {
      configurable: true,
      value: 720,
    });

    let allowDocumentMeasure = true;
    let documentMeasureCount = 0;
    vi.spyOn(documentElement!, "getBoundingClientRect").mockImplementation(() => {
      documentMeasureCount++;
      if (!allowDocumentMeasure) {
        throw new Error("width drag frame measured full document layout");
      }
      return {
        x: 120,
        y: 0,
        width: 720,
        height: 1600,
        top: 0,
        right: 840,
        bottom: 1600,
        left: 120,
        toJSON: () => ({}),
      } as DOMRect;
    });

    let allowMinimapMeasure = false;
    let minimapMeasureCount = 0;
    Object.defineProperty(minimapContent!, "scrollHeight", {
      configurable: true,
      get: () => {
        minimapMeasureCount++;
        if (!allowMinimapMeasure) {
          throw new Error("width drag frame measured detailed minimap clone");
        }
        return 2400;
      },
    });

    handle!.dispatchEvent(makePointerEvent("pointerdown", 100));
    const documentMeasureCountAfterPointerDown = documentMeasureCount;
    allowDocumentMeasure = false;
    handle!.dispatchEvent(makePointerEvent("pointermove", 140));

    expect(() => flushQueuedRafs()).not.toThrow();
    expect(documentMeasureCount).toBe(documentMeasureCountAfterPointerDown);
    expect(minimapMeasureCount).toBe(0);

    allowDocumentMeasure = true;
    allowMinimapMeasure = true;
    handle!.dispatchEvent(makePointerEvent("pointerup", 140));
    flushQueuedRafs();

    expect(documentMeasureCount).toBeGreaterThan(documentMeasureCountAfterPointerDown);
    expect(minimapMeasureCount).toBeGreaterThan(0);
    expect(messages).toContainEqual(expect.objectContaining({
      type: "perf-mark",
      name: "mm-width-drag-start",
    }));
    const endMark = messages.find((message): message is { type: string; name: string; detail: string } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "perf-mark"
      && (message as { name?: unknown }).name === "mm-width-drag-end");
    expect(endMark).toBeTruthy();
    expect(JSON.parse(endMark!.detail)).toMatchObject({
      moveEvents: 1,
      movePosts: 1,
      applyFrames: 1,
      deltaX: 40,
    });
  });

  it("does not measure document layout for viewport-only updates while heavy auto minimap is hidden", async () => {
    vi.useFakeTimers();
    const messages: unknown[] = [];
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) }
    };
    setDocumentMetrics(2400, 600);

    load({ type: "reading-preferences", ...makePreferences(true, "auto") });
    flushQueuedRafs();
    loadMinimapPolicy(1000);

    load({ type: "load-document", html: "<p>Heavy document</p>", hasMermaid: false });
    await Promise.resolve();
    await Promise.resolve();
    flushQueuedRafs();

    const documentElement = document.querySelector<HTMLElement>(".mm-document");
    expect(documentElement).not.toBeNull();
    Object.defineProperty(documentElement!, "clientWidth", {
      configurable: true,
      get: () => {
        throw new Error("hidden minimap viewport update measured source layout");
      },
    });

    load({ type: "reading-preferences", ...makePreferences(true, "auto"), maxWidth: 760 });
    flushQueuedRafs();
    await vi.advanceTimersByTimeAsync(100);

    expect(() => flushQueuedRafs()).not.toThrow();
  });

  it("keeps minimap hidden when mode is off", async () => {
    setDocumentMetrics(2400, 600);
    await loadDocumentWithMinimapPolicy(10000);

    loadModeSettleProbe("off");
    flushQueuedRafs();

    expect(document.body.classList.contains("mm-has-minimap")).toBe(false);
  });

  it("keeps minimap hidden for invalid viewport metrics even when mode is on", async () => {
    setDocumentMetrics(2400, 0);
    await loadDocumentWithMinimapPolicy(1000);

    loadModeSettleProbe("on");
    flushQueuedRafs();

    expect(document.body.classList.contains("mm-has-minimap")).toBe(false);
  });

  it("responds to transaction minimap settle probes without changing minimap-state shape", async () => {
    const messages: unknown[] = [];
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) }
    };

    setDocumentMetrics(2400, 600);
    await loadDocumentWithMinimapPolicy(10000);
    messages.length = 0;

    loadModeSettleProbe("on");
    flushQueuedRafs();
    messages.length = 0;

    load({ type: "minimap-settle-probe", transactionGeneration: 99 });
    flushQueuedRafs();

    const minimapState = messages.find((message): message is { type: "minimap-state"; visible: boolean; reservedWidth: number } =>
      typeof message === "object" && message !== null && (message as { type?: string }).type === "minimap-state");
    const settled = messages.find((message): message is { type: "minimap-settled"; transactionGeneration: number; visible: boolean; reservedWidth: number } =>
      typeof message === "object" && message !== null && (message as { type?: string }).type === "minimap-settled");

    expect(minimapState).toBeTruthy();
    expect(Object.keys(minimapState ?? {}).sort()).toEqual(["reservedWidth", "type", "visible"]);
    expect(settled).toEqual({
      type: "minimap-settled",
      transactionGeneration: 99,
      visible: true,
      reservedWidth: expect.any(Number),
    });
  });

  it("settles mode toggle one paint after applying minimap visibility", async () => {
    const messages: unknown[] = [];
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) }
    };

    load({
      type: "minimap-policy",
      minimapPolicy: {
        minHostWidth: 500,
        minScrollableViewportRatio: 0.1,
        maxDetailedDocumentHeight: 10000,
      },
    });
    load({ type: "load-document", html: "<p>Loaded document</p>", hasMermaid: false });
    await Promise.resolve();
    await Promise.resolve();
    flushQueuedRafs();

    load({ type: "reading-preferences", ...makePreferences(false, "on") });
    flushQueuedRafs();
    messages.length = 0;

    expect(document.body.classList.contains("mm-has-minimap")).toBe(false);

    load({ type: "reading-preferences", ...makePreferences(true, "on") });
    load({ type: "mode-settle-probe" });

    // The stale preference rAF was queued before the probe consumed the
    // pending preferences synchronously.
    flushNextRaf();
    expect(messages.some((message: { type?: string } | null) =>
      message?.type === "mode-toggle-settled")).toBe(false);

    flushNextRaf();
    expect(document.body.classList.contains("mm-has-minimap")).toBe(true);
    expect(messages.some((message: { type?: string } | null) =>
      message?.type === "mode-toggle-settled")).toBe(false);

    flushNextRaf();
    expect(messages.some((message: { type?: string } | null) =>
      message?.type === "mode-toggle-settled")).toBe(false);

    flushNextRaf();
    expect(messages.some((message: { type?: string } | null) =>
      message?.type === "mode-toggle-settled")).toBe(true);
  });

  it("skip-frame settle probe applies preferences before ack without waiting for requestAnimationFrame", () => {
    const messages: unknown[] = [];
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) }
    };

    load({ ...makePreferences(false, "off"), type: "reading-preferences" });
    flushQueuedRafs();
    messages.length = 0;

    load({
      ...makePreferences(true, "on"),
      type: "mode-settle-probe",
      transactionGeneration: 42,
      skipFrameWait: true,
    });

    expect(messages).toContainEqual({
      type: "mode-toggle-settled",
      transactionGeneration: 42,
    });
    expect(document.documentElement.dataset.mmChrome).toBe("on");
  });

  it("transactional load-document skipFrameWait emits layout-ready without flushing requestAnimationFrame", async () => {
    vi.useFakeTimers();
    const messages: unknown[] = [];
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) }
    };
    setDocumentMetrics(2400, 600);

    load({
      type: "load-document",
      html: "<h1>Transactional</h1><p>Ready</p>",
      hasMermaid: false,
      skipFrameWait: true,
      renderId: 14,
    });

    await advanceLayoutReadyTimer();

    const documentReadyIndex = findMessageIndex("document-ready", messages);
    const layoutReadyIndex = findMessageIndex("layout-ready", messages);
    const layoutReady = messages[layoutReadyIndex] as {
      scrollTop?: number;
      scrollHeight?: number;
      clientHeight?: number;
    };

    expect(documentReadyIndex).toBeGreaterThanOrEqual(0);
    expect(layoutReadyIndex).toBeGreaterThan(documentReadyIndex);
    expect(layoutReady).toMatchObject({
      scrollTop: 0,
      scrollHeight: 2400,
      clientHeight: 600,
    });
    expect(messages.some((message: { type?: string; name?: string } | null) =>
      message?.type === "perf-mark" && message.name === "mm-layout-ready-frame-wait-skipped")).toBe(true);
  });

  it("non-transactional load-document keeps layout-ready behind animation frames", async () => {
    vi.useFakeTimers();
    const messages: unknown[] = [];
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) }
    };

    load({
      type: "load-document",
      html: "<h1>Normal</h1><p>Ready</p>",
      hasMermaid: false,
      renderId: 15,
    });

    await advanceLayoutReadyTimer();

    expect(findMessageIndex("layout-ready", messages)).toBe(-1);
    expect(rafCallbacks.length).toBeGreaterThan(0);
    flushQueuedRafs();
    expect(findMessageIndex("layout-ready", messages)).toBeGreaterThanOrEqual(0);
  });

  it("non-transactional load-document emits layout-ready from fallback when animation frames are throttled", async () => {
    vi.useFakeTimers();
    const messages: unknown[] = [];
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) }
    };

    load({
      type: "load-document",
      html: "<h1>Hidden WebView</h1><p>Ready</p>",
      hasMermaid: false,
      renderId: 16,
    });

    await advanceLayoutReadyTimer();

    expect(findMessageIndex("layout-ready", messages)).toBe(-1);
    await vi.advanceTimersByTimeAsync(150);

    expect(findMessageIndex("layout-ready", messages)).toBeGreaterThanOrEqual(0);
    expect(messages.some((message: { type?: string; name?: string } | null) =>
      message?.type === "perf-mark" && message.name === "mm-layout-ready-frame-fallback")).toBe(true);
  });

  it("applies preferences carried by the settle probe before ack", async () => {
    const messages: unknown[] = [];
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) }
    };

    load({
      type: "minimap-policy",
      minimapPolicy: {
        minHostWidth: 500,
        minScrollableViewportRatio: 0.1,
        maxDetailedDocumentHeight: 10000,
      },
    });
    load({ type: "load-document", html: "<p>Loaded document</p>", hasMermaid: false });
    await Promise.resolve();
    await Promise.resolve();
    flushQueuedRafs();

    load({ type: "reading-preferences", ...makePreferences(false, "on") });
    flushQueuedRafs();
    messages.length = 0;

    expect(document.body.classList.contains("mm-has-minimap")).toBe(false);
    expect(document.documentElement.dataset.mmChrome).toBe("off");

    load({ ...makePreferences(true, "on"), type: "mode-settle-probe" });
    flushQueuedRafs();

    expect(document.body.classList.contains("mm-has-minimap")).toBe(true);
    expect(document.documentElement.dataset.mmChrome).toBe("on");
    expect(messages.some((message: { type?: string } | null) =>
      message?.type === "mode-toggle-settled")).toBe(true);
  });

  it("waits for the host-sized viewport before acking the settle probe", async () => {
    const messages: unknown[] = [];
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) }
    };
    Object.defineProperty(window, "innerWidth", {
      configurable: true,
      value: 900,
    });

    load({
      type: "minimap-policy",
      minimapPolicy: {
        minHostWidth: 500,
        minScrollableViewportRatio: 0.1,
        maxDetailedDocumentHeight: 10000,
      },
    });
    load({ type: "load-document", html: "<p>Loaded document</p>", hasMermaid: false });
    await Promise.resolve();
    await Promise.resolve();
    flushQueuedRafs();
    messages.length = 0;

    load({
      ...makePreferences(true, "on"),
      type: "mode-settle-probe",
      viewportWidth: 1600,
      viewportHeight: 600,
    });

    flushNextRaf();
    flushNextRaf();

    expect(messages.some((message: { type?: string } | null) =>
      message?.type === "mode-toggle-settled")).toBe(false);

    Object.defineProperty(window, "innerWidth", {
      configurable: true,
      value: 1600,
    });
    flushQueuedRafs();

    expect(messages.some((message: { type?: string } | null) =>
      message?.type === "mode-toggle-settled")).toBe(true);
  });

  it("echoes transaction generation on tagged mode settle ack", () => {
    const messages: unknown[] = [];
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) }
    };

    load({
      ...makePreferences(true, "on"),
      type: "mode-settle-probe",
      transactionGeneration: 42,
    });
    flushQueuedRafs();

    expect(messages.some((message) =>
      typeof message === "object"
      && message !== null
      && (message as { type?: string; transactionGeneration?: number }).type === "mode-toggle-settled"
      && (message as { transactionGeneration?: number }).transactionGeneration === 42)).toBe(true);
  });

  it("keeps an internal reveal shield until host starts the mode reveal", () => {
    const messages: unknown[] = [];
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) }
    };

    load({ type: "mode-reveal-prepare", durationMs: 180 });

    const shield = document.querySelector<HTMLElement>(".mm-mode-reveal-shield");
    expect(shield).toBeTruthy();
    expect(shield!.style.opacity).toBe("1");
    expect(shield!.style.position).toBe("fixed");

    load({
      ...makePreferences(true, "on"),
      type: "mode-settle-probe",
      transactionGeneration: 42,
    });
    flushQueuedRafs();

    expect(document.querySelector(".mm-mode-reveal-shield")).toBe(shield);
    expect(messages.some((message) =>
      typeof message === "object"
      && message !== null
      && (message as { type?: string; transactionGeneration?: number }).type === "mode-toggle-settled"
      && (message as { transactionGeneration?: number }).transactionGeneration === 42)).toBe(true);

    load({ type: "mode-reveal-start", durationMs: 0 });

    expect(document.querySelector(".mm-mode-reveal-shield")).toBeNull();
  });

  it("keeps a document reveal shield independent from the mode reveal shield", () => {
    load({ type: "document-reveal-prepare", durationMs: 0, theme: "dark" });

    const documentShield = document.querySelector<HTMLElement>(".mm-document-reveal-shield");
    expect(documentShield).toBeTruthy();
    expect(documentShield!.style.opacity).toBe("1");
    expect(documentShield!.style.position).toBe("fixed");
    expect(document.querySelector(".mm-mode-reveal-shield")).toBeNull();

    load({ type: "mode-reveal-prepare", durationMs: 180 });

    expect(document.querySelector(".mm-document-reveal-shield")).toBe(documentShield);
    expect(document.querySelector(".mm-mode-reveal-shield")).toBeTruthy();

    load({ type: "document-reveal-start", durationMs: 0 });

    expect(document.querySelector(".mm-document-reveal-shield")).toBeNull();
    expect(document.querySelector(".mm-mode-reveal-shield")).toBeTruthy();
  });

  it("lets a newer tagged settle probe supersede an older pending probe", () => {
    const messages: unknown[] = [];
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) }
    };

    load({
      ...makePreferences(true, "on"),
      type: "mode-settle-probe",
      transactionGeneration: 41,
    });
    load({
      ...makePreferences(true, "on"),
      type: "mode-settle-probe",
      transactionGeneration: 42,
    });
    flushQueuedRafs();

    const settleMessages = messages.filter((message): message is { type: "mode-toggle-settled"; transactionGeneration?: number } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: string }).type === "mode-toggle-settled");

    expect(settleMessages.at(-1)).toEqual({
      type: "mode-toggle-settled",
      transactionGeneration: 42,
    });
    expect(settleMessages.some((message) => message.transactionGeneration === 41)).toBe(false);
  });
});
