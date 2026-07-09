import { afterEach, describe, expect, it, vi } from "vitest";

type HostBridge = (msg: unknown) => void;

type ScrollCall = {
  element: HTMLElement;
  options?: ScrollIntoViewOptions | boolean;
};

type RendererHarness = {
  flushNextRaf: () => Promise<void>;
  flushQueuedRafs: () => Promise<void>;
  load: HostBridge;
  messages: unknown[];
  scrollCalls: ScrollCall[];
};

const SECTION_HEIGHT = 80;
const SECTION_PITCH = 120;
const VIEWPORT_HEIGHT = 200;

async function loadRendererHarness(options: {
  sectionCount: number;
  virtualization: boolean;
}): Promise<RendererHarness> {
  vi.resetModules();
  document.documentElement.innerHTML = `<body><main class="mm-document"></main></body>`;

  if (options.virtualization) {
    (window as unknown as { MARKMELLO_VIRTUALIZATION?: boolean }).MARKMELLO_VIRTUALIZATION = true;
  } else {
    delete (window as unknown as { MARKMELLO_VIRTUALIZATION?: boolean }).MARKMELLO_VIRTUALIZATION;
  }

  const messages: unknown[] = [];
  (window as unknown as { chrome: { webview: { postMessage: (message: unknown) => void } } }).chrome = {
    webview: { postMessage: (message: unknown) => messages.push(message) },
  };

  const rafCallbacks: FrameRequestCallback[] = [];
  vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
    rafCallbacks.push(callback);
    return rafCallbacks.length;
  });

  class TestIntersectionObserver implements IntersectionObserver {
    readonly root: Element | Document | null = null;
    readonly rootMargin = "0px";
    readonly thresholds: ReadonlyArray<number> = [];
    disconnect(): void { }
    observe(): void { }
    takeRecords(): IntersectionObserverEntry[] { return []; }
    unobserve(): void { }
  }
  vi.stubGlobal("IntersectionObserver", TestIntersectionObserver);

  const root = installVirtualizedDocumentLayout(options.sectionCount);

  const scrollCalls: ScrollCall[] = [];
  Object.defineProperty(window.HTMLElement.prototype, "scrollIntoView", {
    configurable: true,
    value(this: HTMLElement, scrollOptions?: ScrollIntoViewOptions | boolean) {
      scrollCalls.push({ element: this, options: scrollOptions });
      root.scrollTop = readSyntheticDocumentTop(this);
    },
  });

  await import("../src/renderer");
  const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;

  const flushNextRaf = async (): Promise<void> => {
    const callback = rafCallbacks.shift();
    if (!callback) {
      throw new Error("Expected a queued requestAnimationFrame callback");
    }

    callback(0);
    await Promise.resolve();
  };

  const flushQueuedRafs = async (): Promise<void> => {
    for (let i = 0; i < 40 && rafCallbacks.length > 0; i++) {
      const callback = rafCallbacks.shift()!;
      callback(i * 16);
      await Promise.resolve();
    }
    if (rafCallbacks.length > 0) {
      throw new Error("requestAnimationFrame queue did not settle");
    }
    await Promise.resolve();
  };

  return { flushNextRaf, flushQueuedRafs, load, messages, scrollCalls };
}

function installVirtualizedDocumentLayout(sectionCount: number): HTMLElement {
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
    get: () => VIEWPORT_HEIGHT,
  });
  Object.defineProperty(root, "scrollHeight", {
    configurable: true,
    get: () => sectionCount * SECTION_PITCH,
  });
  vi.spyOn(window.HTMLElement.prototype, "offsetTop", "get").mockImplementation(function (this: HTMLElement) {
    if (this.dataset.mmVirtualSpacer === "bottom") {
      return sectionCount * SECTION_PITCH;
    }

    const blockIndex = Number.parseInt(this.dataset.mmBlockIndex ?? "", 10);
    if (!Number.isFinite(blockIndex)) {
      return 0;
    }

    return this.parentElement?.matches("main.mm-document") ? blockIndex * SECTION_PITCH : 20;
  });
  vi.spyOn(window.HTMLElement.prototype, "offsetHeight", "get").mockImplementation(function (this: HTMLElement) {
    if (this.dataset.mmVirtualSpacer !== undefined) {
      return Number.parseFloat(this.style.height) || 0;
    }

    return this.hasAttribute("data-mm-block-index") ? SECTION_HEIGHT : 0;
  });
  vi.spyOn(window, "scrollTo").mockImplementation((options?: ScrollToOptions | number, y?: number) => {
    root.scrollTop = typeof options === "number" ? (y ?? 0) : (options?.top ?? 0);
  });
  return root;
}

function readSyntheticDocumentTop(element: HTMLElement): number {
  let top = 0;
  let current: HTMLElement | null = element;
  while (current !== null && current !== document.body && current !== document.documentElement) {
    top += current.offsetTop;
    current = current.parentElement instanceof HTMLElement ? current.parentElement : null;
  }
  return top;
}

function buildHeadingDocument(count: number): string {
  return Array.from({ length: count }, (_, index) =>
    `<h2 id="heading-${index}" data-mm-block-index="${index}" data-mm-block-kind="heading">Heading ${index}</h2>`
  ).join("");
}

function buildNestedBlockDocument(count: number, ownerIndex: number, nestedIndex: number): string {
  return Array.from({ length: count }, (_, index) => {
    if (index === ownerIndex) {
      return `<section data-mm-block-index="${index}" data-mm-block-kind="quote"><blockquote data-mm-block-index="${nestedIndex}">Nested target ${nestedIndex}</blockquote></section>`;
    }

    return `<p data-mm-block-index="${index}" data-mm-block-kind="paragraph">Block ${index}</p>`;
  }).join("");
}

function latestHeadingsUpdated(messages: readonly unknown[]): { headings: Array<{ id: string }> } | undefined {
  return messages
    .filter((message): message is { type: "headings-updated"; headings: Array<{ id: string }> } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "headings-updated")
    .at(-1);
}

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  delete (window as unknown as { MARKMELLO_VIRTUALIZATION?: boolean }).MARKMELLO_VIRTUALIZATION;
  delete (window as unknown as { chrome?: unknown }).chrome;
});

describe("renderer scroll-family virtualization integration", () => {
  it("posts all model headings to the TOC when virtualization has rendered only the first window", async () => {
    const headingCount = 1_005;
    const { load, messages } = await loadRendererHarness({
      sectionCount: headingCount,
      virtualization: true,
    });

    load({ type: "load-document", html: buildHeadingDocument(headingCount), hasMermaid: false, hasHljs: false });

    const headings = latestHeadingsUpdated(messages)?.headings ?? [];
    expect(document.getElementById("heading-999")).toBeNull();
    expect(headings).toHaveLength(headingCount);
    expect(headings.at(-1)).toMatchObject({ id: "heading-1004" });
  });

  it("renders an off-window anchor target before handling a scroll-to anchor message", async () => {
    const { flushNextRaf, flushQueuedRafs, load, scrollCalls } = await loadRendererHarness({
      sectionCount: 120,
      virtualization: true,
    });
    load({ type: "load-document", html: buildHeadingDocument(120), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();
    scrollCalls.length = 0;

    expect(document.getElementById("heading-90")).toBeNull();
    load({ type: "scroll-to", anchor: "heading-90" });
    await flushNextRaf();

    const target = document.getElementById("heading-90");
    expect(target).not.toBeNull();
    expect(scrollCalls).toEqual([
      { element: target, options: { block: "start" } },
    ]);
  });

  it("renders an off-window TOC target before smooth scrolling the heading", async () => {
    const { flushNextRaf, flushQueuedRafs, load, scrollCalls } = await loadRendererHarness({
      sectionCount: 120,
      virtualization: true,
    });
    load({ type: "load-document", html: buildHeadingDocument(120), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();
    scrollCalls.length = 0;

    expect(document.getElementById("heading-95")).toBeNull();
    load({ type: "scroll-to-heading", id: "heading-95" });
    await flushNextRaf();

    const target = document.getElementById("heading-95");
    expect(target).not.toBeNull();
    expect(scrollCalls).toEqual([
      { element: target, options: { behavior: "smooth", block: "start" } },
    ]);
  });

  it("renders the containing section and scrolls the nested block descendant", async () => {
    const { flushNextRaf, flushQueuedRafs, load, scrollCalls } = await loadRendererHarness({
      sectionCount: 120,
      virtualization: true,
    });
    load({
      type: "load-document",
      html: buildNestedBlockDocument(120, 88, 8801),
      hasMermaid: false,
      hasHljs: false,
    });
    await flushQueuedRafs();
    scrollCalls.length = 0;

    expect(document.querySelector('[data-mm-block-index="8801"]')).toBeNull();
    load({ type: "scroll-to-block", blockIndex: 8801 });
    await flushNextRaf();

    const target = document.querySelector<HTMLElement>('[data-mm-block-index="8801"]');
    expect(target).not.toBeNull();
    expect(scrollCalls).toEqual([
      { element: target!, options: { block: "start", behavior: "instant" } },
    ]);
  });

  it("leaves invalid virtualized anchor and block targets as no-ops", async () => {
    const { flushQueuedRafs, load, scrollCalls } = await loadRendererHarness({
      sectionCount: 20,
      virtualization: true,
    });
    load({ type: "load-document", html: buildHeadingDocument(20), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();
    scrollCalls.length = 0;

    load({ type: "scroll-to", anchor: "missing-heading" });
    load({ type: "scroll-to-heading", id: "missing-heading" });
    load({ type: "scroll-to-block", blockIndex: 999_999 });
    await Promise.resolve();

    expect(scrollCalls).toEqual([]);
  });

  it("keeps flag-off scroll messages on the existing live-DOM paths", async () => {
    const { flushQueuedRafs, load, scrollCalls } = await loadRendererHarness({
      sectionCount: 3,
      virtualization: false,
    });
    load({
      type: "load-document",
      html: [
        `<h2 id="live-heading" data-mm-block-index="0" data-mm-block-kind="heading">Live heading</h2>`,
        `<p data-mm-block-index="1" data-mm-block-kind="paragraph">Live block</p>`,
      ].join(""),
      hasMermaid: false,
      hasHljs: false,
    });
    await flushQueuedRafs();
    scrollCalls.length = 0;

    const heading = document.getElementById("live-heading");
    const block = document.querySelector<HTMLElement>('[data-mm-block-index="1"]');
    load({ type: "scroll-to", anchor: "live-heading" });
    load({ type: "scroll-to-heading", id: "live-heading" });
    load({ type: "scroll-to-block", blockIndex: 1 });

    expect(scrollCalls).toEqual([
      { element: heading, options: { block: "start" } },
      { element: heading, options: { behavior: "smooth", block: "start" } },
      { element: block!, options: { block: "start", behavior: "instant" } },
    ]);
  });
});
