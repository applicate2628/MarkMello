import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { readFileSync } from "node:fs";

// Import for side effects — bundles all module-globals and event wiring.
import "../src/renderer";

type HostBridge = (msg: unknown) => void;

beforeEach(() => {
  document.documentElement.innerHTML =
    `<body><main class="mm-document"></main></body>`;
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("handleHostMessage(load-document)", () => {
  it("swaps mm-document innerHTML", () => {
    const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;
    load({ type: "load-document", html: "<article><h1>Hello</h1></article>" });
    expect(document.querySelector("main.mm-document")?.innerHTML).toContain("<h1>Hello</h1>");
  });

  it("re-runs document-ready emission via the pipeline", async () => {
    const messages: unknown[] = [];
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (m: unknown) => messages.push(m) }
    };
    const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;
    load({ type: "load-document", html: "<p>x</p>" });
    await new Promise(r => setTimeout(r, 50));
    const documentReady = messages.find((m) => (m as { type?: string } | null)?.type === "document-ready");
    expect(documentReady).toBeTruthy();
  });

  it("does not throw if html is empty string", () => {
    const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;
    expect(() => load({ type: "load-document", html: "" })).not.toThrow();
  });

  // The shell page is navigated once and reused, so its <title> is the app name
  // ("MarkMello") until a document swap updates it. The page title is EXPORTED
  // metadata — WebView2's PrintToPdfAsync writes it into the PDF Title field and
  // the HTML snapshot serializes <head><title> verbatim — so a stale shell title
  // means every exported file is named after the editor instead of the document.
  // Mirrors ApplicateHtmlDocumentTemplate.BuildShell: a <head> whose <title> is
  // the app name, and an empty <main class="mm-document"> waiting for a swap.
  const mountShell = () => {
    document.documentElement.innerHTML =
      `<head><title>MarkMello</title></head><body><main class="mm-document"></main></body>`;
  };

  it("titles the page with the loaded document name, not the app name", () => {
    const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;
    mountShell();

    load({ type: "load-document", html: "<p>x</p>", documentName: "wave_ports.md" });

    expect(document.title).toBe("wave_ports.md");
  });

  // Guards the actual export leak rather than only the DOM property:
  // captureRenderedHtmlSnapshot clones document.documentElement and serializes
  // outerHTML, so whatever <title> stands here is what lands in the exported file.
  it("carries the document name into the serialized head the export captures", () => {
    const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;
    mountShell();

    load({ type: "load-document", html: "<p>x</p>", documentName: "wave_ports.md" });

    const serialized = document.documentElement.outerHTML;
    expect(serialized).toContain("<title>wave_ports.md</title>");
    expect(serialized).not.toContain("<title>MarkMello</title>");
  });

  // No name to report ⇒ no document identity to claim. The shell title is the
  // correct answer for "nothing loaded"; blanking it would be a worse export.
  it("leaves the shell title standing when the host sends no document name", () => {
    const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;
    mountShell();

    load({ type: "load-document", html: "<p>x</p>" });

    expect(document.title).toBe("MarkMello");
  });

  it("applies load-document theme before renderer pipeline starts", () => {
    const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;
    document.documentElement.dataset.theme = "light";

    load({ type: "load-document", html: "<p>x</p>", theme: "dark" });

    expect(document.documentElement.dataset.theme).toBe("dark");
  });

  it("appends progressive document html without replacing the initial body", async () => {
    const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;

    load({ type: "load-document", html: "<h1>Intro</h1>", renderId: 1, hasMermaid: false, hasHljs: false });
    load({ type: "append-document", html: "<h2>Later</h2>", renderId: 1, cacheKey: "full-cache" });
    await new Promise(r => setTimeout(r, 20));

    const main = document.querySelector("main.mm-document");
    expect(main?.querySelector("h1")?.textContent).toBe("Intro");
    expect(main?.querySelector("h2")?.textContent).toBe("Later");
  });

  it("accepts progressive append chunks and finalizes on the last chunk", async () => {
    const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;

    load({ type: "load-document", html: "<h1>Intro</h1>", renderId: 1, hasMermaid: false, hasHljs: false });
    load({ type: "append-document", html: "<h2>Middle</h2>", renderId: 1, isFinal: false });
    load({ type: "append-document", html: "<h3>Final</h3>", renderId: 1, isFinal: true, cacheKey: "full-cache" });
    await new Promise(r => setTimeout(r, 20));

    const main = document.querySelector("main.mm-document");
    expect(main?.querySelector("h1")?.textContent).toBe("Intro");
    expect(main?.querySelector("h2")?.textContent).toBe("Middle");
    expect(main?.querySelector("h3")?.textContent).toBe("Final");
  });

  it("highlights progressive code chunks once instead of rescanning finished chunks", async () => {
    const highlightElement = vi.fn((node: HTMLElement) => {
      node.classList.add("hljs");
    });
    (window as unknown as {
      hljs: { getLanguage: (language: string) => boolean; highlightElement: (node: HTMLElement) => void };
    }).hljs = {
      getLanguage: () => true,
      highlightElement,
    };
    const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;

    load({ type: "load-document", html: "<h1>Intro</h1>", renderId: 1, hasMermaid: false, hasHljs: true });
    load({
      type: "append-document",
      html: `<pre><code data-mm-code class="language-js">const a = 1;</code></pre>`,
      renderId: 1,
      isFinal: false,
      hasHljs: true,
    });
    load({
      type: "append-document",
      html: `<pre><code data-mm-code class="language-js">const b = 2;</code></pre>`,
      renderId: 1,
      isFinal: true,
      hasHljs: true,
      cacheKey: "full-cache",
    });
    await new Promise(r => setTimeout(r, 20));

    expect(highlightElement).toHaveBeenCalledTimes(2);
  });

  it("applies live theme before deferred mermaid refresh", () => {
    const source = readFileSync("RendererWeb/src/renderer.ts", "utf8");
    const themeStart = source.indexOf("function handleThemeChange(theme: RendererTheme, requestId?: number): void");
    const themeEnd = source.indexOf("function getScrollState", themeStart);
    const themeHandler = source.slice(themeStart, themeEnd);
    const schedulerStart = source.indexOf("function scheduleThemeMermaidRefresh(");
    const schedulerEnd = source.indexOf("function handleThemeChange", schedulerStart);
    const scheduler = source.slice(schedulerStart, schedulerEnd);

    expect(themeStart).toBeGreaterThanOrEqual(0);
    expect(themeEnd).toBeGreaterThan(themeStart);
    expect(themeHandler.indexOf("applyTheme(theme);")).toBeLessThan(themeHandler.indexOf("scheduleThemeMermaidRefresh(theme);"));
    expect(themeHandler).toContain('postPerfMark("mm-theme-change-applied", { theme });');
    expect(themeHandler).not.toContain("await renderMermaid()");
    expect(scheduler).toContain("window.setTimeout");
    expect(scheduler).toContain("THEME_MERMAID_REFRESH_DELAY_MS");
    expect(scheduler).toContain("++mermaidRenderGeneration;");
  });

  it("acks theme messages after paint with the matching request id", () => {
    const rafCallbacks: FrameRequestCallback[] = [];
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
      rafCallbacks.push(callback);
      return rafCallbacks.length;
    });

    const messages: unknown[] = [];
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) }
    };
    const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;

    load({ type: "theme", theme: "dark", requestId: 42 });

    expect(messages.some((message) => (message as { type?: string } | null)?.type === "theme-applied")).toBe(false);
    rafCallbacks.shift()?.(0);
    expect(messages.some((message) => (message as { type?: string } | null)?.type === "theme-applied")).toBe(false);
    rafCallbacks.shift()?.(16);

    expect(messages).toContainEqual({ type: "theme-applied", theme: "dark", requestId: 42 });
  });

  it("keeps offscreen mermaid diagrams out of the blocking post-ready path", () => {
    const source = readFileSync("RendererWeb/src/renderer.ts", "utf8");
    const renderStart = source.indexOf("async function renderMermaidNodes(");
    const renderEnd = source.indexOf("function renderCodeBlocks", renderStart);
    const renderMermaidNodes = source.slice(renderStart, renderEnd);

    expect(renderStart).toBeGreaterThanOrEqual(0);
    expect(renderEnd).toBeGreaterThan(renderStart);
    expect(renderMermaidNodes).toContain("isMermaidNodeNearViewport");
    expect(renderMermaidNodes).toContain("installLazyMermaidObserver(lazyNodes, generation, mermaid);");
    expect(renderMermaidNodes).toContain("mm-mermaid-visible-first");
    expect(renderMermaidNodes).toContain("mm-mermaid-lazy-observe");
    expect(renderMermaidNodes).not.toContain("allNodes.slice");
  });

  // G-M2, the source half. The text assertion above pins that `lazyNodes` reaches the
  // observer; it cannot pin WHICH nodes `lazyNodes` holds, and that is the property the
  // one-decision-per-diagram change turns on: a minimap clone node must never be an
  // observer target, because the observer only ever sees what it was handed at install
  // time. The BEHAVIOURAL guard for it — proven to go red on a build where the partition
  // is bypassed, by observing the clone's nodes and reading their rects — lives in
  // mermaidEagerBudget.test.ts, "one render decision per diagram". This assertion is the
  // cheap source-level companion: the eager/lazy split must be taken over the partition's
  // deciding set, never over the swept set.
  it("derives the observed set from the live/unscoped partition, never from the raw sweep", () => {
    const source = readFileSync("RendererWeb/src/renderer.ts", "utf8");
    const renderStart = source.indexOf("async function renderMermaidNodes(");
    const renderEnd = source.indexOf("function renderCodeBlocks", renderStart);
    const renderMermaidNodes = source.slice(renderStart, renderEnd);

    expect(renderStart).toBeGreaterThanOrEqual(0);
    expect(renderEnd).toBeGreaterThan(renderStart);
    expect(renderMermaidNodes).toContain("partitionMermaidNodesBySurface(");
    expect(renderMermaidNodes).toContain("const decidingNodes = surfaces.deciding;");
    expect(renderMermaidNodes).toContain("decidingNodes.filter(node =>");
    expect(renderMermaidNodes).toContain("const lazyNodes = decidingNodes.filter(");
    expect(renderMermaidNodes).not.toContain("allNodes.filter");
  });

  // I-1 / G-B0-d. The minimap clone is TRUTHFUL because the math and mermaid passes are
  // document-wide: the clone is mounted before they capture their node sets, so it renders
  // its own content in place. Scoping any of them to the live document is barred on its
  // own (2 527 formulas revert to raw source in the minimap), and barred TWICE OVER
  // together with the Phase-B rebuild removal, which leaves no rebuild to repair them.
  // The fix for the double render is PROPAGATION, and propagation narrows nothing — so
  // the danger signature is checked directly: every mermaid sweep still starts at
  // `document`, and renderMath still receives `documentRoot: document`.
  it("keeps every mermaid sweep and the math documentRoot document-wide", () => {
    const source = readFileSync("RendererWeb/src/renderer.ts", "utf8");

    const sweeps = Array.from(source.matchAll(/([A-Za-z0-9_.]+)\.querySelectorAll<HTMLElement>\(\s*"pre\.mm-mermaid(:not\(\.is-rendered\))?"/g));
    // renderMermaid (theme refresh + post-ready), scheduleCachedMermaidResume,
    // recoverMermaidBarrierFailure, driveFullRenderBarrier.
    expect(sweeps).toHaveLength(4);
    for (const sweep of sweeps) {
      expect(sweep[1]).toBe("document");
    }

    expect(source).toContain("documentRoot: document");
    expect(source).not.toContain('documentRoot: document.querySelector');
  });

  // Claim 10. There are exactly TWO edges that mount clone nodes, and both are already
  // marked by a rebuildMinimapCloneBlockElementIndex call. The pull attaches beside each of
  // them — BEFORE the rebuild, so the geometry invalidation the rebuild performs covers the
  // content the pull just mirrored in. A third mount edge appearing without a pull re-opens
  // the hole silently, so the call-site inventory is pinned at two.
  it("runs the mirror pull at both clone-mount edges, before the index rebuild", () => {
    const source = readFileSync("RendererWeb/src/renderer.ts", "utf8");

    const mountEdges = Array.from(source.matchAll(/rebuildMinimapCloneBlockElementIndex\((?!root)/g));
    expect(mountEdges).toHaveLength(2);

    expect(source).toContain(
      "  mirrorRenderedMermaidIntoMinimapClone();\n"
      + "  rebuildMinimapCloneBlockElementIndex(clone);\n"
    );
    expect(source).toContain(
      "  mirrorRenderedMermaidIntoMinimapClone();\n"
      + "  rebuildMinimapCloneBlockElementIndex(minimapContent!);\n"
    );
  });

  // The eager gate's already-rendered defect is deliberately NOT admitted here: fixing it
  // needs the predicate AND the observer target changed together, because a lazy
  // display:none <pre> never intersects, and landing only the predicate half strands a
  // diagram with stale-theme colours for the rest of the session. So the predicate is
  // pinned byte for byte, to stop a mid-implementation tidy-up from landing that half alone.
  it("leaves isMermaidNodeNearViewport's body untouched", () => {
    const source = readFileSync("RendererWeb/src/mermaidRender.ts", "utf8");
    expect(source).toContain(
      "  const rect = node.getBoundingClientRect();\n"
      + "  return rect.bottom >= -marginPx && rect.top <= viewportHeight + marginPx;\n"
    );
  });

  // No timer, deadline, elapsed-time budget, requestIdleCallback or polling participates
  // in the render decision or in propagation. Both propagation moments are state edges: a
  // render settling, and a clone being mounted.
  it("arms no clock anywhere in the mermaid render or surface leaves", () => {
    for (const path of ["RendererWeb/src/mermaidRender.ts", "RendererWeb/src/mermaidSurface.ts"]) {
      const source = readFileSync(path, "utf8");
      for (const primitive of [
        "setTimeout",
        "setInterval",
        "requestIdleCallback",
        "requestAnimationFrame",
        "performance.now"
      ]) {
        expect(source, `${path} must not use ${primitive}`).not.toContain(primitive);
      }
    }

    // The single pre-existing Date.now() is a component of the render ID mermaid is
    // handed, not a decision about elapsed time. Pinned so it cannot quietly become one,
    // and so the propagation path can never acquire a second reading of the clock.
    const renderSource = readFileSync("RendererWeb/src/mermaidRender.ts", "utf8");
    const clockReads = renderSource.match(/Date\.now\(\)/g) ?? [];
    expect(clockReads).toHaveLength(1);
    expect(renderSource).toContain("`mm-mermaid-${generation}-${Date.now()}-${Math.random()");
  });

  it("does not rebuild a full-DOM minimap clone twice before first reveal", () => {
    const source = readFileSync("RendererWeb/src/renderer.ts", "utf8");
    const renderMathStart = source.indexOf("function renderMath(): MathReadinessController");
    const renderMathEnd = source.indexOf("function getCurrentTheme", renderMathStart);
    const renderMath = source.slice(renderMathStart, renderMathEnd);
    const helperStart = source.indexOf("function refreshInitialVisibleMinimapContent()");
    const helperEnd = source.indexOf("function postCachedMinimapState", helperStart);
    const helper = source.slice(helperStart, helperEnd);

    expect(renderMathStart).toBeGreaterThanOrEqual(0);
    expect(renderMathEnd).toBeGreaterThan(renderMathStart);
    expect(renderMath).toContain("refreshInitialVisibleMinimapContent();");
    expect(helper).toContain("if (!minimapSourceReady)");
    expect(helper).toContain('postPerfMark("mm-minimap-refresh-skipped"');
    expect(helper).toContain("updateMinimapVisibility(true);");
    expect(helper).toContain("updateMinimapViewport({ skipVisibilityUpdate: true });");
  });

  it("prepares and starts mode reveal on the renderer document", () => {
    const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;
    const main = document.querySelector<HTMLElement>("main.mm-document")!;

    load({ type: "mode-reveal-prepare", durationMs: 240 });

    expect(main.style.opacity).toBe("1");
    expect(main.style.transform).toBe("translateY(4px)");
    expect(main.style.willChange).toBe("transform");
    expect(main.style.transition).toBe("none");

    load({ type: "mode-reveal-start", durationMs: 240 });

    expect(main.style.opacity).toBe("1");
    expect(main.style.transform).toBe("translateY(0)");
    expect(main.style.transition).toContain("transform 240ms");
  });

  it("acks mode settle after chrome viewport work", () => {
    const rafCallbacks: FrameRequestCallback[] = [];
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
      rafCallbacks.push(callback);
      return rafCallbacks.length;
    });

    const messages: unknown[] = [];
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) }
    };
    const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;

    load({ type: "mode-settle-probe" });
    for (let frame = 0; frame < 8 && rafCallbacks.length > 0; frame++) {
      rafCallbacks.shift()?.(frame * 16);
    }

    const settledIndex = messages.findIndex((message) =>
      (message as { type?: string } | null)?.type === "mode-toggle-settled");
    const chromeReadyIndex = messages.findIndex((message) => {
      const m = message as { type?: string; name?: string } | null;
      return m?.type === "perf-mark" && m.name === "mm-mode-settle-chrome-ready";
    });

    expect(settledIndex).toBeGreaterThanOrEqual(0);
    expect(chromeReadyIndex).toBeGreaterThanOrEqual(0);
    expect(settledIndex).toBeGreaterThan(chromeReadyIndex);
  });

  it("keeps mode-settle ack behind layout-dependent chrome refreshes", () => {
    const source = readFileSync("RendererWeb/src/renderer.ts", "utf8");
    const handlerStart = source.indexOf('if (message.type === "mode-settle-probe")');
    const handlerEnd = source.indexOf('if (message.type === "mode-reveal-prepare")');
    const handler = source.slice(handlerStart, handlerEnd);
    const ackStart = handler.indexOf("const postModeToggleSettleAck = () => {");
    const paintGateStart = handler.indexOf("const completeModeToggleSettleAfterPaint = () => {");
    const paintGate = handler.slice(paintGateStart, handler.indexOf("window.requestAnimationFrame", paintGateStart + 1));
    const paintCallback = handler.slice(handler.indexOf("window.requestAnimationFrame", paintGateStart + 1), handler.indexOf("};", paintGateStart));
    const chromeReadyIndex = handler.indexOf('postPerfMark("mm-mode-settle-chrome-ready");');
    const ackIndex = handler.indexOf('postHostMessage({ type: "mode-toggle-settled" });');
    const paintMarkIndex = handler.indexOf('postPerfMark("mm-mode-settle-post-chrome-paint");');
    const paintCallbackMarkIndex = paintCallback.indexOf('postPerfMark("mm-mode-settle-post-chrome-paint");');
    const paintCallbackAckCallIndex = paintCallback.indexOf("postModeToggleSettleAck();");
    const visibilityRefreshIndex = handler.indexOf("updateMinimapVisibility();");
    const paintGateCallIndex = handler.indexOf("completeModeToggleSettleAfterPaint();");

    expect(handlerStart).toBeGreaterThanOrEqual(0);
    expect(handlerEnd).toBeGreaterThan(handlerStart);
    expect(ackStart).toBeGreaterThanOrEqual(0);
    expect(paintGateStart).toBeGreaterThan(ackStart);
    expect(chromeReadyIndex).toBeGreaterThanOrEqual(0);
    expect(ackIndex).toBeGreaterThanOrEqual(0);
    expect(chromeReadyIndex).toBeLessThan(ackIndex);
    expect(paintMarkIndex).toBeGreaterThan(paintGateStart);
    expect(paintCallbackMarkIndex).toBeGreaterThanOrEqual(0);
    expect(paintCallbackAckCallIndex).toBeGreaterThan(paintCallbackMarkIndex);
    expect(visibilityRefreshIndex).toBeGreaterThanOrEqual(0);
    expect(paintGateCallIndex).toBeGreaterThan(visibilityRefreshIndex);
    expect(paintGate).toContain("updateMinimapViewport();");
    expect(paintGate).toContain("updateWidthHandlePositionForCurrentLayout();");
  });

  it("defers edit-preview post-ready work behind mode-settle messages", () => {
    const source = readFileSync("RendererWeb/src/renderer.ts", "utf8");
    const flushStart = source.indexOf("function flushPostLayoutReadyWork()");
    const flushEnd = source.indexOf("function restoreCachedScrollPosition", flushStart);
    const flush = source.slice(flushStart, flushEnd);

    expect(flushStart).toBeGreaterThanOrEqual(0);
    expect(flushEnd).toBeGreaterThan(flushStart);
    expect(flush).toContain("viewerChromeEnabled ? 0 : POST_LAYOUT_READY_EDIT_PREVIEW_DELAY_MS");
    expect(flush).toContain('postPerfMark("post-ready-enhancements-deferred"');
  });
});
