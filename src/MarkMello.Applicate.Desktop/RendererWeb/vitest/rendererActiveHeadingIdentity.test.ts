import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The cached-headings active-heading observer is rebuilt at layout-ready (no timer). Its async
// continuations — the IntersectionObserver callback and the initial requestAnimationFrame — close
// over the document's heading nodes. A document swap disconnects the observer but CANNOT un-queue an
// already-scheduled frame, and `active-heading-changed` carries no renderId (the host and ViewModel
// apply any non-empty id unconditionally). So a stale frame would highlight the OLD document's
// heading in the NEW document's TOC. The observer instance is the liveness token.

type HostBridge = (msg: unknown) => void;

describe("active-heading observer identity", () => {
  let rafCallbacks: FrameRequestCallback[];
  let messages: unknown[];

  beforeEach(async () => {
    vi.resetModules();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    document.documentElement.innerHTML = `<body><main class="mm-document"></main></body>`;
    Object.defineProperty(document, "scrollingElement", {
      configurable: true,
      get: () => document.documentElement,
    });

    rafCallbacks = [];
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
      rafCallbacks.push(callback);
      return rafCallbacks.length;
    });
    vi.stubGlobal("IntersectionObserver", class {
      observe(): void {}
      unobserve(): void {}
      disconnect(): void {}
      takeRecords(): [] { return []; }
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

  function activeHeadingIds(): string[] {
    return messages
      .filter((message): message is { type: string; id: string } =>
        typeof message === "object"
        && message !== null
        && (message as { type?: unknown }).type === "active-heading-changed")
      .map(message => message.id);
  }

  it("does not post a superseded document's heading id from a frame queued before the swap", async () => {
    load({
      type: "load-document",
      html: "<h1 id='doc-a-heading'>Doc A</h1><p>alpha</p>",
      hasMermaid: false,
      renderId: 1,
    });
    await new Promise(resolve => setTimeout(resolve, 300));

    // A queued initial-guess frame now closes over Doc A's headings. Swap documents BEFORE it runs:
    // the swap disconnects the observer but cannot un-queue this frame.
    messages.length = 0;
    load({
      type: "load-document",
      html: "<h1 id='doc-b-heading'>Doc B</h1><p>beta</p>",
      hasMermaid: false,
      renderId: 2,
    });

    // Flush every pending frame, including the stale one captured for Doc A.
    for (const callback of rafCallbacks.splice(0)) {
      callback(performance.now());
    }
    await new Promise(resolve => setTimeout(resolve, 300));

    // Doc A's heading must never be announced while Doc B is loaded — that would highlight the wrong
    // row (the host/ViewModel apply the id without checking it belongs to the current headings).
    expect(activeHeadingIds()).not.toContain("doc-a-heading");

    // Positive half: without this, a refactor that stops posting active headings entirely would make
    // the assertion above vacuously green. Doc B's own heading must still be announced, which also
    // proves the flush really exercised the rebuild path rather than doing nothing.
    expect(activeHeadingIds()).toContain("doc-b-heading");
  });
});
