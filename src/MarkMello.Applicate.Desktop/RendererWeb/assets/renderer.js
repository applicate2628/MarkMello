"use strict";
(() => {
  // RendererWeb/src/minimapState.ts
  var DEFAULT_MINIMAP_POST_EPSILON = 0.5;
  function shouldPostMinimapState(previous, next, force = false, epsilon = DEFAULT_MINIMAP_POST_EPSILON) {
    if (force || !previous.hasPosted || previous.visible !== next.visible) {
      return true;
    }
    return Math.abs(next.reservedWidth - previous.reservedWidth) >= epsilon;
  }

  // RendererWeb/src/minimapLayout.ts
  var DEFAULT_MINIMUM_THUMB_HEIGHT = 22;
  function calculateMinimapViewportLayout(input) {
    if (input.minimapWidth <= 0 || input.minimapHeight <= 0 || input.documentWidth <= 0 || input.documentHeight <= 0 || input.viewportHeight <= 0) {
      return null;
    }
    const minimumThumbHeight = input.minimumThumbHeight ?? DEFAULT_MINIMUM_THUMB_HEIGHT;
    const scale = input.minimapWidth / input.documentWidth;
    const projectedDocumentHeight = input.documentHeight * scale;
    const maximumScrollTop = Math.max(0, input.documentHeight - input.viewportHeight);
    const scrollProgress = maximumScrollTop > 0 ? Math.max(0, Math.min(1, input.scrollTop / maximumScrollTop)) : 0;
    const overflowHeight = Math.max(0, projectedDocumentHeight - input.minimapHeight);
    const contentTranslateY = overflowHeight > 0 ? -scrollProgress * overflowHeight : 0;
    const thumbHeight = Math.min(
      input.minimapHeight,
      Math.max(minimumThumbHeight, input.viewportHeight * scale)
    );
    const rawThumbTop = input.scrollTop * scale + contentTranslateY;
    const thumbTop = Math.max(0, Math.min(input.minimapHeight - thumbHeight, rawThumbTop));
    return {
      contentWidth: input.documentWidth,
      scale,
      contentTranslateY,
      transform: `translateY(${contentTranslateY}px) scale(${scale})`,
      thumbTop,
      thumbHeight
    };
  }

  // RendererWeb/src/widthResizerVisibility.ts
  function normalizeWidthResizerVisibility(raw) {
    return raw === "always" ? "always" : "on-hover";
  }
  function getWidthResizerVisibilityClasses(visibility) {
    return {
      alwaysClass: visibility === "always"
    };
  }

  // RendererWeb/src/mermaidRender.ts
  async function renderMermaidNode(node, generation, getCurrentGeneration, mermaid, perDiagramTimeoutMs) {
    const codeEl = node.querySelector("code[data-mm-mermaid]");
    if (!codeEl) return;
    const source = codeEl.textContent ?? "";
    let timeoutHandle;
    try {
      const id = `mm-mermaid-${generation}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
      const timeoutPromise = new Promise((_, reject) => {
        timeoutHandle = setTimeout(() => reject(new Error("mermaid render timeout")), perDiagramTimeoutMs);
      });
      const { svg } = await Promise.race([mermaid.render(id, source), timeoutPromise]);
      if (getCurrentGeneration() !== generation) return;
      let svgHost = node.nextElementSibling;
      if (!svgHost || !svgHost.classList.contains("mm-mermaid-svg")) {
        svgHost = document.createElement("div");
        svgHost.className = "mm-mermaid-svg";
        node.after(svgHost);
      }
      svgHost.innerHTML = svg;
      node.classList.add("is-rendered");
    } catch {
      if (getCurrentGeneration() !== generation) return;
      node.classList.remove("is-rendered");
      const sibling = node.nextElementSibling;
      if (sibling?.classList.contains("mm-mermaid-svg")) sibling.remove();
    } finally {
      if (timeoutHandle !== void 0) clearTimeout(timeoutHandle);
    }
  }

  // RendererWeb/src/hljsLanguage.ts
  var ALIASES = {
    js: "javascript",
    ts: "typescript",
    py: "python",
    rb: "ruby",
    sh: "bash",
    ps1: "powershell",
    rs: "rust",
    cs: "csharp",
    kt: "kotlin"
  };
  function normalizeHljsLanguage(name) {
    if (!name) return "plaintext";
    const lower = name.toLowerCase();
    return ALIASES[lower] ?? lower;
  }

  // RendererWeb/src/initialRenderPipeline.ts
  async function runInitialRenderPipeline(deps) {
    const theme = deps.getCurrentTheme();
    deps.applyTheme(theme);
    deps.initMermaidWithTheme(theme);
    const mathController = deps.renderMath();
    try {
      await deps.renderMermaid();
    } catch {
    }
    deps.renderCodeBlocks();
    await mathController.initialVisibleReady;
    deps.scheduleLayoutReady();
  }

  // RendererWeb/src/performanceMarks.ts
  var state = {
    marks: [],
    pendingStarts: /* @__PURE__ */ new Map(),
    longTasks: [],
    scrollIpcCount: 0,
    mathRenderCount: 0,
    queueSlices: [],
    fpsSessions: {}
  };
  var hasPerformanceApi = typeof performance !== "undefined" && typeof performance.now === "function";
  function emitMark(name, detail) {
    if (!hasPerformanceApi) return;
    const mark = detail !== void 0 ? { name, startTime: performance.now(), duration: 0, detail } : { name, startTime: performance.now(), duration: 0 };
    state.marks.push(mark);
  }
  function recordScrollIpc() {
    state.scrollIpcCount++;
    emitMark("mm-scroll-ipc");
  }
  function recordQueueSlice(name, durationMs, tasksCompleted) {
    state.queueSlices.push({ name, durationMs, tasksCompleted });
  }
  function getReport() {
    return {
      marks: [...state.marks],
      longTasks: [...state.longTasks],
      scrollIpcCount: state.scrollIpcCount,
      mathRenderCount: state.mathRenderCount,
      queueSlices: [...state.queueSlices],
      fpsSessions: { ...state.fpsSessions }
    };
  }
  function installLongTaskObserver() {
    if (typeof PerformanceObserver === "undefined") return () => {
    };
    try {
      const observer = new PerformanceObserver((list) => {
        for (const entry of list.getEntries()) {
          state.longTasks.push(entry);
        }
      });
      observer.observe({ entryTypes: ["longtask"] });
      return () => observer.disconnect();
    } catch {
      emitMark("mm-longtask-observer-unsupported");
      return () => {
      };
    }
  }
  var currentSampler = null;
  function getFpsSampler() {
    return {
      start(key) {
        if (currentSampler?.running) currentSampler.running = false;
        currentSampler = {
          key,
          deltas: [],
          lastTime: 0,
          rafId: 0,
          running: true
        };
        const tick = (t) => {
          if (!currentSampler || !currentSampler.running) return;
          if (currentSampler.lastTime > 0) {
            currentSampler.deltas.push(t - currentSampler.lastTime);
          }
          currentSampler.lastTime = t;
          currentSampler.rafId = requestAnimationFrame(tick);
        };
        currentSampler.rafId = requestAnimationFrame(tick);
      },
      stop() {
        if (!currentSampler) {
          return { minFps: 0, p50: 0, p95: 0, sampleCount: 0 };
        }
        currentSampler.running = false;
        cancelAnimationFrame(currentSampler.rafId);
        const fps = currentSampler.deltas.map((d) => d > 0 ? 1e3 / d : 0).sort((a, b) => a - b);
        const session = {
          minFps: fps[0] ?? 0,
          p50: fps[Math.floor(fps.length * 0.5)] ?? 0,
          p95: fps[Math.floor(fps.length * 0.95)] ?? 0,
          sampleCount: fps.length
        };
        state.fpsSessions[currentSampler.key] = session;
        currentSampler = null;
        return session;
      }
    };
  }

  // RendererWeb/src/mathRenderQueue.ts
  function isTerminalMathState(state2) {
    return state2 === "true" || state2 === "failed";
  }
  var MathRenderQueue = class {
    constructor(deps) {
      this.deps = deps;
      this.high = [];
      this.low = [];
      this.inQueue = /* @__PURE__ */ new Map();
      this.taskListeners = /* @__PURE__ */ new Set();
      this.cancelled = false;
      this.processing = false;
      this.idlePromise = null;
      this.idleResolver = null;
      this.sliceCounter = 0;
    }
    enqueue(task, priority) {
      if (this.cancelled) return;
      if (isTerminalMathState(task.node.dataset["mmMathRendered"])) return;
      const existing = this.inQueue.get(task.node);
      if (existing) {
        if (priority === "high" && existing.priority === "low") {
          const idx = this.low.indexOf(existing);
          if (idx >= 0) this.low.splice(idx, 1);
          existing.priority = "high";
          this.high.push(existing);
        }
        return;
      }
      const entry = { task, priority };
      this.inQueue.set(task.node, entry);
      if (priority === "high") this.high.push(entry);
      else this.low.push(entry);
      this.kick();
    }
    start() {
      if (!this.idlePromise) {
        this.idlePromise = new Promise((resolve) => {
          this.idleResolver = resolve;
        });
      }
      const promise = this.idlePromise;
      if (this.high.length + this.low.length === 0 && !this.processing) {
        this.resolveIdle();
        return promise;
      }
      this.kick();
      return promise;
    }
    kick() {
      if (this.processing || this.cancelled) return;
      if (this.high.length + this.low.length === 0) return;
      this.processing = true;
      void this.processLoop();
    }
    async processLoop() {
      try {
        await this.deps.yield();
        while (!this.cancelled && this.high.length + this.low.length > 0) {
          const frameStart = this.deps.now();
          const budget = this.deps.timeBudgetMs ?? 7;
          let tasksCompleted = 0;
          while (!this.cancelled && this.high.length + this.low.length > 0) {
            const entry = this.high.length > 0 ? this.high.shift() : this.low.shift();
            this.inQueue.delete(entry.task.node);
            if (isTerminalMathState(entry.task.node.dataset["mmMathRendered"])) {
              continue;
            }
            try {
              this.deps.katex.render(entry.task.tex, entry.task.node, {
                throwOnError: false,
                displayMode: entry.task.displayMode,
                strict: "warn",
                trust: false
              });
              entry.task.node.dataset["mmMathRendered"] = "true";
              if (entry.task.displayMode) {
                entry.task.node.style.minHeight = "";
              }
            } catch (e) {
              entry.task.node.dataset["mmMathRendered"] = "failed";
              emitMark("mm-render-math-fail", { tex: entry.task.tex, error: String(e) });
            } finally {
              tasksCompleted++;
              for (const listener of this.taskListeners) listener(entry.task.node);
            }
            if (this.deps.now() - frameStart > budget) break;
          }
          const sliceName = `mm-queue-slice-${this.sliceCounter++}`;
          const sliceDurationMs = this.deps.now() - frameStart;
          emitMark(sliceName, { tasksCompleted, durationMs: sliceDurationMs });
          recordQueueSlice(sliceName, sliceDurationMs, tasksCompleted);
          if (!this.cancelled && this.high.length + this.low.length > 0) {
            await this.deps.yield();
          }
        }
      } finally {
        this.processing = false;
        this.resolveIdle();
      }
    }
    resolveIdle() {
      if (this.idleResolver) {
        const r = this.idleResolver;
        this.idleResolver = null;
        this.idlePromise = null;
        r();
      }
    }
    cancel() {
      this.cancelled = true;
      this.high.length = 0;
      this.low.length = 0;
      this.inQueue.clear();
      if (!this.processing) this.resolveIdle();
    }
    isProcessing() {
      return this.processing;
    }
    size() {
      return { high: this.high.length, low: this.low.length };
    }
    onTaskComplete(listener) {
      this.taskListeners.add(listener);
      return () => {
        this.taskListeners.delete(listener);
      };
    }
  };

  // RendererWeb/src/mathRenderInit.ts
  var INITIAL_LOOKAHEAD_PX = 500;
  function complexityScore(tex) {
    let score = 1;
    score += (tex.match(/\\frac/g)?.length ?? 0) * 2;
    score += (tex.match(/\\sum/g)?.length ?? 0) * 2;
    score += (tex.match(/\\int/g)?.length ?? 0) * 2;
    score += (tex.match(/\\\\/g)?.length ?? 0) * 3;
    return score;
  }
  function reserveMathPlaceholder(node) {
    if (!node.classList.contains("math-display")) return;
    const tex = node.dataset["tex"] ?? "";
    const score = complexityScore(tex);
    const minHeight = Math.max(28, 28 * Math.ceil(score / 5));
    node.style.minHeight = `${minHeight}px`;
  }
  function getVisibilityElement(node) {
    if (node.classList.contains("math-inline")) {
      return node.parentElement ?? node;
    }
    return node;
  }
  function rafYield() {
    return new Promise((r) => window.requestAnimationFrame(() => r()));
  }
  function renderMath(deps) {
    const mathNodes = Array.from(deps.documentRoot.querySelectorAll("[data-tex]"));
    const katex = deps.katex;
    if (!katex || mathNodes.length === 0) {
      return {
        initialVisibleReady: Promise.resolve(),
        allMathRendered: Promise.resolve(),
        cancel: () => {
        },
        initialVisibleNodes: /* @__PURE__ */ new Set(),
        totalMathCount: mathNodes.length,
        isCancelled: () => false
      };
    }
    mathNodes.forEach(reserveMathPlaceholder);
    const queue = new MathRenderQueue({
      katex,
      timeBudgetMs: 7,
      now: () => performance.now(),
      yield: rafYield
    });
    const viewportHeight = window.innerHeight;
    const initialVisibleNodes = /* @__PURE__ */ new Set();
    for (const node of mathNodes) {
      const visEl = getVisibilityElement(node);
      const rect = visEl.getBoundingClientRect();
      const tex = node.dataset["tex"] ?? "";
      const task = {
        node,
        tex,
        displayMode: node.classList.contains("math-display")
      };
      if (rect.bottom >= -INITIAL_LOOKAHEAD_PX && rect.top <= viewportHeight + INITIAL_LOOKAHEAD_PX) {
        initialVisibleNodes.add(node);
        queue.enqueue(task, "high");
      } else {
        queue.enqueue(task, "low");
      }
    }
    let initialPending = initialVisibleNodes.size;
    const initialVisibleReady = new Promise((resolve) => {
      if (initialPending === 0) {
        resolve();
        return;
      }
      const unsubscribe = queue.onTaskComplete((node) => {
        if (initialVisibleNodes.has(node)) {
          initialPending--;
          if (initialPending === 0) {
            unsubscribe();
            resolve();
          }
        }
      });
    });
    const allMathRendered = queue.start();
    const observedToMathNodes = /* @__PURE__ */ new Map();
    for (const node of mathNodes) {
      const visEl = getVisibilityElement(node);
      const bucket = observedToMathNodes.get(visEl) ?? [];
      bucket.push(node);
      observedToMathNodes.set(visEl, bucket);
    }
    const observer = new IntersectionObserver((entries) => {
      for (const entry of entries) {
        const visEl = entry.target;
        const targets = observedToMathNodes.get(visEl);
        if (!targets) continue;
        for (const targetNode of targets) {
          if (isTerminalMathState(targetNode.dataset["mmMathRendered"])) continue;
          const tex = targetNode.dataset["tex"] ?? "";
          const task = {
            node: targetNode,
            tex,
            displayMode: targetNode.classList.contains("math-display")
          };
          queue.enqueue(task, entry.isIntersecting ? "high" : "low");
        }
      }
    }, { rootMargin: `${INITIAL_LOOKAHEAD_PX}px` });
    for (const visEl of observedToMathNodes.keys()) {
      observer.observe(visEl);
    }
    let cancelled = false;
    return {
      initialVisibleReady,
      allMathRendered,
      cancel: () => {
        cancelled = true;
        observer.disconnect();
        queue.cancel();
      },
      initialVisibleNodes,
      totalMathCount: mathNodes.length,
      isCancelled: () => cancelled
    };
  }

  // RendererWeb/src/schematicMinimap.ts
  function shouldTriggerPhaseB(currentHeight, cachedHeight) {
    if (cachedHeight <= 0) return false;
    return Math.abs(currentHeight - cachedHeight) >= 1;
  }
  function schedulePhaseBRebuild(deps) {
    deps.allMathRendered.then(() => {
      if (shouldTriggerPhaseB(deps.getCurrentDocumentHeight(), deps.getCachedDocumentHeight())) {
        deps.refresh("B");
      }
    });
  }

  // RendererWeb/src/scrollCoalescer.ts
  function createScrollCoalescer(deps) {
    let pending = false;
    return function queuePostScroll2() {
      if (pending) return;
      pending = true;
      deps.schedule(() => {
        pending = false;
        deps.postScroll();
      });
    };
  }

  // RendererWeb/src/renderer.ts
  var hostWindow = window;
  var MINIMAP_CLASS = "mm-minimap";
  var MINIMAP_VIEWPORT_CLASS = "mm-minimap-viewport";
  var MINIMAP_VISIBLE_CLASS = "mm-has-minimap";
  var MINIMAP_REFRESH_DEBOUNCE_MS = 100;
  var WIDTH_HANDLE_CLASS = "mm-width-handle";
  var WIDTH_HANDLE_DRAGGING_CLASS = "mm-dragging";
  var WIDTH_RESIZER_ALWAYS_CLASS = "mm-width-resizer-always";
  var minimapMode = "off";
  var hasReceivedHostPreferences = false;
  var minimapViewportFrameRequested = false;
  var minimapRefreshTimer;
  var minimapRoot = null;
  var minimapContent = null;
  var minimapViewport = null;
  var currentMinimapLayout = null;
  var minimapDragging = false;
  var minimapSourceReady = false;
  var mermaidRenderGeneration = 0;
  var initialRenderPipelineCompleted = false;
  var currentController = null;
  var MAX_MERMAID_DIAGRAMS = 50;
  var MERMAID_PER_DIAGRAM_TIMEOUT_MS = 3e3;
  var MERMAID_WATCHDOG_MS = 15e3;
  var widthResizerVisibility = "on-hover";
  var viewerChromeEnabled = false;
  var documentScrollEnabled = true;
  var wheelProxyEnabled = false;
  var widthHandleRoot = null;
  var widthHandleDragging = false;
  var widthHandleStartClientX = 0;
  var widthHandleStartMaxWidth = 0;
  var pendingWidthDragDeltaX = 0;
  var widthDragFrameRequested = false;
  var layoutReadyGeneration = 0;
  var layoutReadyTimer;
  var lastPostedMinimapState = { hasPosted: false, visible: false, reservedWidth: 0 };
  var minimapPolicy = {
    // Mirrors ApplicateDocumentMinimapBuildPolicy until the host sends minimap-policy.
    // WebView uses CSS scrollHeight while Native uses Avalonia visual height; keep
    // this shared value intentionally permissive until WebView-specific tuning exists.
    minHostWidth: 1100,
    minScrollableViewportRatio: 1.5,
    maxDetailedDocumentHeight: 24e4
  };
  function applyViewerChromeState() {
    document.documentElement.dataset.mmChrome = viewerChromeEnabled ? "on" : "off";
  }
  function applyDocumentScrollState() {
    document.documentElement.dataset.mmDocumentScroll = documentScrollEnabled ? "on" : "off";
    if (!documentScrollEnabled) {
      window.scrollTo({ left: 0, top: 0, behavior: "instant" });
    }
  }
  function postHostMessage(message) {
    const serialized = JSON.stringify(message);
    if (hostWindow.chrome?.webview) {
      hostWindow.chrome.webview.postMessage(message);
      return;
    }
    hostWindow.invokeCSharpAction?.(serialized);
  }
  function countFailedInSet(nodes) {
    let count = 0;
    for (const node of nodes) {
      if (node.dataset["mmMathRendered"] === "failed") count++;
    }
    return count;
  }
  function renderMath2() {
    emitMark("mm-render-math-start", { mathCount: document.querySelectorAll("[data-tex]").length });
    const katex = hostWindow.katex ?? void 0;
    const controller = renderMath({ katex, documentRoot: document });
    currentController = controller;
    schedulePhaseBRebuild({
      allMathRendered: controller.allMathRendered,
      getCurrentDocumentHeight: () => (document.scrollingElement ?? document.documentElement).scrollHeight,
      getCachedDocumentHeight: () => minimapDocumentHeight,
      refresh: refreshMinimapContent
    });
    controller.initialVisibleReady.then(() => {
      emitMark("mm-initial-visible-ready", {
        visibleCount: controller.initialVisibleNodes.size,
        failedCount: countFailedInSet(controller.initialVisibleNodes)
      });
      refreshMinimapContent("A");
    });
    controller.allMathRendered.then(() => {
      const allMathNodes = Array.from(document.querySelectorAll("[data-tex]"));
      emitMark("mm-all-math-rendered", {
        totalCount: controller.totalMathCount,
        failedCount: countFailedInSet(allMathNodes),
        cancelled: controller.isCancelled()
      });
    });
    return controller;
  }
  function getCurrentTheme() {
    return document.documentElement.dataset.theme === "dark" ? "dark" : "light";
  }
  function applyTheme(theme) {
    document.documentElement.dataset.theme = theme;
  }
  function initMermaidWithTheme(theme) {
    hostWindow.mermaid?.initialize({
      startOnLoad: false,
      theme: theme === "dark" ? "dark" : "default",
      securityLevel: "strict",
      maxTextSize: 1e5
    });
  }
  async function renderMermaid() {
    const mermaid = hostWindow.mermaid;
    if (!mermaid) return;
    const allNodes = Array.from(document.querySelectorAll("pre.mm-mermaid"));
    const nodes = allNodes.slice(0, MAX_MERMAID_DIAGRAMS);
    if (nodes.length === 0) return;
    const generation = ++mermaidRenderGeneration;
    const watchdog = window.setTimeout(() => {
      if (generation === mermaidRenderGeneration) {
        ++mermaidRenderGeneration;
      }
    }, MERMAID_WATCHDOG_MS);
    try {
      for (const node of nodes) {
        await renderMermaidNode(node, generation, () => mermaidRenderGeneration, mermaid, MERMAID_PER_DIAGRAM_TIMEOUT_MS);
        if (generation !== mermaidRenderGeneration) return;
      }
    } finally {
      window.clearTimeout(watchdog);
    }
  }
  function renderCodeBlocks() {
    const hljs = hostWindow.hljs;
    if (!hljs) return;
    const nodes = Array.from(document.querySelectorAll("code[data-mm-code], code[data-mm-mermaid]"));
    for (const node of nodes) {
      const langClass = Array.from(node.classList).find((c) => c.startsWith("language-"));
      const rawLang = langClass?.slice("language-".length);
      const normalized = normalizeHljsLanguage(rawLang);
      if (!hljs.getLanguage(normalized)) continue;
      if (langClass && langClass !== `language-${normalized}`) {
        node.classList.remove(langClass);
        node.classList.add(`language-${normalized}`);
      }
      try {
        hljs.highlightElement(node);
      } catch {
      }
    }
  }
  async function handleThemeChange(theme) {
    applyTheme(theme);
    initMermaidWithTheme(theme);
    await renderMermaid();
  }
  function getScrollState() {
    const root = document.scrollingElement ?? document.documentElement;
    return {
      scrollTop: root.scrollTop,
      scrollHeight: root.scrollHeight,
      clientHeight: root.clientHeight
    };
  }
  function findTopVisibleBlockIndex() {
    const elements = document.querySelectorAll("[data-mm-block-index]");
    if (elements.length === 0) return null;
    const viewportTop = 0;
    for (const el of Array.from(elements)) {
      const rect = el.getBoundingClientRect();
      if (rect.bottom >= viewportTop) {
        const raw = el.dataset["mmBlockIndex"];
        const parsed = raw === void 0 ? Number.NaN : Number.parseInt(raw, 10);
        return Number.isFinite(parsed) ? parsed : null;
      }
    }
    const lastRaw = elements[elements.length - 1].dataset["mmBlockIndex"];
    const lastParsed = lastRaw === void 0 ? Number.NaN : Number.parseInt(lastRaw, 10);
    return Number.isFinite(lastParsed) ? lastParsed : null;
  }
  function postScroll() {
    recordScrollIpc();
    postHostMessage({
      type: "scroll",
      ...getScrollState(),
      topBlockIndex: findTopVisibleBlockIndex()
    });
  }
  function postLayoutReady() {
    postScroll();
    postHostMessage({
      type: "layout-ready",
      ...getScrollState()
    });
    document.querySelector("main.mm-document")?.classList.add("mm-rendered");
    document.querySelector("aside.mm-minimap")?.classList.add("mm-rendered");
    document.querySelector("div.mm-width-handle")?.classList.add("mm-rendered");
  }
  function scheduleLayoutReady() {
    const generation = ++layoutReadyGeneration;
    let completed = false;
    if (layoutReadyTimer !== void 0) {
      window.clearTimeout(layoutReadyTimer);
    }
    const complete = () => {
      if (completed || generation !== layoutReadyGeneration) {
        return;
      }
      completed = true;
      if (layoutReadyTimer !== void 0) {
        window.clearTimeout(layoutReadyTimer);
        layoutReadyTimer = void 0;
      }
      window.requestAnimationFrame(() => {
        window.requestAnimationFrame(() => {
          if (generation === layoutReadyGeneration) {
            postLayoutReady();
          }
        });
      });
    };
    layoutReadyTimer = window.setTimeout(complete, 250);
    document.fonts?.ready.then(complete).catch(complete);
  }
  function readRootPixelVariable(name, fallback) {
    const raw = getComputedStyle(document.documentElement).getPropertyValue(name);
    const parsed = Number.parseFloat(raw);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
  }
  function ensureWidthHandle() {
    if (widthHandleRoot) {
      return;
    }
    widthHandleRoot = document.createElement("div");
    widthHandleRoot.className = WIDTH_HANDLE_CLASS;
    widthHandleRoot.hidden = true;
    widthHandleRoot.setAttribute("aria-hidden", "true");
    const track = document.createElement("div");
    track.className = "mm-width-handle-track";
    widthHandleRoot.append(track);
    document.body.append(widthHandleRoot);
    widthHandleRoot.addEventListener("pointerdown", handleWidthHandlePointerDown);
    widthHandleRoot.addEventListener("pointermove", handleWidthHandlePointerMove);
    widthHandleRoot.addEventListener("pointerup", handleWidthHandlePointerUp);
    widthHandleRoot.addEventListener("pointercancel", handleWidthHandlePointerUp);
    widthHandleRoot.addEventListener("lostpointercapture", handleWidthHandlePointerCaptureLost);
    window.addEventListener("pointerup", handleWidthHandlePointerUp, true);
    window.addEventListener("pointercancel", handleWidthHandlePointerUp, true);
    window.addEventListener("blur", cancelWidthHandleDrag);
  }
  function updateWidthHandlePosition() {
    ensureWidthHandle();
    if (!widthHandleRoot) {
      return;
    }
    widthHandleRoot.hidden = !hasReceivedHostPreferences || !viewerChromeEnabled;
    if (widthHandleRoot.hidden) {
      return;
    }
    const documentElement = document.querySelector(".mm-document");
    if (!documentElement) {
      widthHandleRoot.hidden = true;
      return;
    }
    const hitArea = readRootPixelVariable("--mm-width-handle-hit-area", 24);
    const minimapReservedWidth = getCurrentMinimapReservedWidth();
    const documentRect = documentElement.getBoundingClientRect();
    const trackGraceRight = 4;
    const idealHandleLeft = documentRect.right + trackGraceRight;
    const minimapLeftEdge = window.innerWidth - minimapReservedWidth;
    const maxLeftBeforeMinimap = Math.max(0, minimapLeftEdge - hitArea);
    const clampedLeft = Math.max(0, Math.min(maxLeftBeforeMinimap, idealHandleLeft));
    widthHandleRoot.style.left = `${Math.round(clampedLeft)}px`;
  }
  function postWidthDragMove() {
    if (widthDragFrameRequested) {
      return;
    }
    widthDragFrameRequested = true;
    window.requestAnimationFrame(() => {
      widthDragFrameRequested = false;
      postHostMessage({ type: "width-drag", phase: "move", deltaX: pendingWidthDragDeltaX });
    });
  }
  function handleWidthHandlePointerDown(event) {
    if (event.button !== 0 || !widthHandleRoot) {
      return;
    }
    widthHandleDragging = true;
    widthHandleStartClientX = event.clientX;
    pendingWidthDragDeltaX = 0;
    const inlineMaxWidth = parseFloat(
      document.documentElement.style.getPropertyValue("--mm-document-max-width")
    );
    widthHandleStartMaxWidth = Number.isFinite(inlineMaxWidth) && inlineMaxWidth > 0 ? inlineMaxWidth : lastAppliedReadingPreferences?.maxWidth ?? 720;
    widthHandleRoot.classList.add(WIDTH_HANDLE_DRAGGING_CLASS);
    widthHandleRoot.setPointerCapture(event.pointerId);
    postHostMessage({ type: "width-drag", phase: "start", deltaX: 0 });
    event.preventDefault();
  }
  function handleWidthHandlePointerMove(event) {
    if (!widthHandleDragging) {
      return;
    }
    pendingWidthDragDeltaX = event.clientX - widthHandleStartClientX;
    const previewMaxWidth = Math.max(200, widthHandleStartMaxWidth + 2 * pendingWidthDragDeltaX);
    document.documentElement.style.setProperty("--mm-document-max-width", `${previewMaxWidth}px`);
    updateWidthHandlePosition();
    postWidthDragMove();
    event.preventDefault();
  }
  function handleWidthHandlePointerUp(event) {
    if (!widthHandleDragging) {
      return;
    }
    const deltaX = event.clientX - widthHandleStartClientX;
    widthHandleDragging = false;
    widthHandleRoot?.classList.remove(WIDTH_HANDLE_DRAGGING_CLASS);
    try {
      widthHandleRoot?.releasePointerCapture(event.pointerId);
    } catch {
    }
    updateWidthHandlePosition();
    postHostMessage({ type: "width-drag", phase: "end", deltaX });
    event.preventDefault();
  }
  function handleWidthHandlePointerCaptureLost() {
    cancelWidthHandleDrag();
  }
  function cancelWidthHandleDrag() {
    if (!widthHandleDragging) {
      return;
    }
    widthHandleDragging = false;
    widthHandleRoot?.classList.remove(WIDTH_HANDLE_DRAGGING_CLASS);
    postHostMessage({ type: "width-drag", phase: "end", deltaX: pendingWidthDragDeltaX });
  }
  function ensureMinimap() {
    if (minimapRoot) {
      return;
    }
    minimapRoot = document.createElement("aside");
    minimapRoot.className = MINIMAP_CLASS;
    minimapRoot.setAttribute("aria-hidden", "true");
    minimapContent = document.createElement("div");
    minimapContent.className = "mm-minimap-content";
    minimapViewport = document.createElement("div");
    minimapViewport.className = MINIMAP_VIEWPORT_CLASS;
    minimapRoot.append(minimapContent, minimapViewport);
    document.body.append(minimapRoot);
    minimapRoot.addEventListener("pointerdown", handleMinimapPointerDown);
    minimapRoot.addEventListener("pointermove", handleMinimapPointerMove);
    minimapRoot.addEventListener("pointerup", handleMinimapPointerUp);
    minimapRoot.addEventListener("pointercancel", handleMinimapPointerUp);
  }
  var minimapDocumentHeight = 0;
  function cloneDocumentForMinimap() {
    const source = document.querySelector(".mm-document");
    if (!source) {
      minimapSourceReady = false;
      return null;
    }
    const clone = source.cloneNode(true);
    minimapSourceReady = true;
    clone.removeAttribute("id");
    clone.setAttribute("aria-hidden", "true");
    clone.inert = true;
    clone.querySelectorAll("[id]").forEach((node) => node.removeAttribute("id"));
    clone.querySelectorAll("*").forEach((node) => {
      for (const attribute of Array.from(node.attributes)) {
        if (attribute.name === "role" || attribute.name === "name" || attribute.name === "for" || attribute.name.startsWith("aria-") && attribute.name !== "aria-hidden") {
          node.removeAttribute(attribute.name);
        }
      }
    });
    clone.querySelectorAll("a, button, input, textarea, select").forEach((node) => {
      node.setAttribute("tabindex", "-1");
      node.removeAttribute("href");
    });
    return clone;
  }
  function refreshMinimapContent(phase = "A") {
    emitMark("mm-minimap-refresh-start", { phase });
    ensureMinimap();
    if (!minimapContent || !minimapRoot) {
      emitMark("mm-minimap-refresh-end", { phase, skipped: "no-mount" });
      return;
    }
    const clone = cloneDocumentForMinimap();
    if (!clone) {
      emitMark("mm-minimap-refresh-end", { phase, skipped: "no-source" });
      return;
    }
    const root = document.scrollingElement ?? document.documentElement;
    minimapDocumentHeight = root.scrollHeight;
    minimapContent.replaceChildren(clone);
    updateMinimapVisibility(true);
    updateMinimapViewport();
    emitMark("mm-minimap-refresh-end", { phase, documentHeight: minimapDocumentHeight });
  }
  function shouldShowMinimap() {
    const root = document.scrollingElement ?? document.documentElement;
    const documentHeight = root.scrollHeight;
    const viewportHeight = root.clientHeight;
    if (!hasReceivedHostPreferences || !viewerChromeEnabled || !minimapSourceReady || minimapMode === "off" || viewportHeight <= 0 || documentHeight <= viewportHeight) {
      return false;
    }
    if (documentHeight > minimapPolicy.maxDetailedDocumentHeight) {
      return false;
    }
    if (minimapMode === "on") {
      return true;
    }
    return window.innerWidth >= minimapPolicy.minHostWidth && documentHeight >= viewportHeight * minimapPolicy.minScrollableViewportRatio;
  }
  function updateMinimapVisibility(forcePostState = false) {
    ensureMinimap();
    if (!minimapRoot) {
      return;
    }
    const visible = shouldShowMinimap();
    minimapRoot.hidden = !visible;
    document.body.classList.toggle(MINIMAP_VISIBLE_CLASS, visible);
    postMinimapState(visible, forcePostState);
    updateWidthHandlePosition();
  }
  function getCurrentMinimapReservedWidth() {
    if (!minimapRoot || minimapRoot.hidden) {
      return 0;
    }
    const minimapWidth = minimapRoot.getBoundingClientRect().width || readRootPixelVariable("--mm-minimap-width", 0);
    const minimapGap = readRootPixelVariable("--mm-minimap-gap", 0);
    return Math.max(0, minimapWidth + minimapGap * 2);
  }
  function postMinimapState(visible, force = false) {
    const reservedWidth = visible ? getCurrentMinimapReservedWidth() : 0;
    const nextState = { visible, reservedWidth };
    if (!shouldPostMinimapState(lastPostedMinimapState, nextState, force)) {
      return;
    }
    lastPostedMinimapState = { ...nextState, hasPosted: true };
    postHostMessage({ type: "minimap-state", visible, reservedWidth });
  }
  function updateMinimapViewport() {
    ensureMinimap();
    if (!minimapRoot || !minimapContent || !minimapViewport) {
      return;
    }
    const root = document.scrollingElement ?? document.documentElement;
    const source = document.querySelector(".mm-document");
    if (!source) {
      return;
    }
    const sourceStyle = getComputedStyle(source);
    const sourcePaddingLeft = Number.parseFloat(sourceStyle.paddingLeft) || 0;
    const sourcePaddingRight = Number.parseFloat(sourceStyle.paddingRight) || 0;
    const minimapHeight = minimapRoot.clientHeight;
    const minimapWidth = minimapRoot.clientWidth;
    const documentHeight = root.scrollHeight;
    const documentWidth = Math.max(
      source.scrollWidth - sourcePaddingLeft - sourcePaddingRight,
      source.clientWidth - sourcePaddingLeft - sourcePaddingRight,
      1
    );
    const viewportHeight = root.clientHeight;
    if (minimapHeight <= 0 || minimapWidth <= 0 || documentHeight <= 0 || viewportHeight <= 0) {
      return;
    }
    const layout = calculateMinimapViewportLayout({
      minimapWidth,
      minimapHeight,
      documentWidth,
      documentHeight,
      viewportHeight,
      scrollTop: root.scrollTop
    });
    if (!layout) {
      currentMinimapLayout = null;
      return;
    }
    currentMinimapLayout = layout;
    minimapContent.style.transform = layout.transform;
    minimapContent.style.width = `${layout.contentWidth}px`;
    minimapViewport.style.transform = `translateY(${layout.thumbTop}px)`;
    minimapViewport.style.height = `${layout.thumbHeight}px`;
  }
  function scrollFromMinimapClientY(clientY) {
    if (!minimapRoot) {
      return;
    }
    const root = document.scrollingElement ?? document.documentElement;
    const rect = minimapRoot.getBoundingClientRect();
    const minimapY = Math.max(0, Math.min(rect.height, clientY - rect.top));
    const documentY = currentMinimapLayout ? (minimapY - currentMinimapLayout.contentTranslateY) / currentMinimapLayout.scale : minimapY / Math.max(1, rect.height) * root.scrollHeight;
    const target = documentY - root.clientHeight / 2;
    const maximum = Math.max(0, root.scrollHeight - root.clientHeight);
    window.scrollTo({ top: Math.max(0, Math.min(maximum, target)), behavior: "instant" });
  }
  function scrollToProgress(progressPercent) {
    const root = document.scrollingElement ?? document.documentElement;
    const maximum = Math.max(0, root.scrollHeight - root.clientHeight);
    const progress = Number.isFinite(progressPercent) ? Math.max(0, Math.min(100, progressPercent)) : 0;
    window.scrollTo({ top: maximum * (progress / 100), behavior: "instant" });
  }
  function handleMinimapPointerDown(event) {
    minimapDragging = true;
    minimapRoot?.setPointerCapture(event.pointerId);
    scrollFromMinimapClientY(event.clientY);
    event.preventDefault();
  }
  function handleMinimapPointerMove(event) {
    if (!minimapDragging) {
      return;
    }
    scrollFromMinimapClientY(event.clientY);
    event.preventDefault();
  }
  function handleMinimapPointerUp(event) {
    minimapDragging = false;
    try {
      minimapRoot?.releasePointerCapture(event.pointerId);
    } catch {
    }
  }
  function queueMinimapViewportUpdate() {
    if (minimapViewportFrameRequested) {
      return;
    }
    minimapViewportFrameRequested = true;
    window.requestAnimationFrame(() => {
      minimapViewportFrameRequested = false;
      updateMinimapVisibility();
      updateMinimapViewport();
    });
  }
  function queueMinimapRefreshAfterLayoutSettles() {
    window.clearTimeout(minimapRefreshTimer);
    minimapRefreshTimer = window.setTimeout(() => {
      queueMinimapViewportUpdate();
    }, MINIMAP_REFRESH_DEBOUNCE_MS);
  }
  var lastAppliedReadingPreferences = null;
  var pendingReadingPreferences = null;
  var applyPrefsFrameRequested = false;
  var heavyLiveUpdateTimer;
  var HEAVY_LIVE_UPDATE_DEBOUNCE_MS = 80;
  function normalizeFontFamilyMode(value) {
    if (value === "sans" || value === "mono") return value;
    return "serif";
  }
  function applyReadingPreferences(message) {
    pendingReadingPreferences = {
      fontFamily: normalizeFontFamilyMode(message.fontFamily),
      fontSize: message.fontSize,
      lineHeight: message.lineHeight,
      maxWidth: message.maxWidth,
      minimapMode: message.minimapMode,
      viewerChromeEnabled: message.viewerChromeEnabled ?? true,
      documentScrollEnabled: message.documentScrollEnabled ?? true,
      wheelProxyEnabled: message.wheelProxyEnabled ?? false,
      widthResizerVisibility: normalizeWidthResizerVisibility(message.widthResizerVisibility)
    };
    if (applyPrefsFrameRequested) return;
    applyPrefsFrameRequested = true;
    requestAnimationFrame(flushPendingReadingPreferences);
  }
  function flushPendingReadingPreferences() {
    applyPrefsFrameRequested = false;
    const next = pendingReadingPreferences;
    pendingReadingPreferences = null;
    if (!next) return;
    const prev = lastAppliedReadingPreferences;
    const fontFamilyChanged = !prev || prev.fontFamily !== next.fontFamily;
    const fontSizeChanged = !prev || prev.fontSize !== next.fontSize;
    const lineHeightChanged = !prev || prev.lineHeight !== next.lineHeight;
    const maxWidthChanged = !prev || prev.maxWidth !== next.maxWidth;
    const minimapModeChanged = !prev || prev.minimapMode !== next.minimapMode;
    const viewerChromeChanged = !prev || prev.viewerChromeEnabled !== next.viewerChromeEnabled;
    const documentScrollChanged = !prev || prev.documentScrollEnabled !== next.documentScrollEnabled;
    const wheelProxyChanged = !prev || prev.wheelProxyEnabled !== next.wheelProxyEnabled;
    const widthResizerVisibilityChanged = !prev || prev.widthResizerVisibility !== next.widthResizerVisibility;
    const root = document.documentElement;
    if (fontFamilyChanged) root.dataset.mmFontFamily = next.fontFamily;
    if (fontSizeChanged) root.style.setProperty("--mm-document-font-size", `${next.fontSize}px`);
    if (lineHeightChanged) root.style.setProperty("--mm-document-line-height", `${next.lineHeight}`);
    if (maxWidthChanged && !widthHandleDragging) {
      root.style.setProperty("--mm-document-max-width", `${next.maxWidth}px`);
    }
    if (minimapModeChanged) minimapMode = next.minimapMode;
    if (viewerChromeChanged) {
      viewerChromeEnabled = next.viewerChromeEnabled;
      applyViewerChromeState();
    }
    if (documentScrollChanged) {
      documentScrollEnabled = next.documentScrollEnabled;
      applyDocumentScrollState();
    }
    if (wheelProxyChanged) {
      wheelProxyEnabled = next.wheelProxyEnabled;
    }
    if (widthResizerVisibilityChanged) {
      widthResizerVisibility = next.widthResizerVisibility;
      const widthResizerClasses = getWidthResizerVisibilityClasses(widthResizerVisibility);
      document.body.classList.toggle(WIDTH_RESIZER_ALWAYS_CLASS, widthResizerClasses.alwaysClass);
    }
    const hadHostPreferences = hasReceivedHostPreferences;
    hasReceivedHostPreferences = true;
    lastAppliedReadingPreferences = next;
    if (maxWidthChanged || viewerChromeChanged || widthResizerVisibilityChanged) {
      updateWidthHandlePosition();
    }
    const layoutAffectingChange = fontFamilyChanged || fontSizeChanged || lineHeightChanged || maxWidthChanged || minimapModeChanged || viewerChromeChanged;
    if (layoutAffectingChange) {
      scheduleHeavyLiveUpdate();
    }
    if (!hadHostPreferences && !initialRenderPipelineCompleted) {
      void runInitialRenderPipeline({
        getCurrentTheme,
        applyTheme,
        initMermaidWithTheme,
        renderMath: renderMath2,
        renderMermaid,
        renderCodeBlocks,
        scheduleLayoutReady: () => {
          initialRenderPipelineCompleted = true;
          scheduleLayoutReady();
        }
      });
    }
  }
  function scheduleHeavyLiveUpdate() {
    if (heavyLiveUpdateTimer !== void 0) {
      window.clearTimeout(heavyLiveUpdateTimer);
    }
    heavyLiveUpdateTimer = window.setTimeout(() => {
      heavyLiveUpdateTimer = void 0;
      queueMinimapViewportUpdate();
    }, HEAVY_LIVE_UPDATE_DEBOUNCE_MS);
  }
  function handleHostMessage(raw) {
    const message = raw;
    if (message.type === "theme") {
      if (initialRenderPipelineCompleted) {
        void handleThemeChange(message.theme);
      } else {
        document.documentElement.dataset.theme = message.theme;
      }
      return;
    }
    if (message.type === "minimap-policy") {
      minimapPolicy = message.minimapPolicy;
      queueMinimapViewportUpdate();
      return;
    }
    if (message.type === "reading-preferences") {
      applyReadingPreferences(message);
      return;
    }
    if (message.type === "scroll-to") {
      document.getElementById(message.anchor)?.scrollIntoView({ block: "start" });
      return;
    }
    if (message.type === "scroll-to-progress") {
      scrollToProgress(message.progressPercent);
      return;
    }
    if (message.type === "scroll-by") {
      window.scrollBy({ top: message.deltaY, behavior: "instant" });
      return;
    }
    if (message.type === "scroll-to-block") {
      const target = document.querySelector(
        `[data-mm-block-index="${message.blockIndex}"]`
      );
      if (target) {
        target.scrollIntoView({ block: "start", behavior: "instant" });
      }
    }
  }
  function wireLinks() {
    document.addEventListener("click", (event) => {
      const target = event.target instanceof Element ? event.target.closest("a[href]") : null;
      if (!target) {
        return;
      }
      event.preventDefault();
      postHostMessage({
        // `target.href` is the absolute URI the browser resolved against the
        // generated HTML's base URL (which lives in the OS temp folder). For
        // relative markdown links like `[doc](other.md)` this hides the
        // actual relative path inside a temp-folder file:// URI — useless to
        // the host because the host needs to resolve against the ORIGINAL
        // markdown source directory. Send the raw attribute value too so
        // the host can pick the right one for resolution.
        type: "link-clicked",
        href: target.getAttribute("href") ?? target.href,
        button: event.button,
        ctrlKey: event.ctrlKey,
        shiftKey: event.shiftKey,
        altKey: event.altKey,
        metaKey: event.metaKey
      });
    });
  }
  function wireViewerInteraction() {
    document.addEventListener("pointerdown", (event) => {
      if (event.button === 0) {
        postHostMessage({ type: "viewer-interaction" });
      }
    }, true);
  }
  function wireWheelProxy() {
    document.addEventListener("wheel", (event) => {
      if (!wheelProxyEnabled) {
        return;
      }
      if (Math.abs(event.deltaY) <= Number.EPSILON || Math.abs(event.deltaX) > Math.abs(event.deltaY)) {
        return;
      }
      postHostMessage({
        type: "wheel",
        deltaY: event.deltaY,
        deltaMode: event.deltaMode
      });
      event.preventDefault();
    }, { capture: true, passive: false });
  }
  var MARKDOWN_EXTENSIONS = [".md", ".markdown", ".mdown", ".markdn"];
  var DROP_OVERLAY_ID = "mm-drop-overlay";
  var DROP_OVERLAY_TEXT = "Drop your Markdown file to open";
  var dropDragCounter = 0;
  function isFileDrag(event) {
    const types = event.dataTransfer?.types;
    if (!types) return false;
    for (let i = 0; i < types.length; i++) {
      if (types[i] === "Files") return true;
    }
    return false;
  }
  function isMarkdownFileName(name) {
    const lower = name.toLowerCase();
    return MARKDOWN_EXTENSIONS.some((ext) => lower.endsWith(ext));
  }
  function ensureDropOverlay() {
    const existing = document.getElementById(DROP_OVERLAY_ID);
    if (existing) return existing;
    const node = document.createElement("div");
    node.id = DROP_OVERLAY_ID;
    node.className = "mm-drop-overlay";
    node.textContent = DROP_OVERLAY_TEXT;
    (document.body ?? document.documentElement).appendChild(node);
    return node;
  }
  function setDropOverlayVisible(visible) {
    const node = ensureDropOverlay();
    if (visible) {
      node.setAttribute("data-visible", "true");
    } else {
      node.removeAttribute("data-visible");
    }
  }
  function wireFileDrop() {
    document.addEventListener("dragenter", (event) => {
      if (!isFileDrag(event)) return;
      event.preventDefault();
      dropDragCounter++;
      if (dropDragCounter === 1) {
        setDropOverlayVisible(true);
        postHostMessage({ type: "drag-hover", hovering: true });
      }
    });
    document.addEventListener("dragover", (event) => {
      if (!isFileDrag(event)) return;
      event.preventDefault();
      if (event.dataTransfer) {
        event.dataTransfer.dropEffect = "copy";
      }
    });
    document.addEventListener("dragleave", (event) => {
      if (!isFileDrag(event)) return;
      dropDragCounter--;
      if (dropDragCounter <= 0) {
        dropDragCounter = 0;
        setDropOverlayVisible(false);
        postHostMessage({ type: "drag-hover", hovering: false });
      }
    });
    document.addEventListener("drop", async (event) => {
      if (!isFileDrag(event)) return;
      event.preventDefault();
      dropDragCounter = 0;
      setDropOverlayVisible(false);
      postHostMessage({ type: "drag-hover", hovering: false });
      const files = event.dataTransfer?.files;
      if (!files || files.length === 0) return;
      for (let i = 0; i < files.length; i++) {
        const file = files.item(i);
        if (file && isMarkdownFileName(file.name)) {
          try {
            const text = await file.text();
            postHostMessage({ type: "drop-file", name: file.name, text });
          } catch {
          }
          return;
        }
      }
    });
  }
  document.addEventListener("securitypolicyviolation", (e) => {
    postHostMessage({
      type: "csp-violation",
      blockedURI: (e.blockedURI ?? "").substring(0, 200),
      violatedDirective: (e.violatedDirective ?? "").substring(0, 200),
      sourceFile: (e.sourceFile ?? "").substring(0, 200),
      lineNumber: e.lineNumber ?? 0,
      columnNumber: e.columnNumber ?? 0
    });
  });
  document.addEventListener("DOMContentLoaded", () => {
    emitMark("mm-doc-loaded");
    requestAnimationFrame(() => emitMark("mm-doc-painted"));
    installLongTaskObserver();
    applyViewerChromeState();
    applyDocumentScrollState();
    wireLinks();
    wireViewerInteraction();
    wireWheelProxy();
    wireFileDrop();
    postHostMessage({
      type: "document-ready",
      mathCount: document.querySelectorAll("[data-tex]").length
    });
    postScroll();
    const documentElement = document.querySelector(".mm-document");
    if (documentElement) {
      const resizeObserver = new ResizeObserver(() => {
        queueMinimapRefreshAfterLayoutSettles();
        updateWidthHandlePosition();
        window.requestAnimationFrame(postScroll);
      });
      resizeObserver.observe(documentElement);
    }
    document.fonts?.ready.then(() => queueMinimapRefreshAfterLayoutSettles()).catch(() => void 0);
  });
  var queuePostScroll = createScrollCoalescer({
    postScroll: () => {
      postScroll();
      queueMinimapViewportUpdate();
    },
    schedule: (cb) => {
      window.requestAnimationFrame(cb);
    }
  });
  document.addEventListener("scroll", () => {
    queuePostScroll();
  }, { passive: true });
  window.addEventListener("message", (event) => handleHostMessage(event.data));
  window.addEventListener("resize", () => {
    updateWidthHandlePosition();
    queueMinimapViewportUpdate();
  });
  window.__mmPerfReport = getReport;
  window.__mmFpsSampler = getFpsSampler();
  window.__mmRendererState = {
    get initialVisibleReady() {
      return currentController?.initialVisibleReady ?? Promise.resolve();
    },
    get allMathRendered() {
      return currentController?.allMathRendered ?? Promise.resolve();
    }
  };
})();
