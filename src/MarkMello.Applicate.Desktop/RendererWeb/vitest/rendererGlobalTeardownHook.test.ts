// Guard for the GLOBAL bench teardown hook (vitest.config.ts -> setupFiles ->
// vitest/setup/rendererDeferredWorkTeardown.ts).
//
// WHAT THIS FILE PROVES, AND WHY IT IS SHAPED THIS WAY
// The hook's whole purpose is to reach renderer.ts's document-lifecycle owner from test
// files that never send `clear-document` themselves — 14 of the 21 renderer-importing
// files. So this file is deliberately shaped like one of those 14: it arms deferred work
// and NEVER sends `clear-document`. If the deferred work is cancelled anyway, the only
// thing that can have cancelled it is the global hook.
//
// WHY THERE IS NO TIMING DEPENDENCE
// The scheduler installed below is fully synthetic: it hands out handles and records them
// but NEVER fires anything. A recorded entry therefore cannot leave the registry by
// elapsing, only by an explicit `clearTimeout` / `cancelAnimationFrame`. That removes the
// classic failure of this kind of test — an assertion that silently passes because the
// work happened to run, or happened not to, inside an unmeasured window. Nothing here
// depends on how long anything takes, and this file arms no real timer of its own.
//
// WHY THE ASSERTION IS SPLIT ACROSS `it` AND `afterAll`
// Hook order in Vitest 4 is stack/LIFO, verified empirically on this bench: a test file's
// own `afterEach` hooks run BEFORE a setupFiles `afterEach`, and `afterAll` runs after
// both. So `afterAll` is the one observation point from which the global hook's effect is
// visible. The two-step assertion also pins causality: the entries are asserted STILL LIVE
// at the end of the test body, and CANCELLED by `afterAll`. The global hook is the only
// thing that runs in between, so a re-arm or an unrelated cancellation inside the module
// cannot masquerade as the hook doing its job.
import { afterAll, beforeAll, describe, expect, it } from "vitest";

type HostBridge = (msg: unknown) => void;

// Mirrors renderer.ts's HEAVY_LIVE_UPDATE_DEBOUNCE_MS — the debounce that armed the Node
// timer in the recorded crash.
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

type ScheduledEntry = {
  key: string;
  kind: "timeout" | "frame";
  delay: number;
  cancelled: boolean;
};

describe("the global bench teardown hook cancels deferred work for a file that never sends clear-document", () => {
  const scheduled = new Map<string, ScheduledEntry>();
  let restoreScheduler: () => void;
  let armed: ScheduledEntry[] = [];

  beforeAll(async () => {
    document.documentElement.innerHTML =
      `<body><main class="mm-document"><p data-mm-block-index="0">Loaded document</p></main></body>`;
    Object.defineProperty(document, "scrollingElement", {
      configurable: true,
      get: () => document.documentElement,
    });
    Object.defineProperty(document.documentElement, "clientHeight", { configurable: true, value: 600 });
    Object.defineProperty(document.documentElement, "scrollHeight", { configurable: true, value: 2400 });
    Object.defineProperty(window, "innerWidth", { configurable: true, value: 1600 });

    const realSetTimeout = window.setTimeout;
    const realClearTimeout = window.clearTimeout;
    const realRequestFrame = window.requestAnimationFrame;
    const realCancelFrame = window.cancelAnimationFrame;

    // Synthetic handles from a private counter. Nothing is handed to the real scheduler,
    // so nothing can fire; `cancelled` therefore records a real cancel call and only that.
    let nextHandle = 1;

    window.setTimeout = ((_handler: unknown, delay?: number) => {
      const id = nextHandle++;
      const key = `timeout:${id}`;
      scheduled.set(key, { key, kind: "timeout", delay: delay ?? 0, cancelled: false });
      return id;
    }) as unknown as typeof window.setTimeout;

    window.clearTimeout = ((id?: number) => {
      const entry = scheduled.get(`timeout:${String(id)}`);
      if (entry) {
        entry.cancelled = true;
      }
    }) as typeof window.clearTimeout;

    window.requestAnimationFrame = ((_callback: FrameRequestCallback) => {
      const id = nextHandle++;
      const key = `frame:${id}`;
      scheduled.set(key, { key, kind: "frame", delay: 0, cancelled: false });
      return id;
    }) as typeof window.requestAnimationFrame;

    window.cancelAnimationFrame = ((id: number) => {
      const entry = scheduled.get(`frame:${String(id)}`);
      if (entry) {
        entry.cancelled = true;
      }
    }) as typeof window.cancelAnimationFrame;

    restoreScheduler = () => {
      window.setTimeout = realSetTimeout;
      window.clearTimeout = realClearTimeout;
      window.requestAnimationFrame = realRequestFrame;
      window.cancelAnimationFrame = realCancelFrame;
    };

    await import("../src/renderer");
  });

  afterAll(() => {
    try {
      // Preconditions are asserted in the test body; by here the global hook has run.
      const survivors = armed
        .filter(entry => !entry.cancelled)
        .map(entry => `${entry.kind}@${entry.delay}ms`);

      // A survivor here is exactly the Node timer / frame that fired after Vitest removed
      // `window` and produced `ReferenceError: window is not defined` — reported as a
      // passing suite with a non-zero exit code.
      expect(
        survivors,
        "the global teardown hook must have routed this file through the document-lifecycle owner"
      ).toEqual([]);
    } finally {
      // Restored only here, never in an afterEach: a setupFiles afterEach runs AFTER a test
      // file's afterEach, so restoring earlier would send the hook's cancel calls to the
      // real scheduler and the registry would record nothing — a false failure.
      restoreScheduler();
    }
  });

  it("arms deferred work and leaves it armed — this file never sends clear-document", () => {
    const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;
    expect(typeof load, "renderer.ts must have installed its host-message seam").toBe("function");

    // applyReadingPreferences defers through one rAF, so drive that frame synchronously to
    // reach flushPendingReadingPreferences, which is what arms the heavy-live-update timer.
    const deferredFrame = window.requestAnimationFrame;
    window.requestAnimationFrame = ((callback: FrameRequestCallback) => {
      callback(0);
      return 0;
    }) as typeof window.requestAnimationFrame;
    load(PREFS);
    window.requestAnimationFrame = deferredFrame;

    // Arm queueMinimapViewportUpdate's frame.
    load(MINIMAP_POLICY);

    armed = [...scheduled.values()].filter(entry =>
      entry.kind === "frame" || entry.delay === HEAVY_LIVE_UPDATE_DEBOUNCE_MS);

    // Preconditions. Without these a green afterAll would prove nothing, because the work
    // under test would simply never have been scheduled.
    expect(
      armed.filter(entry => entry.kind === "timeout" && entry.delay === HEAVY_LIVE_UPDATE_DEBOUNCE_MS),
      "scheduleHeavyLiveUpdate must have armed its debounce timer"
    ).toHaveLength(1);
    expect(
      armed.filter(entry => entry.kind === "frame"),
      "queueMinimapViewportUpdate must have armed a frame"
    ).toHaveLength(1);

    // The causal pin: still armed when the test body ends. Anything that cancels these
    // between here and `afterAll` is the global teardown hook, because nothing else runs.
    expect(
      armed.filter(entry => entry.cancelled),
      "nothing may have cancelled this work yet — the test body must hand it over still armed"
    ).toEqual([]);
  });
});
