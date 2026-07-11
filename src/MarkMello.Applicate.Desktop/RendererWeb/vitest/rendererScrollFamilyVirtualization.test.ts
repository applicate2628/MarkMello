import { afterEach, describe, expect, it, vi } from "vitest";
import { createH3DiagnosticMonitor } from "./h3DiagnosticMonitor";

type HostBridge = (msg: unknown) => void;

type ScrollCall = {
  element: HTMLElement;
  options?: ScrollIntoViewOptions | boolean;
};

type IntersectionObserverRecord = {
  disconnected: boolean;
  observed: Element[];
  rootMargin: string;
  unobserved: Element[];
};

type RendererHarness = {
  flushAnimationFrame: () => Promise<void>;
  flushCanceledRafs: () => Promise<void>;
  flushNextRaf: () => Promise<void>;
  flushQueuedRafs: () => Promise<void>;
  flushRafsUntil: (predicate: () => boolean, maxFrames?: number) => Promise<void>;
  flushResizeRafsSynchronously: () => void;
  highlights: Map<string, Range[]>;
  intersectionObservers: IntersectionObserverRecord[];
  load: HostBridge;
  messages: unknown[];
  pendingRafCount: () => number;
  root: HTMLElement;
  scrollCalls: ScrollCall[];
  scrollWrites: number[];
  setRenderedSectionHeight: (height: number) => void;
  triggerResize: () => void;
};

let readPendingRendererRafCount: (() => number) | null = null;
let clearPendingRendererRafs: (() => void) | null = null;

type ControllerFaults = {
  adoptRenderedHeights?: boolean;
  ensureSectionRendered?: boolean;
  onAdoptRenderedHeights?: () => void;
  onCreated?: (controller: import("../src/virtualizedDocumentWindow").VirtualizedDocumentWindowController) => void;
};

type TestKatexApi = {
  render: ReturnType<typeof vi.fn>;
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
  controllerFaults?: ControllerFaults;
  deliverRealizationEventsAfterFrame?: boolean;
  fontsReady?: Promise<unknown>;
  intersectionObserverAvailable?: boolean;
  katex?: TestKatexApi;
  mathReadiness?: readonly Promise<void>[];
  rectTopShiftByBlockIndex?: Record<number, number>;
  renderedSectionHeight?: number;
  sectionCount: number;
  virtualization: boolean;
}): Promise<RendererHarness> {
  vi.resetModules();
  vi.doUnmock("../src/virtualizedDocumentWindow");
  vi.doUnmock("../src/mathRenderInit");
  if (options.mathReadiness !== undefined) {
    const mathReadiness = options.mathReadiness;
    vi.doMock("../src/mathRenderInit", async () => {
      const actual = await vi.importActual<typeof import("../src/mathRenderInit")>("../src/mathRenderInit");
      let invocation = 0;
      return {
        ...actual,
        renderMath: (deps: Parameters<typeof actual.renderMath>[0]) => {
          const readiness = mathReadiness[invocation++];
          if (readiness === undefined) {
            return actual.renderMath(deps);
          }
          let canceled = false;
          return {
            allMathRendered: readiness,
            cancel: () => { canceled = true; },
            initialVisibleNodes: new Set<HTMLElement>(),
            initialVisibleReady: readiness,
            isCancelled: () => canceled,
            totalMathCount: deps.documentRoot.querySelectorAll("[data-tex]").length,
          };
        },
      };
    });
  }
  if (options.controllerFaults !== undefined) {
    const controllerFaults = options.controllerFaults;
    vi.doMock("../src/virtualizedDocumentWindow", async () => {
      const actual = await vi.importActual<typeof import("../src/virtualizedDocumentWindow")>(
        "../src/virtualizedDocumentWindow"
      );
      return {
        ...actual,
        createVirtualizedDocumentWindowController: (
          deps: Parameters<typeof actual.createVirtualizedDocumentWindowController>[0]
        ) => {
          const controller = actual.createVirtualizedDocumentWindowController(deps);
          controllerFaults.onCreated?.(controller);
          const adoptRenderedHeights = controller.adoptRenderedHeights.bind(controller);
          const ensureSectionRendered = controller.ensureSectionRendered.bind(controller);
          controller.adoptRenderedHeights = adoptOptions => {
            if (controllerFaults.adoptRenderedHeights === true) {
              throw new Error("injected adoptRenderedHeights failure");
            }
            const result = adoptRenderedHeights(adoptOptions);
            controllerFaults.onAdoptRenderedHeights?.();
            return result;
          };
          controller.ensureSectionRendered = (sectionIndex, ensureOptions) => {
            if (controllerFaults.ensureSectionRendered === true) {
              throw new Error("injected ensureSectionRendered failure");
            }
            return ensureSectionRendered(sectionIndex, ensureOptions);
          };
          return controller;
        },
      };
    });
  }
  document.documentElement.innerHTML = `<body><main class="mm-document"></main></body>`;
  if (options.fontsReady !== undefined) {
    Object.defineProperty(document, "fonts", {
      configurable: true,
      value: { ready: options.fontsReady },
    });
  }
  trackRendererEventListeners();

  if (options.virtualization) {
    (window as unknown as { MARKMELLO_VIRTUALIZATION?: boolean }).MARKMELLO_VIRTUALIZATION = true;
  } else {
    delete (window as unknown as { MARKMELLO_VIRTUALIZATION?: boolean }).MARKMELLO_VIRTUALIZATION;
  }
  if (options.katex !== undefined) {
    (window as unknown as { katex?: TestKatexApi }).katex = options.katex;
  } else {
    delete (window as unknown as { katex?: TestKatexApi }).katex;
  }

  const messages: unknown[] = [];
  (window as unknown as { chrome: { webview: { postMessage: (message: unknown) => void } } }).chrome = {
    webview: { postMessage: (message: unknown) => messages.push(message) },
  };

  const rafCallbacks: Array<{ callback: FrameRequestCallback; id: number }> = [];
  const canceledRafCallbacks: FrameRequestCallback[] = [];
  readPendingRendererRafCount = () => rafCallbacks.length;
  clearPendingRendererRafs = () => { rafCallbacks.splice(0, rafCallbacks.length); };
  let nextRafId = 1;
  const requestAnimationFrameStub = (callback: FrameRequestCallback) => {
    const id = nextRafId++;
    rafCallbacks.push({ callback, id });
    return id;
  };
  const cancelAnimationFrameStub = (id: number) => {
    const index = rafCallbacks.findIndex(frame => frame.id === id);
    if (index >= 0) {
      canceledRafCallbacks.push(rafCallbacks[index]!.callback);
      rafCallbacks.splice(index, 1);
    }
  };
  vi.stubGlobal("requestAnimationFrame", requestAnimationFrameStub);
  vi.stubGlobal("cancelAnimationFrame", cancelAnimationFrameStub);
  Object.defineProperty(window, "requestAnimationFrame", {
    configurable: true,
    value: requestAnimationFrameStub,
  });
  Object.defineProperty(window, "cancelAnimationFrame", {
    configurable: true,
    value: cancelAnimationFrameStub,
  });

  const intersectionObservers: IntersectionObserverRecord[] = [];
  class TestIntersectionObserver implements IntersectionObserver {
    readonly root: Element | Document | null;
    readonly rootMargin: string;
    readonly thresholds: ReadonlyArray<number>;
    private readonly record: IntersectionObserverRecord;

    constructor(_callback: IntersectionObserverCallback, init?: IntersectionObserverInit) {
      this.root = init?.root ?? null;
      this.rootMargin = init?.rootMargin ?? "0px";
      this.thresholds = Array.isArray(init?.threshold)
        ? init.threshold
        : [init?.threshold ?? 0];
      this.record = {
        disconnected: false,
        observed: [],
        rootMargin: this.rootMargin,
        unobserved: [],
      };
      intersectionObservers.push(this.record);
    }

    disconnect(): void { this.record.disconnected = true; }
    observe(target: Element): void { this.record.observed.push(target); }
    takeRecords(): IntersectionObserverEntry[] { return []; }
    unobserve(target: Element): void { this.record.unobserved.push(target); }
  }
  vi.stubGlobal(
    "IntersectionObserver",
    options.intersectionObserverAvailable === false ? undefined : TestIntersectionObserver
  );

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

  const renderedLayout = {
    height: options.renderedSectionHeight ?? SECTION_HEIGHT,
  };
  const root = installVirtualizedDocumentLayout(
    options.sectionCount,
    renderedLayout
  );
  delete root.dataset.mmVirtualizationActive;
  const scrollWrites: number[] = [];
  let scrollEventQueued = false;
  const scrollTopDescriptor = Object.getOwnPropertyDescriptor(root, "scrollTop");
  Object.defineProperty(root, "scrollTop", {
    configurable: true,
    get: () => scrollTopDescriptor?.get?.call(root) as number,
    set: value => {
      const previous = scrollTopDescriptor?.get?.call(root) as number;
      scrollWrites.push(value);
      scrollTopDescriptor?.set?.call(root, value);
      if (value !== previous && !scrollEventQueued) {
        scrollEventQueued = true;
        queueMicrotask(() => {
          scrollEventQueued = false;
          document.dispatchEvent(new Event("scroll"));
        });
      }
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
  const realizationEventsDelivered = new WeakSet<HTMLElement>();
  const deliverMountedRealizationEvents = (): void => {
    const blocks = document.querySelectorAll<HTMLElement>("main.mm-document > [data-mm-block-index]");
    for (const block of blocks) {
      const contentVisibility = block.style.getPropertyValue("content-visibility")
        || getComputedStyle(block).getPropertyValue("content-visibility");
      if (contentVisibility.trim() !== "auto" || realizationEventsDelivered.has(block)) {
        continue;
      }
      realizationEventsDelivered.add(block);
      const event = new Event("contentvisibilityautostatechange");
      Object.defineProperty(event, "skipped", { configurable: true, value: false });
      block.dispatchEvent(event);
    }
  };
  const flushRendererMicrotasks = async (): Promise<void> => {
    for (let index = 0; index < 4; index++) {
      await Promise.resolve();
    }
  };

  const flushNextRaf = async (): Promise<void> => {
    const frame = rafCallbacks.shift();
    if (!frame) {
      throw new Error("Expected a queued requestAnimationFrame callback");
    }

    if (options.deliverRealizationEventsAfterFrame !== true) {
      deliverMountedRealizationEvents();
    }
    frame.callback(0);
    if (options.deliverRealizationEventsAfterFrame === true) {
      deliverMountedRealizationEvents();
    }
    await flushRendererMicrotasks();
  };

  const flushAnimationFrame = async (): Promise<void> => {
    const callbacks = rafCallbacks.splice(0, rafCallbacks.length);
    if (callbacks.length === 0) {
      throw new Error("Expected queued requestAnimationFrame callbacks for a frame");
    }
    if (options.deliverRealizationEventsAfterFrame !== true) {
      deliverMountedRealizationEvents();
    }
    for (const frame of callbacks) {
      frame.callback(0);
      await flushRendererMicrotasks();
    }
    if (options.deliverRealizationEventsAfterFrame === true) {
      deliverMountedRealizationEvents();
    }
    await flushRendererMicrotasks();
  };

  const flushCanceledRafs = async (): Promise<void> => {
    const callbacks = canceledRafCallbacks.splice(0, canceledRafCallbacks.length);
    for (const callback of callbacks) {
      callback(0);
      await flushRendererMicrotasks();
    }
    await flushRendererMicrotasks();
  };

  const flushQueuedRafs = async (): Promise<void> => {
    for (let i = 0; i < 160; i++) {
      await flushRendererMicrotasks();
      if (rafCallbacks.length === 0) {
        break;
      }
      const callbacks = rafCallbacks.splice(0, rafCallbacks.length);
      if (options.deliverRealizationEventsAfterFrame !== true) {
        deliverMountedRealizationEvents();
      }
      for (const frame of callbacks) {
        frame.callback(i * 16);
        await flushRendererMicrotasks();
      }
      if (options.deliverRealizationEventsAfterFrame === true) {
        deliverMountedRealizationEvents();
      }
    }
    if (rafCallbacks.length > 0) {
      throw new Error("requestAnimationFrame queue did not settle");
    }
    await flushRendererMicrotasks();
  };

  const flushRafsUntil = async (predicate: () => boolean, maxFrames = 40): Promise<void> => {
    for (let i = 0; i < maxFrames && !predicate() && rafCallbacks.length > 0; i++) {
      const callbacks = rafCallbacks.splice(0, rafCallbacks.length);
      if (options.deliverRealizationEventsAfterFrame !== true) {
        deliverMountedRealizationEvents();
      }
      for (const frame of callbacks) {
        frame.callback(i * 16);
        await flushRendererMicrotasks();
      }
      if (options.deliverRealizationEventsAfterFrame === true) {
        deliverMountedRealizationEvents();
      }
    }
    await flushRendererMicrotasks();
  };

  const triggerResize = (): void => {
    for (const observer of resizeObservers) {
      observer.trigger();
    }
  };

  const flushResizeRafsSynchronously = (): void => {
    const existingIds = new Set(rafCallbacks.map(frame => frame.id));
    triggerResize();
    const callbacks = rafCallbacks.filter(frame => !existingIds.has(frame.id));
    for (const frame of callbacks) {
      const index = rafCallbacks.findIndex(candidate => candidate.id === frame.id);
      if (index >= 0) {
        rafCallbacks.splice(index, 1);
      }
      frame.callback(0);
    }
  };

  return {
    flushAnimationFrame,
    flushCanceledRafs,
    flushNextRaf,
    flushQueuedRafs,
    flushRafsUntil,
    flushResizeRafsSynchronously,
    highlights,
    intersectionObservers,
    load,
    messages,
    pendingRafCount: () => rafCallbacks.length,
    root,
    scrollCalls,
    scrollWrites,
    setRenderedSectionHeight: height => { renderedLayout.height = height; },
    triggerResize,
  };
}

function installVirtualizedDocumentLayout(
  sectionCount: number,
  renderedLayout: { height: number }
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

    return this.hasAttribute("data-mm-block-index") ? renderedLayout.height : 0;
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

function buildLazyMermaidDocument(count: number, mermaidIndex: number): string {
  return Array.from({ length: count }, (_, index) => index === mermaidIndex
    ? `<pre class="mm-mermaid" data-mm-block-index="${index}" data-mm-block-kind="mermaid"><code data-mm-mermaid>graph TD; A--&gt;B</code></pre>`
    : `<p data-mm-block-index="${index}" data-mm-block-kind="paragraph">Block ${index}</p>`
  ).join("");
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

function buildInterpolatedSourceLineDocument(count: number, ownerIndex: number): string {
  return Array.from({ length: count }, (_, index) => {
    if (index === ownerIndex) {
      return [
        `<section data-mm-block-index="${index}" data-mm-block-kind="quote">`,
        `<p data-mm-block-index="${ownerIndex * 100 + 1}" data-mm-source-line="${ownerIndex * 10}" data-mm-source-end-line="${ownerIndex * 10 + 10}">Interpolation start ${index}</p>`,
        `<p data-mm-block-index="${ownerIndex * 100 + 2}" data-mm-source-line="${ownerIndex * 10 + 10}" data-mm-source-end-line="${ownerIndex * 10 + 10}">Interpolation end ${index}</p>`,
        `</section>`,
      ].join("");
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

function buildNestedFindDocument(count: number, ownerIndex: number, nestedIndex: number): string {
  return Array.from({ length: count }, (_, index) => {
    if (index === ownerIndex) {
      return [
        `<section data-mm-block-index="${index}" data-mm-block-kind="quote">`,
        `<blockquote data-mm-block-index="${nestedIndex}">Nested needle ${nestedIndex}</blockquote>`,
        `</section>`,
      ].join("");
    }

    return `<p data-mm-block-index="${index}" data-mm-block-kind="paragraph">Block ${index} filler</p>`;
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

function buildModelRenderedMathDocument(count: number, mathIndexes: readonly number[]): string {
  const math = new Set(mathIndexes);
  return Array.from({ length: count }, (_, index) => {
    const formula = math.has(index)
      ? ` <span class="math-inline" data-tex="x_${index}">x_${index}</span>`
      : "";
    return [
      `<section data-mm-block-index="${index}" data-mm-block-kind="paragraph">`,
      `<p>Block ${index} needle${formula}</p>`,
      `</section>`,
    ].join("");
  }).join("");
}

function buildCloneHtmlIdentityDocument(count: number): string {
  return Array.from({ length: count }, (_, index) => [
    `<section id="html-section-${index}" data-mm-block-index="${index}" data-mm-block-kind="paragraph">`,
    `<p id="html-paragraph-${index}">Block ${index} with SVG paint identity</p>`,
    `</section>`,
  ].join("")).join("");
}

function buildSvgPaintIdentityAppendBlock(blockIndex: number): string {
  return [
    `<section id="html-svg-section" data-mm-block-index="${blockIndex}" data-mm-block-kind="paragraph">`,
    `<p id="html-svg-paragraph">Block with SVG paint identity</p>`,
    `<svg id="svg-root" viewBox="0 0 10 10" role="img">`,
    `<defs><linearGradient id="paint-gradient"><stop offset="100%" stop-color="red"></stop></linearGradient></defs>`,
    `<rect id="paint-rect" width="10" height="10" fill="url(#paint-gradient)"></rect>`,
    `</svg>`,
    `</section>`,
  ].join("");
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

function makeRendererKatex(): TestKatexApi {
  return {
    render: vi.fn((tex: string, node: Element) => {
      const element = node as HTMLElement;
      const rendered = element.ownerDocument.createElement("span");
      rendered.className = "katex";
      rendered.textContent = `rendered:${tex}`;
      element.replaceChildren(rendered);
    }),
  };
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

function perfDetails<T extends Record<string, unknown>>(
  messages: readonly unknown[],
  name: string
): T[] {
  return messages
    .filter((message): message is { type: "perf-mark"; name: string; detail?: string } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "perf-mark"
      && (message as { name?: unknown }).name === name)
    .flatMap(message => message.detail ? [JSON.parse(message.detail) as T] : []);
}

function expectCommittedWriterOwned(
  messages: readonly unknown[],
  writer: string,
  owner: string
): void {
  const commit = perfDetails<{ operationEpoch: number; writer?: string }>(
    messages,
    "mm-virt-scroll-write-committed"
  ).filter(detail => detail.writer === writer).at(-1);
  expect(commit, `missing committed writer ${writer}`).toBeDefined();
  expect(perfDetails<{ operationEpoch: number; owner?: string }>(
    messages,
    "mm-virt-scroll-lease-acquired"
  )).toContainEqual(expect.objectContaining({
    operationEpoch: commit!.operationEpoch,
    owner,
  }));
}

function expectCommittedWriterFamilyOwned(
  messages: readonly unknown[],
  writerPrefix: string,
  owner: string
): void {
  const commit = perfDetails<{ operationEpoch: number; writer?: string }>(
    messages,
    "mm-virt-scroll-write-committed"
  ).filter(detail => detail.writer?.startsWith(writerPrefix) === true).at(-1);
  expect(commit, `missing committed writer family ${writerPrefix}`).toBeDefined();
  expect(perfDetails<{ operationEpoch: number; owner?: string }>(
    messages,
    "mm-virt-scroll-lease-acquired"
  )).toContainEqual(expect.objectContaining({
    operationEpoch: commit!.operationEpoch,
    owner,
  }));
}

function highestRenderedHeadingIndex(): number {
  return [...document.querySelectorAll<HTMLElement>("body > main.mm-document [id^='heading-']")]
    .map(element => Number.parseInt(element.id.replace("heading-", ""), 10))
    .filter(Number.isFinite)
    .reduce((max, index) => Math.max(max, index), -1);
}

function activeHeadingObserverRecords(harness: RendererHarness): IntersectionObserverRecord[] {
  return harness.intersectionObservers.filter(record => record.rootMargin === "0px 0px -85% 0px");
}

function activeHeadingChangedIds(messages: readonly unknown[]): string[] {
  return messages
    .filter((message): message is { id: string; type: "active-heading-changed" } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "active-heading-changed"
      && typeof (message as { id?: unknown }).id === "string")
    .map(message => message.id);
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
  textDomain: "rendered-dom-v1";
};

type FindMatchDescriptor = {
  matchId: string;
  blockIndex: number;
  startBlockIndex?: number;
  endBlockIndex?: number;
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

function descriptorForNestedNeedle(ownerIndex: number, nestedIndex: number, ordinal: number): FindMatchDescriptor {
  return {
    blockIndex: nestedIndex,
    blockLocalOffset: "Nested ".length,
    endBlockIndex: ownerIndex,
    length: "needle".length,
    matchId: `nested-${nestedIndex}-needle-${ordinal}`,
    normalizedText: "needle",
    ordinal,
    startBlockIndex: ownerIndex,
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

function isHtmlNamespaceElement(element: Element): boolean {
  return element.namespaceURI === "http://www.w3.org/1999/xhtml" || element.namespaceURI === null;
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
  const htmlIds = Array.from(minimapContent.querySelectorAll<Element>("[id]"))
    .filter(isHtmlNamespaceElement)
    .map(element => element.id);
  expect(htmlIds).toEqual([]);
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

async function settleFakeTimedRenderer(harness: RendererHarness): Promise<void> {
  for (let pass = 0; pass < 6; pass++) {
    await harness.flushQueuedRafs();
    await vi.advanceTimersByTimeAsync(1_000);
    await Promise.resolve();
  }
  await harness.flushQueuedRafs();
}

function terminalCacheRestoreDetails(messages: readonly unknown[]): Array<{
  reason?: string;
  status?: string;
}> {
  return perfDetails<{ reason?: string; status?: string }>(messages, "mm-virt-cache-restore-terminal");
}

function maintenanceTerminalDetails(messages: readonly unknown[]): Array<{
  owner?: string;
  reason?: string;
  status?: string;
}> {
  return perfDetails<{
    owner?: string;
    reason?: string;
    status?: string;
  }>(messages, "mm-virt-maintenance-terminal");
}

const maintenanceLifecycleEventNames = [
  "mm-virt-maintenance-requested",
  "mm-virt-maintenance-coalesced",
  "mm-virt-maintenance-bound",
  "mm-virt-maintenance-retry",
  "mm-virt-maintenance-terminal",
] as const;

type MaintenanceLifecycleEventName = typeof maintenanceLifecycleEventNames[number];

type MaintenanceLifecycleDetail = {
  [key: string]: unknown;
  documentEpoch?: number;
  executionCount?: number;
  operationEpoch?: number;
  owner?: string;
  ownsLease?: boolean;
  reason?: string;
  requestId?: {
    documentEpoch?: number;
    requestSerial?: number;
  };
  requestSerial?: number;
  status?: string;
  workRevision?: number;
};

type MaintenanceLifecycleEvent = {
  detail: MaintenanceLifecycleDetail;
  name: MaintenanceLifecycleEventName;
};

function maintenanceLifecycleEvents(messages: readonly unknown[]): MaintenanceLifecycleEvent[] {
  return messages.flatMap(message => {
    if (
      typeof message !== "object"
      || message === null
      || (message as { type?: unknown }).type !== "perf-mark"
    ) {
      return [];
    }
    const mark = message as { detail?: string; name?: string };
    if (!maintenanceLifecycleEventNames.includes(mark.name as MaintenanceLifecycleEventName)) {
      return [];
    }
    return [{
      detail: mark.detail ? JSON.parse(mark.detail) as MaintenanceLifecycleDetail : {},
      name: mark.name as MaintenanceLifecycleEventName,
    }];
  });
}

function perfMarkMessageIndex(
  messages: readonly unknown[],
  name: string,
  predicate: (detail: Record<string, unknown>) => boolean = () => true,
  startIndex = 0
): number {
  const relativeIndex = messages.slice(startIndex).findIndex(message => {
    if (
      typeof message !== "object"
      || message === null
      || (message as { type?: unknown }).type !== "perf-mark"
      || (message as { name?: unknown }).name !== name
    ) {
      return false;
    }
    const detail = (message as { detail?: string }).detail;
    return predicate(detail ? JSON.parse(detail) as Record<string, unknown> : {});
  });
  return relativeIndex < 0 ? -1 : startIndex + relativeIndex;
}

function maintenanceRequestKey(detail: MaintenanceLifecycleDetail): string {
  return `${detail.documentEpoch}:${detail.requestSerial}`;
}

function maintenanceRequestsForOwner(
  messages: readonly unknown[],
  owner: string
): MaintenanceLifecycleDetail[] {
  return maintenanceLifecycleEvents(messages)
    .filter(event => event.name === "mm-virt-maintenance-requested" && event.detail.owner === owner)
    .map(event => event.detail);
}

function maintenanceEventsForRequest(
  messages: readonly unknown[],
  request: MaintenanceLifecycleDetail
): MaintenanceLifecycleEvent[] {
  const key = maintenanceRequestKey(request);
  return maintenanceLifecycleEvents(messages)
    .filter(event => maintenanceRequestKey(event.detail) === key);
}

function expectExactMaintenanceLifecycle(
  messages: readonly unknown[],
  request: MaintenanceLifecycleDetail,
  terminal: {
    executionCount: 0 | 1;
    reason: string;
    status: "canceled" | "completed" | "failed";
  }
): void {
  expect(request.requestId).toEqual({
    documentEpoch: request.documentEpoch,
    requestSerial: request.requestSerial,
  });
  const events = maintenanceEventsForRequest(messages, request);
  expect(events.filter(event => event.name === "mm-virt-maintenance-requested")).toHaveLength(1);
  expect(events.filter(event => event.name === "mm-virt-maintenance-bound").length).toBeLessThanOrEqual(1);
  const coalescedRevisions = events
    .filter(event => event.name === "mm-virt-maintenance-coalesced")
    .map(event => event.detail.workRevision);
  for (let index = 1; index < coalescedRevisions.length; index++) {
    expect(coalescedRevisions[index]).toBeGreaterThan(coalescedRevisions[index - 1]!);
  }
  expect(events.filter(event => event.name === "mm-virt-maintenance-terminal")).toEqual([
    expect.objectContaining({
      detail: expect.objectContaining(terminal),
      name: "mm-virt-maintenance-terminal",
    }),
  ]);
  expect(events.at(0)?.name).toBe("mm-virt-maintenance-requested");
  expect(events.at(-1)?.name).toBe("mm-virt-maintenance-terminal");
}

async function loadMaintenanceLifecycleHarness(
  controllerFaults: ControllerFaults = {}
): Promise<RendererHarness> {
  const harness = await loadRendererHarness({
    controllerFaults,
    sectionCount: 120,
    virtualization: true,
  });
  document.dispatchEvent(new Event("DOMContentLoaded"));
  document.documentElement.style.setProperty("--mm-minimap-width", "136px");
  setMinimapViewportHeight(592);
  harness.load({ type: "reading-preferences", ...makeReadingPreferences("on") });
  loadMinimapPolicy(harness.load);
  harness.load({
    type: "load-document",
    html: buildHeadingDocument(120),
    hasMermaid: false,
    hasHljs: false,
    renderId: 60,
  });
  await harness.flushQueuedRafs();
  harness.messages.length = 0;
  return harness;
}

function beginMinimapMaintenanceLease(harness: RendererHarness): HTMLElement {
  const minimap = document.querySelector<HTMLElement>(".mm-minimap")!;
  Object.defineProperty(minimap, "setPointerCapture", { configurable: true, value: vi.fn() });
  Object.defineProperty(minimap, "releasePointerCapture", { configurable: true, value: vi.fn() });
  vi.spyOn(minimap, "getBoundingClientRect").mockReturnValue({
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
  minimap.dispatchEvent(pointerEvent("pointerdown", 12));
  expect(harness.messages).toBeDefined();
  return minimap;
}

async function flushNextRafsUntil(
  harness: RendererHarness,
  predicate: () => boolean,
  maxCallbacks = 80
): Promise<void> {
  for (let callback = 0; callback < maxCallbacks && !predicate(); callback++) {
    if ((readPendingRendererRafCount?.() ?? 0) === 0) {
      break;
    }
    await harness.flushNextRaf();
  }
  expect(predicate()).toBe(true);
}

type MaintenanceTestState = "joined" | "owned" | "retry-pending";

async function startMeasuredMaintenanceInState(
  harness: RendererHarness,
  state: MaintenanceTestState,
  height = SECTION_HEIGHT + 40
): Promise<MaintenanceLifecycleDetail> {
  if (state === "joined") {
    beginMinimapMaintenanceLease(harness);
    harness.setRenderedSectionHeight(height);
    harness.triggerResize();
    await flushNextRafsUntil(harness, () => perfDetails<MaintenanceLifecycleDetail>(
      harness.messages,
      "mm-virt-maintenance-bound"
    ).some(detail => detail.owner === "measured-height-adoption" && detail.ownsLease === false));
  } else if (state === "owned") {
    harness.setRenderedSectionHeight(height);
    harness.triggerResize();
    await flushNextRafsUntil(harness, () => perfDetails<MaintenanceLifecycleDetail>(
      harness.messages,
      "mm-virt-maintenance-bound"
    ).some(detail => detail.owner === "measured-height-adoption" && detail.ownsLease === true));
  } else {
    harness.setRenderedSectionHeight(height);
    harness.triggerResize();
    await harness.flushNextRaf();
    const minimap = beginMinimapMaintenanceLease(harness);
    minimap.dispatchEvent(pointerEvent("pointermove", 588));
    await flushNextRafsUntil(harness, () => perfDetails<MaintenanceLifecycleDetail>(
      harness.messages,
      "mm-virt-maintenance-retry"
    ).some(detail => detail.owner === "measured-height-adoption"));
  }
  const requests = maintenanceRequestsForOwner(harness.messages, "measured-height-adoption");
  expect(requests).toHaveLength(1);
  return requests[0]!;
}

afterEach(() => {
  window.dispatchEvent(new Event("pagehide"));
  clearPendingRendererRafs?.();
  expect(readPendingRendererRafCount?.() ?? 0).toBe(0);
  clearPendingRendererRafs = null;
  readPendingRendererRafCount = null;
  removeRendererEventListeners();
  vi.useRealTimers();
  vi.doUnmock("../src/virtualizedDocumentWindow");
  vi.doUnmock("../src/mathRenderInit");
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  window.history.replaceState(null, "", window.location.pathname);
  delete (window as unknown as { MARKMELLO_VIRTUALIZATION?: boolean }).MARKMELLO_VIRTUALIZATION;
  delete (document as unknown as { fonts?: unknown }).fonts;
  delete (window as unknown as { chrome?: unknown }).chrome;
  delete (window as unknown as { katex?: unknown }).katex;
  delete (window as unknown as { mermaid?: unknown }).mermaid;
});

describe("renderer scroll-family virtualization integration", () => {
  it("emits geometry settled after a non-convergent realization watch is quarantined", async () => {
    const sectionCount = 120;
    const harness = await loadRendererHarness({
      deliverRealizationEventsAfterFrame: true,
      sectionCount,
      virtualization: true,
    });
    harness.load({
      type: "load-document",
      html: Array.from({ length: sectionCount }, (_, index) =>
        `<h2 id="heading-${index}" data-mm-block-index="${index}" data-mm-block-kind="heading" style="content-visibility:auto">Heading ${index}</h2>`
      ).join(""),
      hasMermaid: false,
      hasHljs: false,
      renderId: 91,
    });

    for (let frame = 0; frame < 400; frame++) {
      if (perfMarkMessageIndex(harness.messages, "mm-virt-realization-quarantined") >= 0) {
        break;
      }
      if (harness.pendingRafCount() === 0) {
        harness.load({ type: "scroll-to-block", blockIndex: 0 });
      }
      expect(harness.pendingRafCount()).toBeGreaterThan(0);
      harness.setRenderedSectionHeight(SECTION_HEIGHT + frame * 3);
      await harness.flushAnimationFrame();
    }

    const expiredCycles = perfDetails<{ blockIndex?: number; cycles?: number }>(
      harness.messages,
      "mm-virt-realization-expired"
    ).filter(detail => detail.blockIndex === 0).map(detail => detail.cycles);
    expect(expiredCycles).toEqual([1, 2, 3]);
    const quarantineIndex = perfMarkMessageIndex(
      harness.messages,
      "mm-virt-realization-quarantined"
    );
    expect(quarantineIndex).toBeGreaterThanOrEqual(0);

    for (let frame = 0; frame < 40; frame++) {
      if (perfMarkMessageIndex(
        harness.messages,
        "mm-virt-geometry-settled",
        () => true,
        quarantineIndex + 1
      ) >= 0) {
        break;
      }
      if (harness.pendingRafCount() === 0) {
        harness.load({ type: "scroll-to-block", blockIndex: 0 });
      }
      expect(harness.pendingRafCount()).toBeGreaterThan(0);
      await harness.flushAnimationFrame();
    }

    expect(perfMarkMessageIndex(
      harness.messages,
      "mm-virt-geometry-settled",
      () => true,
      quarantineIndex + 1
    )).toBeGreaterThan(quarantineIndex);
  });

  it("keeps progressive enhancement scheduling fire-and-forget when virtualization is off", async () => {
    let nextIdleId = 1;
    const requestIdleCallback = vi.fn(() => nextIdleId++);
    const cancelIdleCallback = vi.fn();
    vi.stubGlobal("requestIdleCallback", requestIdleCallback);
    vi.stubGlobal("cancelIdleCallback", cancelIdleCallback);
    const { load } = await loadRendererHarness({ sectionCount: 2, virtualization: false });
    requestIdleCallback.mockClear();
    cancelIdleCallback.mockClear();

    load({ type: "append-document", html: "<p>first</p>", renderId: 1, hasMermaid: false, hasHljs: false });
    const firstProgressiveHandle = requestIdleCallback.mock.results.at(-1)?.value;
    load({ type: "append-document", html: "<p>second</p>", renderId: 1, hasMermaid: false, hasHljs: false });

    expect(requestIdleCallback).toHaveBeenCalled();
    expect(cancelIdleCallback).not.toHaveBeenCalledWith(firstProgressiveHandle);
  });

  it("cancels superseded progressive enhancement work when virtualization is on", async () => {
    let nextIdleId = 1;
    const requestIdleCallback = vi.fn(() => nextIdleId++);
    const cancelIdleCallback = vi.fn();
    vi.stubGlobal("requestIdleCallback", requestIdleCallback);
    vi.stubGlobal("cancelIdleCallback", cancelIdleCallback);
    const { load } = await loadRendererHarness({ sectionCount: 2, virtualization: true });
    requestIdleCallback.mockClear();
    cancelIdleCallback.mockClear();

    load({ type: "append-document", html: buildHeadingDocument(1), renderId: 1, hasMermaid: false, hasHljs: false });
    const firstProgressiveHandle = requestIdleCallback.mock.results.at(-1)?.value;
    load({ type: "append-document", html: buildHeadingDocument(1), renderId: 1, hasMermaid: false, hasHljs: false });

    expect(cancelIdleCallback).toHaveBeenCalledWith(firstProgressiveHandle);
  });

  it("keeps initial virtual-window DOM and root state unchanged until the owning frame", async () => {
    const sectionCount = 120;
    const { flushAnimationFrame, load, root, scrollWrites } = await loadRendererHarness({
      sectionCount,
      virtualization: true,
    });

    load({ type: "load-document", html: buildHeadingDocument(sectionCount), hasMermaid: false, hasHljs: false });

    const main = document.querySelector<HTMLElement>("main.mm-document")!;
    expect(main.querySelectorAll("[data-mm-block-index]")).toHaveLength(sectionCount);
    expect(scrollWrites).toEqual([]);

    await flushAnimationFrame();

    expect(main.querySelectorAll("[data-mm-block-index]").length).toBeLessThan(sectionCount);
    expect(scrollWrites).toHaveLength(1);
    expect(root.scrollTop).toBe(scrollWrites[0]);
  });

  it.each([
    { ensureThrows: false, expectedStatus: "committed" },
    { ensureThrows: true, expectedStatus: "failed" },
  ])("terminates cache restoration after frame work with status $expectedStatus", async ({
    ensureThrows,
    expectedStatus,
  }) => {
    vi.useFakeTimers();
    const controllerFaults: ControllerFaults = {};
    const harness = await loadRendererHarness({
      controllerFaults,
      sectionCount: 120,
      virtualization: true,
    });
    const firstHtml = buildHeadingDocument(120);
    const secondHtml = buildHeadingDocument(8);

    harness.load({ type: "load-document", html: firstHtml, theme: "light", hasMermaid: false, hasHljs: false, renderId: 1 });
    await settleFakeTimedRenderer(harness);
    harness.load({ type: "load-document", html: secondHtml, theme: "light", hasMermaid: false, hasHljs: false, renderId: 2 });
    await settleFakeTimedRenderer(harness);

    harness.messages.length = 0;
    controllerFaults.ensureSectionRendered = ensureThrows;
    harness.load({ type: "load-document", html: firstHtml, theme: "light", hasMermaid: false, hasHljs: false, renderId: 3 });
    await settleFakeTimedRenderer(harness);

    expect(latestPerfDetail(harness.messages, "mm-load-document-cache-hit")).toBeDefined();
    expect(terminalCacheRestoreDetails(harness.messages)).toEqual([
      expect.objectContaining({ status: expectedStatus }),
    ]);
    expect(harness.messages).toContainEqual(expect.objectContaining({
      type: "post-ready-enhancements-complete",
      renderId: 3,
    }));
  });

  it("terminates a stale cache restore once without publishing the stale document", async () => {
    vi.useFakeTimers();
    const harness = await loadRendererHarness({ sectionCount: 120, virtualization: true });
    const firstHtml = buildHeadingDocument(120);
    const secondHtml = buildHeadingDocument(8);
    const successorHtml = buildHeadingDocument(12);

    harness.load({ type: "load-document", html: firstHtml, theme: "light", hasMermaid: false, hasHljs: false, renderId: 1 });
    await settleFakeTimedRenderer(harness);
    harness.load({ type: "load-document", html: secondHtml, theme: "light", hasMermaid: false, hasHljs: false, renderId: 2 });
    await settleFakeTimedRenderer(harness);

    harness.messages.length = 0;
    harness.load({ type: "load-document", html: firstHtml, theme: "light", hasMermaid: false, hasHljs: false, renderId: 3 });
    harness.load({ type: "load-document", html: successorHtml, theme: "light", hasMermaid: false, hasHljs: false, renderId: 4 });
    await settleFakeTimedRenderer(harness);

    expect(terminalCacheRestoreDetails(harness.messages)).toEqual([
      expect.objectContaining({ reason: "stale-document", status: "canceled" }),
    ]);
    expect(harness.messages).not.toContainEqual(expect.objectContaining({
      type: "post-ready-enhancements-complete",
      renderId: 3,
    }));
    expect(harness.messages).toContainEqual(expect.objectContaining({
      type: "post-ready-enhancements-complete",
      renderId: 4,
    }));
  });

  it("posts all model headings to the TOC when virtualization has rendered only the first window", async () => {
    const headingCount = 1_005;
    const { flushQueuedRafs, load, messages } = await loadRendererHarness({
      sectionCount: headingCount,
      virtualization: true,
    });

    load({ type: "load-document", html: buildHeadingDocument(headingCount), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();

    const headings = latestHeadingsUpdated(messages)?.headings ?? [];
    expect(document.getElementById("heading-999")).toBeNull();
    expect(headings).toHaveLength(headingCount);
    expect(headings.at(-1)).toMatchObject({ id: "heading-1004" });
  });

  it("re-arms the active-heading observer with live headings after virtual window replacement", async () => {
    const sectionCount = 120;
    const harness = await loadRendererHarness({
      sectionCount,
      virtualization: true,
    });

    harness.load({ type: "load-document", html: buildHeadingDocument(sectionCount), hasMermaid: false, hasHljs: false });
    await harness.flushQueuedRafs();

    const initialObserver = activeHeadingObserverRecords(harness).at(-1);
    expect(initialObserver).toBeDefined();

    harness.root.scrollTop = 90 * SECTION_PITCH;
    document.dispatchEvent(new Event("scroll"));
    await harness.flushRafsUntil(() => document.getElementById("heading-90") !== null);
    await harness.flushQueuedRafs();

    const liveHeadings = Array.from(
      document.querySelectorAll<HTMLHeadingElement>("body > main.mm-document [id^='heading-']")
    );
    const latestObserver = activeHeadingObserverRecords(harness).at(-1);
    expect(liveHeadings.length).toBeGreaterThan(0);
    expect(latestObserver).toBeDefined();
    expect(latestObserver).not.toBe(initialObserver);
    expect(initialObserver!.disconnected).toBe(true);
    expect(latestObserver!.observed).toEqual(liveHeadings);
    expect(latestObserver!.observed.every(node => node.isConnected)).toBe(true);
  });

  it("does not post an active heading from a superseded virtual window", async () => {
    let controller: import("../src/virtualizedDocumentWindow").VirtualizedDocumentWindowController | null = null;
    const harness = await loadRendererHarness({
      controllerFaults: {
        onCreated: created => { controller = created; },
      },
      sectionCount: 120,
      virtualization: true,
    });

    harness.load({ type: "load-document", html: buildHeadingDocument(120), hasMermaid: false, hasHljs: false });
    expect(controller).not.toBeNull();
    controller!.ensureSectionRendered(90, { force: true, preserveAnchor: false });
    controller!.ensureSectionRendered(0, { force: true, preserveAnchor: false });

    const liveHeadingIds = new Set(
      Array.from(document.querySelectorAll<HTMLHeadingElement>("body > main.mm-document [id^='heading-']"))
        .map(node => node.id)
    );
    expect(liveHeadingIds.has("heading-0")).toBe(true);
    expect(liveHeadingIds.has("heading-90")).toBe(false);
    harness.messages.length = 0;

    await harness.flushQueuedRafs();

    const postedIds = activeHeadingChangedIds(harness.messages);
    expect(postedIds.length).toBeGreaterThan(0);
    expect(postedIds.every(id => liveHeadingIds.has(id))).toBe(true);
  });

  it("keeps a non-smooth off-window anchor landing instant", async () => {
    const { flushNextRaf, flushQueuedRafs, load, messages, root, scrollCalls, scrollWrites } = await loadRendererHarness({
      sectionCount: 120,
      virtualization: true,
    });
    load({ type: "load-document", html: buildHeadingDocument(120), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();
    scrollCalls.length = 0;
    scrollWrites.length = 0;

    expect(document.getElementById("heading-90")).toBeNull();
    load({ type: "scroll-to", anchor: "heading-90" });
    await flushNextRaf();

    expect(document.getElementById("heading-90")).not.toBeNull();
    expect(scrollCalls).toEqual([]);
    expect(root.scrollTop).toBeGreaterThan(0);
    expect(scrollWrites.filter(value => value > 0)).toHaveLength(1);
    expectCommittedWriterOwned(messages, "navigation-initial", "heading-navigation");
  });

  it("smoothly lands an off-window TOC target through owned intermediate frames", async () => {
    const { flushNextRaf, flushQueuedRafs, load, messages, root, scrollCalls, scrollWrites } = await loadRendererHarness({
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
    await flushQueuedRafs();

    const positiveWrites = scrollWrites.filter(value => value > 0);
    expect(positiveWrites.length).toBeGreaterThan(2);
    expect(positiveWrites.slice(1).every((value, index) => value > positiveWrites[index]!)).toBe(true);
    expect(positiveWrites.slice(0, -1).every(value => value < root.scrollTop)).toBe(true);
    expect(root.scrollTop).toBeCloseTo(positiveWrites.at(-1)!, 5);
    expect(document.getElementById("heading-95")!.getBoundingClientRect().top).toBeCloseTo(0, 0);
    expectCommittedWriterOwned(messages, "navigation-smooth", "heading-navigation");
    expect(perfDetails(messages, "mm-virt-navigation-settled")).toHaveLength(1);
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

  it("stops a smooth TOC transition after genuine user supersession", async () => {
    const { flushNextRaf, flushQueuedRafs, load, messages, root, scrollCalls, scrollWrites } = await loadRendererHarness({
      sectionCount: 120,
      virtualization: true,
    });
    load({ type: "load-document", html: buildHeadingDocument(120), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();
    scrollCalls.length = 0;
    scrollWrites.length = 0;

    load({ type: "scroll-to-heading", id: "heading-95" });
    await flushNextRaf();
    await flushNextRaf();

    expect(document.getElementById("heading-95")).not.toBeNull();
    expect(scrollWrites.filter(value => value > 0)).toHaveLength(1);
    expect(scrollCalls).toEqual([]);

    root.scrollTop += 333;
    document.dispatchEvent(new Event("scroll"));
    await Promise.resolve();
    const smoothWritesAfterUserTakeover = perfDetails<{ writer?: string }>(
      messages,
      "mm-virt-scroll-write-committed"
    ).filter(detail => detail.writer === "navigation-smooth").length;
    await flushQueuedRafs();

    expect(perfDetails<{ writer?: string }>(messages, "mm-virt-scroll-write-committed")
      .filter(detail => detail.writer === "navigation-smooth"))
      .toHaveLength(smoothWritesAfterUserTakeover);
    expect(perfDetails(messages, "mm-virt-navigation-settled")).toHaveLength(0);
    expect(perfDetails<{ owner?: string; supersessionSource?: string }>(
      messages,
      "mm-virt-scroll-lease-superseded"
    )).toContainEqual(expect.objectContaining({
      owner: "heading-navigation",
      supersessionSource: "native-scroll",
    }));
  });

  it("renders the containing section and scrolls the nested block descendant", async () => {
    const { flushNextRaf, flushQueuedRafs, load, messages, root, scrollCalls } = await loadRendererHarness({
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
    expectCommittedWriterOwned(messages, "navigation-initial", "block-navigation");
  });

  it("lets native user scroll supersede a nested programmatic block target", async () => {
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
    expect(Math.abs(await land(true))).toBeGreaterThan(100);
  });

  it("corrects a deep block landing after estimated and rendered heights diverge", async () => {
    const { flushQueuedRafs, load, messages } = await loadRendererHarness({
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
    const blockOperation = perfDetails<{ operationEpoch: number; owner?: string }>(
      messages,
      "mm-virt-scroll-lease-acquired"
    ).find(detail => detail.owner === "block-navigation");
    expect(blockOperation).toBeDefined();
    expect(perfDetails<{ operationEpoch: number; writer?: string }>(
      messages,
      "mm-virt-scroll-write-committed"
    ).filter(detail => detail.operationEpoch === blockOperation!.operationEpoch).map(detail => detail.writer))
      .toEqual(expect.arrayContaining(["navigation-initial", "navigation-residual"]));
    const committedFrames = perfDetails<{ frame: number }>(messages, "mm-virt-scroll-write-committed")
      .map(detail => detail.frame);
    expect(new Set(committedFrames).size).toBe(committedFrames.length);
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
    const { flushQueuedRafs, load, root, scrollCalls } = await loadRendererHarness({
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

  it("marks only the actual flag-on scroll root as virtualization active", async () => {
    const on = await loadRendererHarness({ sectionCount: 4, virtualization: true });

    expect(on.root.dataset.mmVirtualizationActive).toBe("true");
  });

  it("routes the flag-on model-less block fallback without scrollIntoView", async () => {
    const { flushQueuedRafs, load, root, scrollCalls, scrollWrites } = await loadRendererHarness({
      sectionCount: 8,
      virtualization: true,
    });
    const main = document.querySelector<HTMLElement>("main.mm-document")!;
    const target = document.createElement("section");
    target.dataset.mmBlockIndex = "7";
    target.textContent = "Live fallback";
    main.append(target);
    scrollWrites.length = 0;

    load({ type: "scroll-to-block", blockIndex: 7 });
    await flushQueuedRafs();

    expect(scrollCalls).toEqual([]);
    expect(scrollWrites).toHaveLength(1);
    expect(root.scrollTop).toBeGreaterThan(0);
  });

  it("routes the flag-on resolver-null block fallback through the existing operation", async () => {
    const { flushQueuedRafs, load, root, scrollCalls, scrollWrites } = await loadRendererHarness({
      sectionCount: 8,
      virtualization: true,
    });
    load({ type: "load-document", html: buildHeadingDocument(8), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();
    const main = document.querySelector<HTMLElement>("main.mm-document")!;
    const target = document.createElement("section");
    target.dataset.mmBlockIndex = "900";
    target.textContent = "Unmodeled live fallback";
    main.append(target);
    scrollWrites.length = 0;

    load({ type: "scroll-to-block", blockIndex: 900 });
    await flushQueuedRafs();

    expect(scrollCalls).toEqual([]);
    expect(scrollWrites).toHaveLength(1);
    expect(root.scrollTop).toBeGreaterThan(0);
  });

  it("keeps flag-off scroll messages on the existing live-DOM paths", async () => {
    const { flushQueuedRafs, load, root, scrollCalls } = await loadRendererHarness({
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
    expect(root.dataset.mmVirtualizationActive).toBeUndefined();
  });

  it("routes cold policy and host scroll commands through traced lease owners", async () => {
    const { flushQueuedRafs, load, messages, root } = await loadRendererHarness({
      sectionCount: 120,
      virtualization: true,
    });
    load({ type: "load-document", html: buildHeadingDocument(120), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();

    expectCommittedWriterOwned(messages, "cold-load-reset", "cold-load-reset");
    expectCommittedWriterOwned(messages, "measured-height-adoption", "measured-height-adoption");

    messages.length = 0;
    load({ type: "scroll-to-progress", progressPercent: 50 });
    await flushQueuedRafs();
    expect(root.scrollTop).toBeGreaterThan(0);
    expectCommittedWriterOwned(messages, "host-progress", "host-progress");

    messages.length = 0;
    const beforeScrollBy = root.scrollTop;
    load({ type: "scroll-by", deltaY: 45 });
    await flushQueuedRafs();
    expect(root.scrollTop).toBeGreaterThan(beforeScrollBy);
    expectCommittedWriterOwned(messages, "host-scroll-by", "host-scroll-by");

    messages.length = 0;
    load({
      ...makeReadingPreferences("off"),
      documentScrollEnabled: false,
    });
    await flushQueuedRafs();
    expect(root.scrollTop).toBe(0);
    expectCommittedWriterOwned(messages, "scroll-disabled-reset", "scroll-disabled-reset");
  });

  it("renders an off-window source line before applying the 38 percent preview anchor", async () => {
    const land = async (): Promise<number> => {
      const { flushQueuedRafs, load, messages } = await loadRendererHarness({
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
      expectCommittedWriterOwned(messages, "navigation-initial", "source-line-navigation");
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

  it("keeps an interpolated in-section source-line target at the preview anchor after settle", async () => {
    const ownerIndex = 90;
    const startBlockIndex = ownerIndex * 100 + 1;
    const endBlockIndex = ownerIndex * 100 + 2;
    const requestedSourceLine = ownerIndex * 10 + 4;
    const { flushQueuedRafs, load } = await loadRendererHarness({
      rectTopShiftByBlockIndex: {
        [ownerIndex]: -64,
        [startBlockIndex]: 0,
        [endBlockIndex]: 100,
      },
      renderedSectionHeight: SECTION_PITCH,
      sectionCount: 120,
      virtualization: true,
    });
    load({
      type: "load-document",
      html: buildInterpolatedSourceLineDocument(120, ownerIndex),
      hasMermaid: false,
      hasHljs: false,
    });
    await flushQueuedRafs();

    load({ type: "scroll-to-source-line", sourceLine: requestedSourceLine });
    await flushQueuedRafs();

    const start = document.querySelector<HTMLElement>(`[data-mm-block-index="${startBlockIndex}"]`);
    const end = document.querySelector<HTMLElement>(`[data-mm-block-index="${endBlockIndex}"]`);
    expect(start).not.toBeNull();
    expect(end).not.toBeNull();
    const startTop = start!.getBoundingClientRect().top;
    const endTop = end!.getBoundingClientRect().top;
    const interpolatedTop = startTop + (endTop - startTop) * 0.4;
    expect(interpolatedTop).toBeCloseTo(VIEWPORT_HEIGHT * 0.38, 0);
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
    expect(postedLines.length).toBeGreaterThanOrEqual(3);
    expect(new Set(postedLines).size).toBe(postedLines.length);
    for (let index = 1; index < postedLines.length; index++) {
      expect(postedLines[index - 1]).toBeLessThan(postedLines[index]!);
    }
  });

  it("sanitizes the model-fragment minimap clone so global document lookups only see the live window", async () => {
    const sectionCount = 120;
    const katex = makeRendererKatex();
    const { flushQueuedRafs, load } = await loadRendererHarness({
      katex,
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

  it("preserves SVG paint identities while stripping HTML IDs from minimap clones", async () => {
    vi.useFakeTimers();
    vi.stubGlobal("requestIdleCallback", undefined);
    vi.stubGlobal("cancelIdleCallback", undefined);
    const sectionCount = 20;
    const { flushQueuedRafs, load } = await loadRendererHarness({
      renderedSectionHeight: SECTION_PITCH,
      sectionCount,
      virtualization: false,
    });
    await enableDetailedMinimap(load, flushQueuedRafs);
    load({ type: "load-document", html: buildCloneHtmlIdentityDocument(sectionCount), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();
    load({
      type: "append-document",
      html: buildSvgPaintIdentityAppendBlock(sectionCount),
      hasMermaid: false,
      hasHljs: false,
      isFinal: true,
    });
    await vi.advanceTimersByTimeAsync(200);
    await flushQueuedRafs();

    const minimapContent = getMinimapContent();
    const htmlIds = Array.from(minimapContent.querySelectorAll<Element>("[id]"))
      .filter(isHtmlNamespaceElement)
      .map(element => element.id);
    const svg = minimapContent.querySelector<SVGSVGElement>("svg");
    const gradient = minimapContent.querySelector<SVGElement>("linearGradient");
    const rect = minimapContent.querySelector<SVGElement>("rect");

    expect(htmlIds).toEqual([]);
    expect(svg).not.toBeNull();
    expect(gradient).not.toBeNull();
    expect(rect).not.toBeNull();
    expect(svg!.getAttribute("id")).toBe("svg-root");
    expect(gradient!.getAttribute("id")).toBe("paint-gradient");
    expect(rect!.getAttribute("id")).toBe("paint-rect");
  });

  it("keeps clone-active programmatic landings anchored to the live window", async () => {
    const sectionCount = 120;
    const katex = makeRendererKatex();
    const { flushNextRaf, flushQueuedRafs, load, root, scrollCalls } = await loadRendererHarness({
      katex,
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
      await Promise.resolve();
      load({ type: "scroll-to-block", blockIndex: 8801 });
      await flushNextRaf();
      await flushQueuedRafs();
      const nestedTarget = document.querySelector<HTMLElement>('body > main.mm-document [data-mm-block-index="8801"]');
      expect(nestedTarget).not.toBeNull();
      expect(nestedTarget!.getBoundingClientRect().top).toBeCloseTo(0, 0);
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

  it("keeps legacy minimap clone blocks at their natural rendered height", async () => {
    const sectionCount = 20;
    const { flushQueuedRafs, load } = await loadRendererHarness({
      renderedSectionHeight: SECTION_PITCH,
      sectionCount,
      virtualization: false,
    });
    await enableDetailedMinimap(load, flushQueuedRafs);
    load({ type: "load-document", html: buildClonePollutionDocument(sectionCount), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();

    const cloneBlocks = Array.from(document.querySelectorAll<HTMLElement>(".mm-minimap-content .mm-document > *"));
    expect(cloneBlocks).toHaveLength(sectionCount);
    expect(cloneBlocks.map(block => block.style.minHeight)).toEqual(Array(sectionCount).fill(""));
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

  it("starts host find navigation at the global first match even from a nonzero reading position", async () => {
    const { flushQueuedRafs, flushRafsUntil, load, messages, root } = await loadRendererHarness({
      sectionCount: 120,
      virtualization: true,
    });
    document.dispatchEvent(new Event("DOMContentLoaded"));
    load({ type: "load-document", html: buildFindDocument(120, [10, 90]), hasMermaid: false, hasHljs: false, renderId: 17 });
    await flushQueuedRafs();
    messages.length = 0;
    root.scrollTop = 80 * SECTION_PITCH;
    document.dispatchEvent(new Event("scroll"));
    await flushQueuedRafs();

    load({ type: "open-find-bar" });
    submitFindQuery("needle");
    const request = findQueryMessages(messages).at(-1);
    expect(request).toMatchObject({ query: "needle", renderId: 17 });

    load({
      status: "ready",
      textDomain: "rendered-dom-v1",
      type: "find-results",
      requestId: request!.requestId,
      query: "needle",
      renderId: 17,
      totalCount: 2,
      matches: [descriptorForBlock(10, 1), descriptorForBlock(90, 2)],
    });

    expect(findBarCount().textContent).toBe("1 of 2");
    await flushRafsUntil(() => document.querySelector('[data-mm-block-index="10"]') !== null);
    const firstTarget = document.querySelector<HTMLElement>('[data-mm-block-index="10"]');
    expect(firstTarget).not.toBeNull();
    expect(firstTarget!.getBoundingClientRect().top).toBeCloseTo((VIEWPORT_HEIGHT - SECTION_HEIGHT) / 2, 0);
  });

  it("lands nested host find matches by section first and then bounded Range re-aim", async () => {
    const { flushQueuedRafs, flushRafsUntil, load, messages, root } = await loadRendererHarness({
      sectionCount: 120,
      virtualization: true,
    });
    document.dispatchEvent(new Event("DOMContentLoaded"));
    const rangePrototype = window.Range.prototype as Range & { getBoundingClientRect?: () => DOMRect };
    const originalRangeRect = Object.getOwnPropertyDescriptor(rangePrototype, "getBoundingClientRect");
    const rangeRects = [
      { height: 16, top: 140 },
      { height: 16, top: (VIEWPORT_HEIGHT - 16) / 2 },
    ];
    let rangeRectIndex = 0;
    const readRangeRect = vi.fn(() => {
      const rect = rangeRects[Math.min(rangeRectIndex++, rangeRects.length - 1)]!;
      return {
        bottom: rect.top + rect.height,
        height: rect.height,
        left: 0,
        right: 0,
        top: rect.top,
        width: 48,
        x: 0,
        y: rect.top,
        toJSON: () => ({}),
      } as DOMRect;
    });
    Object.defineProperty(rangePrototype, "getBoundingClientRect", {
      configurable: true,
      value: readRangeRect,
    });

    try {
      load({
        type: "load-document",
        html: buildNestedFindDocument(120, 88, 8801),
        hasMermaid: false,
        hasHljs: false,
        renderId: 18,
      });
      await flushQueuedRafs();
      messages.length = 0;
      root.scrollTop = 0;

      load({ type: "open-find-bar" });
      submitFindQuery("needle");
      const request = findQueryMessages(messages).at(-1);
      load({
        status: "ready",
        textDomain: "rendered-dom-v1",
        type: "find-results",
        requestId: request!.requestId,
        query: "needle",
        renderId: 18,
        totalCount: 1,
        matches: [descriptorForNestedNeedle(88, 8801, 1)],
      });

      await flushRafsUntil(() => document.querySelector('[data-mm-block-index="8801"]') !== null);
      await flushQueuedRafs();

      const owner = document.querySelector<HTMLElement>('body > main.mm-document > [data-mm-block-index="88"]');
      expect(owner).not.toBeNull();
      const writes = perfDetails<{ after: number; operationEpoch: number; writer?: string }>(
        messages,
        "mm-virt-scroll-write-committed"
      ).filter(detail => detail.writer === "find-navigation");
      const expectedSectionTarget = readSyntheticDocumentTop(owner!) - (VIEWPORT_HEIGHT - owner!.offsetHeight) / 2;
      const expectedRangeTarget = expectedSectionTarget + 140 - (VIEWPORT_HEIGHT - 16) / 2;
      expect(writes.map(write => write.after)).toEqual([
        expectedSectionTarget,
        expectedRangeTarget,
      ]);
      expect(new Set(writes.map(write => write.operationEpoch)).size).toBe(1);
      expect(readRangeRect).toHaveBeenCalledTimes(2);
    } finally {
      if (originalRangeRect === undefined) {
        delete rangePrototype.getBoundingClientRect;
      } else {
        Object.defineProperty(rangePrototype, "getBoundingClientRect", originalRangeRect);
      }
    }
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
      status: "ready",
      textDomain: "rendered-dom-v1",
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
    expect(firstTarget!.getBoundingClientRect().top).toBeCloseTo((VIEWPORT_HEIGHT - SECTION_HEIGHT) / 2, 0);
    expectCommittedWriterOwned(messages, "find-navigation", "find-navigation");
    expect(highlights.get("mm-find-all")?.map(range => range.toString())).toEqual(["needle"]);
    expect(highlights.get("mm-find-current")?.map(range => range.toString())).toEqual(["needle"]);

    findButton("next").click();
    await flushRafsUntil(() => document.querySelector('[data-mm-block-index="95"]') !== null);
    await flushRafsUntil(() => document.querySelector('[data-mm-block-index="95"]') !== null);
    const secondTarget = document.querySelector<HTMLElement>('[data-mm-block-index="95"]');
    expect(secondTarget).not.toBeNull();
    expect(findBarCount().textContent).toBe("2 of 2");
    expect(secondTarget!.getBoundingClientRect().top).toBeCloseTo((VIEWPORT_HEIGHT - SECTION_HEIGHT) / 2, 0);

    findButton("prev").click();
    await flushRafsUntil(() => document.querySelector('[data-mm-block-index="90"]') !== null);
    await flushRafsUntil(() => document.querySelector('[data-mm-block-index="90"]') !== null);
    const previousTarget = document.querySelector<HTMLElement>('[data-mm-block-index="90"]');
    expect(previousTarget).not.toBeNull();
    expect(findBarCount().textContent).toBe("1 of 2");
    expect(previousTarget!.getBoundingClientRect().top).toBeCloseTo((VIEWPORT_HEIGHT - SECTION_HEIGHT) / 2, 0);
  });

  it("releases an obsolete find lease without restoring or writing", async () => {
    const { flushQueuedRafs, load, messages } = await loadRendererHarness({
      sectionCount: 120,
      virtualization: true,
    });
    document.dispatchEvent(new Event("DOMContentLoaded"));
    load({ type: "load-document", html: buildFindDocument(120, [90]), hasMermaid: false, hasHljs: false, renderId: 8 });
    await flushQueuedRafs();
    messages.length = 0;

    load({ type: "open-find-bar" });
    submitFindQuery("needle");
    const request = findQueryMessages(messages).at(-1)!;
    load({
      status: "ready",
      textDomain: "rendered-dom-v1",
      type: "find-results",
      requestId: request.requestId,
      query: "needle",
      renderId: 8,
      totalCount: 1,
      matches: [descriptorForBlock(90, 1)],
    });
    load({ type: "open-find-bar" });
    await flushQueuedRafs();

    const acquired = perfDetails<{ operationEpoch: number; owner?: string }>(
      messages,
      "mm-virt-scroll-lease-acquired"
    ).find(detail => detail.owner === "find-navigation");
    expect(acquired).toBeDefined();
    expect(perfDetails<{ operationEpoch: number; owner?: string }>(
      messages,
      "mm-virt-scroll-lease-released"
    )).toContainEqual(expect.objectContaining({
      operationEpoch: acquired!.operationEpoch,
      owner: "find-navigation",
    }));
    expect(perfDetails<{ writer?: string }>(messages, "mm-virt-scroll-write-committed"))
      .not.toContainEqual(expect.objectContaining({ writer: "find-navigation" }));
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
      status: "ready",
      textDomain: "rendered-dom-v1",
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
    const katex = makeRendererKatex();
    const { flushQueuedRafs, load, messages, root } = await loadRendererHarness({
      katex,
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
      status: "ready",
      textDomain: "rendered-dom-v1",
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

  it("waits for model rendered content before publishing model-fragment minimap formulas", async () => {
    const sectionCount = 120;
    const katex = makeRendererKatex();
    const { flushQueuedRafs, load, messages } = await loadRendererHarness({
      katex,
      renderedSectionHeight: SECTION_PITCH,
      sectionCount,
      virtualization: true,
    });
    document.dispatchEvent(new Event("DOMContentLoaded"));
    await enableDetailedMinimap(load, flushQueuedRafs);

    load({
      type: "load-document",
      html: buildModelRenderedMathDocument(sectionCount, [0, 90]),
      hasMermaid: false,
      hasHljs: false,
    });
    await flushQueuedRafs();

    const liveMain = document.querySelector<HTMLElement>("body > main.mm-document");
    expect(liveMain).not.toBeNull();
    expect(liveMain!.querySelector('[data-mm-block-index="90"]')).toBeNull();
    const minimapContent = getMinimapContent();
    expect(minimapContent.querySelectorAll(".katex")).toHaveLength(2);
    expect(minimapContent.textContent).toContain("rendered:x_0");
    expect(minimapContent.textContent).toContain("rendered:x_90");
    expectMinimapCloneIdentitySanitized();

    const start = perfDetails<{ consumers?: string[] }>(messages, "mm-model-rendered-content-start");
    const end = perfDetails<{ status?: string; consumers?: string[] }>(messages, "mm-model-rendered-content-end");
    expect(start).toHaveLength(1);
    expect(start[0]!.consumers).toContain("minimap-detail");
    expect(end).toContainEqual(expect.objectContaining({
      status: "ready",
      consumers: expect.arrayContaining(["minimap-detail"]),
    }));
    const readinessEndIndex = perfMarkMessageIndex(messages, "mm-model-rendered-content-end");
    const publishIndex = perfMarkMessageIndex(messages, "mm-minimap-refresh-end", detail =>
      detail["source"] === "model-fragment");
    expect(readinessEndIndex).toBeGreaterThanOrEqual(0);
    expect(publishIndex).toBeGreaterThan(readinessEndIndex);
  });

  it("publishes the rendered find projection while detailed minimap is denied", async () => {
    const sectionCount = 120;
    const katex = makeRendererKatex();
    const { flushQueuedRafs, flushRafsUntil, highlights, load, messages } = await loadRendererHarness({
      katex,
      renderedSectionHeight: SECTION_PITCH,
      sectionCount,
      virtualization: true,
    });
    document.dispatchEvent(new Event("DOMContentLoaded"));
    document.documentElement.style.setProperty("--mm-minimap-width", "136px");
    setMinimapViewportHeight(592);
    load({ type: "reading-preferences", ...makeReadingPreferences("auto") });
    await flushQueuedRafs();
    loadMinimapPolicy(load);
    load({
      type: "load-document",
      html: buildModelRenderedMathDocument(sectionCount, [90]),
      hasMermaid: false,
      hasHljs: false,
      renderId: 41,
    });
    await flushQueuedRafs();

    load({ type: "open-find-bar" });
    submitFindQuery("rendered:x_90");
    for (
      let pass = 0;
      pass < 128 && perfDetails(messages, "mm-find-projection-terminal").length === 0;
      pass++
    ) {
      await flushQueuedRafs();
      await Promise.resolve();
    }

    expect(katex.render).toHaveBeenCalledWith("x_90", expect.any(Element), expect.any(Object));
    expect(findQueryMessages(messages)).toContainEqual(expect.objectContaining({
      renderId: 41,
      textDomain: "rendered-dom-v1",
    }));
    const protocolTypes = messages
      .filter((message): message is { type: string } => typeof message === "object" && message !== null && "type" in message)
      .map(message => message.type)
      .filter(type => type.startsWith("find-domain-") || type.startsWith("find-text-index-"));
    const domainBeginIndex = messages.findIndex(message =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "find-domain-begin");
    const readinessStartIndex = perfMarkMessageIndex(messages, "mm-model-rendered-content-start");
    const readinessEndIndex = perfMarkMessageIndex(messages, "mm-model-rendered-content-end");
    const transferStartIndex = messages.findIndex(message =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "find-text-index-start");
    expect(domainBeginIndex).toBeGreaterThanOrEqual(0);
    expect(domainBeginIndex).toBeLessThan(readinessStartIndex);
    expect(readinessStartIndex).toBeLessThan(readinessEndIndex);
    expect(readinessEndIndex).toBeLessThan(transferStartIndex);
    expect(perfDetails<{ status?: string }>(messages, "mm-find-projection-terminal"))
      .toContainEqual(expect.objectContaining({ status: "complete" }));
    expect(protocolTypes).toEqual([
      "find-domain-begin",
      "find-text-index-start",
      "find-text-index-chunk",
      "find-text-index-complete",
    ]);
    expect(perfDetails<{ consumers?: string[] }>(messages, "mm-model-rendered-content-start"))
      .toEqual([expect.objectContaining({
        consumers: ["rendered-find-projection"],
      })]);
    expect(perfDetails<{ status?: string; consumers?: string[] }>(messages, "mm-model-rendered-content-end"))
      .toContainEqual(expect.objectContaining({
        status: "ready",
        consumers: expect.arrayContaining(["rendered-find-projection"]),
      }));
    expect(perfDetails<{ source?: string }>(messages, "mm-minimap-refresh-end")
      .filter(detail => detail.source === "model-fragment")).toEqual([]);
    expect(getMinimapContent().childElementCount).toBe(0);

    const query = findQueryMessages(messages).at(-1)!;
    const projectionParts = messages
      .filter((message): message is {
        type: "find-text-index-chunk";
        parts: Array<{
          blockIndex: number;
          blockLocalStart: number;
          text: string;
        }>;
      } => typeof message === "object"
        && message !== null
        && (message as { type?: unknown }).type === "find-text-index-chunk")
      .flatMap(message => message.parts);
    const formulaPart = projectionParts.find(part => part.text.includes(query.query));
    expect(formulaPart).toBeDefined();
    const formulaOffset = formulaPart!.blockLocalStart + formulaPart!.text.indexOf(query.query);

    load({
      status: "ready",
      textDomain: "rendered-dom-v1",
      type: "find-results",
      requestId: query.requestId,
      query: query.query,
      renderId: 41,
      totalCount: 1,
      matches: [{
        blockIndex: formulaPart!.blockIndex,
        blockLocalOffset: formulaOffset,
        length: query.query.length,
        matchId: "41:1:90:formula:1",
        normalizedText: query.query,
        ordinal: 1,
      }],
    });
    await flushRafsUntil(() => document.querySelector('[data-mm-block-index="90"]') !== null);
    await flushQueuedRafs();

    expect(findBarCount().textContent).toBe("1 of 1");
    expect(highlights.get("mm-find-all")?.map(range => range.toString())).toEqual([query.query]);
    expect(highlights.get("mm-find-current")?.map(range => range.toString())).toEqual([query.query]);
  });

  it("shares one model rendered content job across minimap and find leases", async () => {
    const sectionCount = 120;
    const katex = makeRendererKatex();
    const { flushQueuedRafs, load, messages } = await loadRendererHarness({
      katex,
      renderedSectionHeight: SECTION_PITCH,
      sectionCount,
      virtualization: true,
    });
    document.dispatchEvent(new Event("DOMContentLoaded"));
    await enableDetailedMinimap(load, flushQueuedRafs);

    load({
      type: "load-document",
      html: buildModelRenderedMathDocument(sectionCount, [0, 90]),
      hasMermaid: false,
      hasHljs: false,
      renderId: 42,
    });
    load({ type: "open-find-bar" });
    submitFindQuery("needle");
    await flushQueuedRafs();

    expect(perfDetails(messages, "mm-model-rendered-content-start")).toHaveLength(1);
    expect(perfDetails<{ activeLeaseCount?: number; consumers?: string[] }>(
      messages,
      "mm-model-rendered-content-progress"
    )).toContainEqual(expect.objectContaining({
      activeLeaseCount: 2,
      consumers: expect.arrayContaining(["minimap-detail", "rendered-find-projection"]),
    }));
    expect(new Set(katex.render.mock.calls.map(call => call[0]).filter(tex => tex === "x_0" || tex === "x_90")))
      .toEqual(new Set(["x_0", "x_90"]));
    expect(perfDetails(messages, "mm-model-rendered-content-cancel")).toEqual([]);
  });

  it("cancels the last model rendered content lease and resumes pending entries later", async () => {
    vi.useFakeTimers();
    const sectionCount = 120;
    const katex = makeRendererKatex();
    const { flushQueuedRafs, load, messages } = await loadRendererHarness({
      katex,
      renderedSectionHeight: SECTION_PITCH,
      sectionCount,
      virtualization: true,
    });
    document.dispatchEvent(new Event("DOMContentLoaded"));
    await enableDetailedMinimap(load, flushQueuedRafs);

    load({
      type: "load-document",
      html: buildModelRenderedMathDocument(sectionCount, [90]),
      hasMermaid: false,
      hasHljs: false,
    });
    expect(perfDetails(messages, "mm-model-rendered-content-start")).toHaveLength(1);
    load({ type: "reading-preferences", ...makeReadingPreferences("off") });
    await flushQueuedRafs();

    expect(perfDetails<{ reason?: string }>(messages, "mm-model-rendered-content-cancel"))
      .toContainEqual(expect.objectContaining({ reason: "last-lease-released" }));
    expect(perfDetails(messages, "mm-model-rendered-content-end")).toEqual([]);

    messages.length = 0;
    katex.render.mockClear();
    load({ type: "reading-preferences", ...makeReadingPreferences("on") });
    await flushQueuedRafs();
    await vi.advanceTimersByTimeAsync(1_000);
    await flushQueuedRafs();

    expect(katex.render.mock.calls.map(call => call[0])).toEqual(["x_90"]);
    expect(getMinimapContent().textContent).toContain("rendered:x_90");
    expect(perfDetails<{ status?: string }>(messages, "mm-model-rendered-content-end"))
      .toContainEqual(expect.objectContaining({ status: "ready" }));
  });

  it("cancels stale model rendered content work on document replacement without blocking a same-model window update", async () => {
    const sectionCount = 120;
    const katex = makeRendererKatex();
    const { flushQueuedRafs, load, messages, root } = await loadRendererHarness({
      katex,
      renderedSectionHeight: SECTION_PITCH,
      sectionCount,
      virtualization: true,
    });
    document.dispatchEvent(new Event("DOMContentLoaded"));
    await enableDetailedMinimap(load, flushQueuedRafs);

    load({
      type: "load-document",
      html: buildModelRenderedMathDocument(sectionCount, [90]),
      hasMermaid: false,
      hasHljs: false,
    });
    load({ type: "load-document", html: buildHeadingDocument(20), hasMermaid: false, hasHljs: false });
    await flushQueuedRafs();

    expect(perfDetails<{ reason?: string }>(messages, "mm-model-rendered-content-cancel"))
      .toContainEqual(expect.objectContaining({ reason: "stale-document" }));

    messages.length = 0;
    load({
      type: "load-document",
      html: buildModelRenderedMathDocument(sectionCount, [90]),
      hasMermaid: false,
      hasHljs: false,
    });
    root.scrollTop = 20 * SECTION_PITCH;
    document.dispatchEvent(new Event("scroll"));
    await flushQueuedRafs();

    expect(perfDetails(messages, "mm-model-rendered-content-start")).toHaveLength(1);
    expect(perfDetails(messages, "mm-model-rendered-content-cancel")).toEqual([]);
    expect(getMinimapContent().textContent).toContain("rendered:x_90");
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

  it.each(["minimap", "find", "navigation"] as const)(
    "retains measured-height adoption while a %s transaction owns the current frame",
    async owner => {
      const sectionCount = 120;
      const harness = await loadRendererHarness({ sectionCount, virtualization: true });
      if (owner === "find" || owner === "minimap") {
        document.dispatchEvent(new Event("DOMContentLoaded"));
      }
      if (owner === "minimap") {
        document.documentElement.style.setProperty("--mm-minimap-width", "136px");
        setMinimapViewportHeight(592);
        harness.load({ type: "reading-preferences", ...makeReadingPreferences("on") });
        loadMinimapPolicy(harness.load);
      }
      const html = owner === "find"
        ? buildFindDocument(sectionCount, [90])
        : buildHeadingDocument(sectionCount);
      harness.load({ type: "load-document", html, hasMermaid: false, hasHljs: false, renderId: 21 });
      await harness.flushQueuedRafs();
      harness.messages.length = 0;
      harness.setRenderedSectionHeight(SECTION_HEIGHT + 40);

      // Queue maintenance first, then occupy Task 3's current frame before
      // that maintenance callback is delivered.
      harness.triggerResize();
      let minimap: HTMLElement | null = null;
      if (owner === "navigation") {
        harness.load({ type: "scroll-to-block", blockIndex: 90 });
      } else if (owner === "find") {
        harness.load({ type: "open-find-bar" });
        submitFindQuery("needle");
        const request = findQueryMessages(harness.messages).at(-1)!;
        harness.load({
          status: "ready",
          textDomain: "rendered-dom-v1",
          type: "find-results",
          requestId: request.requestId,
          query: "needle",
          renderId: 21,
          totalCount: 1,
          matches: [descriptorForBlock(90, 1)],
        });
      } else {
        await harness.flushNextRaf();
        minimap = document.querySelector<HTMLElement>(".mm-minimap")!;
        Object.defineProperty(minimap, "setPointerCapture", { configurable: true, value: vi.fn() });
        Object.defineProperty(minimap, "releasePointerCapture", { configurable: true, value: vi.fn() });
        vi.spyOn(minimap, "getBoundingClientRect").mockReturnValue({
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
        minimap.dispatchEvent(pointerEvent("pointerdown", 12));
        minimap.dispatchEvent(pointerEvent("pointermove", 588));
      }

      if (owner === "minimap") {
        await harness.flushRafsUntil(() => perfDetails(
          harness.messages,
          "mm-virt-maintenance-retry"
        ).some(detail => detail["owner"] === "measured-height-adoption"));
        expect(perfDetails(harness.messages, "mm-virt-maintenance-retry")).toContainEqual(
          expect.objectContaining({ owner: "measured-height-adoption" })
        );
        minimap!.dispatchEvent(pointerEvent("pointerup", 588));
      }
      await harness.flushQueuedRafs();

      expect(perfDetails(harness.messages, "mm-virt-window-height-adopted").length).toBeGreaterThan(0);
      expect(maintenanceTerminalDetails(harness.messages)).toContainEqual(expect.objectContaining({
        owner: "measured-height-adoption",
        status: "completed",
      }));
    }
  );

  it("attached maintenance emits one programmatic terminal for its request and one later same-owner request delivers once", async () => {
    const harness = await loadMaintenanceLifecycleHarness();
    const oldRequest = await startMeasuredMaintenanceInState(harness, "joined");

    harness.load({ type: "scroll-to-block", blockIndex: 90 });
    expectExactMaintenanceLifecycle(harness.messages, oldRequest, {
      executionCount: 0,
      reason: "programmatic-supersession",
      status: "canceled",
    });
    expect(perfDetails(harness.messages, "mm-virt-window-height-adopted")).toEqual([]);

    harness.setRenderedSectionHeight(SECTION_HEIGHT + 80);
    harness.triggerResize();
    await harness.flushQueuedRafs();
    const requests = maintenanceRequestsForOwner(harness.messages, "measured-height-adoption");
    expect(requests.length).toBeGreaterThanOrEqual(2);
    expect(requests[1]!.requestSerial).toBeGreaterThan(requests[0]!.requestSerial!);
    for (const laterRequest of requests.slice(1)) {
      expectExactMaintenanceLifecycle(harness.messages, laterRequest, {
        executionCount: 1,
        reason: "delivered",
        status: "completed",
      });
    }

    const eventCount = maintenanceLifecycleEvents(harness.messages).length;
    await harness.flushQueuedRafs();
    expect(maintenanceLifecycleEvents(harness.messages)).toHaveLength(eventCount);
  });

  it("maintenance-owned scheduled request emits one programmatic terminal before its frame can run", async () => {
    const harness = await loadMaintenanceLifecycleHarness();
    const request = await startMeasuredMaintenanceInState(harness, "owned");
    expect(perfDetails<MaintenanceLifecycleDetail>(harness.messages, "mm-virt-maintenance-bound")
      .filter(detail => maintenanceRequestKey(detail) === maintenanceRequestKey(request))).toEqual([
      expect.objectContaining({ ownsLease: true }),
    ]);

    harness.load({ type: "scroll-to-block", blockIndex: 90 });
    expectExactMaintenanceLifecycle(harness.messages, request, {
      executionCount: 0,
      reason: "programmatic-supersession",
      status: "canceled",
    });
    expect(perfDetails(harness.messages, "mm-virt-window-height-adopted")).toEqual([]);
    const eventCount = maintenanceEventsForRequest(harness.messages, request).length;
    await harness.flushQueuedRafs();
    expect(maintenanceEventsForRequest(harness.messages, request)).toHaveLength(eventCount);
  });

  it("same-owner producer updates coalesce to one request id and execute only the latest work once", async () => {
    const harness = await loadMaintenanceLifecycleHarness();
    const request = await startMeasuredMaintenanceInState(harness, "owned");
    harness.setRenderedSectionHeight(SECTION_HEIGHT + 80);
    harness.flushResizeRafsSynchronously();
    harness.setRenderedSectionHeight(SECTION_HEIGHT + 120);
    harness.flushResizeRafsSynchronously();

    expect(maintenanceRequestsForOwner(harness.messages, "measured-height-adoption")).toHaveLength(1);
    expect(maintenanceEventsForRequest(harness.messages, request)
      .filter(event => event.name === "mm-virt-maintenance-coalesced")
      .map(event => event.detail.workRevision)).toEqual([2, 3]);
    await harness.flushQueuedRafs();
    expectExactMaintenanceLifecycle(harness.messages, request, {
      executionCount: 1,
      reason: "delivered",
      status: "completed",
    });
    expect(maintenanceEventsForRequest(harness.messages, request).at(-1)?.detail.workRevision).toBe(3);
    const producerTickets = perfDetails<{
      source?: string;
      ticket?: number;
    }>(harness.messages, "mm-virt-geometry-work-start")
      .filter(detail => detail.source === "measured-height-adoption");
    expect(producerTickets.length).toBeGreaterThanOrEqual(3);
    for (const producerTicket of producerTickets) {
      expect(perfDetails(harness.messages, "mm-virt-geometry-work-end")
        .filter(detail => detail["ticket"] === producerTicket.ticket)).toHaveLength(1);
    }
  });

  it("frame-transaction rejection is the only retry edge and retains the request id", async () => {
    const harness = await loadMaintenanceLifecycleHarness();
    const request = await startMeasuredMaintenanceInState(harness, "retry-pending");
    expect(maintenanceEventsForRequest(harness.messages, request)
      .filter(event => event.name === "mm-virt-maintenance-retry")).toEqual([
      expect.objectContaining({
        detail: expect.objectContaining({ reason: "frame-transaction-occupied" }),
      }),
    ]);

    document.querySelector<HTMLElement>(".mm-minimap")!
      .dispatchEvent(pointerEvent("pointerup", 588));
    await harness.flushQueuedRafs();
    expectExactMaintenanceLifecycle(harness.messages, request, {
      executionCount: 1,
      reason: "delivered",
      status: "completed",
    });
    expect(maintenanceEventsForRequest(harness.messages, request)
      .filter(event => event.name === "mm-virt-maintenance-retry")
      .every(event => event.detail.reason === "frame-transaction-occupied")).toBe(true);
  });

  it("joined scheduled maintenance defers lease release and delivers without a joined-release retry", async () => {
    const harness = await loadMaintenanceLifecycleHarness();
    const request = await startMeasuredMaintenanceInState(harness, "joined");
    const binding = maintenanceEventsForRequest(harness.messages, request)
      .find(event => event.name === "mm-virt-maintenance-bound")!.detail;
    const minimap = document.querySelector<HTMLElement>(".mm-minimap")!;
    minimap.dispatchEvent(pointerEvent("pointermove", 588));
    minimap.dispatchEvent(pointerEvent("pointerup", 588));
    await harness.flushQueuedRafs();

    expectExactMaintenanceLifecycle(harness.messages, request, {
      executionCount: 1,
      reason: "delivered",
      status: "completed",
    });
    expect(maintenanceEventsForRequest(harness.messages, request)
      .filter(event => event.name === "mm-virt-maintenance-retry")).toEqual([]);
    expect(perfDetails<MaintenanceLifecycleDetail>(harness.messages, "mm-virt-maintenance-retry")
      .some(detail => detail.reason === "joined-lease-released")).toBe(false);
    const terminalIndex = perfMarkMessageIndex(harness.messages, "mm-virt-maintenance-terminal", detail =>
      detail["requestSerial"] === request.requestSerial);
    const releaseIndex = perfMarkMessageIndex(harness.messages, "mm-virt-scroll-lease-released", detail =>
      detail["operationEpoch"] === binding.operationEpoch);
    expect(terminalIndex).toBeGreaterThanOrEqual(0);
    expect(releaseIndex).toBeGreaterThan(terminalIndex);
  });

  it("programmatic supersession cancels retry-pending maintenance without migration", async () => {
    const harness = await loadMaintenanceLifecycleHarness();
    const request = await startMeasuredMaintenanceInState(harness, "retry-pending");
    harness.load({ type: "scroll-to-block", blockIndex: 90 });
    expectExactMaintenanceLifecycle(harness.messages, request, {
      executionCount: 0,
      reason: "programmatic-supersession",
      status: "canceled",
    });
    expect(perfDetails(harness.messages, "mm-virt-window-height-adopted")).toEqual([]);
    await harness.flushQueuedRafs();
    await harness.flushCanceledRafs();
    expect(maintenanceEventsForRequest(harness.messages, request).at(-1)?.name)
      .toBe("mm-virt-maintenance-terminal");
  });

  it.each(["joined", "owned"] as const)(
    "user supersession terminalizes joined and maintenance-owned requests exactly once (%s)",
    async state => {
      const harness = await loadMaintenanceLifecycleHarness();
      const request = await startMeasuredMaintenanceInState(harness, state);
      harness.root.scrollTop += 17;
      document.dispatchEvent(new Event("scroll"));
      await harness.flushQueuedRafs();
      expectExactMaintenanceLifecycle(harness.messages, request, {
        executionCount: 0,
        reason: "user-supersession",
        status: "canceled",
      });
    }
  );

  it.each(["joined", "owned", "retry-pending"] as const)(
    "document replacement terminalizes joined owned and retry-pending requests exactly once (%s)",
    async state => {
      const harness = await loadMaintenanceLifecycleHarness();
      const request = await startMeasuredMaintenanceInState(harness, state);
      harness.load({
        type: "load-document",
        html: buildHeadingDocument(12),
        hasMermaid: false,
        hasHljs: false,
        renderId: 61,
      });
      await harness.flushQueuedRafs();
      await harness.flushCanceledRafs();
      expectExactMaintenanceLifecycle(harness.messages, request, {
        executionCount: 0,
        reason: "stale-document",
        status: "canceled",
      });
    }
  );

  it.each(["joined", "owned", "retry-pending"] as const)(
    "pagehide terminalizes joined owned and retry-pending requests exactly once (%s)",
    async state => {
      const harness = await loadMaintenanceLifecycleHarness();
      const request = await startMeasuredMaintenanceInState(harness, state);
      window.dispatchEvent(new Event("pagehide"));
      await harness.flushQueuedRafs();
      await harness.flushCanceledRafs();
      expectExactMaintenanceLifecycle(harness.messages, request, {
        executionCount: 0,
        reason: "teardown",
        status: "canceled",
      });
    }
  );

  it.each(["owned", "retry-pending"] as const)(
    "stale frame and retry callbacks cannot execute or emit a second terminal (%s)",
    async state => {
      const harness = await loadMaintenanceLifecycleHarness();
      const request = await startMeasuredMaintenanceInState(harness, state);
      harness.load({ type: "scroll-to-block", blockIndex: 90 });
      const eventsAtTerminal = maintenanceEventsForRequest(harness.messages, request);
      await harness.flushQueuedRafs();
      await harness.flushCanceledRafs();
      expect(maintenanceEventsForRequest(harness.messages, request)).toEqual(eventsAtTerminal);
      expectExactMaintenanceLifecycle(harness.messages, request, {
        executionCount: 0,
        reason: "programmatic-supersession",
        status: "canceled",
      });
    }
  );

  it.each(["completed", "failed", "canceled"] as const)(
    "later same-owner request after completed failed or canceled terminal receives a new id and delivers (%s)",
    async terminalKind => {
      const faults: ControllerFaults = {};
      const harness = await loadMaintenanceLifecycleHarness(faults);
      if (terminalKind === "failed") {
        faults.adoptRenderedHeights = true;
      }
      const oldRequest = await startMeasuredMaintenanceInState(harness, "owned");
      if (terminalKind === "canceled") {
        harness.load({ type: "scroll-to-block", blockIndex: 90 });
      }
      await harness.flushQueuedRafs();
      if (terminalKind === "failed") {
        faults.adoptRenderedHeights = false;
      }
      expectExactMaintenanceLifecycle(harness.messages, oldRequest, terminalKind === "completed"
        ? { executionCount: 1, reason: "delivered", status: "completed" }
        : terminalKind === "failed"
          ? { executionCount: 1, reason: "frame-work-failed", status: "failed" }
          : { executionCount: 0, reason: "programmatic-supersession", status: "canceled" });

      const requestCount = maintenanceRequestsForOwner(harness.messages, "measured-height-adoption").length;
      harness.setRenderedSectionHeight(SECTION_HEIGHT + 160);
      harness.triggerResize();
      await harness.flushQueuedRafs();
      const requests = maintenanceRequestsForOwner(harness.messages, "measured-height-adoption");
      expect(requests).toHaveLength(requestCount + 1);
      const laterRequest = requests.at(-1)!;
      expect(laterRequest.requestSerial).toBeGreaterThan(oldRequest.requestSerial!);
      expectExactMaintenanceLifecycle(harness.messages, laterRequest, {
        executionCount: 1,
        reason: "delivered",
        status: "completed",
      });
    }
  );

  it("same-owner request during execution queues one coalesced successor and delivers it after terminal", async () => {
    const faults: ControllerFaults = {};
    let harness!: RendererHarness;
    let injectSuccessor = false;
    let injected = false;
    faults.onAdoptRenderedHeights = () => {
      if (!injectSuccessor || injected) {
        return;
      }
      injected = true;
      harness.setRenderedSectionHeight(SECTION_HEIGHT + 80);
      harness.flushResizeRafsSynchronously();
      harness.setRenderedSectionHeight(SECTION_HEIGHT + 120);
      harness.flushResizeRafsSynchronously();
    };
    harness = await loadMaintenanceLifecycleHarness(faults);
    injectSuccessor = true;
    const activeRequest = await startMeasuredMaintenanceInState(harness, "owned");
    await harness.flushQueuedRafs();

    const requests = maintenanceRequestsForOwner(harness.messages, "measured-height-adoption");
    expect(requests).toHaveLength(2);
    const successor = requests[1]!;
    expect(successor.requestSerial).toBeGreaterThan(activeRequest.requestSerial!);
    expect(maintenanceEventsForRequest(harness.messages, successor)
      .filter(event => event.name === "mm-virt-maintenance-coalesced")
      .map(event => event.detail.workRevision)).toEqual([2]);
    expectExactMaintenanceLifecycle(harness.messages, activeRequest, {
      executionCount: 1,
      reason: "delivered",
      status: "completed",
    });
    expectExactMaintenanceLifecycle(harness.messages, successor, {
      executionCount: 1,
      reason: "delivered",
      status: "completed",
    });
    const activeTerminalIndex = perfMarkMessageIndex(harness.messages, "mm-virt-maintenance-terminal", detail =>
      detail["requestSerial"] === activeRequest.requestSerial);
    const successorBoundIndex = perfMarkMessageIndex(harness.messages, "mm-virt-maintenance-bound", detail =>
      detail["requestSerial"] === successor.requestSerial);
    expect(successorBoundIndex).toBeGreaterThan(activeTerminalIndex);
  });

  it("same-owner successor queued during throwing work terminally cancels exactly once", async () => {
    const faults: ControllerFaults = {};
    let harness!: RendererHarness;
    let injectSuccessor = false;
    let injected = false;
    faults.onAdoptRenderedHeights = () => {
      if (!injectSuccessor || injected) {
        return;
      }
      injected = true;
      harness.setRenderedSectionHeight(SECTION_HEIGHT + 80);
      harness.flushResizeRafsSynchronously();
      throw new Error("injected active maintenance failure after successor admission");
    };
    harness = await loadMaintenanceLifecycleHarness(faults);
    injectSuccessor = true;
    const activeRequest = await startMeasuredMaintenanceInState(harness, "owned");
    await harness.flushQueuedRafs();

    const requests = maintenanceRequestsForOwner(harness.messages, "measured-height-adoption");
    expect(requests).toHaveLength(2);
    const successor = requests[1]!;
    expect(successor.requestSerial).toBeGreaterThan(activeRequest.requestSerial!);
    expectExactMaintenanceLifecycle(harness.messages, activeRequest, {
      executionCount: 1,
      reason: "frame-work-failed",
      status: "failed",
    });
    expectExactMaintenanceLifecycle(harness.messages, successor, {
      executionCount: 0,
      reason: "frame-work-failed",
      status: "canceled",
    });
    expect(maintenanceEventsForRequest(harness.messages, successor)
      .filter(event => event.name === "mm-virt-maintenance-bound")).toEqual([]);

    const eventsAtTerminal = maintenanceLifecycleEvents(harness.messages);
    await harness.flushQueuedRafs();
    await harness.flushCanceledRafs();
    expect(maintenanceLifecycleEvents(harness.messages)).toEqual(eventsAtTerminal);
  });

  it("flag off emits no maintenance request binding retry or terminal events", async () => {
    const harness = await loadRendererHarness({ sectionCount: 12, virtualization: false });
    harness.load({
      type: "load-document",
      html: buildHeadingDocument(12),
      hasMermaid: false,
      hasHljs: false,
      renderId: 62,
    });
    harness.triggerResize();
    harness.load({ type: "scroll-to-block", blockIndex: 7 });
    await harness.flushQueuedRafs();
    expect(maintenanceLifecycleEvents(harness.messages)).toEqual([]);
  });

  it("navigation nominal zero waits for same-epoch confirmation before release", async () => {
    const harness = await loadMaintenanceLifecycleHarness();
    harness.load({ type: "scroll-to-block", blockIndex: 90 });
    await harness.flushQueuedRafs();

    const settled = perfDetails(harness.messages, "mm-virt-geometry-settled");
    expect(settled.length).toBeGreaterThanOrEqual(2);
    expect(settled.at(-1)?.geometryEpoch).toBe(settled.at(-2)?.geometryEpoch);
    const secondSettleIndex = perfMarkMessageIndex(harness.messages, "mm-virt-geometry-settled", detail =>
      detail["geometryEpoch"] === settled.at(-1)?.geometryEpoch);
    const releaseIndex = perfMarkMessageIndex(harness.messages, "mm-virt-scroll-lease-released", detail =>
      detail["owner"] === "block-navigation");
    expect(releaseIndex).toBeGreaterThan(secondSettleIndex);
  });

  it("minimap nominal zero waits for same-epoch confirmation before release", async () => {
    const harness = await loadMaintenanceLifecycleHarness();
    const minimap = beginMinimapMaintenanceLease(harness);
    minimap.dispatchEvent(pointerEvent("pointerup", 12));
    await harness.flushQueuedRafs();

    const settled = perfDetails(harness.messages, "mm-virt-geometry-settled");
    expect(settled.length).toBeGreaterThanOrEqual(2);
    expect(settled.at(-1)?.geometryEpoch).toBe(settled.at(-2)?.geometryEpoch);
  });

  it("window mount holds font ticket through adoption", async () => {
    let resolveFonts!: () => void;
    const fontsReady = new Promise<void>(resolve => { resolveFonts = resolve; });
    const harness = await loadRendererHarness({
      fontsReady,
      sectionCount: 120,
      virtualization: true,
    });
    document.dispatchEvent(new Event("DOMContentLoaded"));
    harness.load({
      type: "load-document",
      html: buildHeadingDocument(120),
      hasMermaid: false,
      hasHljs: false,
      renderId: 81,
    });
    await harness.flushRafsUntil(() => perfDetails(harness.messages, "mm-virt-geometry-work-start")
      .some(detail => detail.source === "window-fonts"), 20);

    expect(perfDetails(harness.messages, "mm-virt-geometry-work-start")).toContainEqual(
      expect.objectContaining({ source: "window-fonts" })
    );
    expect(perfDetails(harness.messages, "mm-virt-geometry-work-end")
      .some(detail => detail.source === "window-fonts")).toBe(false);
    resolveFonts();
    await Promise.resolve();
    await harness.flushQueuedRafs();
    const currentFontStart = perfDetails<{
      source?: string;
      ticket?: number;
    }>(harness.messages, "mm-virt-geometry-work-start")
      .filter(detail => detail.source === "window-fonts")
      .at(-1)!;
    const mutationIndex = perfMarkMessageIndex(harness.messages, "mm-virt-geometry-mutated", detail =>
      detail["source"] === "window-fonts" && detail["ticket"] === currentFontStart.ticket);
    const adoptionIndex = perfMarkMessageIndex(harness.messages, "mm-virt-maintenance-terminal", detail =>
      detail["owner"] === "measured-height-adoption", mutationIndex + 1);
    const endIndex = perfMarkMessageIndex(harness.messages, "mm-virt-geometry-work-end", detail =>
      detail["source"] === "window-fonts" && detail["ticket"] === currentFontStart.ticket);
    expect(mutationIndex).toBeGreaterThanOrEqual(0);
    expect(adoptionIndex).toBeGreaterThan(mutationIndex);
    expect(endIndex).toBeGreaterThan(adoptionIndex);
  });

  it("stale window font completion cannot mutate the next document", async () => {
    let resolveFonts!: () => void;
    const fontsReady = new Promise<void>(resolve => { resolveFonts = resolve; });
    const harness = await loadRendererHarness({
      fontsReady,
      sectionCount: 20,
      virtualization: true,
    });
    document.dispatchEvent(new Event("DOMContentLoaded"));
    harness.load({ type: "load-document", html: buildHeadingDocument(20), renderId: 82 });
    await harness.flushRafsUntil(() => perfDetails<{
      source?: string;
    }>(harness.messages, "mm-virt-geometry-work-start")
      .some(detail => detail.source === "window-fonts"), 20);
    const staleFontTicket = perfDetails<{
      documentEpoch?: number;
      mountGeneration?: number;
      source?: string;
      ticket?: number;
    }>(harness.messages, "mm-virt-geometry-work-start")
      .filter(detail => detail.source === "window-fonts")
      .at(-1)!;
    harness.load({ type: "load-document", html: buildHeadingDocument(12), renderId: 83 });
    resolveFonts();
    await Promise.resolve();
    await harness.flushQueuedRafs();
    const fontMutations = perfDetails<{
      documentEpoch?: number;
      mountGeneration?: number;
      source?: string;
      ticket?: number;
    }>(harness.messages, "mm-virt-geometry-mutated")
      .filter(detail => detail.source === "window-fonts");
    expect(fontMutations.filter(detail =>
      detail.documentEpoch === staleFontTicket.documentEpoch
      && detail.mountGeneration === staleFontTicket.mountGeneration
      && detail.ticket === staleFontTicket.ticket)).toEqual([]);
    expect(fontMutations.length).toBeGreaterThan(0);
    expect(fontMutations.every(detail => detail.documentEpoch !== staleFontTicket.documentEpoch)).toBe(true);
  });

  it("stale same-document window math completion cannot mutate after remount", async () => {
    let resolveOldMath!: () => void;
    const oldMathReady = new Promise<void>(resolve => { resolveOldMath = resolve; });
    const harness = await loadRendererHarness({
      mathReadiness: [Promise.resolve(), oldMathReady, Promise.resolve()],
      sectionCount: 120,
      virtualization: true,
    });
    harness.load({
      type: "load-document",
      html: buildClonePollutionDocument(120),
      hasMermaid: false,
      hasHljs: false,
      renderId: 84,
    });
    await harness.flushRafsUntil(() => perfDetails<{
      source?: string;
    }>(harness.messages, "mm-virt-geometry-work-start")
      .some(detail => detail.source === "window-math"), 20);
    const oldMathTicket = perfDetails<{
      mountGeneration?: number;
      source?: string;
      ticket?: number;
    }>(harness.messages, "mm-virt-geometry-work-start")
      .filter(detail => detail.source === "window-math")
      .at(-1)!;

    harness.load({ type: "scroll-to-block", blockIndex: 90 });
    await harness.flushRafsUntil(() => perfDetails<{
      mountGeneration?: number;
      source?: string;
    }>(harness.messages, "mm-virt-geometry-work-start")
      .some(detail => detail.source === "window-math"
        && detail.mountGeneration !== oldMathTicket.mountGeneration), 40);
    const adoptionCountBeforeStaleCompletion = perfDetails(harness.messages, "mm-virt-geometry-work-start")
      .filter(detail => detail["source"] === "measured-height-adoption").length;

    resolveOldMath();
    for (let pass = 0; pass < 4; pass++) {
      await Promise.resolve();
    }

    expect(perfDetails(harness.messages, "mm-virt-geometry-mutated")
      .filter(detail => detail["ticket"] === oldMathTicket.ticket)).toEqual([]);
    expect(perfDetails(harness.messages, "mm-virt-geometry-work-end")
      .filter(detail => detail["ticket"] === oldMathTicket.ticket)).toHaveLength(1);
    expect(perfDetails(harness.messages, "mm-virt-geometry-work-start")
      .filter(detail => detail["source"] === "measured-height-adoption")).toHaveLength(
      adoptionCountBeforeStaleCompletion
    );
    await harness.flushQueuedRafs();
  });

  it("stale same-document lazy Mermaid completion cannot mutate after a zero-Mermaid remount", async () => {
    let resolveOldMermaid!: (value: { svg: string }) => void;
    const oldMermaidReady = new Promise<{ svg: string }>(resolve => { resolveOldMermaid = resolve; });
    const render = vi.fn(() => oldMermaidReady);
    const harness = await loadRendererHarness({
      intersectionObserverAvailable: false,
      sectionCount: 120,
      virtualization: true,
    });
    (window as unknown as {
      mermaid?: {
        initialize: (config: unknown) => void;
        render: typeof render;
      };
    }).mermaid = {
      initialize: vi.fn(),
      render,
    };
    harness.load({
      type: "load-document",
      html: buildLazyMermaidDocument(120, 40),
      hasMermaid: true,
      hasHljs: false,
      renderId: 85,
    });
    await harness.flushRafsUntil(() => render.mock.calls.length > 0 && perfDetails<{
      mountGeneration?: number;
      source?: string;
    }>(harness.messages, "mm-virt-geometry-work-start")
      .some(detail => detail.source === "lazy-mermaid" && (detail.mountGeneration ?? 0) > 0), 40);
    const oldMermaidTicket = perfDetails<{
      mountGeneration?: number;
      source?: string;
      ticket?: number;
    }>(harness.messages, "mm-virt-geometry-work-start")
      .filter(detail => detail.source === "lazy-mermaid" && (detail.mountGeneration ?? 0) > 0)
      .at(-1)!;

    harness.load({ type: "scroll-to-block", blockIndex: 90 });
    await harness.flushRafsUntil(() => perfDetails<{
      mountGeneration?: number;
      source?: string;
    }>(harness.messages, "mm-virt-geometry-work-start")
      .some(detail => detail.source === "window-render"
        && (detail.mountGeneration ?? 0) > (oldMermaidTicket.mountGeneration ?? 0)), 40);
    expect(document.querySelector("pre.mm-mermaid")).toBeNull();
    const adoptionCountBeforeStaleCompletion = perfDetails(harness.messages, "mm-virt-geometry-work-start")
      .filter(detail => detail["source"] === "measured-height-adoption").length;

    resolveOldMermaid({ svg: "<svg></svg>" });
    for (let pass = 0; pass < 6; pass++) {
      await Promise.resolve();
    }

    expect(perfDetails(harness.messages, "mm-virt-geometry-mutated")
      .filter(detail => detail["ticket"] === oldMermaidTicket.ticket)).toEqual([]);
    expect(perfDetails(harness.messages, "mm-virt-geometry-work-end")
      .filter(detail => detail["ticket"] === oldMermaidTicket.ticket)).toHaveLength(1);
    expect(perfDetails(harness.messages, "mm-virt-geometry-work-start")
      .filter(detail => detail["source"] === "measured-height-adoption")).toHaveLength(
      adoptionCountBeforeStaleCompletion
    );
    await harness.flushQueuedRafs();
    delete (window as unknown as { mermaid?: unknown }).mermaid;
  });

  it("H3 diagnostic fails an unregistered late mover", () => {
    const monitor = createH3DiagnosticMonitor(100);
    monitor.registerCausalEpoch(1);
    expect(() => monitor.sample(128, 1)).not.toThrow();
    expect(() => monitor.sample(156, 2)).toThrow("unregistered late geometry mover at epoch 2");
    monitor.dispose();
    expect(() => monitor.sample(200, 3)).not.toThrow();
  });

  it("registers leased calibration at schedule time and performs it once", async () => {
    const sectionCount = 120;
    const harness = await loadRendererHarness({ sectionCount, virtualization: true });
    document.dispatchEvent(new Event("DOMContentLoaded"));
    document.documentElement.style.setProperty("--mm-minimap-width", "136px");
    setMinimapViewportHeight(592);
    harness.load({ type: "reading-preferences", ...makeReadingPreferences("on") });
    loadMinimapPolicy(harness.load);
    harness.load({
      type: "load-document",
      html: buildHeadingDocument(sectionCount),
      hasMermaid: false,
      hasHljs: false,
      renderId: 31,
    });
    await harness.flushQueuedRafs();
    harness.messages.length = 0;

    const minimap = beginMinimapMaintenanceLease(harness);
    harness.setRenderedSectionHeight(SECTION_HEIGHT + 80);
    harness.triggerResize();
    await harness.flushRafsUntil(() => maintenanceRequestsForOwner(
      harness.messages,
      "calibration"
    ).length > 0, 80);
    const calibrationRequest = maintenanceRequestsForOwner(harness.messages, "calibration")[0]!;
    const calibrationStart = perfDetails<{
      source?: string;
      ticket?: number;
    }>(harness.messages, "mm-virt-geometry-work-start")
      .filter(detail => detail.source === "calibration")
      .at(-1)!;
    const ticketStartIndex = perfMarkMessageIndex(harness.messages, "mm-virt-geometry-work-start", detail =>
      detail["source"] === "calibration" && detail["ticket"] === calibrationStart.ticket);
    const requestIndex = perfMarkMessageIndex(harness.messages, "mm-virt-maintenance-requested", detail =>
      detail["requestSerial"] === calibrationRequest.requestSerial);
    expect(ticketStartIndex).toBeGreaterThanOrEqual(0);
    expect(requestIndex).toBeGreaterThan(ticketStartIndex);
    minimap.dispatchEvent(pointerEvent("pointerup", 12));
    await harness.flushQueuedRafs();

    expect(perfDetails(harness.messages, "mm-virt-window-calibrated")).toHaveLength(1);
    expectExactMaintenanceLifecycle(harness.messages, calibrationRequest, {
      executionCount: 1,
      reason: "delivered",
      status: "completed",
    });
    expect(perfDetails(harness.messages, "mm-virt-geometry-work-end")
      .filter(detail => detail["ticket"] === calibrationStart.ticket)).toHaveLength(1);
  });

  it.each(["user", "document", "teardown"] as const)(
    "terminalizes a retry-pending producer ticket exactly once on %s cancellation",
    async cancellation => {
      const harness = await loadMaintenanceLifecycleHarness();
      const request = await startMeasuredMaintenanceInState(harness, "retry-pending");
      const producerTicket = perfDetails<{
        source?: string;
        ticket?: number;
      }>(harness.messages, "mm-virt-geometry-work-start")
        .filter(detail => detail.source === "measured-height-adoption")
        .at(-1)!;
      const actionIndex = harness.messages.length;
      const reason = cancellation === "user"
        ? "user-supersession"
        : cancellation === "document"
          ? "stale-document"
          : "teardown";

      if (cancellation === "user") {
        harness.root.scrollTop += 17;
        document.dispatchEvent(new Event("scroll"));
      } else if (cancellation === "document") {
        harness.load({
          type: "load-document",
          html: buildHeadingDocument(12),
          hasMermaid: false,
          hasHljs: false,
        });
      } else {
        window.dispatchEvent(new Event("pagehide"));
      }

      expectExactMaintenanceLifecycle(harness.messages, request, {
        executionCount: 0,
        reason,
        status: "canceled",
      });
      const terminalIndex = perfMarkMessageIndex(harness.messages, "mm-virt-maintenance-terminal", detail =>
        detail["requestSerial"] === request.requestSerial, actionIndex);
      const ticketEndIndex = perfMarkMessageIndex(harness.messages, "mm-virt-geometry-work-end", detail =>
        detail["ticket"] === producerTicket.ticket, actionIndex);
      expect(terminalIndex).toBeGreaterThanOrEqual(actionIndex);
      expect(ticketEndIndex).toBeGreaterThan(terminalIndex);
      expect(perfDetails(harness.messages, "mm-virt-geometry-work-end")
        .filter(detail => detail["ticket"] === producerTicket.ticket)).toHaveLength(1);

      const lifecycleCount = maintenanceEventsForRequest(harness.messages, request).length;
      const ticketEndCount = perfDetails(harness.messages, "mm-virt-geometry-work-end")
        .filter(detail => detail["ticket"] === producerTicket.ticket).length;
      await harness.flushQueuedRafs();
      expect(maintenanceEventsForRequest(harness.messages, request)).toHaveLength(lifecycleCount);
      expect(perfDetails(harness.messages, "mm-virt-geometry-work-end")
        .filter(detail => detail["ticket"] === producerTicket.ticket)).toHaveLength(ticketEndCount);
    }
  );

  it("clicks and drags the model-fragment minimap clone to off-window sections", async () => {
    const sectionCount = 120;
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
    await flushQueuedRafs();

    const clickedScrollTop = root.scrollTop;
    const clickedHighest = highestRenderedHeadingIndex();
    expect(clickedScrollTop).toBeGreaterThan(0);
    expect(clickedHighest).toBeGreaterThan(25);
    expectCommittedWriterFamilyOwned(messages, "minimap-", "minimap-gesture");

    root.scrollTop = 0;
    document.dispatchEvent(new Event("scroll"));
    await flushQueuedRafs();

    expect(highestRenderedHeadingIndex()).toBeLessThan(sectionCount - 25);
    minimap!.dispatchEvent(pointerEvent("pointerdown", 12));
    minimap!.dispatchEvent(pointerEvent("pointermove", 588));
    await flushQueuedRafs();

    expect(root.scrollTop).toBeGreaterThanOrEqual(clickedScrollTop);
    expect(highestRenderedHeadingIndex()).toBeGreaterThanOrEqual(clickedHighest);
  });
});
