import { beforeEach, describe, expect, it, vi } from "vitest";

type HostBridge = (msg: unknown) => void;

async function loadRendererWithMessages() {
  vi.resetModules();
  document.documentElement.innerHTML = `<body><main class="mm-document"></main></body>`;
  const messages: unknown[] = [];
  (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
    webview: { postMessage: (m: unknown) => messages.push(m) }
  };
  await import("../src/renderer");
  const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;
  return { load, messages };
}

async function letPipelineSettle(): Promise<void> {
  await new Promise(resolve => setTimeout(resolve, 50));
}

beforeEach(() => {
  delete (window as unknown as { chrome?: unknown }).chrome;
});

describe("renderer document cache", () => {
  it("restores cached minimap content on document cache hits without synchronously refreshing the minimap", async () => {
    const { load, messages } = await loadRendererWithMessages();
    const firstHtml = "<h1 id='first'>First</h1><p>cached document</p>";
    const secondHtml = "<h1 id='second'>Second</h1><p>other document</p>";

    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();
    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    messages.length = 0;
    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 3 });
    await letPipelineSettle();

    const perfMarks = messages
      .filter((message): message is { type: "perf-mark"; name: string } =>
        typeof message === "object"
        && message !== null
        && (message as { type?: unknown }).type === "perf-mark")
      .map(message => message.name);

    expect(perfMarks).toContain("mm-load-document-cache-hit");
    expect(perfMarks).toContain("mm-minimap-cache-hit");
    expect(perfMarks).not.toContain("mm-minimap-refresh-start");
  });
});
