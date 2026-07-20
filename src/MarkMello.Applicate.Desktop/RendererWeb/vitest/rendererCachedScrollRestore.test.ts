import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { liveBlockSelectorForIndex } from "../src/topVisibleBlockIndex";

// A3 unified block+offset cached-scroll restore (Stage-B first-return ratchet fix).
//
// These are the DETERMINISTIC structural guards. The intra-block-offset geometry
// (fable B1 top-of-doc = 0px, fable B2 geomRefresh recompute = no jump-back) is proven
// at RUNTIME on a real 7MB doc by `.scratch/stage-b/a3_verify.py`
// (A deep-ratchet +0px x4, B top-of-doc 0px, C scroll->return +0px) — happy-dom has no
// real layout so those pixel invariants cannot be reproduced here.

type HostBridge = (msg: unknown) => void;

async function loadRendererWithMessages() {
  vi.resetModules();
  // <head><title> mirrors the SHIPPED shell (ApplicateHtmlDocumentTemplate.cs
  // BuildShell). These loads carry a documentName, and applyLoadDocument writes
  // document.title; happy-dom's setter dereferences `this.head` unguarded when no
  // <title> exists, so a headless fixture throws on a DOM shape navigation cannot
  // produce. Keep the head.
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

beforeEach(() => {
  delete (window as unknown as { chrome?: unknown }).chrome;
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  document.body.innerHTML = "";
});

describe("A3 cached-scroll block+offset restore", () => {
  it("restores by scrolling the cached top block into view, not by the drift-prone raw scrollTop", async () => {
    const root = document.documentElement;
    Object.defineProperty(root, "scrollHeight", { configurable: true, value: 2000 });
    Object.defineProperty(root, "clientHeight", { configurable: true, value: 800 });
    vi.spyOn(window, "scrollTo").mockImplementation((options?: ScrollToOptions | number, y?: number) => {
      root.scrollTop = typeof options === "number" ? (y ?? 0) : (options?.top ?? 0);
    });

    const { load } = await loadRendererWithMessages();
    const firstHtml = "<h1 id='first'>First</h1><p data-mm-block-index='42'>cached document</p>";
    const secondHtml = "<h1 id='second'>Second</h1><p>other document</p>";

    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();
    // Establish a non-zero reading position so the top block anchor (42) is captured.
    root.scrollTop = 240;
    document.dispatchEvent(new Event("scroll"));
    await letPipelineSettle();

    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    // The cache-hit restore is synchronous on load(). Capture whether it anchored to the block.
    const scrollIntoView = vi.spyOn(HTMLElement.prototype, "scrollIntoView").mockImplementation(() => {});
    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 3 });
    const anchoredToBlock42 = scrollIntoView.mock.instances.some(
      instance => instance instanceof HTMLElement && instance.getAttribute("data-mm-block-index") === "42");
    scrollIntoView.mockRestore();
    await letPipelineSettle();

    expect(anchoredToBlock42).toBe(true);
  });

  it("re-applies the offset from the block's live document top, so a clamped scrollIntoView cannot land short (TB1 bottom edge)", async () => {
    const root = document.documentElement;
    Object.defineProperty(root, "scrollHeight", { configurable: true, value: 2000 });
    Object.defineProperty(root, "clientHeight", { configurable: true, value: 800 });
    vi.spyOn(window, "scrollTo").mockImplementation((options?: ScrollToOptions | number, y?: number) => {
      root.scrollTop = typeof options === "number" ? (y ?? 0) : (options?.top ?? 0);
    });

    const { load } = await loadRendererWithMessages();
    const firstHtml = "<h1 id='first'>First</h1><p data-mm-block-index='42'>anchor</p>";
    const secondHtml = "<h1 id='second'>Second</h1><p>other</p>";

    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();

    // Pin the anchor block's document top; offsetParent=null stops the blockDocumentTop walk there.
    const block = document.querySelector<HTMLElement>('[data-mm-block-index="42"]')!;
    Object.defineProperty(block, "offsetTop", { configurable: true, value: 340 });
    Object.defineProperty(block, "offsetParent", { configurable: true, get: () => null });

    root.scrollTop = 240; // saved reading position S; captured offset = blockDocumentTop(340) - 240 = 100
    document.dispatchEvent(new Event("scroll"));
    await letPipelineSettle();

    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    // Simulate a CLAMPED scrollIntoView: the browser could only reach a low maxScroll, not docTop 340.
    vi.spyOn(HTMLElement.prototype, "scrollIntoView").mockImplementation(function (this: HTMLElement) {
      root.scrollTop = 100;
    });

    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 3 });

    // Fix restores from the block's own doc-top: scrollTo(340 - 100) = 240 = S.
    // The old double-clamp bug would have used the clamped result: scrollTo(100 - 100) = 0.
    expect(root.scrollTop).toBe(240);
  });

  it("scopes the fallback selector to the live document so it never resolves a minimap clone (B3)", () => {
    // The minimap clone (`.mm-minimap-content`) carries the same data-mm-block-index attrs and
    // sits EARLIER in document order than the live document, so an unscoped querySelector would
    // pick it. The A3 fallback is scoped to `body > main.mm-document`.
    document.documentElement.innerHTML = `
      <body>
        <div class="mm-minimap"><div class="mm-minimap-content">
          <p data-mm-block-index="5">clone</p>
        </div></div>
        <main class="mm-document">
          <p data-mm-block-index="5">live</p>
        </main>
      </body>`;

    const unscoped = document.querySelector<HTMLElement>('[data-mm-block-index="5"]');
    expect(unscoped?.textContent).toBe("clone"); // proves the unscoped bug the scope guards against

    const scoped = document.querySelector<HTMLElement>('body > main.mm-document [data-mm-block-index="5"]');
    expect(scoped?.textContent).toBe("live");

    // With the live block absent, the scoped selector yields null (raw-scrollTop fallback), never the clone.
    scoped!.remove();
    const missing = document.querySelector<HTMLElement>('body > main.mm-document [data-mm-block-index="5"]');
    expect(missing).toBeNull();
  });

  it("fallback selector excludes display:none rendered-mermaid <pre> so a hidden anchor falls back to raw scroll (NB1)", () => {
    // A rendered mermaid <pre> keeps its data-mm-block-index but is display:none (.is-rendered)
    // and has NO layout box, so scrollIntoView on it is a no-op. The anchor captured pre-render can
    // point at it; the fallback must carry the LIVE_DOCUMENT_BLOCK_SELECTOR contract and MISS it,
    // so restore falls through to the correct raw scrollTop.
    document.documentElement.innerHTML = `
      <body>
        <main class="mm-document">
          <pre class="mm-mermaid is-rendered" data-mm-block-index="7">graph TD; A--&gt;B</pre>
        </main>
      </body>`;

    const selector = liveBlockSelectorForIndex(7);
    expect(selector).toContain(":not(.is-rendered)");
    expect(document.querySelector(selector)).toBeNull(); // hidden rendered-mermaid is NOT matched

    document.querySelector("pre")!.classList.remove("is-rendered");
    expect(document.querySelector<HTMLElement>(selector)).not.toBeNull(); // a live block IS matched
  });
});
