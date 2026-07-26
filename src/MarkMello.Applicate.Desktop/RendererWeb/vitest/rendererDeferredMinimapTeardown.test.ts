// Resource-lifecycle guard for renderer.ts's deferred minimap viewport pipeline.
//
// Recorded failure (2026-07-18, recurred 2026-07-26): the full renderer suite
// reported every file and test passed and then exited 1 on
//
//   ReferenceError: window is not defined
//    ❯ queueMinimapViewportUpdate RendererWeb/src/renderer.ts (window.requestAnimationFrame)
//    ❯ Timeout._onTimeout        RendererWeb/src/renderer.ts (scheduleHeavyLiveUpdate)
//    ❯ listOnTimeout node:internal/timers
//
// i.e. a real Node timer armed by `scheduleHeavyLiveUpdate` outlived the test
// file, fired after Vitest removed `window` from the global scope, and reached
// `queueMinimapViewportUpdate`. The root cause is not the missing `window`: it
// is that `resetModuleGlobalsForLoadDocument` — the document-lifecycle owner
// that BOTH `load-document` and `clear-document` route through, and which
// already cancels `layoutReadyTimer`, `minimapContentRefreshTimer`,
// `cachedGeometryRefreshTimer`, `mermaidCacheResumeTimer` and
// `themeMermaidRefreshTimer` — did not cancel the deferred *viewport* entries.
//
// This guard asserts the cancellation contract directly: once the document
// lifecycle owner has run, nothing it scheduled is still scheduled. It fails if
// a fix only clears the `minimapViewportFrameRequested` bookkeeping flag without
// actually calling `cancelAnimationFrame`, because the registry below is driven
// by the real cancel calls, not by module state.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

type HostBridge = (msg: unknown) => void;

// Mirrors renderer.ts's HEAVY_LIVE_UPDATE_DEBOUNCE_MS — the debounce that armed
// the Node timer in the recorded crash.
const HEAVY_LIVE_UPDATE_DEBOUNCE_MS = 80;

const PREFS = {
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
} as const;

const MINIMAP_POLICY = {
  type: "minimap-policy",
  minimapPolicy: { minHostWidth: 0, minScrollableViewportRatio: 0, maxDetailedDocumentHeight: 1e9 },
} as const;

type ScheduledEntry = { key: string; kind: "timeout" | "frame"; delay: number };

describe("renderer deferred minimap work is cancelled by the document lifecycle owner", () => {
  let live: Map<string, ScheduledEntry>;
  let restoreScheduler: () => void;

  const snapshot = (): Set<string> => new Set(live.keys());
  const newSince = (before: Set<string>): ScheduledEntry[] =>
    [...live.values()].filter(entry => !before.has(entry.key));

  beforeEach(async () => {
    vi.resetModules();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    live = new Map();

    document.documentElement.innerHTML =
      `<body><main class="mm-document"><p data-mm-block-index="0">Loaded document</p></main></body>`;
    Object.defineProperty(document, "scrollingElement", {
      configurable: true,
      get: () => document.documentElement,
    });
    Object.defineProperty(document.documentElement, "clientHeight", { configurable: true, value: 600 });
    Object.defineProperty(document.documentElement, "scrollHeight", { configurable: true, value: 2400 });
    Object.defineProperty(window, "innerWidth", { configurable: true, value: 1600 });

    // Wrap the real schedulers by direct assignment (not vi.stubGlobal, which
    // does not reliably intercept the `window.clearTimeout` calls renderer.ts
    // makes). The registry only loses an entry when the module actually calls
    // clearTimeout / cancelAnimationFrame with that handle.
    const realSetTimeout = window.setTimeout;
    const realClearTimeout = window.clearTimeout;
    const realRequestFrame = window.requestAnimationFrame;
    const realCancelFrame = window.cancelAnimationFrame;

    window.setTimeout = ((handler: (...args: unknown[]) => unknown, delay?: number, ...rest: unknown[]) => {
      const id = realSetTimeout.call(window, (...args: unknown[]) => {
        live.delete(`timeout:${String(id)}`);
        return handler(...args);
      }, delay, ...rest) as unknown as number;
      live.set(`timeout:${String(id)}`, { key: `timeout:${String(id)}`, kind: "timeout", delay: delay ?? 0 });
      return id;
    }) as typeof window.setTimeout;

    window.clearTimeout = ((id?: number) => {
      live.delete(`timeout:${String(id)}`);
      return realClearTimeout.call(window, id as never);
    }) as typeof window.clearTimeout;

    window.requestAnimationFrame = ((callback: FrameRequestCallback) => {
      const id = realRequestFrame.call(window, (time: number) => {
        live.delete(`frame:${String(id)}`);
        callback(time);
      });
      live.set(`frame:${String(id)}`, { key: `frame:${String(id)}`, kind: "frame", delay: 0 });
      return id;
    }) as typeof window.requestAnimationFrame;

    window.cancelAnimationFrame = ((id: number) => {
      live.delete(`frame:${String(id)}`);
      return realCancelFrame.call(window, id);
    }) as typeof window.cancelAnimationFrame;

    restoreScheduler = () => {
      window.setTimeout = realSetTimeout;
      window.clearTimeout = realClearTimeout;
      window.requestAnimationFrame = realRequestFrame;
      window.cancelAnimationFrame = realCancelFrame;
    };

    await import("../src/renderer");
  });

  afterEach(() => {
    (window as unknown as { __mmRendererLoad?: HostBridge }).__mmRendererLoad?.({ type: "clear-document" });
    restoreScheduler();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    document.body.innerHTML = "";
  });

  it("leaves no minimap viewport timer or frame scheduled after clear-document", () => {
    const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;
    const before = snapshot();

    // Arm scheduleHeavyLiveUpdate: applyReadingPreferences defers through one
    // rAF, so drive that frame synchronously to reach flushPendingReadingPreferences.
    const deferredFrame = window.requestAnimationFrame;
    window.requestAnimationFrame = ((callback: FrameRequestCallback) => {
      callback(0);
      return 0;
    }) as typeof window.requestAnimationFrame;
    load(PREFS);
    window.requestAnimationFrame = deferredFrame;

    // Arm queueMinimapViewportUpdate's frame.
    load(MINIMAP_POLICY);

    // Scoped to the two entries this owner is responsible for. `load(PREFS)`
    // also bootstraps initialRenderPipeline, which arms its own 1200 ms
    // initial-visible-ready timeout; that one has a real cancellation path
    // (`currentController.cancel()`, cleared in a `finally`) which settles on a
    // microtask, so asserting on it here would only couple this guard to
    // another module's async settling.
    const armed = newSince(before).filter(entry =>
      entry.kind === "frame" || entry.delay === HEAVY_LIVE_UPDATE_DEBOUNCE_MS);
    // Preconditions: without these a green run would prove nothing, because the
    // work under test would simply never have been scheduled.
    expect(
      armed.filter(entry => entry.kind === "timeout" && entry.delay === HEAVY_LIVE_UPDATE_DEBOUNCE_MS),
      "scheduleHeavyLiveUpdate must have armed its debounce timer"
    ).toHaveLength(1);
    expect(
      armed.filter(entry => entry.kind === "frame"),
      "queueMinimapViewportUpdate must have armed a frame"
    ).toHaveLength(1);

    load({ type: "clear-document" });

    const survivors = armed
      .filter(entry => live.has(entry.key))
      .map(entry => `${entry.kind}@${entry.delay}ms`);
    // A survivor here is the exact Node timer / frame that fired after Vitest
    // removed `window` and produced `ReferenceError: window is not defined`.
    expect(survivors).toEqual([]);
  });

  it("re-arms a fresh viewport frame after cancellation (the frame token is not left latched)", () => {
    const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;

    load(MINIMAP_POLICY);
    load({ type: "clear-document" });

    const before = snapshot();
    load(MINIMAP_POLICY);
    expect(
      newSince(before).filter(entry => entry.kind === "frame"),
      "cancelling must also release minimapViewportFrameRequested, or every later update is swallowed"
    ).toHaveLength(1);
  });
});
