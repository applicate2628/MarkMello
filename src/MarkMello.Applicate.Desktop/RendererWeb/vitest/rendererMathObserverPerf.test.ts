import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

type HostBridge = (message: unknown) => void;

type MathPerfWindow = Window & {
  __mmMathObserverPerfEnabled?: boolean;
  __mmPerfReport: () => {
    marks: Array<{ name: string; detail?: unknown }>;
  };
  __mmRendererLoad: HostBridge;
  katex?: {
    render: () => void;
  };
  chrome?: {
    webview?: {
      postMessage: HostBridge;
    };
  };
};

describe("renderer math observer performance marker", () => {
  let observerCallback: IntersectionObserverCallback | null;
  let postedHostMessages: unknown[];
  let rafCallbacks: FrameRequestCallback[];

  beforeEach(async () => {
    vi.resetModules();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    document.documentElement.innerHTML = `
      <body>
        <main class="mm-document">
          <p data-mm-block-index="0"><span data-tex="x"></span></p>
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
    Object.defineProperty(window, "innerHeight", {
      configurable: true,
      value: 600,
    });
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockReturnValue({
      x: 0,
      y: 0,
      width: 600,
      height: 40,
      top: 0,
      right: 600,
      bottom: 40,
      left: 0,
      toJSON: () => ({}),
    } as DOMRect);

    observerCallback = null;
    class FakeIntersectionObserver {
      constructor(callback: IntersectionObserverCallback) {
        observerCallback = callback;
      }

      observe(): void {}
      unobserve(): void {}
      disconnect(): void {}
    }
    vi.stubGlobal("IntersectionObserver", FakeIntersectionObserver as unknown as typeof IntersectionObserver);
    rafCallbacks = [];
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
      rafCallbacks.push(callback);
      return rafCallbacks.length;
    });

    postedHostMessages = [];
    const hostWindow = window as MathPerfWindow;
    hostWindow.katex = { render: vi.fn() };
    hostWindow.chrome = {
      webview: {
        postMessage: (message) => postedHostMessages.push(message),
      },
    };
    await import("../src/renderer");
  });

  afterEach(() => {
    const hostWindow = window as MathPerfWindow;
    hostWindow.__mmRendererLoad?.({ type: "clear-document" });
    delete hostWindow.__mmMathObserverPerfEnabled;
    delete hostWindow.katex;
    delete hostWindow.chrome;
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    document.body.innerHTML = "";
  });

  function mathObserverMarks(): Array<{ name: string; detail?: unknown }> {
    return (window as MathPerfWindow).__mmPerfReport().marks.filter(
      mark => mark.name === "mm-math-observer-window"
    );
  }

  function minimapViewportPerfMarks(): unknown[] {
    return postedHostMessages.filter(message => {
      if (typeof message !== "object" || message === null) {
        return false;
      }
      const record = message as Record<string, unknown>;
      return record["type"] === "perf-mark" && record["name"] === "mm-minimap-viewport-update";
    });
  }

  function runScrollFrame(): void {
    document.dispatchEvent(new Event("scroll"));
    let frameCount = 0;
    while (rafCallbacks.length > 0) {
      const frame = rafCallbacks.shift()!;
      frame(performance.now());
      frameCount++;
      if (frameCount > 20) {
        throw new Error("Renderer kept queuing animation frames during the scroll probe");
      }
    }
    if (frameCount === 0) {
      throw new Error("Expected scroll IPC to queue a frame");
    }
  }

  it("emits only while the dynamic renderer-window flag is exactly true", () => {
    const hostWindow = window as MathPerfWindow;
    hostWindow.__mmRendererLoad({
      type: "reading-preferences",
      fontFamily: "serif",
      fontSize: 16,
      lineHeight: 1.6,
      maxWidth: 720,
      minimapMode: "off",
      viewerChromeEnabled: true,
      documentScrollEnabled: true,
      wheelProxyEnabled: false,
      widthResizerVisibility: "always",
    });
    const applyPreferences = rafCallbacks.shift();
    if (!applyPreferences) {
      throw new Error("Expected reading preferences to queue a frame");
    }
    applyPreferences(performance.now());

    expect(observerCallback).not.toBeNull();
    expect(mathObserverMarks()).toHaveLength(0);

    hostWindow.__mmMathObserverPerfEnabled = true;
    observerCallback!([], {} as IntersectionObserver);
    expect(mathObserverMarks()).toHaveLength(1);

    hostWindow.__mmMathObserverPerfEnabled = false;
    observerCallback!([], {} as IntersectionObserver);
    expect(mathObserverMarks()).toHaveLength(1);
  });

  it("posts minimap viewport timing only while the telemetry flag is exactly true", () => {
    const hostWindow = window as MathPerfWindow;
    postedHostMessages = [];

    runScrollFrame();
    expect(minimapViewportPerfMarks()).toHaveLength(0);

    hostWindow.__mmMathObserverPerfEnabled = true;
    runScrollFrame();
    const enabledMarkCount = minimapViewportPerfMarks().length;
    expect(enabledMarkCount).toBeGreaterThan(0);

    hostWindow.__mmMathObserverPerfEnabled = false;
    runScrollFrame();
    expect(minimapViewportPerfMarks()).toHaveLength(enabledMarkCount);
  });
});
