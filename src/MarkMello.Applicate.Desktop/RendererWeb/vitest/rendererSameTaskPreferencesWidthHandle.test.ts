import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// `flushPendingReadingPreferences` is the arrival owner for
// `hasReceivedHostPreferences`, and the width handle's `hidden` flag is one of
// that flag's readers (`updateWidthHandlePosition` /
// `updateWidthHandlePositionFromCssModel`). The function used to call
// `updateWidthHandlePositionForCurrentLayout()` on BOTH branches of its
// `viewerChromeChanged` block and only raise the arrival flag seventeen lines
// later -- so it announced that preferences had arrived after acting as though
// they had not, and both calls wrote `hidden = true`.
//
// Whether that is observable depends purely on message timing:
//
//   * preferences flushed BEFORE the load  -- the cold path's settle owner
//     (`initialVisibleReady.then`, which raises `hasInitialLayoutSettled` and
//     re-runs the write) fires last and finds the arrival flag already true, so
//     it reveals the handle. This is what rendererCachedWidthHandleReveal.test.ts
//     sets up with its `letPreferencesFlush()`.
//   * both messages delivered in ONE task -- the load's settle owner runs
//     BEFORE the preferences frame, hides the handle because the arrival flag is
//     still false, and then the preferences flush re-hides it and raises the flag
//     with nothing left to re-run the write. The handle stays hidden forever.
//
// This file pins the second ordering. It is the same defect class as the cached
// restore one (`ba89024`): a function raising a settle flag AFTER running the
// work whose outcome depends on it.

type HostBridge = (message: unknown) => void;

const DOCUMENT_HTML = "<h1 id='first'>First</h1><p>same-task document</p>";

function makePreferences(viewerChromeEnabled: boolean) {
  return {
    type: "reading-preferences",
    fontFamily: "serif",
    fontSize: 16,
    lineHeight: 1.6,
    maxWidth: 720,
    // `off` keeps the minimap out of this test entirely: both `shouldShowMinimap`
    // and `shouldBuildDetailedMinimapContent` deny on mode-off, so the only
    // surface under test is the width handle.
    minimapMode: "off",
    viewerChromeEnabled,
    documentScrollEnabled: true,
    wheelProxyEnabled: false,
    widthResizerVisibility: "on-hover",
  };
}

function makeLoad(renderId: number) {
  return {
    type: "load-document",
    html: DOCUMENT_HTML,
    documentName: "first.md",
    theme: "light",
    hasMermaid: false,
    renderId,
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

describe("reading preferences delivered in the same task as the load", () => {
  it("leaves the width handle visible once the pipeline settles", async () => {
    const { load, messages } = await loadRendererWithMessages();

    // The whole point: no await between the two, so `applyReadingPreferences`
    // has only QUEUED its frame when the load arrives.
    load(makePreferences(true));
    load(makeLoad(1));
    await letPipelineSettle();

    // Anti-vacuity 1 -- the preferences really were applied. Without this the
    // assertion below could pass on a run where the message was dropped and the
    // handle happened to be visible for an unrelated reason.
    expect(
      document.documentElement.dataset.mmChrome,
      "the preferences must have been applied"
    ).toBe("on");
    // Anti-vacuity 2 -- the load's settle owner really did run to terminal
    // state, so `hasInitialLayoutSettled` is true and the arrival flag is the
    // only remaining term in the visibility predicate.
    expect(
      hasPerfMark(messages, "mm-initial-visible-ready"),
      "the initial-visible pipeline must have settled"
    ).toBe(true);

    expect(
      widthHandle().hidden,
      "same-task preferences must still leave the handle visible"
    ).toBe(false);
  });

  // Only-forward: raising the arrival flag earlier must not widen the
  // visibility predicate. Viewer chrome off (edit-preview) still hides the
  // handle, on the same-task ordering too.
  it("keeps the width handle hidden when viewer chrome is off", async () => {
    const { load, messages } = await loadRendererWithMessages();

    load(makePreferences(false));
    load(makeLoad(1));
    await letPipelineSettle();

    expect(
      document.documentElement.dataset.mmChrome,
      "the preferences must have been applied"
    ).toBe("off");
    expect(
      hasPerfMark(messages, "mm-initial-visible-ready"),
      "the initial-visible pipeline must have settled"
    ).toBe(true);

    expect(
      widthHandle().hidden,
      "chrome off must keep the handle hidden regardless of message timing"
    ).toBe(true);
  });
});
