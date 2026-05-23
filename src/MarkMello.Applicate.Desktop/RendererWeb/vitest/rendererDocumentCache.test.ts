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
  it("reports current geometry on cached layout-ready instead of stale cache geometry", async () => {
    const root = document.documentElement;
    Object.defineProperty(root, "scrollHeight", { configurable: true, value: 2000 });
    Object.defineProperty(root, "clientHeight", { configurable: true, value: 800 });
    vi.spyOn(window, "scrollTo").mockImplementation((options?: ScrollToOptions | number, y?: number) => {
      root.scrollTop = typeof options === "number"
        ? (y ?? 0)
        : (options?.top ?? 0);
    });

    const { load, messages } = await loadRendererWithMessages();
    const firstHtml = "<h1 id='first'>First</h1><p>cached document</p>";
    const secondHtml = "<h1 id='second'>Second</h1><p>other document</p>";

    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();
    root.scrollTop = 240;
    document.dispatchEvent(new Event("scroll"));
    await letPipelineSettle();

    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    Object.defineProperty(root, "scrollHeight", { configurable: true, value: 2600 });
    Object.defineProperty(root, "clientHeight", { configurable: true, value: 900 });
    messages.length = 0;
    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 3 });
    await letPipelineSettle();

    const cachedLayoutReady = messages.find((message): message is { type: "layout-ready"; cached?: boolean; scrollTop: number; scrollHeight: number; clientHeight: number } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "layout-ready");

    expect(cachedLayoutReady).toMatchObject({
      type: "layout-ready",
      cached: true,
      scrollTop: 240,
      scrollHeight: 2600,
      clientHeight: 900,
    });
  });

  it("marks cached layout-ready messages so the host does not restore by progress twice", async () => {
    const root = document.documentElement;
    Object.defineProperty(root, "scrollHeight", { configurable: true, value: 2000 });
    Object.defineProperty(root, "clientHeight", { configurable: true, value: 800 });
    vi.spyOn(window, "scrollTo").mockImplementation((options?: ScrollToOptions | number, y?: number) => {
      root.scrollTop = typeof options === "number"
        ? (y ?? 0)
        : (options?.top ?? 0);
    });

    const { load, messages } = await loadRendererWithMessages();
    const firstHtml = "<h1 id='first'>First</h1><p>cached document</p>";
    const secondHtml = "<h1 id='second'>Second</h1><p>other document</p>";

    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();
    root.scrollTop = 240;
    document.dispatchEvent(new Event("scroll"));
    await letPipelineSettle();

    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    messages.length = 0;
    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 3 });
    await letPipelineSettle();

    const cachedLayoutReady = messages.find((message): message is { type: "layout-ready"; cached?: boolean; scrollTop: number } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "layout-ready");

    expect(cachedLayoutReady).toMatchObject({
      type: "layout-ready",
      cached: true,
      scrollTop: 240,
    });
  });

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
