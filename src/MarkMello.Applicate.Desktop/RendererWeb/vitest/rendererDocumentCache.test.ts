import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { readFileSync } from "node:fs";

type HostBridge = (msg: unknown) => void;

type TestKatexApi = {
  render: ReturnType<typeof vi.fn>;
};

type ReadingPreferencesMessage = {
  type: "reading-preferences";
  documentScrollEnabled: boolean;
  fontFamily: "serif";
  fontSize: number;
  lineHeight: number;
  maxWidth: number;
  minMaxWidth: number;
  minimapMode: "off" | "auto" | "on";
  viewerChromeEnabled: boolean;
  wheelProxyEnabled: boolean;
  widthResizerVisibility: "always" | "on-hover";
  viewportHeight: number;
  viewportWidth: number;
};

async function loadRendererWithMessages(options: {
  deferCacheClone?: boolean;
  katex?: TestKatexApi;
  virtualization?: boolean;
} = {}) {
  vi.resetModules();
  document.documentElement.innerHTML = `<body><main class="mm-document"></main></body>`;
  if (options.virtualization === true) {
    (window as unknown as { MARKMELLO_VIRTUALIZATION?: boolean }).MARKMELLO_VIRTUALIZATION = true;
  } else {
    delete (window as unknown as { MARKMELLO_VIRTUALIZATION?: boolean }).MARKMELLO_VIRTUALIZATION;
  }
  if (options.katex !== undefined) {
    (window as unknown as { katex?: TestKatexApi }).katex = options.katex;
  } else {
    delete (window as unknown as { katex?: TestKatexApi }).katex;
  }
  const idleCallbacks: Array<() => void> = [];
  if (options.deferCacheClone === true) {
    let nextIdleCallbackId = 1;
    Object.defineProperty(window, "requestIdleCallback", {
      configurable: true,
      value: vi.fn((callback: () => void) => {
        idleCallbacks.push(callback);
        return nextIdleCallbackId++;
      }),
    });
    Object.defineProperty(window, "cancelIdleCallback", {
      configurable: true,
      value: vi.fn(),
    });
  }
  const messages: unknown[] = [];
  (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
    webview: { postMessage: (m: unknown) => messages.push(m) }
  };
  await import("../src/renderer");
  const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;
  return { idleCallbacks, load, messages };
}

async function letPipelineSettle(): Promise<void> {
  // Cache entries are published only after post-ready enhancements complete.
  // The live renderer now gives edit-preview mode-settle messages a short
  // head start before post-ready Mermaid/hljs work, so fixed sleeps in these
  // cache tests need to cover that debounce plus Phase B minimap settle.
  await new Promise(resolve => setTimeout(resolve, 700));
}

function rendererCacheKey(html: string, theme: "light" | "dark" | "classic-white"): string {
  let hash = 2166136261;
  for (let index = 0; index < html.length; index++) {
    hash ^= html.charCodeAt(index);
    hash = Math.imul(hash, 16777619);
  }
  return `${theme}|${html.length}|${(hash >>> 0).toString(16).padStart(8, "0")}`;
}

function perfMarkDetail(messages: readonly unknown[], name: string): Record<string, unknown> | null {
  const mark = messages
    .filter((message): message is { type: "perf-mark"; name: string; detail?: string } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "perf-mark"
      && (message as { name?: unknown }).name === name)
    .at(-1);
  if (!mark?.detail) {
    return null;
  }

  return JSON.parse(mark.detail) as Record<string, unknown>;
}

function perfMarkNames(messages: readonly unknown[]): string[] {
  return messages
    .filter((message): message is { type: "perf-mark"; name: string } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "perf-mark")
    .map(message => message.name);
}

function makeReadingPreferences(minimapMode: ReadingPreferencesMessage["minimapMode"]): ReadingPreferencesMessage {
  return {
    type: "reading-preferences",
    documentScrollEnabled: true,
    fontFamily: "serif",
    fontSize: 16,
    lineHeight: 1.6,
    maxWidth: 820,
    minMaxWidth: 320,
    minimapMode,
    viewerChromeEnabled: true,
    viewportHeight: 800,
    viewportWidth: 1200,
    wheelProxyEnabled: true,
    widthResizerVisibility: "on-hover",
  };
}

function buildVirtualizedFormulaDocument(
  sectionCount: number,
  formulaSections: readonly number[],
  formulaState: "pending" | "ready" | "ready-with-failures" = "pending"
): string {
  const formulaSectionSet = new Set(formulaSections);
  return Array.from({ length: sectionCount }, (_value, index) => {
    const formula = formulaSectionSet.has(index)
      ? ` <span class="math-inline" data-tex="x_${index}"${formulaState === "pending" ? "" : ` data-mm-math-rendered="${formulaState === "ready" ? "true" : "failed"}"`}>x_${index}${formulaState === "pending" ? "" : `<span class="katex">rendered:x_${index}</span>`}</span>`
      : "";
    return `<section data-mm-block-index="${index}" data-mm-block-kind="paragraph">Section ${index}${formula}</section>`;
  }).join("");
}

function seedPlaceholderMinimapSnapshot(): void {
  const minimapContent = document.querySelector<HTMLElement>(".mm-minimap-content");
  if (!minimapContent) {
    throw new Error("minimap content was not mounted");
  }
  const source = document.createElement("main");
  source.className = "mm-document";
  source.innerHTML = `<section data-mm-block-index="90" data-mm-block-kind="paragraph"><span class="math-inline" data-tex="x_90">x_90</span></section>`;
  minimapContent.replaceChildren(source);
}

function expectNoRestoredPlaceholderMinimap(messages: readonly unknown[]): void {
  expect(perfMarkNames(messages)).toContain("mm-load-document-cache-hit");
  expect(perfMarkNames(messages)).not.toContain("mm-minimap-cache-hit");
  expect(document.querySelector(".mm-minimap-content [data-tex]")).toBeNull();
}

function dataMmAttributeNames(root: ParentNode): string[] {
  return Array.from(root.querySelectorAll<Element>("*"))
    .flatMap(element => Array.from(element.attributes))
    .map(attribute => attribute.name)
    .filter(name => name.startsWith("data-mm-"));
}

function readRendererSource(): string {
  return readFileSync("RendererWeb/src/renderer.ts", "utf8");
}

function readCacheRestoreSource(): string {
  const source = readRendererSource();
  const start = source.indexOf("function restoreCachedScrollPosition()");
  const end = source.indexOf("function scheduleLayoutReady", start);
  expect(start).toBeGreaterThanOrEqual(0);
  expect(end).toBeGreaterThan(start);
  return source.slice(start, end);
}

function installVirtualizedDocumentLayout(sectionHeight: number, sectionGap: number, sectionCount: number): void {
  const root = document.documentElement;
  let mutableScrollTop = 0;
  Object.defineProperty(document, "scrollingElement", {
    configurable: true,
    get: () => root,
  });
  Object.defineProperty(root, "scrollTop", {
    configurable: true,
    get: () => mutableScrollTop,
    set: value => {
      mutableScrollTop = value;
    },
  });
  Object.defineProperty(root, "clientHeight", {
    configurable: true,
    get: () => 400,
  });
  Object.defineProperty(root, "scrollHeight", {
    configurable: true,
    get: () => sectionCount * (sectionHeight + sectionGap),
  });
  vi.spyOn(window.HTMLElement.prototype, "offsetTop", "get").mockImplementation(function (this: HTMLElement) {
    const blockIndex = Number.parseInt(this.dataset.mmBlockIndex ?? "", 10);
    if (Number.isFinite(blockIndex)) {
      return blockIndex * (sectionHeight + sectionGap);
    }

    if (this.dataset.mmVirtualSpacer === "bottom") {
      return sectionCount * (sectionHeight + sectionGap);
    }

    return 0;
  });
  vi.spyOn(window.HTMLElement.prototype, "offsetHeight", "get").mockImplementation(function (this: HTMLElement) {
    if (this.hasAttribute("data-mm-block-index")) {
      return sectionHeight;
    }

    if (this.dataset.mmVirtualSpacer !== undefined) {
      return Number.parseFloat(this.style.height) || 0;
    }

    return 0;
  });
}

beforeEach(() => {
  delete (window as unknown as { chrome?: unknown }).chrome;
  delete (window as unknown as { MARKMELLO_VIRTUALIZATION?: boolean }).MARKMELLO_VIRTUALIZATION;
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("renderer document cache", () => {
  it("reclaims cloned mermaid proxy ownership before cache-hit model rebuild", () => {
    const source = readRendererSource();
    const start = source.indexOf("function ensureChromeNodes(");
    const end = source.indexOf("async function runLoadDocumentInitialRenderPipeline", start);
    const ensureChromeNodes = source.slice(start, end);
    expect(ensureChromeNodes).toContain("if (useCachedDocumentState && virtualizationEnabled)");
    expect(ensureChromeNodes.indexOf("reclaimClonedMermaidProxyLifecycles(main)"))
      .toBeLessThan(ensureChromeNodes.indexOf("initializeVirtualizedDocumentWindow()"));
  });

  it("captures live block plus intra-offset synchronously before reset", () => {
    const source = readRendererSource();
    const captureStart = source.indexOf("function captureCurrentProcessedDocumentCacheEntry");
    const captureEnd = source.indexOf("function storeProcessedDocumentCacheEntry", captureStart);
    const capture = source.slice(captureStart, captureEnd);
    expect(capture).toContain("captureCurrentVirtualizedReadingAnchor()");
    expect(capture.indexOf("captureCurrentVirtualizedReadingAnchor()"))
      .toBeLessThan(capture.indexOf("cloneNode"));
  });

  it("cache restore ignores raw scrollTop when geometry changed", () => {
    const restore = readCacheRestoreSource();
    const flagOnRestore = restore.slice(restore.indexOf(
      'const operation = acquireVirtualizedScrollOperation("cache-restore"'
    ));
    expect(flagOnRestore).not.toContain("layoutState.scrollTop");
    expect(flagOnRestore).toContain("scrollTopForReadingAnchor");
  });

  it("flag-on restore settles realized anchor geometry before its first root write", () => {
    const restore = readCacheRestoreSource();
    const prepareIndex = restore.indexOf("const prepared = await scheduleFrameWork");
    const settleIndex = restore.indexOf("await waitForCurrentVirtualizedGeometry(operation, 0)");
    const writeIndex = restore.indexOf("const initialReceipt = await scheduleWrite");
    expect(prepareIndex).toBeGreaterThanOrEqual(0);
    expect(settleIndex).toBeGreaterThan(prepareIndex);
    expect(writeIndex).toBeGreaterThan(settleIndex);
    expect(restore.slice(prepareIndex, settleIndex)).not.toContain("operation.requestScrollTop");
  });

  it("cache restore has no 180ms correctness retry", () => {
    const source = readRendererSource();
    const restore = readCacheRestoreSource();
    const cachedReadyStart = source.indexOf("function postCachedLayoutReady");
    const cachedReadyEnd = source.indexOf("function flushPostLayoutReadyWork", cachedReadyStart);
    const cachedReady = source.slice(cachedReadyStart, cachedReadyEnd);
    expect(restore).not.toContain("queueCachedGeometryRefresh");
    expect(cachedReady).toContain("if (!virtualizationEnabled && cachedLayoutState !== null)");
    expect(cachedReady).toContain("}, 180);");
  });

  it("cached ready follows semantic-anchor agreement under the current epoch", () => {
    const restore = readCacheRestoreSource();
    expect(restore).toContain("awaitConfirmedVirtualizedGeometry");
    expect(restore).toContain("semantic-anchor-agreed");
    expect(restore.indexOf("semantic-anchor-agreed"))
      .toBeLessThan(restore.indexOf("publishCachedRestoreReady"));
  });

  it("user-canceled restore still posts one cached ready with live geometry", () => {
    const restore = readCacheRestoreSource();
    expect(restore).toContain("user-supersession");
    expect(restore).toContain("publishCachedRestoreReady");
    expect(restore).toContain("getScrollState()");
  });

  it("delivered-frame non-convergence posts ready without settled", () => {
    const restore = readCacheRestoreSource();
    expect(restore).toContain("non-converged");
    expect(restore).toContain('finish("failed", "non-converged", "non-converged")');
    expect(restore).not.toContain('status: "settled", reason: "non-converged"');
  });

  it("frame-starved restore is paused not canceled", () => {
    const plane = readFileSync("RendererWeb/src/scrollOwnershipControlPlane.ts", "utf8");
    expect(plane).toContain('watchdogPaused');
    expect(plane).toContain('reason: "awaiting-delivered-frame"');
    expect(plane).not.toContain("setTimeout(failNonConvergence");
  });

  it("model-less restore cold-tops under lease and posts ready", () => {
    const restore = readCacheRestoreSource();
    expect(restore).toContain('coldTop ? "cache-cold-top" : "cache-restore"');
    expect(restore).toContain("operation.requestScrollTop(target, writer)");
    expect(restore).toContain("publishCachedRestoreReady");
  });

  it("stale restore event cannot write into the next document", () => {
    const restore = readCacheRestoreSource();
    expect(restore).toContain("isCurrentDocumentEpoch(documentEpoch)");
    expect(restore).toContain('finish("canceled", "stale-document"');
  });

  it("prestores the prepared active document so leaving a tab refreshes state instead of moving DOM", async () => {
    const root = document.documentElement;
    Object.defineProperty(root, "scrollHeight", { configurable: true, value: 2400 });
    Object.defineProperty(root, "clientHeight", { configurable: true, value: 800 });

    const { load, messages } = await loadRendererWithMessages();
    const firstHtml = "<h1 id='first'>First</h1><p>cached document</p>";
    const secondHtml = "<h1 id='second'>Second</h1><p>other document</p>";

    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();

    expect(messages).toContainEqual(expect.objectContaining({
      type: "perf-mark",
      name: "mm-document-cache-prestore",
    }));

    messages.length = 0;
    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    expect(messages).toContainEqual(expect.objectContaining({
      type: "perf-mark",
      name: "mm-document-cache-refresh",
    }));
    expect(messages).not.toContainEqual(expect.objectContaining({
      type: "perf-mark",
      name: "mm-document-cache-store",
    }));
  });

  it("reports cached geometry immediately and refreshes current geometry after cached reveal", async () => {
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
      scrollHeight: 2000,
      clientHeight: 800,
    });

    const currentGeometryScroll = messages.find((message): message is { type: "scroll"; scrollTop: number; scrollHeight: number; clientHeight: number } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "scroll"
      && (message as { scrollHeight?: unknown }).scrollHeight === 2600);

    expect(currentGeometryScroll).toMatchObject({
      type: "scroll",
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

  it("runs cached documents with unrendered math through the initial pipeline before layout-ready", async () => {
    const { load, messages } = await loadRendererWithMessages();
    const firstHtml = "<h1 id='first'>First</h1><p><span class='math-inline' data-tex='x'>x</span></p>";
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
    const layoutReady = messages.find((message): message is { type: "layout-ready"; cached?: boolean } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "layout-ready");

    expect(perfMarks).toContain("mm-load-document-cache-hit");
    expect(perfMarks).toContain("mermaid-skipped");
    expect(layoutReady).toBeTruthy();
    expect(layoutReady).not.toMatchObject({ cached: true });
  });

  it("reuses the cached top block index without a cached layout-ready DOM scan", async () => {
    const root = document.documentElement;
    Object.defineProperty(root, "scrollHeight", { configurable: true, value: 2000 });
    Object.defineProperty(root, "clientHeight", { configurable: true, value: 800 });
    vi.spyOn(window, "scrollTo").mockImplementation((options?: ScrollToOptions | number, y?: number) => {
      root.scrollTop = typeof options === "number"
        ? (y ?? 0)
        : (options?.top ?? 0);
    });

    const { load, messages } = await loadRendererWithMessages();
    const firstHtml = "<h1 id='first'>First</h1><p data-mm-block-index='42'>cached document</p>";
    const secondHtml = "<h1 id='second'>Second</h1><p>other document</p>";

    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();
    root.scrollTop = 240;
    document.dispatchEvent(new Event("scroll"));
    await letPipelineSettle();

    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    const rectSpy = vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      if (this.hasAttribute("data-mm-block-index")) {
        throw new Error("cached layout-ready measured block geometry");
      }
      return {
        x: 0,
        y: 0,
        width: 1,
        height: 1,
        top: 0,
        right: 1,
        bottom: 1,
        left: 0,
        toJSON: () => ({}),
      };
    });

    messages.length = 0;
    expect(() => load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 3 })).not.toThrow();
    await letPipelineSettle();
    rectSpy.mockRestore();

    const cachedScroll = messages.find((message): message is { type: "scroll"; topBlockIndex: number | null } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "scroll");

    expect(cachedScroll).toMatchObject({
      type: "scroll",
      topBlockIndex: 42,
    });
  });

  it("restores cached minimap content on document cache hits without synchronously refreshing the minimap", async () => {
    const root = document.documentElement;
    Object.defineProperty(root, "scrollHeight", { configurable: true, value: 2000 });
    Object.defineProperty(root, "clientHeight", { configurable: true, value: 800 });
    const { load, messages } = await loadRendererWithMessages();
    const firstHtml = "<h1 id='first'>First</h1><p>cached document</p>";
    const secondHtml = "<h1 id='second'>Second</h1><p>other document</p>";

    load({
      type: "reading-preferences",
      fontFamily: "serif",
      fontSize: 16,
      lineHeight: 1.6,
      maxWidth: 820,
      minMaxWidth: 320,
      minimapMode: "on",
      viewerChromeEnabled: true,
      documentScrollEnabled: true,
      wheelProxyEnabled: true,
      widthResizerVisibility: "on-hover",
      viewportWidth: 1200,
      viewportHeight: 800,
    });
    load({
      type: "minimap-policy",
      minimapPolicy: {
        minHostWidth: 0,
        minScrollableViewportRatio: 1,
        maxDetailedDocumentHeight: 1000,
      },
    });

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
    const restoredClone = document.querySelector<HTMLElement>(".mm-minimap-content .mm-document");
    expect(restoredClone).not.toBeNull();
    expect(restoredClone!.style.maxHeight).toBe("1000px");
    expect(restoredClone!.style.overflowY).toBe("hidden");
  });

  it("reuses post-ready mermaid documents from the processed document cache", async () => {
    const { load, messages } = await loadRendererWithMessages();
    const firstHtml = "<h1 id='first'>First</h1><pre class='mm-mermaid'>graph TD; A-->B;</pre>";
    const secondHtml = "<h1 id='second'>Second</h1><p>other document</p>";

    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "dark", hasMermaid: true, renderId: 1 });
    await letPipelineSettle();
    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "dark", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    messages.length = 0;
    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "dark", hasMermaid: true, renderId: 3 });
    await letPipelineSettle();

    const perfMarks = messages
      .filter((message): message is { type: "perf-mark"; name: string } =>
        typeof message === "object"
        && message !== null
        && (message as { type?: unknown }).type === "perf-mark")
      .map(message => message.name);
    const postReadyComplete = messages.find((message): message is { type: "post-ready-enhancements-complete"; renderId?: number } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "post-ready-enhancements-complete");

    expect(perfMarks).toContain("mm-load-document-cache-hit");
    expect(postReadyComplete).toMatchObject({ renderId: 3 });
  });

  it("restores cached documents by key without receiving the full html body again", async () => {
    const { load, messages } = await loadRendererWithMessages();
    const firstHtml = "<h1 id='first'>First</h1><p>cached document</p>";
    const secondHtml = "<h1 id='second'>Second</h1><p>other document</p>";

    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();
    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    messages.length = 0;
    load({
      type: "load-cached-document",
      cacheKey: rendererCacheKey(firstHtml, "light"),
      documentName: "first.md",
      theme: "light",
      hasMermaid: false,
      renderId: 3,
    });
    await letPipelineSettle();

    expect(document.querySelector("main.mm-document")?.textContent).toContain("First");
    expect(messages).toContainEqual(expect.objectContaining({
      type: "perf-mark",
      name: "mm-load-document-cache-hit",
    }));
    expect(messages).not.toContainEqual(expect.objectContaining({
      type: "document-cache-miss",
    }));
  });

  it("keeps cached minimap snapshots alive through load-cached-document reset", async () => {
    const root = document.documentElement;
    Object.defineProperty(root, "scrollHeight", { configurable: true, value: 2400 });
    Object.defineProperty(root, "clientHeight", { configurable: true, value: 800 });

    const { load, messages } = await loadRendererWithMessages();
    const firstHtml = "<h1 id='first'>First</h1><p>cached document</p>";
    const secondHtml = "<h1 id='second'>Second</h1><p>other document</p>";

    load({
      type: "reading-preferences",
      fontFamily: "serif",
      fontSize: 16,
      lineHeight: 1.6,
      maxWidth: 820,
      minMaxWidth: 320,
      minimapMode: "on",
      viewerChromeEnabled: true,
      documentScrollEnabled: true,
      wheelProxyEnabled: true,
      widthResizerVisibility: "on-hover",
      viewportWidth: 1200,
      viewportHeight: 800,
    });
    load({
      type: "minimap-policy",
      minimapPolicy: {
        minHostWidth: 0,
        minScrollableViewportRatio: 1,
        maxDetailedDocumentHeight: 10000,
      },
    });
    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();
    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    messages.length = 0;
    load({
      type: "load-cached-document",
      cacheKey: rendererCacheKey(firstHtml, "light"),
      documentName: "first.md",
      theme: "light",
      hasMermaid: false,
      renderId: 3,
    });
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

  it("re-extracts headings from cached DOM when the cached heading snapshot is empty", async () => {
    const { load, messages } = await loadRendererWithMessages();
    const firstHtml = "<p>cached document</p>";
    const secondHtml = "<h1 id='second'>Second</h1><p>other document</p>";

    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();
    document.querySelector("main.mm-document")!.innerHTML = "<h1 id='late'>Late heading</h1><p>cached document</p>";

    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    messages.length = 0;
    load({
      type: "load-cached-document",
      cacheKey: rendererCacheKey(firstHtml, "light"),
      documentName: "first.md",
      theme: "light",
      hasMermaid: false,
      renderId: 3,
    });
    await letPipelineSettle();

    expect(messages).toContainEqual(expect.objectContaining({
      type: "headings-updated",
      headings: [expect.objectContaining({ id: "late", text: "Late heading" })],
    }));
  });

  it("preserves the full model-backed document in cache hits while virtualization windows the live DOM", async () => {
    const sectionCount = 120;
    installVirtualizedDocumentLayout(80, 20, sectionCount);
    const { load, messages } = await loadRendererWithMessages({ virtualization: true });
    const firstHtml = Array.from({ length: sectionCount }, (_value, index) =>
      `<section data-mm-block-index="${index}" data-mm-block-kind="paragraph">First ${index}</section>`
    ).join("");
    const secondHtml = "<section data-mm-block-index='0' data-mm-block-kind='paragraph'>Second</section>";

    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();
    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    messages.length = 0;
    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 3 });
    await letPipelineSettle();

    expect(perfMarkDetail(messages, "mm-load-document-cache-hit")).toMatchObject({
      nodeCount: sectionCount,
    });
    expect(document.querySelectorAll("main.mm-document > [data-mm-block-index]").length).toBeLessThan(sectionCount);
  });

  it("does not restore unprepared placeholder minimap content from the tab-away cache fallback", async () => {
    const sectionCount = 120;
    installVirtualizedDocumentLayout(80, 20, sectionCount);
    const { load, messages } = await loadRendererWithMessages({
      deferCacheClone: true,
      virtualization: true,
    });
    const firstHtml = buildVirtualizedFormulaDocument(sectionCount, [90]);
    const secondHtml = "<section data-mm-block-index='0' data-mm-block-kind='paragraph'>Second</section>";

    load({ type: "reading-preferences", ...makeReadingPreferences("off") });
    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();
    seedPlaceholderMinimapSnapshot();

    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    messages.length = 0;
    load({
      type: "load-cached-document",
      cacheKey: rendererCacheKey(firstHtml, "light"),
      documentName: "first.md",
      theme: "light",
      hasMermaid: false,
      renderId: 3,
    });
    await letPipelineSettle();

    expectNoRestoredPlaceholderMinimap(messages);
  });

  it("keeps existing-entry refresh snapshot capture behind the cache payload helper", () => {
    const source = readRendererSource();
    const refreshStart = source.indexOf("function refreshProcessedDocumentCacheState");
    const refreshEnd = source.indexOf("function scheduleCurrentProcessedDocumentCacheClone", refreshStart);
    const refresh = source.slice(refreshStart, refreshEnd);

    expect(refreshStart).toBeGreaterThanOrEqual(0);
    expect(refreshEnd).toBeGreaterThan(refreshStart);
    expect(refresh).toContain("captureProcessedDocumentMinimapPayload");
    expect(refresh).not.toContain("captureMinimapSnapshot({");
    expect(refresh).toContain("headings: lastExtractedHeadings.map(cloneHeadingPayload)");
    expect(refresh).toContain("layoutState: virtualizationEnabled");
  });

  it("admits model-fragment minimap snapshots only for the current model generation", () => {
    const source = readRendererSource();
    const start = source.indexOf("function isCurrentModelFragmentMinimapSnapshot");
    const end = source.indexOf("function createMinimapContentProvenance", start);
    const admission = source.slice(start, end);

    expect(start).toBeGreaterThanOrEqual(0);
    expect(end).toBeGreaterThan(start);
    expect(admission).toContain('snapshot.provenance.source === "model-fragment"');
    expect(admission).toContain("snapshot.provenance.modelGeneration === virtualizedDocumentWindowModelGeneration");
    expect(admission).toContain('snapshot.content.querySelector("[data-tex]") === null');
  });

  it.each([
    ["ready", "ready"] as const,
    ["ready-with-failures", "ready-with-failures"] as const,
  ])("rejects a stale production terminal %s model-fragment minimap snapshot without DOM provenance markers", async (_label, formulaState) => {
    const sectionCount = 120;
    installVirtualizedDocumentLayout(80, 20, sectionCount);
    const { load, messages } = await loadRendererWithMessages({ virtualization: true });
    const firstHtml = buildVirtualizedFormulaDocument(sectionCount, [90], formulaState);
    const secondHtml = "<section data-mm-block-index='0' data-mm-block-kind='paragraph'>Second</section>";

    load({ type: "reading-preferences", ...makeReadingPreferences("on") });
    load({
      type: "minimap-policy",
      minimapPolicy: {
        maxDetailedDocumentHeight: 10000,
        minHostWidth: 0,
        minScrollableViewportRatio: 1,
      },
    });
    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();
    const minimapContent = document.querySelector<HTMLElement>(".mm-minimap-content");
    expect(minimapContent).not.toBeNull();
    expect(minimapContent!.querySelector(".katex")?.textContent).toBe(`rendered:x_90`);
    expect(dataMmAttributeNames(minimapContent!)).toEqual([]);

    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    messages.length = 0;
    load({
      type: "load-cached-document",
      cacheKey: rendererCacheKey(firstHtml, "light"),
      documentName: "first.md",
      theme: "light",
      hasMermaid: false,
      renderId: 3,
    });
    await letPipelineSettle();

    expect(perfMarkNames(messages)).toContain("mm-load-document-cache-hit");
    expect(perfMarkNames(messages)).not.toContain("mm-minimap-cache-hit");
    expect(perfMarkNames(messages)).toContain("mm-minimap-refresh-start");
    expect(document.querySelector(".mm-minimap-content .katex")?.textContent).toBe("rendered:x_90");
    expect(dataMmAttributeNames(document.querySelector<HTMLElement>(".mm-minimap-content")!)).toEqual([]);
  });

  it("keeps every processed-document cache writer behind the shared minimap payload helper", () => {
    const source = readRendererSource();
    const captureStart = source.indexOf("function captureCurrentProcessedDocumentCacheEntry");
    const storeStart = source.indexOf("function storeProcessedDocumentCacheEntry", captureStart);
    const refreshStart = source.indexOf("function refreshProcessedDocumentCacheState");
    const scheduleStart = source.indexOf("function scheduleCurrentProcessedDocumentCacheClone");
    const preserveStart = source.indexOf("function preserveCurrentProcessedDocument");
    const helperName = "captureProcessedDocumentMinimapPayload";

    expect(captureStart).toBeGreaterThanOrEqual(0);
    expect(refreshStart).toBeGreaterThanOrEqual(0);
    expect(scheduleStart).toBeGreaterThanOrEqual(0);
    expect(preserveStart).toBeGreaterThanOrEqual(0);
    expect(source.slice(captureStart, storeStart)).toContain(helperName);
    expect(source.slice(refreshStart, scheduleStart)).toContain(helperName);
    expect(source.slice(scheduleStart, preserveStart)).toContain("captureCurrentProcessedDocumentCacheEntry(\"clone\")");
    expect(source.slice(preserveStart, source.indexOf("function applyViewerChromeState", preserveStart)))
      .toContain("captureCurrentProcessedDocumentCacheEntry(\"move\")");
    expect(source.slice(source.indexOf("function getCachedProcessedDocumentFragment"), captureStart))
      .toContain("validateCachedRenderedContentState");
    expect(source.slice(source.indexOf("function getCachedProcessedDocumentFragment"), captureStart))
      .toContain(": null");
  });

  it("posts heading inline segments for math spans", async () => {
    const { load, messages } = await loadRendererWithMessages();
    const html = "<h1 id='wave'>Wave <span class='math-inline' data-tex='Z_{0}'></span> ports</h1>";

    load({ type: "load-document", html, documentName: "math-heading.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();

    expect(messages).toContainEqual(expect.objectContaining({
      type: "headings-updated",
      headings: [expect.objectContaining({
        id: "wave",
        text: "Wave Z_{0} ports",
        segments: [
          { kind: "text", text: "Wave " },
          { kind: "math", text: "Z_{0}" },
          { kind: "text", text: " ports" },
        ],
      })],
    }));
  });

  it("leaves the current document in place and asks the host for fallback html when a key restore misses", async () => {
    const { load, messages } = await loadRendererWithMessages();
    const currentHtml = "<h1 id='current'>Current</h1>";

    load({ type: "load-document", html: currentHtml, documentName: "current.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();

    messages.length = 0;
    load({
      type: "load-cached-document",
      cacheKey: "light|123|deadbeef",
      documentName: "missing.md",
      theme: "light",
      hasMermaid: false,
      renderId: 2,
    });
    await letPipelineSettle();

    expect(document.querySelector("main.mm-document")?.textContent).toContain("Current");
    expect(messages).toContainEqual(expect.objectContaining({
      type: "document-cache-miss",
      renderId: 2,
      cacheKey: "light|123|deadbeef",
    }));
    expect(messages).toContainEqual(expect.objectContaining({
      type: "perf-mark",
      name: "mm-load-document-cache-miss",
    }));
  });

  it("resumes lazy mermaid rendering for missing diagrams after a processed document cache hit", async () => {
    type FakeObserverRecord = {
      callback: IntersectionObserverCallback;
      elements: Set<Element>;
    };
    const observers: FakeObserverRecord[] = [];
    class FakeIntersectionObserver {
      private readonly record: FakeObserverRecord;

      constructor(callback: IntersectionObserverCallback) {
        this.record = { callback, elements: new Set<Element>() };
        observers.push(this.record);
      }

      observe(element: Element): void {
        this.record.elements.add(element);
      }

      unobserve(element: Element): void {
        this.record.elements.delete(element);
      }

      disconnect(): void {
        this.record.elements.clear();
      }
    }

    vi.stubGlobal("IntersectionObserver", FakeIntersectionObserver as unknown as typeof IntersectionObserver);
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      if (this.classList.contains("mm-mermaid")) {
        return {
          x: 0,
          y: 5000,
          width: 600,
          height: 120,
          top: 5000,
          right: 600,
          bottom: 5120,
          left: 0,
          toJSON: () => ({}),
        } as DOMRect;
      }

      return {
        x: 0,
        y: 0,
        width: 600,
        height: 40,
        top: 0,
        right: 600,
        bottom: 40,
        left: 0,
        toJSON: () => ({}),
      } as DOMRect;
    });

    const root = document.documentElement;
    Object.defineProperty(root, "scrollHeight", { configurable: true, value: 6000 });
    Object.defineProperty(root, "clientHeight", { configurable: true, value: 800 });
    const mermaidRender = vi.fn(async (_id: string, source: string) => ({ svg: `<svg>${source}</svg>` }));
    (window as unknown as {
      mermaid: {
        initialize: (config: unknown) => void;
        render: (id: string, source: string) => Promise<{ svg: string }>;
      };
    }).mermaid = {
      initialize: vi.fn(),
      render: mermaidRender,
    };

    const { load, messages } = await loadRendererWithMessages();
    const firstHtml = "<h1 id='first'>First</h1><pre class='mm-mermaid'><code data-mm-mermaid>graph TD; A-->B;</code></pre>";
    const secondHtml = "<h1 id='second'>Second</h1><p>other document</p>";

    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: true, renderId: 1 });
    await letPipelineSettle();
    expect(mermaidRender).not.toHaveBeenCalled();

    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    messages.length = 0;
    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: true, renderId: 3 });
    await letPipelineSettle();

    const restoredMermaid = document.querySelector<HTMLElement>("pre.mm-mermaid");
    expect(restoredMermaid).not.toBeNull();
    expect(restoredMermaid!.classList.contains("is-rendered")).toBe(false);
    expect(messages).toContainEqual(expect.objectContaining({
      type: "perf-mark",
      name: "mm-mermaid-cache-resume",
    }));
    expect(observers.at(-1)?.elements.has(restoredMermaid!)).toBe(true);

    observers.at(-1)?.callback([
      { target: restoredMermaid!, isIntersecting: true } as IntersectionObserverEntry
    ], {} as IntersectionObserver);
    await letPipelineSettle();

    expect(mermaidRender).toHaveBeenCalledWith(expect.any(String), "graph TD; A-->B;");
    expect(restoredMermaid!.classList.contains("is-rendered")).toBe(true);
    expect(restoredMermaid!.nextElementSibling?.classList.contains("mm-mermaid-svg")).toBe(true);
  });
});
