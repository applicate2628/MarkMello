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
    const documentReady = messages.find((m: { type?: string } | null) => m?.type === "document-ready");
    expect(documentReady).toBeTruthy();
  });

  it("does not throw if html is empty string", () => {
    const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;
    expect(() => load({ type: "load-document", html: "" })).not.toThrow();
  });

  it("applies load-document theme before renderer pipeline starts", () => {
    const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;
    document.documentElement.dataset.theme = "light";

    load({ type: "load-document", html: "<p>x</p>", theme: "dark" });

    expect(document.documentElement.dataset.theme).toBe("dark");
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

    expect(messages.some((message: { type?: string } | null) => message?.type === "theme-applied")).toBe(false);
    rafCallbacks.shift()?.(0);
    expect(messages.some((message: { type?: string } | null) => message?.type === "theme-applied")).toBe(false);
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
    expect(helper).toContain("updateMinimapViewport();");
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

    const settledIndex = messages.findIndex((message: { type?: string } | null) =>
      message?.type === "mode-toggle-settled");
    const chromeReadyIndex = messages.findIndex((message: { type?: string; name?: string } | null) =>
      message?.type === "perf-mark" && message.name === "mm-mode-settle-chrome-ready");

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
