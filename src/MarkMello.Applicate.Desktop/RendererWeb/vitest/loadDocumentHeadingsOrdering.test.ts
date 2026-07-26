import { describe, it, expect, vi, beforeEach } from "vitest";
import { applyLoadDocument, type LoadDocumentDeps } from "../src/loadDocument";

// G4a (design work-items/active/2026-07-25-toc-empty-on-open/design.md §6/§8,
// INV-ORDER). This is a NEW test file -- it adds coverage without touching
// loadDocument.ts production code (Must-NOT-touch: the entire wire contract
// and RendererWeb production sources, design §5).
//
// Per design §0 FACT 1, ensureChromeNodes is the renderer's ONLY producer of
// headings-updated (extractAndPostHeadings / postCachedHeadings are called
// ONLY from inside ensureChromeNodes, renderer.ts:4804/:4806). The C#-side
// consumer pull (ApplicateWebMarkdownDocumentView.
// TryRaiseRetainedHeadingsForConsumerDebt) is correct only because of
// INV-ORDER: for every render generation the host ever marks loaded
// (_hasLoadedDocument, set only by the layout-ready handler), that
// generation's own headings-updated has already been delivered. This file
// proves the renderer-side half of that claim: applyLoadDocument calls
// deps.ensureChromeNodes (loadDocument.ts:136) strictly BEFORE either
// deps.completeCachedDocumentLoad (:152, cache-hit path) or
// deps.runInitialRenderPipeline (:156, cold-load path) -- the two functions
// that eventually own emitting layout-ready.
//
// Harness caveat this file exists to cover (design §8): the C# real-host
// tests (ApplicateSharedWebViewHostRealHostTests.DriveViewToLoadedAndPainted)
// feed layout-ready and only THEN call PostHeadings -- the REVERSE of
// production order -- so they provide NO evidence for INV-ORDER on their own.
// This vitest file is the renderer-side half that closes that gap; the C#
// G4bRetainedHeadingsAreNotPullableWhileARenderIsInFlight test is the other
// half (feeds headings before layout-ready and proves the pull still cannot
// fire mid-flight).
function makeOrderedDeps(order: string[], overrides: Partial<LoadDocumentDeps> = {}): LoadDocumentDeps {
  return {
    runInitialRenderPipeline: vi.fn(() => {
      order.push("runInitialRenderPipeline(owns-layout-ready)");
      return Promise.resolve();
    }),
    cancelCurrentMathController: vi.fn(),
    resetModuleGlobals: vi.fn(),
    scrollWindowToTop: vi.fn(),
    emitMark: vi.fn(),
    ensureChromeNodes: vi.fn(() => {
      order.push("ensureChromeNodes(posts-headings-updated)");
    }),
    applyTheme: vi.fn(),
    debugLog: vi.fn(),
    ...overrides,
  };
}

// Same fixture shape as loadDocument.test.ts (a headless documentElement is a
// shape navigation can never produce -- happy-dom's title setter dereferences
// this.head unguarded when no <title> exists, so the fixture keeps <head>).
beforeEach(() => {
  document.documentElement.innerHTML =
    `<head><title>MarkMello</title></head>`
    + `<body><main class="mm-document"><p>old</p></main></body>`;
});

describe("applyLoadDocument -- INV-ORDER (G4a: headings before layout-ready)", () => {
  it("cold load: ensureChromeNodes runs before runInitialRenderPipeline", () => {
    const order: string[] = [];
    const deps = makeOrderedDeps(order);

    applyLoadDocument({ html: "<h1>new</h1>", documentName: "doc.md" }, deps);

    expect(order).toEqual([
      "ensureChromeNodes(posts-headings-updated)",
      "runInitialRenderPipeline(owns-layout-ready)",
    ]);
  });

  it("cache-hit load: ensureChromeNodes runs before completeCachedDocumentLoad", () => {
    const order: string[] = [];
    const fragment = document.createDocumentFragment();
    fragment.append(document.createElement("p"));
    const deps = makeOrderedDeps(order, {
      getCachedDocumentFragment: vi.fn(() => fragment),
      completeCachedDocumentLoad: vi.fn(() => {
        order.push("completeCachedDocumentLoad(owns-layout-ready-cache-hit)");
      }),
    });

    applyLoadDocument(
      { html: "<p>raw</p>", documentName: "doc.md", cacheKey: "doc-cache" },
      deps,
    );

    expect(order).toEqual([
      "ensureChromeNodes(posts-headings-updated)",
      "completeCachedDocumentLoad(owns-layout-ready-cache-hit)",
    ]);
  });
});
