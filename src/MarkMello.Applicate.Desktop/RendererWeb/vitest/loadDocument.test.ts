import { describe, it, expect, vi, beforeEach } from "vitest";
import { applyLoadDocument, clearDocumentState, type LoadDocumentDeps } from "../src/loadDocument";

function makeDeps(overrides: Partial<LoadDocumentDeps> = {}): LoadDocumentDeps {
  return {
    runInitialRenderPipeline: vi.fn(() => Promise.resolve()),
    cancelCurrentMathController: vi.fn(),
    resetModuleGlobals: vi.fn(),
    scrollWindowToTop: vi.fn(),
    emitMark: vi.fn(),
    ensureChromeNodes: vi.fn(),
    applyTheme: vi.fn(),
    debugLog: vi.fn(),
    ...overrides,
  };
}

beforeEach(() => {
  document.documentElement.innerHTML =
    `<body><main class="mm-document"><p>old</p></main></body>`;
});

describe("applyLoadDocument", () => {
  it("swaps mm-document innerHTML with new body html", () => {
    const deps = makeDeps();
    applyLoadDocument({ html: "<h1>new</h1>", documentName: "doc.md" }, deps);
    const main = document.querySelector("main.mm-document");
    expect(main?.innerHTML).toContain("<h1>new</h1>");
    expect(main?.innerHTML).not.toContain("old");
  });

  it("uses cached processed html when cache key resolves", () => {
    const fragment = document.createDocumentFragment();
    const cached = document.createElement("p");
    cached.dataset["processed"] = "true";
    cached.textContent = "cached";
    fragment.append(cached);

    const deps = makeDeps({
      getCachedDocumentFragment: vi.fn(() => fragment),
      setCurrentDocumentCacheKey: vi.fn(),
      completeCachedDocumentLoad: vi.fn(),
    });

    applyLoadDocument({ html: "<p>raw</p>", documentName: "doc.md", cacheKey: "doc-cache" }, deps);

    const main = document.querySelector("main.mm-document");
    expect(main?.innerHTML).toContain("data-processed=\"true\"");
    expect(main?.innerHTML).not.toContain("<p>raw</p>");
    expect(deps.setCurrentDocumentCacheKey).toHaveBeenCalledWith("doc-cache");
    expect(deps.emitMark).toHaveBeenCalledWith(
      "mm-load-document-cache-hit",
      expect.objectContaining({ documentName: "doc.md" }));
    expect(deps.runInitialRenderPipeline).not.toHaveBeenCalled();
    expect(deps.completeCachedDocumentLoad).toHaveBeenCalledTimes(1);
  });

  it("calls cancelCurrentMathController before resetModuleGlobals", () => {
    const order: string[] = [];
    const deps = makeDeps({
      cancelCurrentMathController: () => { order.push("cancel"); },
      resetModuleGlobals: () => { order.push("reset"); },
    });
    applyLoadDocument({ html: "<p>AFTER</p>" }, deps);
    expect(order).toEqual(["cancel", "reset"]);
  });

  it("calls runInitialRenderPipeline after reset", async () => {
    const order: string[] = [];
    const deps = makeDeps({
      runInitialRenderPipeline: () => { order.push("pipeline"); return Promise.resolve(); },
      resetModuleGlobals: () => { order.push("reset"); },
    });
    applyLoadDocument({ html: "<p>x</p>" }, deps);
    await Promise.resolve();
    expect(order).toEqual(["reset", "pipeline"]);
  });

  it("scrolls to top after swap", () => {
    const deps = makeDeps();
    applyLoadDocument({ html: "<p>x</p>" }, deps);
    expect(deps.scrollWindowToTop).toHaveBeenCalledTimes(1);
  });

  it("emits a mark for the load-document boundary", () => {
    const deps = makeDeps();
    applyLoadDocument({ html: "<p>x</p>", documentName: "wave.md" }, deps);
    expect(deps.emitMark).toHaveBeenCalledWith(
      "mm-load-document",
      expect.objectContaining({ documentName: "wave.md" }));
  });

  it("re-anchors chrome nodes after swap", () => {
    const deps = makeDeps();
    applyLoadDocument({ html: "<p>x</p>" }, deps);
    expect(deps.ensureChromeNodes).toHaveBeenCalledTimes(1);
  });

  it("applies the host theme before swapping document html", () => {
    const order: string[] = [];
    const deps = makeDeps({
      applyTheme: () => { order.push("theme"); },
      ensureChromeNodes: () => { order.push("chrome"); },
    });

    applyLoadDocument({ html: "<p>x</p>", theme: "dark" }, deps);

    expect(order).toEqual(["theme", "chrome"]);
  });

  it("survives missing mm-document element by no-op", () => {
    document.body.innerHTML = "<div>no main here</div>";
    const deps = makeDeps();
    expect(() => applyLoadDocument({ html: "<p>x</p>" }, deps)).not.toThrow();
    expect(deps.runInitialRenderPipeline).not.toHaveBeenCalled();
  });
});

describe("applyLoadDocument — chrome survival (Decision 5)", () => {
  it("preserves minimap aside / width-handle / drop-overlay across body swap", () => {
    document.body.innerHTML = `
      <aside class="mm-minimap"></aside>
      <div class="mm-width-handle" hidden></div>
      <main class="mm-document"><p>old</p></main>
      <div id="mm-drop-overlay" class="mm-drop-overlay"></div>
    `;
    const minimapBefore = document.querySelector("aside.mm-minimap");
    const handleBefore = document.querySelector(".mm-width-handle");
    const overlayBefore = document.querySelector("#mm-drop-overlay");

    applyLoadDocument({ html: "<p>new</p>" }, {
      runInitialRenderPipeline: () => Promise.resolve(),
      cancelCurrentMathController: () => {},
      resetModuleGlobals: () => {},
      scrollWindowToTop: () => {},
      emitMark: () => {},
      ensureChromeNodes: () => {},
      applyTheme: () => {},
      debugLog: () => {},
    });

    // Same element identity preserved across the swap.
    expect(document.querySelector("aside.mm-minimap")).toBe(minimapBefore);
    expect(document.querySelector(".mm-width-handle")).toBe(handleBefore);
    expect(document.querySelector("#mm-drop-overlay")).toBe(overlayBefore);
    expect(document.querySelector("main.mm-document p")?.textContent).toBe("new");
  });
});

describe("clearDocumentState", () => {
  it("empties mm-document and resets module globals", () => {
    const deps = makeDeps();
    const main = document.querySelector("main.mm-document")!;
    main.innerHTML = "<p>old</p>";
    clearDocumentState(deps);
    expect(main.innerHTML).toBe("");
    expect(deps.cancelCurrentMathController).toHaveBeenCalled();
    expect(deps.resetModuleGlobals).toHaveBeenCalled();
  });

  it("does not invoke the initial render pipeline (no content)", () => {
    const deps = makeDeps();
    clearDocumentState(deps);
    expect(deps.runInitialRenderPipeline).not.toHaveBeenCalled();
  });
});
