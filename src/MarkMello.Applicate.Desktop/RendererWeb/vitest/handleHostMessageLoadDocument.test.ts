import { describe, it, expect, beforeEach } from "vitest";

// Import for side effects — bundles all module-globals and event wiring.
import "../src/renderer";

type HostBridge = (msg: unknown) => void;

beforeEach(() => {
  document.documentElement.innerHTML =
    `<body><main class="mm-document"></main></body>`;
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
});
