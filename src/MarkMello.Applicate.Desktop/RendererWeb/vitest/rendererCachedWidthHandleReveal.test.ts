import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The width handle's `hidden` flag is computed from `hasInitialLayoutSettled`,
// which `resetModuleGlobals` clears at the top of EVERY load. On the cached
// restore path `ensureChromeNodes` runs the width-handle write (called from
// loadDocument.ts, before the completion callback) while the flag is still
// false, and `completeCachedDocumentLoad` raises the flag afterwards. The
// restore's settle owner therefore owes the same pairing the cold path's
// `initialVisibleReady` owes -- raise the flag AND re-run the write. Without
// it the restore leaves the handle hidden.
//
// The case pinned here is a restore whose cached entry carries NO minimap
// snapshot: `captureMinimapSnapshot` returns null for an empty minimap
// content, which is what `refreshMinimapContent` leaves behind whenever
// `shouldBuildDetailedMinimapContent` is not allowed (minimap off,
// non-scrollable document, or auto-heavy). That is the sub-path with nothing
// else to cover the gap -- on a snapshot restore `restoreCachedMinimapContent`
// happens to schedule a frame that re-runs the same write, which is why the
// defect went unreported.
//
// No animation frame is allowed to run between the restore and the assertion,
// so a reveal deferred to a frame, a timer, or a poll cannot pass this.

type HostBridge = (message: unknown) => void;

const FIRST_HTML = "<h1 id='first'>First</h1><p>cached document</p>";
const SECOND_HTML = "<h1 id='second'>Second</h1><p>other document</p>";

// `minimapMode: "off"` guarantees the minimap content stays empty, so the
// outgoing document is stored WITHOUT a minimap snapshot.
function makePreferences(viewerChromeEnabled: boolean) {
  return {
    type: "reading-preferences",
    fontFamily: "serif",
    fontSize: 16,
    lineHeight: 1.6,
    maxWidth: 720,
    minimapMode: "off",
    viewerChromeEnabled,
    documentScrollEnabled: true,
    wheelProxyEnabled: false,
    widthResizerVisibility: "on-hover",
  };
}

async function loadRendererWithMessages() {
  vi.resetModules();
  // <head><title> mirrors the shipped shell; happy-dom's title setter
  // dereferences this.head unguarded, and these loads carry a documentName.
  document.documentElement.innerHTML =
    `<head><title>MarkMello</title></head>`
    + `<body><main class="mm-document"></main></body>`;
  const messages: unknown[] = [];
  (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
    webview: { postMessage: (m: unknown) => messages.push(m) }
  };
  await import("../src/renderer");
  const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;
  return { load, messages };
}

async function letPipelineSettle(): Promise<void> {
  await new Promise(resolve => setTimeout(resolve, 700));
}

// The host sends reading-preferences and then documents; `applyReadingPreferences`
// only QUEUES, flushing on a frame, so the tests below let that frame land before
// the first load rather than racing it. Sending both in one task would exercise a
// different (and separately defective) ordering inside
// `flushPendingReadingPreferences`, not the restore ordering under test here.
async function letPreferencesFlush(): Promise<void> {
  await new Promise(resolve => setTimeout(resolve, 200));
}

function rendererCacheKey(html: string, theme: "light" | "dark" | "classic-white"): string {
  let hash = 2166136261;
  for (let index = 0; index < html.length; index++) {
    hash ^= html.charCodeAt(index);
    hash = Math.imul(hash, 16777619);
  }
  return `${theme}|${html.length}|${(hash >>> 0).toString(16).padStart(8, "0")}`;
}

function hasPerfMark(messages: unknown[], name: string): boolean {
  return messages.some(message =>
    typeof message === "object"
    && message !== null
    && (message as { type?: unknown }).type === "perf-mark"
    && (message as { name?: unknown }).name === name);
}

function widthHandle(): HTMLElement {
  const handle = document.querySelector<HTMLElement>(".mm-width-handle");
  if (!handle) {
    throw new Error("expected the width handle to be mounted");
  }
  return handle;
}

beforeEach(() => {
  delete (window as unknown as { chrome?: unknown }).chrome;
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("cached restore width-handle reveal", () => {
  it("leaves the width handle visible after a cached restore, without a frame", async () => {
    const { load, messages } = await loadRendererWithMessages();

    load(makePreferences(true));
    await letPreferencesFlush();
    load({ type: "load-document", html: FIRST_HTML, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();

    // Anti-vacuity: the cold path's settle owner must have revealed it, or the
    // restore assertion below would be asserting a state that never holds.
    expect(widthHandle().hidden, "a settled cold load leaves the handle visible").toBe(false);

    load({ type: "load-document", html: SECOND_HTML, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    // From here on NO animation frame runs, so nothing outside the restore's
    // own synchronous burst can reveal the handle.
    const frames: FrameRequestCallback[] = [];
    vi.spyOn(window, "requestAnimationFrame").mockImplementation(callback => {
      frames.push(callback);
      return frames.length;
    });

    messages.length = 0;
    load({
      type: "load-cached-document",
      cacheKey: rendererCacheKey(FIRST_HTML, "light"),
      documentName: "first.md",
      theme: "light",
      hasMermaid: false,
      renderId: 3,
    });

    // Anti-vacuity: a cache MISS returns from applyLoadDocument before
    // resetModuleGlobals, so the handle would still carry the previous
    // document's visible state and this test would pass having exercised
    // nothing.
    expect(hasPerfMark(messages, "mm-load-document-cache-hit"), "the restore must hit the cache").toBe(true);
    // And it must be the no-snapshot sub-path: a minimap cache hit would queue
    // the frame callback that papers over the ordering.
    expect(hasPerfMark(messages, "mm-minimap-cache-hit"), "this restore must carry no minimap snapshot").toBe(false);
    // Frames WERE requested by this restore and deliberately left unrun.
    expect(frames.length, "the restore should have queued frames we are not running").toBeGreaterThan(0);

    expect(widthHandle().hidden, "the cached restore must leave the handle visible").toBe(false);
  });

  // Only-forward: the reveal must not widen the visibility predicate. Viewer
  // chrome off (edit-preview) still hides the handle across a cached restore.
  it("keeps the width handle hidden across a cached restore while viewer chrome is off", async () => {
    const { load, messages } = await loadRendererWithMessages();

    load(makePreferences(false));
    await letPreferencesFlush();
    load({ type: "load-document", html: FIRST_HTML, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();

    expect(widthHandle().hidden, "chrome off hides the handle on a cold load").toBe(true);

    load({ type: "load-document", html: SECOND_HTML, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    messages.length = 0;
    load({
      type: "load-cached-document",
      cacheKey: rendererCacheKey(FIRST_HTML, "light"),
      documentName: "first.md",
      theme: "light",
      hasMermaid: false,
      renderId: 3,
    });

    expect(hasPerfMark(messages, "mm-load-document-cache-hit"), "the restore must hit the cache").toBe(true);
    expect(widthHandle().hidden, "chrome off must stay hidden across a restore").toBe(true);
  });
});
