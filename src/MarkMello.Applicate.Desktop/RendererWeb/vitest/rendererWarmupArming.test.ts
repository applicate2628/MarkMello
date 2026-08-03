import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { readFileSync } from "node:fs";

// The cache-restore path used to arm the document warm-up in the same task as
// the DOM attach, so the two animation frames the host's reveal cover waits on
// each carried a full-document forced layout from warmupSlice. Arming now
// belongs to loadDocument.ts's double rAF — the load's first-settled-paint
// owner. These guards pin the three things that fix is easy to get wrong:
// what must NOT be deferred with it (B-1, B-2), that a superseded switch cannot
// consume it (B-3), and the message order the host's gate depends on (B-4).

type HostBridge = (message: unknown) => void;

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

const FIRST_HTML = "<h1 id='first'>First</h1><p>cached document</p>";
const SECOND_HTML = "<h1 id='second'>Second</h1><p>other document</p>";
const THIRD_HTML = "<h1 id='third'>Third</h1><p>third document</p>";

beforeEach(() => {
  delete (window as unknown as { chrome?: unknown }).chrome;
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("cache-restore warm-up arming", () => {
  // B-1. THE anti-silent-revert guard. preserveCurrentProcessedDocument returns
  // early while postReadyEnhancementsCompleted is false, so if a future change
  // defers the whole postPostReadyEnhancementsComplete call instead of only the
  // warm-up arming, documents silently stop being cached on a fast switch —
  // undoing the commit this fix sits on top of, with every suite still green.
  it("stores the outgoing document even when it is switched away before first paint", async () => {
    const { load, messages } = await loadRendererWithMessages();

    load({ type: "load-document", html: FIRST_HTML, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();
    load({ type: "load-document", html: SECOND_HTML, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    // From here on NO animation frame is allowed to run, so the restored
    // document is switched away strictly before its first paint.
    const frames: FrameRequestCallback[] = [];
    vi.spyOn(window, "requestAnimationFrame").mockImplementation(callback => {
      frames.push(callback);
      return frames.length;
    });

    load({
      type: "load-cached-document",
      cacheKey: rendererCacheKey(FIRST_HTML, "light"),
      documentName: "first.md",
      theme: "light",
      hasMermaid: false,
      renderId: 3,
    });
    expect(hasPerfMark(messages, "mm-load-document-cache-hit")).toBe(true);

    messages.length = 0;
    load({ type: "load-document", html: THIRD_HTML, documentName: "third.md", theme: "light", hasMermaid: false, renderId: 4 });

    expect(hasPerfMark(messages, "mm-document-cache-store")).toBe(true);
  });

  // B-2. hasInitialLayoutSettled drives the width handle's hidden flag and is
  // raised on the same synchronous burst the warm-up arming was moved OFF of.
  // Deferring it along with the arming would make the handle flicker on every
  // cached restore, so this pins that it is still raised synchronously and was
  // not carried into the deferred path.
  //
  // Source-text rather than behavioural, deliberately: this pins WHERE the flag
  // is raised, which is what the arming move could get wrong. That the raise is
  // actually observable on the restore is a separate contract, and it is now
  // covered behaviourally in rendererCachedWidthHandleReveal.test.ts -- the
  // restore path re-runs the width-handle write itself, because ensureChromeNodes
  // runs before this callback and therefore reads the flag while
  // resetModuleGlobals still has it false.
  it("does not defer the settled-layout flag along with the warm-up arming", () => {
    const source = readFileSync("RendererWeb/src/renderer.ts", "utf8");
    const start = source.indexOf("completeCachedDocumentLoad: (");
    const end = source.indexOf("notifyDocumentCacheMiss:", start);
    expect(start).toBeGreaterThan(-1);
    expect(end).toBeGreaterThan(start);
    const restorePath = source.slice(start, end);

    // Raised on the synchronous burst, before the arming is handed to a frame.
    const settledAt = restorePath.indexOf("hasInitialLayoutSettled = true");
    const armAt = restorePath.indexOf("armDocumentWarmupAfterFirstPaint(");
    expect(settledAt).toBeGreaterThan(-1);
    expect(armAt).toBeGreaterThan(-1);
    expect(settledAt).toBeLessThan(armAt);

    // And NOT moved into either half of the deferred path. These are
    // .not.toContain assertions, which pass on an empty string -- so both
    // region anchors are asserted found first.
    const consumerStart = source.indexOf("function consumePendingWarmupArm(");
    expect(consumerStart).toBeGreaterThan(-1);
    const consumerEnd = source.indexOf("function warmupSlice(", consumerStart);
    expect(consumerEnd).toBeGreaterThan(consumerStart);
    expect(source.slice(consumerStart, consumerEnd)).not.toContain("hasInitialLayoutSettled");

    const paintStart = source.indexOf("notifyDocumentFirstPaint: (renderId) => {");
    expect(paintStart).toBeGreaterThan(-1);
    const paintEnd = source.indexOf("},", paintStart);
    expect(paintEnd).toBeGreaterThan(paintStart);
    expect(source.slice(paintStart, paintEnd)).not.toContain("hasInitialLayoutSettled");
  });

  // B-3. The generation/renderId token. A superseded switch must never arm the
  // warm-up of the document that replaced it: load A's outer rAF can already
  // have fired when load B arrives, leaving A's inner rAF to run a frame later.
  it("never arms warm-up for a switch that was superseded mid-flight", async () => {
    const { load } = await loadRendererWithMessages();

    load({ type: "load-document", html: FIRST_HTML, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();
    load({ type: "load-document", html: SECOND_HTML, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    const frames: FrameRequestCallback[] = [];
    vi.spyOn(window, "requestAnimationFrame").mockImplementation(callback => {
      frames.push(callback);
      return frames.length;
    });

    // Restore doc 1 -> a warm-up arm is now pending for THIS load.
    const beforeRestore = frames.length;
    load({
      type: "load-cached-document",
      cacheKey: rendererCacheKey(FIRST_HTML, "light"),
      documentName: "first.md",
      theme: "light",
      hasMermaid: false,
      renderId: 4,
    });

    // Run ONLY this load's OUTER frame, leaving its inner one queued. That is
    // the state the token exists for: simply clearing the pending arm on reset
    // does not cover it, because the next load installs its own arm before this
    // stale inner frame gets to run.
    const beforeInner = frames.length;
    frames[beforeRestore]?.(0);
    const staleInnerFrame = frames[beforeInner];
    expect(staleInnerFrame, "the superseded load should have an inner frame queued").toBeTypeOf("function");

    // Supersede with a second cached restore, which installs ITS own pending arm.
    load({
      type: "load-cached-document",
      cacheKey: rendererCacheKey(SECOND_HTML, "light"),
      documentName: "second.md",
      theme: "light",
      hasMermaid: false,
      renderId: 5,
    });

    // Now let the SUPERSEDED load's first-paint land. It must not consume the
    // arm belonging to the document that replaced it. Arming is observable
    // synchronously: allowDocumentWarmup -> ensureDocumentWarmup queues
    // warmupSlice, so an armed warm-up grows the frame queue here.
    const framesBeforeStale = frames.length;
    staleInnerFrame?.(16);

    expect(frames.length, "stale first-paint must not schedule a warm-up slice")
      .toBe(framesBeforeStale);
    expect(document.querySelectorAll("body > main.mm-document > .mm-warmed")).toHaveLength(0);
  });

  // B-4. Source-text guard. The host's reveal gate requires layout-ready STRICTLY
  // before post-ready-enhancements-complete, or the cover hangs to its 8 s
  // fallback. Anchor indices are asserted found first: a -1 anchor slices an
  // empty region, and this repository has already shipped a guard whose
  // assertions all passed against "".
  it("posts cached layout-ready strictly before post-ready-enhancements-complete", () => {
    const source = readFileSync("RendererWeb/src/renderer.ts", "utf8");
    const start = source.indexOf("completeCachedDocumentLoad: (");
    const end = source.indexOf("notifyDocumentCacheMiss:", start);
    expect(start).toBeGreaterThan(-1);
    expect(end).toBeGreaterThan(start);
    const restorePath = source.slice(start, end);

    const layoutReadyAt = restorePath.indexOf("postCachedLayoutReady()");
    const postReadyAt = restorePath.indexOf("postPostReadyEnhancementsComplete(");
    expect(layoutReadyAt).toBeGreaterThan(-1);
    expect(postReadyAt).toBeGreaterThan(-1);
    expect(layoutReadyAt).toBeLessThan(postReadyAt);

    // The flag preserveCurrentProcessedDocument gates on must be raised on this
    // synchronous burst, not handed to a later frame (B-1 as source text).
    expect(restorePath).toContain("postReadyEnhancementsCompleted = true");
    expect(restorePath).toContain("hasInitialLayoutSettled = true");

    // Only the ARMING is deferred, and it is deferred to a frame event.
    expect(restorePath).toContain("armDocumentWarmupAfterFirstPaint(renderId)");
    expect(restorePath).not.toContain("setTimeout");
    expect(restorePath).not.toContain("setInterval");

    // The consumer is the existing double rAF, not a new readiness mechanism.
    const consumerStart = source.indexOf("notifyDocumentFirstPaint: (renderId) => {");
    expect(consumerStart).toBeGreaterThan(-1);
    const consumerEnd = source.indexOf("},", consumerStart);
    expect(consumerEnd).toBeGreaterThan(consumerStart);
    expect(source.slice(consumerStart, consumerEnd)).toContain("consumePendingWarmupArm(renderId)");
  });
});
