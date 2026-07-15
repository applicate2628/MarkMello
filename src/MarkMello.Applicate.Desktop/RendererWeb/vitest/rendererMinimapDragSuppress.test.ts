import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

type HostBridge = (message: unknown) => void;

type DragPerfWindow = Window & {
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

const DOCUMENT_HTML = `
  <p data-mm-block-index="0" data-mm-source-line="10" data-mm-source-end-line="20">
    <span data-tex="x"></span>
  </p>
  <p data-mm-block-index="1" data-mm-source-line="30" data-mm-source-end-line="40">
    Second block
  </p>`;

describe("renderer minimap panning suppression", () => {
  let rafCallbacks: FrameRequestCallback[];
  let postedHostMessages: unknown[];
  let liveBlockLayoutReadCount: number;

  beforeEach(async () => {
    vi.resetModules();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    document.documentElement.innerHTML = `<body><main class="mm-document">${DOCUMENT_HTML}</main></body>`;

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
      value: 2400,
    });
    Object.defineProperty(window, "innerWidth", {
      configurable: true,
      value: 1600,
    });
    Object.defineProperty(window, "innerHeight", {
      configurable: true,
      value: 600,
    });
    Object.defineProperty(window, "scrollY", {
      configurable: true,
      get: () => document.documentElement.scrollTop,
    });

    rafCallbacks = [];
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
      rafCallbacks.push(callback);
      return rafCallbacks.length;
    });

    class FakeIntersectionObserver {
      constructor(_callback: IntersectionObserverCallback) {}
      observe(): void {}
      unobserve(): void {}
      disconnect(): void {}
    }
    vi.stubGlobal("IntersectionObserver", FakeIntersectionObserver as unknown as typeof IntersectionObserver);

    postedHostMessages = [];
    const hostWindow = window as DragPerfWindow;
    hostWindow.katex = { render: vi.fn() };
    hostWindow.chrome = {
      webview: {
        postMessage: (message) => postedHostMessages.push(message),
      },
    };

    vi.spyOn(window, "scrollTo").mockImplementation((options?: ScrollToOptions | number, y?: number) => {
      const requestedTop = typeof options === "number"
        ? (y ?? options)
        : (options?.top ?? document.documentElement.scrollTop);
      document.documentElement.scrollTop = requestedTop;
      document.dispatchEvent(new Event("scroll"));
    });

    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function () {
      const element = this as HTMLElement;
      if (element.classList.contains("mm-minimap")) {
        return rect(0, 0, 80, 300);
      }
      if (element.classList.contains("mm-minimap-viewport")) {
        const top = readTranslateY(element.style.transform);
        return rect(0, top, 80, Number.parseFloat(element.style.height) || 67);
      }
      if (element.classList.contains("mm-document")) {
        return rect(120, 0, 720, 2400);
      }
      if (element.hasAttribute("data-mm-block-index")) {
        const blockIndex = Number.parseInt(element.dataset["mmBlockIndex"] ?? "0", 10);
        const inMinimapClone = element.closest(".mm-minimap-content") !== null;
        const top = blockIndex * 1200 - (inMinimapClone ? 0 : document.documentElement.scrollTop);
        return rect(120, top, 720, 1200);
      }
      return rect(0, 0, 0, 0);
    });

    await import("../src/renderer");
  });

  afterEach(() => {
    const hostWindow = window as DragPerfWindow;
    hostWindow.__mmRendererLoad?.({ type: "clear-document" });
    delete hostWindow.__mmMathObserverPerfEnabled;
    delete hostWindow.katex;
    delete hostWindow.chrome;
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    document.body.innerHTML = "";
  });

  function load(message: unknown): void {
    (window as DragPerfWindow).__mmRendererLoad(message);
  }

  function flushQueuedRafs(limit = 40): void {
    for (let frame = 0; frame < limit && rafCallbacks.length > 0; frame++) {
      const callback = rafCallbacks.shift()!;
      callback(performance.now());
    }
    if (rafCallbacks.length > 0) {
      throw new Error("requestAnimationFrame queue did not settle");
    }
  }

  async function prepareDetailedMinimap(): Promise<HTMLElement> {
    load({
      type: "minimap-policy",
      minimapPolicy: {
        minHostWidth: 500,
        minScrollableViewportRatio: 0.1,
        maxDetailedDocumentHeight: 10000,
      },
    });
    load({ type: "load-document", html: DOCUMENT_HTML, hasMermaid: false });
    await Promise.resolve();
    await Promise.resolve();
    flushQueuedRafs();
    load({
      type: "mode-settle-probe",
      fontFamily: "serif",
      fontSize: 16,
      lineHeight: 1.6,
      maxWidth: 720,
      minimapMode: "on",
      viewerChromeEnabled: false,
      documentScrollEnabled: true,
      wheelProxyEnabled: false,
      widthResizerVisibility: "always",
    });
    flushQueuedRafs();

    const minimap = document.querySelector<HTMLElement>(".mm-minimap");
    const minimapContent = document.querySelector<HTMLElement>(".mm-minimap-content");
    const viewport = document.querySelector<HTMLElement>(".mm-minimap-viewport");
    const documentElement = document.querySelector<HTMLElement>("body > main.mm-document");
    expect(minimap).not.toBeNull();
    expect(minimapContent).not.toBeNull();
    expect(viewport).not.toBeNull();
    expect(documentElement).not.toBeNull();

    Object.defineProperty(minimap!, "clientWidth", { configurable: true, value: 80 });
    Object.defineProperty(minimap!, "clientHeight", { configurable: true, value: 300 });
    Object.defineProperty(minimapContent!, "scrollHeight", { configurable: true, value: 2400 });
    Object.defineProperty(documentElement!, "clientWidth", { configurable: true, value: 720 });
    Object.defineProperty(minimap!, "setPointerCapture", { configurable: true, value: vi.fn() });
    Object.defineProperty(minimap!, "releasePointerCapture", { configurable: true, value: vi.fn() });

    liveBlockLayoutReadCount = 0;
    for (const block of Array.from(document.querySelectorAll<HTMLElement>("[data-mm-block-index]"))) {
      const blockIndex = Number.parseInt(block.dataset["mmBlockIndex"] ?? "0", 10);
      const inMinimapClone = block.closest(".mm-minimap-content") !== null;
      Object.defineProperty(block, "offsetTop", {
        configurable: true,
        get: () => {
          if (!inMinimapClone) liveBlockLayoutReadCount++;
          return blockIndex * 1200;
        },
      });
      Object.defineProperty(block, "offsetHeight", {
        configurable: true,
        get: () => {
          if (!inMinimapClone) liveBlockLayoutReadCount++;
          return 1200;
        },
      });
    }

    document.dispatchEvent(new Event("scroll"));
    flushQueuedRafs();
    return minimap!;
  }

  it("suppresses intermediate panning work and flushes it once at the final scroll position", async () => {
    const minimap = await prepareDetailedMinimap();
    const hostWindow = window as DragPerfWindow;
    hostWindow.__mmMathObserverPerfEnabled = true;
    postedHostMessages.length = 0;
    liveBlockLayoutReadCount = 0;
    const initialMathScrollMarks = countMathScrollMarks();

    minimap.dispatchEvent(pointerEvent("pointerdown", 10));
    minimap.dispatchEvent(pointerEvent("pointermove", 110));
    flushQueuedRafs();
    minimap.dispatchEvent(pointerEvent("pointermove", 210));
    flushQueuedRafs();

    const finalDragScrollTop = document.documentElement.scrollTop;
    expect(finalDragScrollTop).toBeGreaterThan(1000);
    expect(liveBlockLayoutReadCount).toBe(0);
    expect(countMathScrollMarks()).toBe(initialMathScrollMarks);
    // The host scrollbar overlay tracks the live drag: the scroll POSITION is
    // published each suppressed frame (2 pointermoves -> 2 messages), so the
    // scrollbar follows the minimap instead of jumping only on release. The heavy
    // per-frame work stays suppressed (liveBlockLayoutReadCount === 0 above, math /
    // minimap-update / preview-source-line below), flushed once at drag end.
    const dragScrollMessages = messagesOfType("scroll");
    expect(dragScrollMessages).toHaveLength(2);
    expect(dragScrollMessages[dragScrollMessages.length - 1]).toMatchObject({ scrollTop: finalDragScrollTop });
    expect(messagesOfType("preview-source-line")).toHaveLength(0);
    expect(perfMessages("mm-minimap-viewport-update")).toHaveLength(0);

    minimap.dispatchEvent(pointerEvent("pointerup", 210));
    flushQueuedRafs();

    expect(document.documentElement.scrollTop).toBe(finalDragScrollTop);
    expect(liveBlockLayoutReadCount).toBeGreaterThan(0);
    expect(countMathScrollMarks()).toBe(initialMathScrollMarks + 1);
    // 2 lightweight position-only messages during the drag + 1 full flush at release.
    expect(messagesOfType("scroll")).toHaveLength(3);
    expect(messagesOfType("preview-source-line")).toHaveLength(1);
    expect(perfMessages("mm-minimap-viewport-update")).toHaveLength(1);
    const endMarks = perfMessages("mm-minimap-drag-suppress-end");
    expect(endMarks).toHaveLength(1);
    expect(JSON.parse(endMarks[0]!.detail ?? "{}")).toMatchObject({
      suppressedScrollFrames: 2,
      intermediateHeavyUpdates: 0,
      finalHeavyUpdates: 1,
      finalScrollTop: finalDragScrollTop,
    });
  });

  function countMathScrollMarks(): number {
    return (window as DragPerfWindow).__mmPerfReport().marks.filter(mark =>
      mark.name === "mm-math-observer-window"
      && typeof mark.detail === "object"
      && mark.detail !== null
      && (mark.detail as { reason?: unknown }).reason === "scroll").length;
  }

  function messagesOfType(type: string): unknown[] {
    return postedHostMessages.filter(message =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === type);
  }

  function perfMessages(name: string): Array<{ detail?: string }> {
    return postedHostMessages.filter((message): message is { type: "perf-mark"; name: string; detail?: string } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "perf-mark"
      && (message as { name?: unknown }).name === name);
  }
});

function pointerEvent(type: string, clientY: number): MouseEvent {
  const event = new MouseEvent(type, {
    bubbles: true,
    cancelable: true,
    button: 0,
    clientY,
  });
  Object.defineProperty(event, "pointerId", {
    configurable: true,
    value: 1,
  });
  return event;
}

function rect(left: number, top: number, width: number, height: number): DOMRect {
  return {
    x: left,
    y: top,
    width,
    height,
    top,
    right: left + width,
    bottom: top + height,
    left,
    toJSON: () => ({}),
  } as DOMRect;
}

function readTranslateY(transform: string): number {
  const match = /translateY\((-?\d+(?:\.\d+)?)px\)/.exec(transform);
  return match ? Number.parseFloat(match[1]!) : 0;
}
