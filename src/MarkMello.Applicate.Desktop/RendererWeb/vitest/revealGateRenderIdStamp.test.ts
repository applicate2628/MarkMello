import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Reveal-gate currency stamp (renderer -> host).
//
// The host arms its document-reveal gate with a renderId
// (ApplicateWebMarkdownDocumentView.ConfigureDocumentRevealGate) and lifts it
// only from HandlePostReadyEnhancementsComplete, which is FAIL-CLOSED: a
// `post-ready-enhancements-complete` whose renderId is absent, non-numeric or
// <= 0 is dropped without a log. If the cold load-document path ever stopped
// echoing the host's renderId, every document carrying Mermaid/highlight.js
// would hold the startup cover until the 15s compositor fallback
// (ApplicateAirspaceCompositor.FallbackTimeout) — the ~16-18s startup regression
// already recorded in the comment at renderer.ts's first-prefs bootstrap.
//
// The cache-HIT and cache-MISS stamps are pinned in rendererDocumentCache.test.ts;
// this file pins the COLD load-document path, which is the one that actually
// arms the gate on a normal document open and was previously unguarded.

type HostBridge = (msg: unknown) => void;

async function loadRendererWithMessages() {
  vi.resetModules();
  // Mirror the shipped shell (ApplicateHtmlDocumentTemplate.BuildShell): a
  // <title> must exist because applyLoadDocument writes document.title and
  // happy-dom's setter dereferences `this.head` unguarded.
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

function findPostReadyComplete(messages: unknown[]) {
  return messages.find((message): message is { type: string; renderId?: number } =>
    typeof message === "object"
    && message !== null
    && (message as { type?: unknown }).type === "post-ready-enhancements-complete");
}

beforeEach(() => {
  delete (window as unknown as { chrome?: unknown }).chrome;
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  delete (window as unknown as { hljs?: unknown }).hljs;
});

describe("reveal-gate renderId stamp", () => {
  it("echoes the host renderId on post-ready-enhancements-complete for a cold load-document", async () => {
    // hasHljs: true is the case that actually arms the host gate
    // (requiresPostReadyEnhancements = HasMermaidBlock || HasCodeBlockWithSyntax),
    // so stub highlight.js the way the progressive-chunk test does.
    (window as unknown as {
      hljs: { getLanguage: (language: string) => boolean; highlightElement: (node: HTMLElement) => void };
    }).hljs = {
      getLanguage: () => true,
      highlightElement: (node: HTMLElement) => { node.classList.add("hljs"); },
    };

    const { load, messages } = await loadRendererWithMessages();

    load({
      type: "load-document",
      html: "<h1>Cold</h1><pre><code class='language-ts'>const x = 1;</code></pre>",
      documentName: "cold.md",
      theme: "light",
      hasMermaid: false,
      hasHljs: true,
      renderId: 42,
    });
    await new Promise(resolve => setTimeout(resolve, 700));

    const postReadyComplete = findPostReadyComplete(messages);
    // Fail-closed host: an absent renderId here is silently dropped, so assert
    // the field is present AND carries the host's value, not merely truthy.
    expect(postReadyComplete).toBeDefined();
    expect(postReadyComplete).toMatchObject({ renderId: 42 });
  });
});
