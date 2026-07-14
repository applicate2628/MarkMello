import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

type HostBridge = (msg: unknown) => void;

type ReadingPreferencesMessage = {
  type: "reading-preferences";
  fontFamily: "serif";
  fontSize: number;
  lineHeight: number;
  maxWidth: number;
  minimapMode: "off";
  viewerChromeEnabled: boolean;
  documentScrollEnabled: boolean;
  wheelProxyEnabled: boolean;
  widthResizerVisibility: "always";
};

const makePreferences = (viewerChromeEnabled: boolean): ReadingPreferencesMessage => ({
  type: "reading-preferences",
  fontFamily: "serif",
  fontSize: 16,
  lineHeight: 1.6,
  maxWidth: 720,
  minimapMode: "off",
  viewerChromeEnabled,
  documentScrollEnabled: true,
  wheelProxyEnabled: false,
  widthResizerVisibility: "always",
});

describe("renderer source-line scroll sync", () => {
  let rafCallbacks: FrameRequestCallback[];
  let messages: unknown[];

  beforeEach(async () => {
    vi.resetModules();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    document.documentElement.innerHTML = `
      <body>
        <main class="mm-document">
          <p data-mm-block-index="0" data-mm-source-line="10" data-mm-source-end-line="20">First</p>
          <p data-mm-block-index="1" data-mm-source-line="30" data-mm-source-end-line="30">Second</p>
        </main>
      </body>`;
    Object.defineProperty(document, "scrollingElement", {
      configurable: true,
      get: () => document.documentElement,
    });
    Object.defineProperty(document.documentElement, "scrollTop", {
      configurable: true,
      writable: true,
      value: 0,
    });
    Object.defineProperty(document.documentElement, "clientHeight", {
      configurable: true,
      value: 600,
    });
    Object.defineProperty(document.documentElement, "scrollHeight", {
      configurable: true,
      value: 2000,
    });
    Object.defineProperty(window, "scrollY", {
      configurable: true,
      writable: true,
      value: 0,
    });

    rafCallbacks = [];
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
      rafCallbacks.push(callback);
      return rafCallbacks.length;
    });

    messages = [];
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) }
    };

    await import("../src/renderer");
  });

  afterEach(() => {
    (window as unknown as { __mmRendererLoad?: HostBridge }).__mmRendererLoad?.({ type: "clear-document" });
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    document.body.innerHTML = "";
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

  function dispatchScroll(): void {
    document.dispatchEvent(new Event("scroll"));
    flushQueuedRafs();
  }

  it("skips preview source-line sampling while pure viewer chrome is active", () => {
    load(makePreferences(true));
    flushQueuedRafs();
    const sourceAnchor = document.querySelector<HTMLElement>("[data-mm-source-line]")!;
    vi.spyOn(sourceAnchor, "getBoundingClientRect").mockImplementation(() => {
      throw new Error("viewer scroll forced source-line anchor geometry");
    });
    messages.length = 0;

    expect(() => dispatchScroll()).not.toThrow();

    expect(messages.some((message: { type?: string } | null) =>
      message?.type === "preview-source-line")).toBe(false);
  });

  it("keeps preview source-line sampling enabled for edit preview", () => {
    load(makePreferences(false));
    flushQueuedRafs();
    const anchors = Array.from(document.querySelectorAll<HTMLElement>("[data-mm-source-line]"));
    vi.spyOn(anchors[0]!, "getBoundingClientRect").mockReturnValue({ top: 100 } as DOMRect);
    vi.spyOn(anchors[1]!, "getBoundingClientRect").mockReturnValue({ top: 500 } as DOMRect);
    messages.length = 0;

    dispatchScroll();

    expect(messages).toContainEqual({
      type: "preview-source-line",
      sourceLine: 15,
    });
  });
});
