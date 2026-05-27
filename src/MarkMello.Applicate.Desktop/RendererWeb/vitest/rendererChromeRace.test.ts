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
});
