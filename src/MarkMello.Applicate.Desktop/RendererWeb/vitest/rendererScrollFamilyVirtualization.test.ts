import { afterEach, describe, expect, it, vi } from "vitest";

type HostBridge = (msg: unknown) => void;

type ScrollCall = {
  element: HTMLElement;
  options?: ScrollIntoViewOptions | boolean;
};

type RendererHarness = {
  flushNextRaf: () => Promise<void>;
  flushQueuedRafs: () => Promise<void>;
  flushRafsUntil: (predicate: () => boolean, maxFrames?: number) => Promise<void>;
  highlights: Map<string, Range[]>;
  load: HostBridge;
  messages: unknown[];
  root: HTMLElement;
  scrollCalls: ScrollCall[];
  scrollWrites: number[];
  triggerResize: () => void;
};

const SECTION_HEIGHT = 80;
const SECTION_PITCH = 120;
const VIEWPORT_HEIGHT = 200;

type ReadingPreferencesMessage = {
  type: "reading-preferences";
  fontFamily: "serif";
  fontSize: number;
  lineHeight: number;
  maxWidth: number;
  minimapMode: "off" | "auto" | "on";
  viewerChromeEnabled: boolean;
  documentScrollEnabled: boolean;
  wheelProxyEnabled: boolean;
  widthResizerVisibility: "always" | "on-hover";
};

const makeReadingPreferences = (
  minimapMode: ReadingPreferencesMessage["minimapMode"] = "on"
): ReadingPreferencesMessage => ({
  type: "reading-preferences",
  documentScrollEnabled: true,
  fontFamily: "serif",
  fontSize: 16,
  lineHeight: 1.6,
  maxWidth: 720,
  minimapMode,
  viewerChromeEnabled: true,
  wheelProxyEnabled: false,
  widthResizerVisibility: "always",
});

async function loadRendererHarness(options: {
  rectTopShiftByBlockIndex?: Record<number, number>;
  renderedSectionHeight?: number;
  sectionCount: number;
  virtualization: boolean;
}): Promise<RendererHarness> {
  vi.resetModules();
  document.documentElement.innerHTML = `<body><main class="mm-document"></main></body>`;
  trackRendererEventListeners();

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
  const requestAnimationFrameStub = (callback: FrameRequestCallback) => {
    rafCallbacks.push(callback);
    return rafCallbacks.length;
  };
  vi.stubGlobal("requestAnimationFrame", requestAnimationFrameStub);
  Object.defineProperty(window, "requestAnimationFrame", {
    configurable: true,
    value: requestAnimationFrameStub,
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

  const resizeObservers: TestResizeObserver[] = [];
  class TestResizeObserver implements ResizeObserver {
    constructor(private readonly callback: ResizeObserverCallback) {
      resizeObservers.push(this);
    }

    disconnect(): void { }
    observe(): void { }
    takeRecords(): ResizeObserverEntry[] { return []; }
    unobserve(): void { }

    trigger(): void {
      this.callback([], this);
    }
  }
  vi.stubGlobal("ResizeObserver", TestResizeObserver);

  const root = installVirtualizedDocumentLayout(
    options.sectionCount,
    options.renderedSectionHeight ?? SECTION_HEIGHT
  );
  const scrollWrites: number[] = [];
  const scrollTopDescriptor = Object.getOwnPropertyDescriptor(root, "scrollTop");
  Object.defineProperty(root, "scrollTop", {
    configurable: true,
    get: () => scrollTopDescriptor?.get?.call(root) as number,
    set: value => {
      scrollWrites.push(value);
      scrollTopDescriptor?.set?.call(root, value);
    },
  });
  vi.spyOn(window.HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
    const blockIndex = Number.parseInt(this.dataset.mmBlockIndex ?? "", 10);
    const rectShift = Number.isFinite(blockIndex) ? options.rectTopShiftByBlockIndex?.[blockIndex] ?? 0 : 0;
    const top = readSyntheticDocumentTop(this) - root.scrollTop + rectShift;
    const height = this.offsetHeight;
    return {
      bottom: top + height,
      height,
      left: 0,
      right: 0,
      top,
      width: 0,
      x: 0,
      y: top,
      toJSON() {
        return this;
      },
    } as DOMRect;
  });

  const scrollCalls: ScrollCall[] = [];
  Object.defineProperty(window.HTMLElement.prototype, "scrollIntoView", {
    configurable: true,
    value(this: HTMLElement, scrollOptions?: ScrollIntoViewOptions | boolean) {
      scrollCalls.push({ element: this, options: scrollOptions });
      root.scrollTop = readSyntheticDocumentTop(this);
    },
  });

  const highlights = new Map<string, Range[]>();
  class TestHighlight {
    readonly ranges: Range[];

    constructor(...ranges: Range[]) {
      this.ranges = ranges;
    }
  }
  Object.defineProperty(window, "Highlight", {
    configurable: true,
    value: TestHighlight,
  });
  Object.defineProperty(window, "CSS", {
    configurable: true,
    value: {
      highlights: {
        delete(name: string): void {
          highlights.delete(name);
        },
        set(name: string, highlight: TestHighlight): void {
          highlights.set(name, [...highlight.ranges]);
        },
      },
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
    for (let i = 0; i < 160 && rafCallbacks.length > 0; i++) {
      const callback = rafCallbacks.shift()!;
      callback(i * 16);
      await Promise.resolve();
    }
    if (rafCallbacks.length > 0) {
      throw new Error("requestAnimationFrame queue did not settle");
    }
    await Promise.resolve();
  };

  const flushRafsUntil = async (predicate: () => boolean, maxFrames = 40): Promise<void> => {
    for (let i = 0; i < maxFrames && !predicate() && rafCallbacks.length > 0; i++) {
      const callback = rafCallbacks.shift()!;
      callback(i * 16);
      await Promise.resolve();
    }
    await Promise.resolve();
  };

  const triggerResize = (): void => {
    for (const observer of resizeObservers) {
      observer.trigger();
    }
  };

  return { flushNextRaf, flushQueuedRafs, flushRafsUntil, highlights, load, messages, root, scrollCalls, scrollWrites, triggerResize };
}

function installVirtualizedDocumentLayout(
  sectionCount: number,
  renderedSectionHeight = SECTION_HEIGHT
): HTMLElement {
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
  Object.defineProperty(window, "innerHeight", {
    configurable: true,
    get: () => VIEWPORT_HEIGHT,
  });
  Object.defineProperty(window, "innerWidth", {
    configurable: true,
    get: () => 1400,
  });
  Object.defineProperty(window, "scrollY", {
    configurable: true,
    get: () => root.scrollTop,
  });
  const hasVirtualSpacers = (): boolean =>
    document.querySelector("[data-mm-virtual-spacer]") !== null;
  const readVirtualFlowOffset = (element: HTMLElement): number => {
    let top = 0;
    let sibling = element.previousElementSibling;
    while (sibling instanceof HTMLElement) {
      top += sibling.offsetHeight;
      sibling = sibling.previousElementSibling;
    }
    return top;
  };
  vi.spyOn(window.HTMLElement.prototype, "offsetTop", "get").mockImplementation(function (this: HTMLElement) {
    if (this.dataset.mmVirtualSpacer !== undefined) {
      return readVirtualFlowOffset(this);
    }

    const blockIndex = Number.parseInt(this.dataset.mmBlockIndex ?? "", 10);
    if (!Number.isFinite(blockIndex)) {
      return 0;
    }

    if (this.parentElement?.matches("main.mm-document")) {
      return hasVirtualSpacers()
        ? readVirtualFlowOffset(this)
        : blockIndex * SECTION_PITCH;
    }

    return 20;
  });
  vi.spyOn(window.HTMLElement.prototype, "offsetHeight", "get").mockImplementation(function (this: HTMLElement) {
    if (this.dataset.mmVirtualSpacer !== undefined) {
      return Number.parseFloat(this.style.height) || 0;
    }

    return this.hasAttribute("data-mm-block-index") ? renderedSectionHeight : 0;
  });
  vi.spyOn(window, "scrollTo").mockImplementation((options?: ScrollToOptions | number, y?: number) => {
    root.scrollTop = typeof options === "number" ? (y ?? 0) : (options?.top ?? 0);
  });
  return root;
}

type TrackedEventListener = {
  listener: EventListenerOrEventListenerObject;
  options?: AddEventListenerOptions | boolean;
  target: EventTarget;
  type: string;
};

const rendererEventListeners: TrackedEventListener[] = [];

function trackRendererEventListeners(): void {
  const track = (
    target: EventTarget,
    type: string,
    listener: EventListenerOrEventListenerObject,
    options?: AddEventListenerOptions | boolean
  ): void => {
    rendererEventListeners.push({ listener, options, target, type });
  };

  const addDocumentEventListener = document.addEventListener.bind(document);
  vi.spyOn(document, "addEventListener").mockImplementation((type, listener, options) => {
    track(document, type, listener, options);
    addDocumentEventListener(type, listener, options);
  });

  const addWindowEventListener = window.addEventListener.bind(window);
  vi.spyOn(window, "addEventListener").mockImplementation((type, listener, options) => {
    track(window, type, listener, options);
    addWindowEventListener(type, listener, options);
  });
}

function removeRendererEventListeners(): void {
  for (const { listener, options, target, type } of rendererEventListeners.splice(0)) {
    target.removeEventListener(type, listener, options);
  }
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

function buildSourceLineDocument(count: number): string {
  return Array.from({ length: count }, (_, index) =>
    `<p data-mm-block-index="${index}" data-mm-block-kind="paragraph" data-mm-source-line="${index * 10}" data-mm-source-end-line="${index * 10 + 4}">Block ${index}</p>`
  ).join("");
}

function buildNestedSourceLineDocument(count: number, ownerIndex: number, nestedIndex: number): string {
  return Array.from({ length: count }, (_, index) => {
    if (index === ownerIndex) {
      return `<section data-mm-block-index="${index}" data-mm-block-kind="quote"><blockquote data-mm-block-index="${nestedIndex}" data-mm-source-line="${index * 10}" data-mm-source-end-line="${index * 10 + 4}">Nested source ${nestedIndex}</blockquote></section>`;
    }

    return `<p data-mm-block-index="${index}" data-mm-block-kind="paragraph" data-mm-source-line="${index * 10}" data-mm-source-end-line="${index * 10 + 4}">Block ${index}</p>`;
  }).join("");
}

function buildSparseSourceLineDocument(count: number, anchorEvery: number): string {
  return Array.from({ length: count }, (_, index) => {
    const sourceLineAttributes = index % anchorEvery === 0
      ? ` data-mm-source-line="${index * 10}" data-mm-source-end-line="${index * 10}"`
      : "";
    return `<p data-mm-block-index="${index}" data-mm-block-kind="paragraph"${sourceLineAttributes}>Block ${index}</p>`;
  }).join("");
}

function buildFindDocument(count: number, matchIndexes: readonly number[]): string {
  const matches = new Set(matchIndexes);
  return Array.from({ length: count }, (_, index) => {
    const text = matches.has(index) ? `Block ${index} needle tail` : `Block ${index} filler`;
    return `<p data-mm-block-index="${index}" data-mm-block-kind="paragraph">${text}</p>`;
  }).join("");
}

function buildClonePollutionDocument(count: number): string {
  return Array.from({ length: count }, (_, index) => {
    const nested = index === 88
      ? `<blockquote data-mm-block-index="8801" data-mm-source-line="880" data-mm-source-end-line="884">Nested gamma ${index}</blockquote>`
      : "";
    return [
      `<section id="section-${index}" data-mm-block-index="${index}" data-mm-block-kind="paragraph" data-mm-source-line="${index * 10}" data-mm-source-end-line="${index * 10 + 4}">`,
      `<h2 id="heading-${index}">Heading ${index}</h2>`,
      `<p>Block ${index} gamma <span class="math-inline" data-tex="x_${index}">x_${index}</span></p>`,
      nested,
      "</section>",
    ].join("");
  }).join("");
}

function loadMinimapPolicy(load: HostBridge): void {
  load({
    type: "minimap-policy",
    minimapPolicy: {
      maxDetailedDocumentHeight: 1,
      minHostWidth: 0,
      minScrollableViewportRatio: 0,
    },
  });
}

function latestPerfDetail<T extends Record<string, unknown>>(
  messages: readonly unknown[],
  name: string
): T | undefined {
  const mark = messages
    .filter((message): message is { type: "perf-mark"; name: string; detail?: string } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "perf-mark"
      && (message as { name?: unknown }).name === name)
    .at(-1);
  return mark?.detail ? JSON.parse(mark.detail) as T : undefined;
}

function highestRenderedHeadingIndex(): number {
  return [...document.querySelectorAll<HTMLElement>("body > main.mm-document [id^='heading-']")]
    .map(element => Number.parseInt(element.id.replace("heading-", ""), 10))
    .filter(Number.isFinite)
    .reduce((max, index) => Math.max(max, index), -1);
}

function pointerEvent(type: string, clientY: number): PointerEvent {
  return new PointerEvent(type, {
    bubbles: true,
    cancelable: true,
    clientY,
    pointerId: 1,
  });
}

function setMinimapViewportHeight(height: number): void {
  Object.defineProperty(window, "innerHeight", {
    configurable: true,
    get: () => height + 128,
  });
}

type FindQueryMessage = {
  type: "find-query";
  requestId: number;
  query: string;
  renderId?: number | null;
};

type FindMatchDescriptor = {
  matchId: string;
  blockIndex: number;
  blockLocalOffset: number;
  length: number;
  normalizedText: string;
  ordinal: number;
};

function findQueryMessages(messages: readonly unknown[]): FindQueryMessage[] {
  return messages.filter((message): message is FindQueryMessage =>
    typeof message === "object"
    && message !== null
    && (message as { type?: unknown }).type === "find-query");
}

function findBarInput(): HTMLInputElement {
  const input = document.querySelector<HTMLInputElement>(".mm-find-input");
  if (input === null) {
    throw new Error("find input was not rendered");
  }
  return input;
}

function findBarCount(): HTMLElement {
  const count = document.querySelector<HTMLElement>(".mm-find-count");
  if (count === null) {
    throw new Error("find count was not rendered");
  }
  return count;
}

function submitFindQuery(query: string): void {
  const input = findBarInput();
  input.value = query;
  input.dispatchEvent(new Event("input", { bubbles: true }));
  input.dispatchEvent(new KeyboardEvent("keydown", { bubbles: true, key: "Enter" }));
}

function findButton(kind: "next" | "prev"): HTMLButtonElement {
  const button = document.querySelector<HTMLButtonElement>(`.mm-find-btn-${kind}`);
  if (button === null) {
    throw new Error(`find ${kind} button was not rendered`);
  }
  return button;
}

function descriptorForBlock(blockIndex: number, ordinal: number): FindMatchDescriptor {
  return {
    blockIndex,
    blockLocalOffset: `Block ${blockIndex} `.length,
    length: "needle".length,
    matchId: `b${blockIndex}-o${`Block ${blockIndex} `.length}-l6-n${ordinal}`,
    normalizedText: "needle",
    ordinal,
  };
}

function latestHeadingsUpdated(messages: readonly unknown[]): { headings: Array<{ id: string }> } | undefined {
  return messages
    .filter((message): message is { type: "headings-updated"; headings: Array<{ id: string }> } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "headings-updated")
    .at(-1);
}

async function enableDetailedMinimap(load: HostBridge, flushQueuedRafs: () => Promise<void>): Promise<void> {
  document.documentElement.style.setProperty("--mm-minimap-width", "136px");
  setMinimapViewportHeight(592);
  load({ type: "reading-preferences", ...makeReadingPreferences("on") });
  await flushQueuedRafs();
  loadMinimapPolicy(load);
}

function getMinimapContent(): HTMLElement {
  const minimapContent = document.querySelector<HTMLElement>(".mm-minimap-content");
  if (minimapContent === null) {
    throw new Error("minimap content was not rendered");
  }
  return minimapContent;
}

function dataMmAttributeNames(root: ParentNode): string[] {
  return Array.from(root.querySelectorAll<Element>("*"))
    .flatMap(element => Array.from(element.attributes))
    .map(attribute => attribute.name)
    .filter(name => name.startsWith("data-mm-"));
}

function countTextMatches(root: Node, needle: string): number {
  let count = 0;
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  let node = walker.nextNode();
  while (node !== null) {
    if ((node.nodeValue ?? "").includes(needle)) {
      count++;
    }
    node = walker.nextNode();
  }
  return count;
}

function expectMinimapCloneIdentitySanitized(): void {
  const minimapContent = getMinimapContent();
  expect(minimapContent.querySelectorAll("[id]")).toHaveLength(0);
  expect(minimapContent.querySelectorAll("[data-tex]")).toHaveLength(0);
  expect(dataMmAttributeNames(minimapContent)).toEqual([]);
}

function expectMinimapCloneTextRetained(needle: string, minimumMatches = 1): void {
  const minimapContent = getMinimapContent();
  const text = minimapContent.textContent ?? "";
  const occurrences = text.split(needle).length - 1;
  expect(text.length).toBeGreaterThan(0);
  expect(occurrences).toBeGreaterThanOrEqual(minimumMatches);
}

afterEach(() => {
  removeRendererEventListeners();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  window.history.replaceState(null, "", window.location.pathname);
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
    const { flushNextRaf, flushQueuedRafs, load, root, scrollCalls } = await loadRendererHarness({
      sectionCount: 120,
      virtualization: true,
    });
    load({ type: "load-document", html: buildHeadingDocument(120), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();
    scrollCalls.length = 0;

    expect(document.getElementById("heading-90")).toBeNull();
    load({ type: "scroll-to", anchor: "heading-90" });
    await flushNextRaf();

    expect(document.getElementById("heading-90")).not.toBeNull();
    expect(scrollCalls).toEqual([]);
    expect(root.scrollTop).toBeGreaterThan(0);
  });

  it("renders an off-window TOC target before smooth scrolling the heading", async () => {
    const { flushNextRaf, flushQueuedRafs, load, root, scrollCalls } = await loadRendererHarness({
      sectionCount: 120,
      virtualization: true,
    });
    load({ type: "load-document", html: buildHeadingDocument(120), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();
    scrollCalls.length = 0;

    expect(document.getElementById("heading-95")).toBeNull();
    load({ type: "scroll-to-heading", id: "heading-95" });
    await flushNextRaf();

    expect(document.getElementById("heading-95")).not.toBeNull();
    expect(scrollCalls).toEqual([]);
    expect(root.scrollTop).toBeGreaterThan(0);
  });

  it("routes hash navigation through the virtualized heading landing owner", async () => {
    const { flushNextRaf, flushQueuedRafs, load, root, scrollCalls } = await loadRendererHarness({
      sectionCount: 120,
      virtualization: true,
    });
    load({ type: "load-document", html: buildHeadingDocument(120), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();
    scrollCalls.length = 0;

    expect(document.getElementById("heading-90")).toBeNull();
    window.location.hash = "#heading-90";
    window.dispatchEvent(new Event("hashchange"));
    await flushNextRaf();

    expect(document.getElementById("heading-90")).not.toBeNull();
    expect(scrollCalls).toEqual([]);
    expect(root.scrollTop).toBeGreaterThan(0);
  });

  it("keeps an off-window TOC target rendered through intermediate smooth-scroll frames", async () => {
    const { flushNextRaf, flushQueuedRafs, load, root, scrollCalls } = await loadRendererHarness({
      sectionCount: 120,
      virtualization: true,
    });
    load({ type: "load-document", html: buildHeadingDocument(120), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();
    scrollCalls.length = 0;
    Object.defineProperty(window.HTMLElement.prototype, "scrollIntoView", {
      configurable: true,
      value(this: HTMLElement, scrollOptions?: ScrollIntoViewOptions | boolean) {
        scrollCalls.push({ element: this, options: scrollOptions });
        if (typeof scrollOptions === "object" && scrollOptions.behavior === "smooth") {
          root.scrollTop = 10 * SECTION_PITCH;
          document.dispatchEvent(new Event("scroll"));
          return;
        }

        root.scrollTop = readSyntheticDocumentTop(this);
      },
    });

    expect(document.getElementById("heading-95")).toBeNull();
    load({ type: "scroll-to-heading", id: "heading-95" });
    await flushNextRaf();
    expect(document.getElementById("heading-95")).not.toBeNull();

    await flushQueuedRafs();

    expect(document.getElementById("heading-95")).not.toBeNull();
    expect(root.scrollTop).toBeGreaterThan(10 * SECTION_PITCH);
  });

  it("renders the containing section and scrolls the nested block descendant", async () => {
    const { flushNextRaf, flushQueuedRafs, load, root, scrollCalls } = await loadRendererHarness({
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

    expect(document.querySelector<HTMLElement>('[data-mm-block-index="8801"]')).not.toBeNull();
    expect(scrollCalls).toEqual([]);
    expect(root.scrollTop).toBeGreaterThan(0);
  });

  it("reasserts a nested scroll-to-block target after a post-navigation window shift", async () => {
    const land = async (shiftDuringNavigation: boolean): Promise<number> => {
      const { flushNextRaf, flushQueuedRafs, load, root } = await loadRendererHarness({
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

      load({ type: "scroll-to-block", blockIndex: 8801 });
      await flushNextRaf();
      expect(document.querySelector<HTMLElement>('[data-mm-block-index="8801"]')).not.toBeNull();

      if (shiftDuringNavigation) {
        root.scrollTop -= 693;
        document.dispatchEvent(new Event("scroll"));
      }

      await flushQueuedRafs();
      const target = document.querySelector<HTMLElement>('[data-mm-block-index="8801"]');
      expect(target).not.toBeNull();
      return target!.getBoundingClientRect().top;
    };

    expect(await land(false)).toBeCloseTo(0, 0);
    expect(await land(true)).toBeCloseTo(0, 0);
  });

  it("corrects a deep block landing after estimated and rendered heights diverge", async () => {
    const { flushQueuedRafs, load } = await loadRendererHarness({
      rectTopShiftByBlockIndex: { 90: -64 },
      renderedSectionHeight: SECTION_PITCH,
      sectionCount: 120,
      virtualization: true,
    });
    load({ type: "load-document", html: buildSourceLineDocument(120), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();

    load({ type: "scroll-to-block", blockIndex: 90 });
    await flushQueuedRafs();

    const target = document.querySelector<HTMLElement>('body > main.mm-document [data-mm-block-index="90"]');
    expect(target).not.toBeNull();
    expect(target!.getBoundingClientRect().top).toBeCloseTo(0, 0);
  });

  it("terminates correction when a deep block landing estimate remains imperfect", async () => {
    const { flushQueuedRafs, load, root } = await loadRendererHarness({
      rectTopShiftByBlockIndex: { 95: 48 },
      renderedSectionHeight: SECTION_PITCH,
      sectionCount: 120,
      virtualization: true,
    });
    load({ type: "load-document", html: buildSourceLineDocument(120), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();

    load({ type: "scroll-to-block", blockIndex: 95 });
    await flushQueuedRafs();

    const target = document.querySelector<HTMLElement>('body > main.mm-document [data-mm-block-index="95"]');
    expect(target).not.toBeNull();
    expect(Number.isFinite(root.scrollTop)).toBe(true);
    expect(target!.getBoundingClientRect().top).toBeCloseTo(0, 0);
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

  it("renders an off-window source line before applying the 38 percent preview anchor", async () => {
    const land = async (): Promise<number> => {
      const { flushQueuedRafs, load } = await loadRendererHarness({
        renderedSectionHeight: SECTION_PITCH,
        sectionCount: 120,
        virtualization: true,
      });
      load({ type: "load-document", html: buildSourceLineDocument(120), hasMermaid: false, hasHljs: false });
      await flushQueuedRafs();

      load({ type: "scroll-to-source-line", sourceLine: 900 });
      await flushQueuedRafs();
      const target = document.querySelector<HTMLElement>('[data-mm-source-line="900"]');
      expect(target).not.toBeNull();
      return target!.getBoundingClientRect().top;
    };

    expect(await land()).toBeCloseTo(VIEWPORT_HEIGHT * 0.38, 0);
  });

  it("corrects a deep source-line landing to the preview anchor after estimates diverge", async () => {
    const { flushQueuedRafs, load } = await loadRendererHarness({
      rectTopShiftByBlockIndex: { 90: -64 },
      renderedSectionHeight: SECTION_PITCH,
      sectionCount: 120,
      virtualization: true,
    });
    load({ type: "load-document", html: buildSourceLineDocument(120), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();

    load({ type: "scroll-to-source-line", sourceLine: 900 });
    await flushQueuedRafs();

    const target = document.querySelector<HTMLElement>('body > main.mm-document [data-mm-source-line="900"]');
    expect(target).not.toBeNull();
    expect(target!.getBoundingClientRect().top).toBeCloseTo(VIEWPORT_HEIGHT * 0.38, 0);
  });

  it("resolves nested source spans through their containing section before edit-to-preview scroll", async () => {
    const land = async (): Promise<number> => {
      const { flushQueuedRafs, load } = await loadRendererHarness({
        renderedSectionHeight: SECTION_PITCH,
        sectionCount: 120,
        virtualization: true,
      });
      load({
        type: "load-document",
        html: buildNestedSourceLineDocument(120, 88, 8801),
        hasMermaid: false,
        hasHljs: false,
      });
      await flushQueuedRafs();

      load({ type: "scroll-to-source-line", sourceLine: 880 });
      await flushQueuedRafs();
      const target = document.querySelector<HTMLElement>('[data-mm-block-index="8801"]');
      expect(target).not.toBeNull();
      return target!.getBoundingClientRect().top;
    };

    expect(await land()).toBeCloseTo(VIEWPORT_HEIGHT * 0.38, 0);
  });

  it("posts monotonic preview source lines across sparse virtual window boundaries without duplicates", async () => {
    const { flushQueuedRafs, load, messages, root } = await loadRendererHarness({
      sectionCount: 260,
      virtualization: true,
    });
    load({ type: "load-document", html: buildSparseSourceLineDocument(260, 100), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();
    messages.length = 0;

    for (const sectionIndex of [50, 130, 210]) {
      root.scrollTop = sectionIndex * SECTION_HEIGHT;
      document.dispatchEvent(new Event("scroll"));
      await flushQueuedRafs();
    }

    const postedLines = messages
      .filter((message): message is { type: "preview-source-line"; sourceLine: number } =>
        typeof message === "object"
        && message !== null
        && (message as { type?: unknown }).type === "preview-source-line")
      .map(message => message.sourceLine)
      .filter(sourceLine => sourceLine > 0);
    expect(postedLines).toHaveLength(3);
    expect(new Set(postedLines).size).toBe(postedLines.length);
    expect(postedLines[0]).toBeLessThan(postedLines[1]!);
    expect(postedLines[1]).toBeLessThan(postedLines[2]!);
  });

  it("sanitizes the model-fragment minimap clone so global document lookups only see the live window", async () => {
    const sectionCount = 120;
    const { flushQueuedRafs, load } = await loadRendererHarness({
      renderedSectionHeight: SECTION_PITCH,
      sectionCount,
      virtualization: true,
    });
    await enableDetailedMinimap(load, flushQueuedRafs);
    load({ type: "load-document", html: buildClonePollutionDocument(sectionCount), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();

    const liveMain = document.querySelector<HTMLElement>("body > main.mm-document");
    expect(liveMain).not.toBeNull();
    expect(document.getElementById("heading-90")).toBeNull();
    expect(document.querySelectorAll("[data-mm-source-line]")).toHaveLength(
      liveMain!.querySelectorAll("[data-mm-source-line]").length
    );
    expect(document.querySelectorAll("[data-mm-block-index]")).toHaveLength(
      liveMain!.querySelectorAll("[data-mm-block-index]").length
    );
    expect(document.querySelectorAll("[data-tex]")).toHaveLength(
      liveMain!.querySelectorAll("[data-tex]").length
    );
    expectMinimapCloneIdentitySanitized();
    expectMinimapCloneTextRetained("gamma", sectionCount);
  });

  it("keeps clone-active programmatic landings anchored to the live window", async () => {
    const sectionCount = 120;
    const { flushNextRaf, flushQueuedRafs, load, root, scrollCalls } = await loadRendererHarness({
      renderedSectionHeight: SECTION_PITCH,
      sectionCount,
      virtualization: true,
    });
    await enableDetailedMinimap(load, flushQueuedRafs);
    load({ type: "load-document", html: buildClonePollutionDocument(sectionCount), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();
    expectMinimapCloneIdentitySanitized();
    expectMinimapCloneTextRetained("gamma", sectionCount);
    scrollCalls.length = 0;

    window.location.hash = "#heading-90";
    window.dispatchEvent(new Event("hashchange"));
    await flushNextRaf();
    await flushQueuedRafs();
    expect(document.querySelector<HTMLElement>("body > main.mm-document #heading-90")?.getBoundingClientRect().top)
      .toBeCloseTo(0, 0);

    load({ type: "scroll-to-heading", id: "heading-95" });
    await flushNextRaf();
    await flushQueuedRafs();
    expect(document.querySelector<HTMLElement>("body > main.mm-document #heading-95")?.getBoundingClientRect().top)
      .toBeCloseTo(0, 0);

    load({ type: "scroll-to-block", blockIndex: 50 });
    await flushNextRaf();
    await flushQueuedRafs();
    expect(document.querySelector<HTMLElement>('body > main.mm-document [data-mm-block-index="50"]')?.getBoundingClientRect().top)
      .toBeCloseTo(0, 0);

    for (let run = 0; run < 3; run++) {
      root.scrollTop += 47;
      document.dispatchEvent(new Event("scroll"));
      load({ type: "scroll-to-block", blockIndex: 8801 });
      await flushNextRaf();
      await flushQueuedRafs();
      expect(document.querySelector<HTMLElement>('body > main.mm-document [data-mm-block-index="8801"]')?.getBoundingClientRect().top)
        .toBeCloseTo(0, 0);
    }

    expect(scrollCalls).toEqual([]);
  });

  it("keeps legacy minimap clone text while legacy find excludes the clone", async () => {
    const sectionCount = 20;
    const { flushQueuedRafs, load } = await loadRendererHarness({
      renderedSectionHeight: SECTION_PITCH,
      sectionCount,
      virtualization: false,
    });
    await enableDetailedMinimap(load, flushQueuedRafs);
    load({ type: "load-document", html: buildClonePollutionDocument(sectionCount), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();

    const liveMain = document.querySelector<HTMLElement>("body > main.mm-document");
    const minimapDocument = document.querySelector<HTMLElement>(".mm-minimap-content .mm-document");
    expect(liveMain).not.toBeNull();
    expect(minimapDocument).not.toBeNull();
    expectMinimapCloneIdentitySanitized();
    expect(minimapDocument!.textContent?.length).toBe(liveMain!.textContent?.length);
    expectMinimapCloneTextRetained("gamma", sectionCount);

    document.dispatchEvent(new Event("DOMContentLoaded"));
    load({ type: "open-find-bar" });
    submitFindQuery("gamma");

    expect(findBarCount().textContent).toBe(`1 of ${sectionCount}`);
  });

  it("reasserts a pending programmatic source-line target after geometry invalidation", async () => {
    const { flushNextRaf, flushQueuedRafs, flushRafsUntil, load, root, triggerResize } = await loadRendererHarness({
      sectionCount: 120,
      virtualization: true,
    });
    load({ type: "load-document", html: buildSourceLineDocument(120), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();

    load({ type: "scroll-to-source-line", sourceLine: 900 });
    await flushNextRaf();
    const anchoredScrollTop = root.scrollTop;
    root.scrollTop = anchoredScrollTop + 240;

    triggerResize();
    const expectedAnchorTop = VIEWPORT_HEIGHT * 0.38;
    const readTargetTop = (): number | null => {
      const target = document.querySelector<HTMLElement>('body > main.mm-document [data-mm-source-line="900"]');
      return target === null ? null : target.getBoundingClientRect().top;
    };
    await flushRafsUntil(() => {
      const targetTop = readTargetTop();
      return targetTop !== null && Math.abs(targetTop - expectedAnchorTop) < 1;
    });

    expect(readTargetTop()).toBeCloseTo(expectedAnchorTop, 0);
  });

  it("excludes the model-fragment minimap clone from source-line anchor landing", async () => {
    const { flushQueuedRafs, load } = await loadRendererHarness({
      renderedSectionHeight: SECTION_PITCH,
      sectionCount: 120,
      virtualization: true,
    });
    load({ type: "load-document", html: buildSourceLineDocument(120), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();

    const liveMain = document.querySelector<HTMLElement>("body > main.mm-document");
    expect(liveMain).not.toBeNull();
    const minimap = document.createElement("aside");
    minimap.className = "mm-minimap";
    const minimapContent = document.createElement("div");
    minimapContent.className = "mm-minimap-content";
    const cloneDocument = document.createElement("main");
    cloneDocument.className = "mm-document";
    cloneDocument.innerHTML = buildSourceLineDocument(120);
    minimapContent.append(cloneDocument);
    minimap.append(minimapContent);
    document.body.append(minimap);

    const cloneAnchor = document.querySelector<HTMLElement>(".mm-minimap-content [data-mm-source-line='900']");
    expect(cloneAnchor).not.toBeNull();
    cloneAnchor!.getBoundingClientRect = () => ({
      bottom: 50_120,
      height: 120,
      left: 0,
      right: 0,
      top: 50_000,
      width: 0,
      x: 0,
      y: 50_000,
      toJSON: () => ({}),
    } as DOMRect);

    load({ type: "scroll-to-source-line", sourceLine: 900 });
    await flushQueuedRafs();

    const target = document.querySelector<HTMLElement>("body > main.mm-document [data-mm-source-line='900']");
    expect(target).not.toBeNull();
    expect(target!.getBoundingClientRect().top).toBeCloseTo(VIEWPORT_HEIGHT * 0.38, 0);
  });

  it("uses host find results for a global count and navigates off-window matches through the window target seam", async () => {
    const { flushQueuedRafs, flushRafsUntil, highlights, load, messages, root } = await loadRendererHarness({
      sectionCount: 120,
      virtualization: true,
    });
    document.dispatchEvent(new Event("DOMContentLoaded"));
    messages.length = 0;
    load({ type: "load-document", html: buildFindDocument(120, [90, 95]), hasMermaid: false, hasHljs: false, renderId: 7 });
    await flushQueuedRafs();
    messages.length = 0;
    root.scrollTop = 0;

    load({ type: "open-find-bar" });
    submitFindQuery("needle");

    const request = findQueryMessages(messages).at(-1);
    expect(request).toMatchObject({ query: "needle", renderId: 7 });
    expect(root.scrollTop).toBe(0);

    load({
      type: "find-results",
      requestId: request!.requestId,
      query: "needle",
      renderId: 7,
      totalCount: 2,
      matches: [descriptorForBlock(90, 1), descriptorForBlock(95, 2)],
    });

    expect(findBarCount().textContent).toBe("1 of 2");
    await flushRafsUntil(() => document.querySelector('[data-mm-block-index="90"]') !== null);
    await flushRafsUntil(() => document.querySelector('[data-mm-block-index="90"]') !== null);

    const firstTarget = document.querySelector<HTMLElement>('[data-mm-block-index="90"]');
    expect(firstTarget).not.toBeNull();
    expect(findBarCount().textContent).toBe("1 of 2");
    expect(firstTarget!.getBoundingClientRect().top).toBeCloseTo(0, 0);
    expect(highlights.get("mm-find-all")?.map(range => range.toString())).toEqual(["needle"]);
    expect(highlights.get("mm-find-current")?.map(range => range.toString())).toEqual(["needle"]);

    findButton("next").click();
    await flushRafsUntil(() => document.querySelector('[data-mm-block-index="95"]') !== null);
    await flushRafsUntil(() => document.querySelector('[data-mm-block-index="95"]') !== null);
    const secondTarget = document.querySelector<HTMLElement>('[data-mm-block-index="95"]');
    expect(secondTarget).not.toBeNull();
    expect(findBarCount().textContent).toBe("2 of 2");
    expect(secondTarget!.getBoundingClientRect().top).toBeCloseTo(0, 0);

    findButton("prev").click();
    await flushRafsUntil(() => document.querySelector('[data-mm-block-index="90"]') !== null);
    await flushRafsUntil(() => document.querySelector('[data-mm-block-index="90"]') !== null);
    const previousTarget = document.querySelector<HTMLElement>('[data-mm-block-index="90"]');
    expect(previousTarget).not.toBeNull();
    expect(findBarCount().textContent).toBe("1 of 2");
    expect(previousTarget!.getBoundingClientRect().top).toBeCloseTo(0, 0);
  });

  it("does not re-query the host index when virtual window replacement changes the live DOM", async () => {
    const { flushNextRaf, flushQueuedRafs, load, messages, root } = await loadRendererHarness({
      sectionCount: 120,
      virtualization: true,
    });
    document.dispatchEvent(new Event("DOMContentLoaded"));
    messages.length = 0;
    load({ type: "load-document", html: buildFindDocument(120, [90]), hasMermaid: false, hasHljs: false, renderId: 8 });
    await flushQueuedRafs();

    load({ type: "open-find-bar" });
    submitFindQuery("needle");
    const request = findQueryMessages(messages).at(-1);
    load({
      type: "find-results",
      requestId: request!.requestId,
      query: "needle",
      renderId: 8,
      totalCount: 1,
      matches: [descriptorForBlock(90, 1)],
    });
    findButton("next").click();
    await flushNextRaf();
    messages.length = 0;

    root.scrollTop = 10 * SECTION_PITCH;
    document.dispatchEvent(new Event("scroll"));
    await flushQueuedRafs();

    expect(findQueryMessages(messages)).toEqual([]);
  });

  it("keeps model-fragment minimap text while virtualized find counts only host results", async () => {
    const sectionCount = 120;
    const { flushQueuedRafs, load, messages, root } = await loadRendererHarness({
      renderedSectionHeight: SECTION_PITCH,
      sectionCount,
      virtualization: true,
    });
    document.dispatchEvent(new Event("DOMContentLoaded"));
    await enableDetailedMinimap(load, flushQueuedRafs);
    load({ type: "load-document", html: buildClonePollutionDocument(sectionCount), hasMermaid: false, hasHljs: false, renderId: 11 });
    await flushQueuedRafs();
    messages.length = 0;
    root.scrollTop = 0;

    const minimapDocument = document.querySelector<HTMLElement>(".mm-minimap-content .mm-document");
    expect(minimapDocument).not.toBeNull();
    expectMinimapCloneIdentitySanitized();
    expectMinimapCloneTextRetained("gamma", sectionCount);

    load({ type: "open-find-bar" });
    submitFindQuery("gamma");

    const request = findQueryMessages(messages).at(-1);
    expect(findQueryMessages(messages)).toHaveLength(1);
    expect(root.scrollTop).toBe(0);
    load({
      type: "find-results",
      requestId: request!.requestId,
      query: "gamma",
      renderId: 11,
      totalCount: 1,
      matches: [descriptorForBlock(90, 1)],
    });

    expect(findBarCount().textContent).toBe("1 of 1");
  });

  it("builds flag-on minimap content from a full model-fragment clone without cloning the live window", async () => {
    const cloneSpy = vi.spyOn(window.HTMLElement.prototype, "cloneNode");
    const sectionCount = 120;
    const { flushQueuedRafs, load, messages } = await loadRendererHarness({
      sectionCount,
      virtualization: true,
    });
    document.documentElement.style.setProperty("--mm-minimap-width", "136px");
    setMinimapViewportHeight(592);

    load({ type: "reading-preferences", ...makeReadingPreferences("on") });
    await flushQueuedRafs();
    loadMinimapPolicy(load);
    load({ type: "load-document", html: buildHeadingDocument(sectionCount), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();

    const liveMain = document.querySelector<HTMLElement>("main.mm-document");
    const minimapDocument = document.querySelector<HTMLElement>(".mm-minimap-content .mm-document");
    const built = latestPerfDetail<{ totalHeight: number }>(messages, "mm-virt-window-built");
    const adopted = latestPerfDetail<{ totalHeight: number | null }>(messages, "mm-virt-window-height-adopted");
    const expectedTotalHeight = adopted?.totalHeight ?? built?.totalHeight;
    const modelCloneSources = cloneSpy.mock.contexts.filter(
      (context): context is HTMLElement =>
        context instanceof window.HTMLElement
        && context.dataset["mmMinimapSource"] === "model-fragment");

    expect(liveMain).not.toBeNull();
    expect(liveMain!.querySelectorAll("[data-mm-block-index]")).not.toHaveLength(sectionCount);
    expect(modelCloneSources).toHaveLength(1);
    expect(modelCloneSources[0]!.querySelectorAll("[data-mm-block-index]")).toHaveLength(sectionCount);
    expect(cloneSpy).toHaveBeenCalledWith(true);
    expect(cloneSpy.mock.contexts).not.toContain(liveMain);
    expect(minimapDocument).not.toBeNull();
    expectMinimapCloneIdentitySanitized();
    expectMinimapCloneTextRetained("Heading", sectionCount);
    expect(document.querySelectorAll(".mm-minimap-content [data-mm-block-index]")).toHaveLength(0);
    expect(document.querySelector(".mm-minimap-content canvas[data-mm-model-minimap='true']")).toBeNull();
    expect(expectedTotalHeight).toBeGreaterThan(0);
  });

  it("rescales the model-fragment minimap after height adoption without rebuilding the clone", async () => {
    const cloneSpy = vi.spyOn(window.HTMLElement.prototype, "cloneNode");
    const sectionCount = 160;
    const { flushQueuedRafs, load, messages, root } = await loadRendererHarness({
      sectionCount,
      virtualization: true,
    });
    document.documentElement.style.setProperty("--mm-minimap-width", "136px");
    setMinimapViewportHeight(592);

    load({ type: "reading-preferences", ...makeReadingPreferences("on") });
    await flushQueuedRafs();
    loadMinimapPolicy(load);
    load({ type: "load-document", html: buildHeadingDocument(sectionCount), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();

    const modelCloneCount = () => cloneSpy.mock.contexts.filter(
      (context): context is HTMLElement =>
        context instanceof window.HTMLElement
        && context.dataset["mmMinimapSource"] === "model-fragment").length;
    expect(modelCloneCount()).toBe(1);

    root.scrollTop = 20 * SECTION_PITCH;
    document.dispatchEvent(new Event("scroll"));
    await flushQueuedRafs();

    const minimapDocument = document.querySelector<HTMLElement>(".mm-minimap-content .mm-document");
    expect(minimapDocument).not.toBeNull();
    expectMinimapCloneIdentitySanitized();
    expectMinimapCloneTextRetained("Heading", sectionCount);
    expect(document.querySelectorAll(".mm-minimap-content [data-mm-block-index]")).toHaveLength(0);
    expect(modelCloneCount()).toBe(1);
    expect(latestPerfDetail(messages, "mm-virt-window-height-adopted")).toBeDefined();
  });

  it("records zero external scroll shifts and terminates source-line correction within the navigation cap", async () => {
    const { flushQueuedRafs, load, messages, scrollWrites, triggerResize } = await loadRendererHarness({
      rectTopShiftByBlockIndex: { 90: -64 },
      renderedSectionHeight: SECTION_PITCH,
      sectionCount: 120,
      virtualization: true,
    });
    load({ type: "load-document", html: buildSourceLineDocument(120), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();
    messages.length = 0;
    scrollWrites.length = 0;

    load({ type: "scroll-to-source-line", sourceLine: 900 });
    triggerResize();
    await flushQueuedRafs();

    const target = document.querySelector<HTMLElement>('body > main.mm-document [data-mm-source-line="900"]');
    const settled = latestPerfDetail<{
      externalShiftCount: number;
      passCount: number;
      residual: number | null;
    }>(messages, "mm-virt-navigation-settled");
    expect(target).not.toBeNull();
    expect(target!.getBoundingClientRect().top).toBeCloseTo(VIEWPORT_HEIGHT * 0.38, 0);
    expect(settled).toBeDefined();
    expect(settled!.externalShiftCount).toBe(0);
    expect(settled!.passCount).toBeLessThanOrEqual(3);
    expect(Math.abs(settled!.residual ?? Number.POSITIVE_INFINITY)).toBeLessThanOrEqual(2);
    expect(scrollWrites.length).toBeGreaterThan(0);
  });

  it("clicks and drags the model-fragment minimap clone to off-window sections", async () => {
    const sectionCount = 120;
    const { flushQueuedRafs, load, root } = await loadRendererHarness({
      sectionCount,
      virtualization: true,
    });
    document.documentElement.style.setProperty("--mm-minimap-width", "136px");
    setMinimapViewportHeight(592);

    load({ type: "reading-preferences", ...makeReadingPreferences("on") });
    await flushQueuedRafs();
    loadMinimapPolicy(load);
    load({ type: "load-document", html: buildHeadingDocument(sectionCount), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();

    const minimap = document.querySelector<HTMLElement>(".mm-minimap");
    expect(minimap).not.toBeNull();
    Object.defineProperty(minimap!, "setPointerCapture", { configurable: true, value: vi.fn() });
    Object.defineProperty(minimap!, "releasePointerCapture", { configurable: true, value: vi.fn() });
    vi.spyOn(minimap!, "getBoundingClientRect").mockReturnValue({
      bottom: 592,
      height: 592,
      left: 0,
      right: 136,
      top: 0,
      width: 136,
      x: 0,
      y: 0,
      toJSON: () => ({}),
    } as DOMRect);

    expect(highestRenderedHeadingIndex()).toBeLessThan(sectionCount - 25);
    minimap!.dispatchEvent(pointerEvent("pointerdown", 588));
    minimap!.dispatchEvent(pointerEvent("pointerup", 588));

    const clickedScrollTop = root.scrollTop;
    const clickedHighest = highestRenderedHeadingIndex();
    expect(clickedScrollTop).toBeGreaterThan(0);
    expect(clickedHighest).toBeGreaterThan(25);

    root.scrollTop = 0;
    document.dispatchEvent(new Event("scroll"));
    await flushQueuedRafs();

    expect(highestRenderedHeadingIndex()).toBeLessThan(sectionCount - 25);
    minimap!.dispatchEvent(pointerEvent("pointerdown", 12));
    minimap!.dispatchEvent(pointerEvent("pointermove", 588));

    expect(root.scrollTop).toBeGreaterThanOrEqual(clickedScrollTop);
    expect(highestRenderedHeadingIndex()).toBeGreaterThanOrEqual(clickedHighest);
  });
});
