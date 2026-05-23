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

  it("prepares and starts mode reveal on the renderer document", () => {
    const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;
    const main = document.querySelector<HTMLElement>("main.mm-document")!;

    load({ type: "mode-reveal-prepare", durationMs: 240 });

    expect(main.style.opacity).toBe("0");
    expect(main.style.transition).toBe("none");

    load({ type: "mode-reveal-start", durationMs: 240 });

    expect(main.style.opacity).toBe("1");
    expect(main.style.transition).toContain("opacity 240ms");
  });

  it("acks mode settle before deferred minimap viewport work", () => {
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
    const deferredIndex = messages.findIndex((message: { type?: string; name?: string } | null) =>
      message?.type === "perf-mark" && message.name === "mm-mode-settle-deferred-minimap-viewport");

    expect(settledIndex).toBeGreaterThanOrEqual(0);
    expect(deferredIndex).toBeGreaterThan(settledIndex);
  });

  it("keeps mode-settle ack before layout-dependent chrome refreshes", () => {
    const source = readFileSync("RendererWeb/src/renderer.ts", "utf8");
    const handlerStart = source.indexOf('if (message.type === "mode-settle-probe")');
    const handlerEnd = source.indexOf('if (message.type === "mode-reveal-prepare")');
    const handler = source.slice(handlerStart, handlerEnd);
    const ackIndex = handler.indexOf('postHostMessage({ type: "mode-toggle-settled" });');

    expect(handlerStart).toBeGreaterThanOrEqual(0);
    expect(handlerEnd).toBeGreaterThan(handlerStart);
    expect(ackIndex).toBeGreaterThanOrEqual(0);
    for (const layoutCall of [
      "updateWidthHandlePosition();",
      "updateMinimapVisibility();",
      'queueMinimapViewportUpdate("mm-mode-settle-deferred-minimap-viewport");'
    ]) {
      const callIndex = handler.indexOf(layoutCall);
      expect(callIndex === -1 || callIndex > ackIndex).toBe(true);
    }
    expect(handler.indexOf("queueModeSettleChromeRefresh();")).toBeGreaterThan(ackIndex);
  });
});
