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
  function calculateMinimapDocumentWidth(input) {
    const width = input.borderBoxWidth - input.paddingLeft - input.paddingRight;
    return Number.isFinite(width) && width > 0 ? width : 1;
  }
  function calculateMinimapViewportLayout(input) {
    if (input.minimapWidth <= 0 || input.minimapHeight <= 0 || input.documentWidth <= 0 || input.documentHeight <= 0 || input.viewportHeight <= 0) {
      return null;
    }
    const minimumThumbHeight = input.minimumThumbHeight ?? DEFAULT_MINIMUM_THUMB_HEIGHT;
    const scale = Math.min(1, input.minimapWidth / input.documentWidth);
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
    const maximumRawThumbTop = Math.max(0, maximumScrollTop * scale - overflowHeight);
    const maximumClampedThumbTop = Math.max(0, input.minimapHeight - thumbHeight);
    const thumbTravel = Math.min(maximumClampedThumbTop, maximumRawThumbTop);
    const thumbTop = Math.max(0, Math.min(thumbTravel, rawThumbTop));
    const thumbSlope = maximumScrollTop > 0 ? scale - overflowHeight / maximumScrollTop : scale;
    return {
      contentWidth: input.documentWidth,
      scale,
      contentTranslateY,
      transform: `translateY(${contentTranslateY}px) scale(${scale})`,
      thumbTop,
      thumbHeight,
      thumbTravel,
      thumbSlope
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
  function isMermaidNodeNearViewport(node, viewportHeight, marginPx) {
    const rect = node.getBoundingClientRect();
    return rect.bottom >= -marginPx && rect.top <= viewportHeight + marginPx;
  }
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
  var DEFAULT_INITIAL_VISIBLE_READY_TIMEOUT_MS = 1200;
  var DEFAULT_INITIAL_VISUAL_SETTLE_TIMEOUT_MS = 1800;
  function deferPostReadyWork(deps, work) {
    if (deps.deferPostReadyWork) {
      deps.deferPostReadyWork(work);
      return;
    }
    globalThis.setTimeout(work, 0);
  }
  function isCurrentPipeline(deps) {
    return deps.isCurrent?.() !== false;
  }
  async function runPostReadyEnhancements(deps, shouldRunMermaid) {
    if (!isCurrentPipeline(deps)) return;
    deps.postPerfMark?.("post-ready-enhancements-start", { hasMermaid: shouldRunMermaid });
    try {
      if (shouldRunMermaid) {
        try {
          await deps.renderMermaid();
        } catch {
        }
      }
      if (!isCurrentPipeline(deps)) return;
      deps.renderCodeBlocks();
    } catch {
      deps.postPerfMark?.("post-ready-enhancements-error");
    } finally {
      if (isCurrentPipeline(deps)) {
        deps.postPerfMark?.("post-ready-enhancements-end", { hasMermaid: shouldRunMermaid });
      }
    }
  }
  async function waitForInitialVisibleReady(deps, mathController) {
    const timeoutMs = deps.initialVisibleReadyTimeoutMs ?? DEFAULT_INITIAL_VISIBLE_READY_TIMEOUT_MS;
    if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) {
      await mathController.initialVisibleReady;
      return;
    }
    let timeout;
    const result = await Promise.race([
      mathController.initialVisibleReady.then(() => "ready"),
      new Promise((resolve) => {
        timeout = globalThis.setTimeout(() => resolve("timeout"), timeoutMs);
      })
    ]);
    if (timeout !== void 0) {
      globalThis.clearTimeout(timeout);
    }
    if (result === "timeout") {
      deps.postPerfMark?.("initial-visible-ready-timeout", {
        timeoutMs,
        totalMathCount: mathController.totalMathCount,
        visibleCount: mathController.initialVisibleNodes.size
      });
    }
  }
  async function waitForInitialVisualSettle(deps, mathController) {
    const visualSettleReady = mathController.initialVisualSettleReady;
    if (!visualSettleReady) {
      return;
    }
    const timeoutMs = DEFAULT_INITIAL_VISUAL_SETTLE_TIMEOUT_MS;
    let timeout;
    const result = await Promise.race([
      visualSettleReady.then(() => "ready", () => "error"),
      new Promise((resolve) => {
        timeout = globalThis.setTimeout(() => resolve("timeout"), timeoutMs);
      })
    ]);
    if (timeout !== void 0) {
      globalThis.clearTimeout(timeout);
    }
    if (result === "timeout") {
      deps.postPerfMark?.("initial-visual-settle-timeout", {
        timeoutMs,
        totalMathCount: mathController.totalMathCount
      });
    } else if (result === "error") {
      deps.postPerfMark?.("initial-visual-settle-error", {
        totalMathCount: mathController.totalMathCount
      });
    }
  }
  async function runInitialRenderPipeline(deps) {
    const theme = deps.getCurrentTheme();
    deps.applyTheme(theme);
    const shouldRunMermaid = deps.hasMermaid !== false;
    if (shouldRunMermaid) {
      deps.initMermaidWithTheme(theme);
    } else {
      deps.postPerfMark?.("mermaid-skipped", { hasMermaid: false });
    }
    const mathController = deps.renderMath();
    await waitForInitialVisibleReady(deps, mathController);
    if (!isCurrentPipeline(deps)) return;
    deps.scheduleLayoutReady();
    deferPostReadyWork(deps, () => {
      if (!isCurrentPipeline(deps)) return;
      void runPostReadyEnhancements(deps, shouldRunMermaid).then(() => {
        if (!isCurrentPipeline(deps)) return;
        deps.notifyPostReadyEnhancementsComplete?.();
        return waitForInitialVisualSettle(deps, mathController);
      });
    });
  }

  // RendererWeb/src/loadDocument.ts
  function applyLoadDocument(message, deps) {
    const main = document.querySelector("main.mm-document");
    if (!main) {
      return;
    }
    deps.emitMark("mm-load-document", {
      documentName: message.documentName ?? "",
      htmlLength: message.html?.length ?? 0,
      renderId: message.renderId ?? null
    });
    deps.debugLog(`load-document:start id=${message.renderId ?? "(none)"} name=${message.documentName ?? ""} theme=${message.theme ?? "(none)"} currentTheme=${document.documentElement.dataset.theme ?? "(none)"} htmlLength=${message.html?.length ?? 0}`);
    const restoreOnly = message.html === void 0;
    const isProgressiveInitial = message.cacheKey === null;
    let cachedFragment = restoreOnly && message.cacheKey ? deps.getCachedDocumentFragment?.(message.cacheKey) : void 0;
    if (cachedFragment === void 0 && restoreOnly) {
      deps.emitMark("mm-load-document-cache-miss", {
        documentName: message.documentName ?? "",
        renderId: message.renderId ?? null
      });
      deps.notifyDocumentCacheMiss?.(message.renderId, message.cacheKey ?? void 0);
      return;
    }
    deps.preserveCurrentDocumentCache?.();
    deps.cancelCurrentMathController();
    deps.resetModuleGlobals();
    if (message.theme) {
      deps.applyTheme(message.theme);
    }
    if (!restoreOnly && message.cacheKey) {
      cachedFragment = deps.getCachedDocumentFragment?.(message.cacheKey);
    }
    if (cachedFragment !== void 0) {
      deps.emitMark("mm-load-document-cache-hit", {
        documentName: message.documentName ?? "",
        nodeCount: cachedFragment.childNodes.length,
        renderId: message.renderId ?? null
      });
    }
    if (cachedFragment !== void 0) {
      main.replaceChildren(cachedFragment);
    } else {
      main.innerHTML = message.html ?? "";
    }
    deps.setCurrentDocumentCacheKey?.(message.cacheKey ?? null);
    const firstHeading = main.querySelector("h1,h2,h3")?.textContent?.trim().replace(/\s+/g, " ").slice(0, 120) ?? "";
    deps.debugLog(`load-document:swapped id=${message.renderId ?? "(none)"} name=${message.documentName ?? ""} theme=${document.documentElement.dataset.theme ?? "(none)"} firstHeading=${firstHeading}`);
    deps.ensureChromeNodes(cachedFragment !== void 0, { allowVirtualization: !isProgressiveInitial });
    if (cachedFragment !== void 0) {
      deps.restoreCachedScrollPosition?.();
    } else {
      deps.scrollWindowToTop();
    }
    if (cachedFragment !== void 0 && deps.completeCachedDocumentLoad) {
      deps.completeCachedDocumentLoad(message.renderId, message.hasMermaid, message.hasHljs, message.skipFrameWait);
      return;
    }
    void deps.runInitialRenderPipeline(
      message.hasMermaid,
      message.skipFrameWait,
      message.renderId,
      message.hasHljs,
      !isProgressiveInitial
    );
  }
  function clearDocumentState(deps) {
    const main = document.querySelector("main.mm-document");
    deps.emitMark("mm-clear-document");
    deps.debugLog("clear-document");
    deps.cancelCurrentMathController();
    deps.resetModuleGlobals();
    deps.setCurrentDocumentCacheKey?.(null);
    if (main) {
      main.innerHTML = "";
    }
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
  var MATH_RENDER_FRAME_FALLBACK_MS = 32;
  var INITIAL_PAST_VIEWPORT_SCAN_LIMIT = 8;
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
    return new Promise((resolve) => {
      let resolved = false;
      let timeout;
      const finish = () => {
        if (resolved) return;
        resolved = true;
        if (timeout !== void 0) {
          window.clearTimeout(timeout);
        }
        resolve();
      };
      if (typeof window.requestAnimationFrame === "function") {
        timeout = window.setTimeout(finish, MATH_RENDER_FRAME_FALLBACK_MS);
        window.requestAnimationFrame(finish);
        return;
      }
      timeout = window.setTimeout(finish, 0);
    });
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
    mathNodes.filter((node) => !isTerminalMathState(node.dataset["mmMathRendered"])).forEach(reserveMathPlaceholder);
    const queue = new MathRenderQueue({
      katex,
      timeBudgetMs: 7,
      now: () => performance.now(),
      yield: rafYield
    });
    const viewportHeight = window.innerHeight;
    const initialVisibleNodes = /* @__PURE__ */ new Set();
    const rectCache = /* @__PURE__ */ new Map();
    let stopMeasuringInitialVisibility = false;
    let consecutivePastViewportElements = 0;
    let lastMeasuredVisibilityElement = null;
    const readRect = (element) => {
      const cached = rectCache.get(element);
      if (cached) return cached;
      const rect = element.getBoundingClientRect();
      rectCache.set(element, rect);
      return rect;
    };
    for (const node of mathNodes) {
      if (isTerminalMathState(node.dataset["mmMathRendered"])) {
        continue;
      }
      const visEl = getVisibilityElement(node);
      const tex = node.dataset["tex"] ?? "";
      const task = {
        node,
        tex,
        displayMode: node.classList.contains("math-display")
      };
      if (stopMeasuringInitialVisibility) {
        queue.enqueue(task, "low");
        continue;
      }
      const rect = readRect(visEl);
      if (rect.bottom >= -INITIAL_LOOKAHEAD_PX && rect.top <= viewportHeight + INITIAL_LOOKAHEAD_PX) {
        initialVisibleNodes.add(node);
        queue.enqueue(task, "high");
        consecutivePastViewportElements = 0;
      } else {
        queue.enqueue(task, "low");
        if (visEl !== lastMeasuredVisibilityElement) {
          lastMeasuredVisibilityElement = visEl;
          if (rect.top > viewportHeight + INITIAL_LOOKAHEAD_PX) {
            consecutivePastViewportElements++;
            if (consecutivePastViewportElements >= INITIAL_PAST_VIEWPORT_SCAN_LIMIT) {
              stopMeasuringInitialVisibility = true;
            }
          } else {
            consecutivePastViewportElements = 0;
          }
        }
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
  var PHASE_B_HEIGHT_DELTA_THRESHOLD_PX = 100;
  function shouldTriggerPhaseB(currentHeight, cachedHeight) {
    if (cachedHeight <= 0) return false;
    return Math.abs(currentHeight - cachedHeight) >= PHASE_B_HEIGHT_DELTA_THRESHOLD_PX;
  }
  function schedulePhaseBRebuild(deps) {
    return deps.allMathRendered.then(() => {
      if (!shouldTriggerPhaseB(deps.getCurrentDocumentHeight(), deps.getCachedDocumentHeight())) {
        return;
      }
      const win = window;
      return new Promise((resolve) => {
        const refresh = () => {
          deps.refresh("B");
          resolve();
        };
        if (typeof win.requestIdleCallback === "function") {
          win.requestIdleCallback(refresh, { timeout: 500 });
        } else {
          window.setTimeout(refresh, 50);
        }
      });
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

  // RendererWeb/src/widthHandleLayout.ts
  function clampWidthHandleLeft(input) {
    const hitArea = Math.max(0, input.hitArea);
    const minimapReservedWidth = Math.max(0, input.minimapReservedWidth);
    const viewportWidth = Math.max(0, input.viewportWidth);
    const minimapLeftEdge = viewportWidth - minimapReservedWidth;
    const maxLeftBeforeMinimap = Math.max(0, minimapLeftEdge - hitArea);
    return Math.max(0, Math.min(maxLeftBeforeMinimap, input.candidateLeft));
  }
  function calculateWidthHandleLeft(input) {
    const hitArea = Math.max(0, input.hitArea);
    const visibleTextRight = input.documentRight - Math.max(0, input.documentPaddingRight);
    const candidateLeft = visibleTextRight + hitArea;
    return clampWidthHandleLeft({
      candidateLeft,
      hitArea,
      minimapReservedWidth: input.minimapReservedWidth,
      viewportWidth: input.viewportWidth
    });
  }

  // RendererWeb/src/findBar.ts
  var FIND_BAR_CLASS = "mm-find-bar";
  var FIND_INPUT_CLASS = "mm-find-input";
  var FIND_COUNT_CLASS = "mm-find-count";
  var FIND_BTN_CLASS = "mm-find-btn";
  var FIND_DEBOUNCE_MS = 150;
  var HIGHLIGHT_ALL = "mm-find-all";
  var HIGHLIGHT_CURRENT = "mm-find-current";
  var SKIP_TAGS = /* @__PURE__ */ new Set(["SCRIPT", "STYLE", "NOSCRIPT", "ASIDE"]);
  var SKIP_CLASSES = /* @__PURE__ */ new Set([
    "mm-minimap",
    "mm-minimap-viewport",
    "mm-width-handle",
    "mm-drop-overlay",
    "katex-mathml",
    FIND_BAR_CLASS
  ]);
  var SKIP_SELECTOR = "pre.mm-mermaid.is-rendered";
  function getHighlightRegistry() {
    const css = window.CSS;
    return css?.highlights ?? null;
  }
  function makeHighlight(ranges) {
    const ctor = window.Highlight;
    if (ctor === void 0 || ranges.length === 0) {
      return null;
    }
    return new ctor(...ranges);
  }
  function findCaseInsensitiveMatchOffsets(haystack, needle) {
    const out = [];
    if (needle.length === 0) {
      return out;
    }
    const lowered = needle.toLowerCase();
    const text = haystack.toLowerCase();
    if (text.length !== haystack.length || text.length < lowered.length) {
      return out;
    }
    let idx = text.indexOf(lowered);
    while (idx !== -1) {
      out.push([idx, idx + lowered.length]);
      idx = text.indexOf(lowered, idx + lowered.length);
    }
    return out;
  }
  function buildMatches(root, needle) {
    const out = [];
    if (needle.length === 0) {
      return out;
    }
    const walker = document.createTreeWalker(
      root,
      NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT,
      {
        acceptNode(node) {
          if (node.nodeType === Node.ELEMENT_NODE) {
            const el = node;
            if (SKIP_TAGS.has(el.tagName)) {
              return NodeFilter.FILTER_REJECT;
            }
            for (const cls of SKIP_CLASSES) {
              if (el.classList.contains(cls)) {
                return NodeFilter.FILTER_REJECT;
              }
            }
            if (el.matches?.(SKIP_SELECTOR)) {
              return NodeFilter.FILTER_REJECT;
            }
            return NodeFilter.FILTER_SKIP;
          }
          return NodeFilter.FILTER_ACCEPT;
        }
      }
    );
    for (let cur = walker.nextNode(); cur !== null; cur = walker.nextNode()) {
      for (const [start, end] of findCaseInsensitiveMatchOffsets(cur.nodeValue ?? "", needle)) {
        const range = document.createRange();
        range.setStart(cur, start);
        range.setEnd(cur, end);
        out.push(range);
      }
    }
    return out;
  }
  function applyHighlights(s) {
    applyFindHighlights(s.matches, s.matches[s.currentIndex]);
  }
  function applyFindHighlights(ranges, currentRange) {
    const reg = getHighlightRegistry();
    if (reg === null) {
      return;
    }
    if (ranges.length === 0) {
      reg.delete(HIGHLIGHT_ALL);
      reg.delete(HIGHLIGHT_CURRENT);
      return;
    }
    const all = makeHighlight(ranges);
    if (all !== null) {
      reg.set(HIGHLIGHT_ALL, all);
    }
    if (currentRange !== void 0) {
      const current = makeHighlight([currentRange]);
      if (current !== null) {
        reg.set(HIGHLIGHT_CURRENT, current);
      }
    } else {
      reg.delete(HIGHLIGHT_CURRENT);
    }
  }
  function clearFindHighlights() {
    const reg = getHighlightRegistry();
    if (reg !== null) {
      reg.delete(HIGHLIGHT_ALL);
      reg.delete(HIGHLIGHT_CURRENT);
    }
  }
  function clearHighlights() {
    clearFindHighlights();
  }
  function rebuildMatches(s) {
    s.matches = buildMatches(document.body, s.lastSearched);
    s.matchesDirty = false;
    if (s.matches.length === 0) {
      s.currentIndex = -1;
    } else {
      s.currentIndex = Math.min(Math.max(s.currentIndex, 0), s.matches.length - 1);
    }
  }
  function ensureFresh(s) {
    if (s.matchesDirty) {
      rebuildMatches(s);
    }
  }
  function scrollToCurrent(s) {
    const range = s.matches[s.currentIndex];
    if (range === void 0) {
      return;
    }
    const host = range.startContainer.parentElement;
    const block = host?.closest("main.mm-document > *") ?? host;
    block?.scrollIntoView({ block: "center" });
    let attempts = 0;
    const reaim = () => {
      if (++attempts > 3) {
        return;
      }
      const rect = range.getBoundingClientRect();
      if (rect.height === 0 && rect.width === 0) {
        window.requestAnimationFrame(reaim);
        return;
      }
      const viewport = window.innerHeight || document.documentElement.clientHeight;
      if (rect.top < 0 || rect.bottom > viewport) {
        const target = window.scrollY + rect.top - viewport / 2 + rect.height / 2;
        window.scrollTo({ top: Math.max(0, target), behavior: "instant" });
        window.requestAnimationFrame(reaim);
      }
    };
    window.requestAnimationFrame(reaim);
  }
  function updateCountDisplay(s) {
    if (s.input.value.length === 0) {
      s.count.textContent = "";
      s.bar.classList.remove("mm-find-no-match");
      return;
    }
    if (s.matches.length === 0) {
      s.count.textContent = "0 of 0";
      s.bar.classList.add("mm-find-no-match");
      return;
    }
    s.bar.classList.remove("mm-find-no-match");
    s.count.textContent = `${s.currentIndex + 1} of ${s.matches.length}`;
  }
  function updateProviderCountDisplay(s, status) {
    if (s.input.value.length === 0 || status.query.length === 0) {
      s.count.textContent = "";
      s.bar.classList.remove("mm-find-no-match");
      return;
    }
    if (status.totalCount === 0) {
      s.count.textContent = "0 of 0";
      s.bar.classList.add("mm-find-no-match");
      return;
    }
    s.bar.classList.remove("mm-find-no-match");
    s.count.textContent = `${status.currentIndex >= 0 ? status.currentIndex + 1 : 0} of ${status.totalCount}`;
  }
  function runSearch(s, query) {
    s.lastSearched = query;
    if (query.length === 0) {
      s.matches = [];
      s.currentIndex = -1;
      s.matchesDirty = false;
      applyHighlights(s);
      updateCountDisplay(s);
      return;
    }
    s.matches = buildMatches(document.body, query);
    s.matchesDirty = false;
    s.currentIndex = s.matches.length > 0 ? 0 : -1;
    applyHighlights(s);
    if (s.currentIndex >= 0) {
      scrollToCurrent(s);
    }
    updateCountDisplay(s);
  }
  function navigate(s, direction) {
    ensureFresh(s);
    const n = s.matches.length;
    if (n === 0) {
      applyHighlights(s);
      updateCountDisplay(s);
      return;
    }
    if (s.currentIndex < 0) {
      s.currentIndex = direction === "next" ? 0 : n - 1;
    } else {
      s.currentIndex = (s.currentIndex + (direction === "next" ? 1 : -1) + n) % n;
    }
    applyHighlights(s);
    scrollToCurrent(s);
    updateCountDisplay(s);
  }
  function createFindBar(provider) {
    let state2 = null;
    function buildDom() {
      const bar = document.createElement("div");
      bar.className = FIND_BAR_CLASS;
      bar.setAttribute("role", "search");
      bar.setAttribute("aria-label", "Find in document");
      const input = document.createElement("input");
      input.type = "search";
      input.className = FIND_INPUT_CLASS;
      input.setAttribute("aria-label", "Find in document");
      input.placeholder = "Find in document";
      input.spellcheck = false;
      input.autocomplete = "off";
      const count = document.createElement("span");
      count.className = FIND_COUNT_CLASS;
      count.setAttribute("aria-live", "polite");
      count.textContent = "";
      const prevBtn = document.createElement("button");
      prevBtn.type = "button";
      prevBtn.className = `${FIND_BTN_CLASS} ${FIND_BTN_CLASS}-prev`;
      prevBtn.setAttribute("aria-label", "Previous match");
      prevBtn.title = "Previous match (Shift+Enter)";
      prevBtn.textContent = "\u2191";
      const nextBtn = document.createElement("button");
      nextBtn.type = "button";
      nextBtn.className = `${FIND_BTN_CLASS} ${FIND_BTN_CLASS}-next`;
      nextBtn.setAttribute("aria-label", "Next match");
      nextBtn.title = "Next match (Enter)";
      nextBtn.textContent = "\u2193";
      const closeBtn = document.createElement("button");
      closeBtn.type = "button";
      closeBtn.className = `${FIND_BTN_CLASS} ${FIND_BTN_CLASS}-close`;
      closeBtn.setAttribute("aria-label", "Close find bar");
      closeBtn.title = "Close (Esc)";
      closeBtn.textContent = "\xD7";
      bar.appendChild(input);
      bar.appendChild(count);
      bar.appendChild(prevBtn);
      bar.appendChild(nextBtn);
      bar.appendChild(closeBtn);
      return {
        bar,
        input,
        count,
        prevBtn,
        nextBtn,
        closeBtn,
        debounceTimer: null,
        lastSearched: "",
        matches: [],
        currentIndex: -1,
        matchesDirty: false,
        observer: null
      };
    }
    function connectObserver(s) {
      if (s.observer !== null) {
        return;
      }
      const main = document.querySelector("main.mm-document");
      if (main === null) {
        return;
      }
      s.observer = new MutationObserver(() => {
        s.matchesDirty = true;
      });
      s.observer.observe(main, { childList: true, subtree: true });
    }
    function attachListeners(s) {
      provider?.setView({
        updateStatus: (status) => updateProviderCountDisplay(s, status)
      });
      s.input.addEventListener("input", () => {
        const query = s.input.value;
        if (s.debounceTimer !== null) {
          window.clearTimeout(s.debounceTimer);
        }
        s.debounceTimer = window.setTimeout(() => {
          s.debounceTimer = null;
          if (provider) {
            provider.search(query);
          } else {
            runSearch(s, query);
          }
        }, FIND_DEBOUNCE_MS);
      });
      s.input.addEventListener("keydown", (event) => {
        if (event.key === "Enter") {
          event.preventDefault();
          if (s.debounceTimer !== null) {
            window.clearTimeout(s.debounceTimer);
            s.debounceTimer = null;
            if (provider) {
              provider.search(s.input.value);
            } else {
              runSearch(s, s.input.value);
            }
            return;
          }
          if (provider) {
            provider.navigate(event.shiftKey ? "prev" : "next");
          } else {
            navigate(s, event.shiftKey ? "prev" : "next");
          }
        } else if (event.key === "Escape") {
          event.preventDefault();
          event.stopPropagation();
          close();
        }
      });
      s.prevBtn.addEventListener("click", () => {
        if (provider) {
          provider.navigate("prev");
        } else {
          navigate(s, "prev");
        }
        s.input.focus();
      });
      s.nextBtn.addEventListener("click", () => {
        if (provider) {
          provider.navigate("next");
        } else {
          navigate(s, "next");
        }
        s.input.focus();
      });
      s.closeBtn.addEventListener("click", () => {
        close();
      });
    }
    function open() {
      if (state2 === null) {
        state2 = buildDom();
        attachListeners(state2);
        document.body.appendChild(state2.bar);
      } else if (state2.bar.parentNode === null) {
        document.body.appendChild(state2.bar);
      }
      state2.bar.classList.add("mm-find-bar-open");
      if (!provider) {
        connectObserver(state2);
      }
      state2.input.focus();
      state2.input.select();
    }
    function close() {
      if (state2 === null) {
        return;
      }
      if (state2.debounceTimer !== null) {
        window.clearTimeout(state2.debounceTimer);
        state2.debounceTimer = null;
      }
      if (state2.observer !== null) {
        state2.observer.disconnect();
        state2.observer = null;
      }
      state2.bar.classList.remove("mm-find-bar-open");
      state2.input.value = "";
      state2.lastSearched = "";
      state2.matches = [];
      state2.currentIndex = -1;
      state2.matchesDirty = false;
      state2.count.textContent = "";
      state2.bar.classList.remove("mm-find-no-match");
      provider?.close();
      clearHighlights();
    }
    function toggle() {
      if (state2 !== null && state2.bar.classList.contains("mm-find-bar-open")) {
        close();
      } else {
        open();
      }
    }
    return {
      open,
      close,
      toggle,
      get isOpen() {
        return state2 !== null && state2.bar.classList.contains("mm-find-bar-open");
      }
    };
  }

  // RendererWeb/src/windowTargetResolver.ts
  async function renderWindowTargetThenAct(input) {
    const model = input.model;
    const controller = input.controller;
    if (!input.virtualizationEnabled || model === null || controller === null) {
      return input.legacyAction();
    }
    const resolution = resolveWindowTarget(model, input.descriptor);
    if (resolution === null) {
      return input.legacyAction();
    }
    const originalAnchor = input.actionKind === "query" ? model.captureAnchor(input.root.scrollTop) : null;
    const originalRange = input.actionKind === "query" ? controller.getCurrentRange() : null;
    const didRender = ensureResolutionRendered(controller, resolution);
    if (didRender) {
      await waitForLayoutTicks(input.ownerWindow, input.layoutTicks ?? 1);
    }
    try {
      return await input.action(readWindowTargetContext(input, resolution));
    } finally {
      if (input.actionKind === "query" && didRender && originalAnchor !== null) {
        restoreReadingAnchor({
          controller,
          model,
          originalAnchor,
          originalRange,
          root: input.root
        });
      }
    }
  }
  function resolveWindowTarget(model, descriptor) {
    switch (descriptor.kind) {
      case "section":
        return resolveSectionIndex(model, descriptor.sectionIndex, descriptor);
      case "block": {
        const entry = model.getEntryContainingBlockIndex(descriptor.blockIndex);
        return entry === void 0 ? null : resolutionForEntry(model, entry, descriptor);
      }
      case "heading-anchor": {
        const entry = model.getEntryByHeadingAnchor(descriptor.anchor);
        return entry === void 0 ? null : resolutionForEntry(model, entry, descriptor);
      }
      case "source-line": {
        const entry = model.getEntryBySourceLine(descriptor.sourceLine);
        return entry === void 0 ? null : resolutionForEntry(model, entry, descriptor);
      }
      case "document-y":
        return resolveSectionIndex(model, model.sectionIndexAtDocumentY(descriptor.documentY), descriptor);
      case "find-match":
        return resolveFindMatch(model, descriptor);
    }
  }
  function resolveFindMatch(model, descriptor) {
    if (descriptor.blockIndex !== void 0) {
      const entry = model.getEntryContainingBlockIndex(descriptor.blockIndex);
      return entry === void 0 ? null : resolutionForEntry(model, entry, descriptor);
    }
    if (descriptor.startBlockIndex === void 0 || descriptor.endBlockIndex === void 0) {
      return null;
    }
    const start = model.getEntryContainingBlockIndex(descriptor.startBlockIndex);
    const end = model.getEntryContainingBlockIndex(descriptor.endBlockIndex);
    if (start === void 0 || end === void 0) {
      return null;
    }
    const startSection = findSectionArrayIndex(model, start);
    const endSection = findSectionArrayIndex(model, end);
    if (startSection < 0 || endSection < 0) {
      return null;
    }
    return {
      descriptor,
      entry: start,
      range: {
        end: Math.max(startSection, endSection),
        start: Math.min(startSection, endSection)
      },
      sectionIndex: startSection
    };
  }
  function resolveSectionIndex(model, sectionIndex, descriptor) {
    if (!Number.isFinite(sectionIndex)) {
      return null;
    }
    const normalized = Math.floor(sectionIndex);
    const entry = model.sections[normalized];
    if (entry === void 0) {
      return null;
    }
    return {
      descriptor,
      entry,
      range: { end: normalized, start: normalized },
      sectionIndex: normalized
    };
  }
  function resolutionForEntry(model, entry, descriptor) {
    const sectionIndex = findSectionArrayIndex(model, entry);
    if (sectionIndex < 0) {
      return null;
    }
    return {
      descriptor,
      entry,
      range: { end: sectionIndex, start: sectionIndex },
      sectionIndex
    };
  }
  function ensureResolutionRendered(controller, resolution) {
    if (resolution.range.start === resolution.range.end && controller.isSectionRendered(resolution.range.start)) {
      return false;
    }
    const options = { preserveAnchor: true };
    return resolution.range.start === resolution.range.end ? controller.ensureSectionRendered(resolution.range.start, options) : controller.ensureSectionRangeRendered(resolution.range.start, resolution.range.end, options);
  }
  function readWindowTargetContext(input, resolution) {
    const sectionElement = findSectionElement(input.main, resolution.entry);
    return {
      element: sectionElement,
      entry: resolution.entry,
      range: input.controller?.getCurrentRange() ?? null,
      sectionHeight: input.model?.sectionEffectiveHeight(resolution.sectionIndex) ?? 0,
      sectionIndex: resolution.sectionIndex,
      sectionTop: input.model?.sectionTop(resolution.sectionIndex) ?? 0,
      targetElement: findTargetElement(input.ownerWindow, sectionElement, resolution.descriptor)
    };
  }
  function findTargetElement(ownerWindow, sectionElement, descriptor) {
    if (sectionElement === null) {
      return null;
    }
    switch (descriptor.kind) {
      case "block":
        return findBlockElement(sectionElement, descriptor.blockIndex);
      case "heading-anchor": {
        const anchor = descriptor.anchor.startsWith("#") ? descriptor.anchor.slice(1) : descriptor.anchor;
        const element = sectionElement.ownerDocument.getElementById(anchor);
        return element instanceof ownerWindow.HTMLElement && sectionElement.contains(element) ? element : null;
      }
      case "source-line":
        return findSourceLineElement(sectionElement, descriptor.sourceLine);
      case "find-match":
        return descriptor.blockIndex === void 0 ? sectionElement : findBlockElement(sectionElement, descriptor.blockIndex);
      case "document-y":
      case "section":
        return sectionElement;
    }
  }
  function findSectionElement(main, entry) {
    for (const child of Array.from(main.children)) {
      if (child instanceof main.ownerDocument.defaultView.HTMLElement && readElementBlockIndex(child) === entry.blockIndex) {
        return child;
      }
    }
    return null;
  }
  function findBlockElement(sectionElement, blockIndex) {
    if (readElementBlockIndex(sectionElement) === blockIndex) {
      return sectionElement;
    }
    for (const element of Array.from(sectionElement.querySelectorAll("[data-mm-block-index]"))) {
      if (readElementBlockIndex(element) === blockIndex) {
        return element;
      }
    }
    return null;
  }
  function findSourceLineElement(sectionElement, sourceLine) {
    if (!Number.isFinite(sourceLine)) {
      return null;
    }
    const normalizedLine = Math.max(0, Math.floor(sourceLine));
    for (const element of Array.from(sectionElement.querySelectorAll("[data-mm-source-line]"))) {
      const start = parseNonNegativeInt(element.dataset["mmSourceLine"]);
      if (start === null) {
        continue;
      }
      const end = Math.max(start, parseNonNegativeInt(element.dataset["mmSourceEndLine"]) ?? start);
      if (normalizedLine >= start && normalizedLine <= end) {
        return element;
      }
    }
    return null;
  }
  function restoreReadingAnchor(input) {
    if (input.originalRange !== null) {
      input.controller.ensureSectionRangeRendered(input.originalRange.start, input.originalRange.end, {
        force: true,
        preserveAnchor: false
      });
    } else if (input.originalAnchor.sectionIndex >= 0) {
      input.controller.ensureSectionRendered(input.originalAnchor.sectionIndex, {
        force: true,
        preserveAnchor: false
      });
    }
    input.root.scrollTop = input.model.scrollTopForAnchor(input.originalAnchor);
  }
  function waitForLayoutTicks(ownerWindow, count) {
    const tick = () => new Promise((resolve) => {
      if (typeof ownerWindow.requestAnimationFrame === "function") {
        ownerWindow.requestAnimationFrame(() => resolve());
        return;
      }
      ownerWindow.setTimeout(() => resolve(), 0);
    });
    return count === 1 ? tick() : tick().then(tick);
  }
  function findSectionArrayIndex(model, entry) {
    return model.sections.findIndex((candidate) => candidate.blockIndex === entry.blockIndex);
  }
  function readElementBlockIndex(element) {
    const raw = element instanceof HTMLElement ? element.dataset["mmBlockIndex"] : void 0;
    if (raw === void 0 || raw.trim() === "") {
      return null;
    }
    const parsed = Number.parseInt(raw, 10);
    return Number.isFinite(parsed) ? parsed : null;
  }
  function parseNonNegativeInt(value) {
    if (value === void 0 || value.trim() === "") {
      return null;
    }
    const parsed = Number.parseInt(value, 10);
    return Number.isFinite(parsed) && parsed >= 0 ? parsed : null;
  }

  // RendererWeb/src/virtualizedFindProvider.ts
  var FIND_VISIBLE_SKIP_SELECTOR = [
    "aside",
    ".mm-minimap",
    ".mm-minimap-viewport",
    ".mm-width-handle",
    ".mm-drop-overlay",
    ".katex-mathml",
    "pre.mm-mermaid.is-rendered",
    ".mm-find-bar"
  ].join(",");
  function createVirtualizedFindProvider(deps) {
    let view = null;
    let requestSequence = 0;
    let latestRequestId = 0;
    let currentQuery = "";
    let matches = [];
    let totalCount = 0;
    let currentIndex = -1;
    let navigationSequence = 0;
    const updateStatus = () => {
      const status = {
        currentIndex,
        query: currentQuery,
        totalCount
      };
      view?.updateStatus(status);
    };
    const resetResults = (query = "") => {
      currentQuery = query;
      matches = [];
      totalCount = 0;
      currentIndex = -1;
      clearFindHighlights();
      updateStatus();
    };
    const paintVisibleHighlights = () => {
      const allRanges = [];
      let currentRange = null;
      const currentMatch = currentIndex >= 0 ? matches[currentIndex] : void 0;
      for (const match of matches) {
        const range = resolveLiveRangeForMatch(match);
        if (range === null) {
          continue;
        }
        allRanges.push(range);
        if (currentMatch !== void 0 && match.matchId === currentMatch.matchId) {
          currentRange = range;
        }
      }
      applyFindHighlights(allRanges, currentRange ?? void 0);
      return currentRange;
    };
    const search = (query) => {
      currentQuery = query;
      navigationSequence++;
      currentIndex = -1;
      totalCount = 0;
      matches = [];
      clearFindHighlights();
      updateStatus();
      if (query.length === 0) {
        return;
      }
      const context = deps.readContext();
      const request = {
        query,
        requestId: ++requestSequence,
        type: "find-query"
      };
      if (context.renderId !== null) {
        request.renderId = context.renderId;
      }
      latestRequestId = request.requestId;
      deps.postHostMessage(request);
    };
    const handleFindResults = (message) => {
      if (message.stale === true || message.requestId !== latestRequestId || message.query !== currentQuery) {
        return;
      }
      const context = deps.readContext();
      if (context.renderId !== null && message.renderId !== void 0 && message.renderId !== null && message.renderId !== context.renderId) {
        return;
      }
      matches = message.matches.filter(isUsableDescriptor).slice().sort((left, right) => left.ordinal - right.ordinal);
      totalCount = Math.max(0, Math.floor(message.totalCount));
      currentIndex = -1;
      paintVisibleHighlights();
      updateStatus();
    };
    const navigate2 = (direction) => {
      if (matches.length === 0) {
        paintVisibleHighlights();
        updateStatus();
        return;
      }
      if (currentIndex < 0) {
        currentIndex = direction === "next" ? 0 : matches.length - 1;
      } else {
        currentIndex = (currentIndex + (direction === "next" ? 1 : -1) + matches.length) % matches.length;
      }
      const match = matches[currentIndex];
      const sequence = ++navigationSequence;
      void renderMatchThenAct(match, sequence);
      updateStatus();
    };
    const renderMatchThenAct = async (match, sequence) => {
      const context = deps.readContext();
      if (!context.virtualizationEnabled || context.model === null || context.controller === null || context.main === null) {
        if (sequence !== navigationSequence) {
          return;
        }
        const currentRange = paintVisibleHighlights();
        scrollRangeIntoView(currentRange, null);
        updateStatus();
        return;
      }
      const descriptor = {
        blockIndex: match.blockIndex,
        kind: "find-match",
        matchId: match.matchId
      };
      if (match.startBlockIndex !== void 0) {
        descriptor.startBlockIndex = match.startBlockIndex;
      }
      if (match.endBlockIndex !== void 0) {
        descriptor.endBlockIndex = match.endBlockIndex;
      }
      const pendingRender = renderWindowTargetThenAct({
        action: ({ element, targetElement }) => {
          if (sequence !== navigationSequence) {
            return;
          }
          const currentRange = paintVisibleHighlights();
          scrollRangeIntoView(currentRange, targetElement ?? element);
        },
        actionKind: "navigate",
        controller: context.controller,
        descriptor,
        legacyAction: () => {
          if (sequence !== navigationSequence) {
            return;
          }
          const currentRange = paintVisibleHighlights();
          scrollRangeIntoView(currentRange, null);
        },
        main: context.main,
        model: context.model,
        ownerWindow: context.ownerWindow,
        root: context.root,
        virtualizationEnabled: context.virtualizationEnabled
      });
      if (sequence === navigationSequence) {
        const currentRange = paintVisibleHighlights();
        scrollRangeIntoView(currentRange, findLiveBlockElement(match.blockIndex));
        updateStatus();
      }
      await pendingRender;
      if (sequence === navigationSequence) {
        const currentRange = paintVisibleHighlights();
        scrollRangeIntoView(currentRange, findLiveBlockElement(match.blockIndex));
        updateStatus();
      }
    };
    return {
      close: () => {
        latestRequestId = ++requestSequence;
        navigationSequence++;
        resetResults("");
      },
      handleFindResults,
      navigate: navigate2,
      refreshVisibleHighlights: () => {
        paintVisibleHighlights();
      },
      search,
      setView: (nextView) => {
        view = nextView;
        updateStatus();
      }
    };
  }
  function isUsableDescriptor(match) {
    return typeof match.matchId === "string" && Number.isFinite(match.blockIndex) && Number.isFinite(match.blockLocalOffset) && match.blockLocalOffset >= 0 && Number.isFinite(match.length) && match.length > 0 && typeof match.normalizedText === "string" && Number.isFinite(match.ordinal) && match.ordinal > 0;
  }
  function resolveLiveRangeForMatch(match) {
    const block = findLiveBlockElement(match.blockIndex);
    if (block === null) {
      return null;
    }
    return rangeFromBlockLocalOffset(block, match.blockLocalOffset, match.length) ?? rangeFromNormalizedText(block, match.normalizedText);
  }
  function findLiveBlockElement(blockIndex) {
    return document.querySelector(`[data-mm-block-index="${blockIndex}"]`);
  }
  function rangeFromBlockLocalOffset(block, offset, length) {
    const endOffset = offset + length;
    if (!Number.isFinite(offset) || !Number.isFinite(length) || offset < 0 || length <= 0) {
      return null;
    }
    let cursor = 0;
    let startNode = null;
    let startInNode = 0;
    let endNode = null;
    let endInNode = 0;
    for (const node of visibleTextNodes(block)) {
      const textLength = node.nodeValue?.length ?? 0;
      const nextCursor = cursor + textLength;
      if (startNode === null && offset >= cursor && offset <= nextCursor) {
        startNode = node;
        startInNode = offset - cursor;
      }
      if (startNode !== null && endOffset >= cursor && endOffset <= nextCursor) {
        endNode = node;
        endInNode = endOffset - cursor;
        break;
      }
      cursor = nextCursor;
    }
    if (startNode === null || endNode === null) {
      return null;
    }
    const range = document.createRange();
    range.setStart(startNode, startInNode);
    range.setEnd(endNode, endInNode);
    return range.toString().length === length ? range : null;
  }
  function rangeFromNormalizedText(block, normalizedText) {
    if (normalizedText.length === 0) {
      return null;
    }
    for (const node of visibleTextNodes(block)) {
      for (const [start, end] of findCaseInsensitiveMatchOffsets(node.nodeValue ?? "", normalizedText)) {
        const range = document.createRange();
        range.setStart(node, start);
        range.setEnd(node, end);
        return range;
      }
    }
    return null;
  }
  function visibleTextNodes(root) {
    const out = [];
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
      acceptNode(node) {
        const parent = node.parentElement;
        if (parent === null || parent.closest(FIND_VISIBLE_SKIP_SELECTOR) !== null) {
          return NodeFilter.FILTER_REJECT;
        }
        return NodeFilter.FILTER_ACCEPT;
      }
    });
    for (let current = walker.nextNode(); current !== null; current = walker.nextNode()) {
      if (current.nodeType === Node.TEXT_NODE) {
        out.push(current);
      }
    }
    return out;
  }
  function scrollRangeIntoView(range, fallback) {
    const host = range?.startContainer.parentElement?.closest("[data-mm-block-index]") ?? fallback;
    host?.scrollIntoView({ block: "center", behavior: "instant" });
  }

  // RendererWeb/src/sourceLineSync.ts
  var SOURCE_LINE_ANCHOR_SELECTOR = "[data-mm-source-line]";
  function readSourceLineAnchors(root = document, scrollY = window.scrollY) {
    const anchors = [];
    for (const element of Array.from(root.querySelectorAll(SOURCE_LINE_ANCHOR_SELECTOR))) {
      const sourceLine = parseNonNegativeInt2(element.dataset["mmSourceLine"]);
      if (sourceLine === null) {
        continue;
      }
      const endLine = parseNonNegativeInt2(element.dataset["mmSourceEndLine"]) ?? sourceLine;
      anchors.push({
        sourceLine,
        endLine: Math.max(sourceLine, endLine),
        top: Math.max(0, element.getBoundingClientRect().top + scrollY)
      });
    }
    anchors.sort((left, right) => {
      const sourceComparison = left.sourceLine - right.sourceLine;
      return sourceComparison !== 0 ? sourceComparison : left.top - right.top;
    });
    return anchors;
  }
  function findScrollTopForSourceLine(anchors, sourceLine) {
    if (anchors.length === 0 || !Number.isFinite(sourceLine)) {
      return null;
    }
    const normalizedLine = Math.max(0, Math.floor(sourceLine));
    const selectedIndex = findLastAnchorIndexAtOrBeforeLine(anchors, normalizedLine);
    const selected = anchors[selectedIndex];
    const next = anchors[selectedIndex + 1] ?? null;
    if (next && normalizedLine > selected.endLine) {
      const lineSpan = Math.max(1, next.sourceLine - selected.sourceLine);
      const visualSpan = Math.max(0, next.top - selected.top);
      const ratio = clamp01((normalizedLine - selected.sourceLine) / lineSpan);
      return Math.max(0, selected.top + visualSpan * ratio);
    }
    if (next && normalizedLine > selected.sourceLine && normalizedLine <= selected.endLine) {
      const lineSpan = Math.max(1, selected.endLine - selected.sourceLine);
      const visualSpan = Math.max(0, next.top - selected.top);
      const ratio = clamp01((normalizedLine - selected.sourceLine) / lineSpan);
      return Math.max(0, selected.top + visualSpan * ratio);
    }
    return Math.max(0, selected.top);
  }
  function findSourceLineAtDocumentY(anchors, documentY) {
    if (anchors.length === 0 || !Number.isFinite(documentY)) {
      return null;
    }
    const normalizedY = Math.max(0, documentY);
    const selectedIndex = findLastAnchorIndexAtOrBeforeTop(anchors, normalizedY);
    const selected = anchors[selectedIndex];
    const next = anchors[selectedIndex + 1] ?? null;
    if (!next) {
      return selected.sourceLine;
    }
    const visualSpan = next.top - selected.top;
    if (visualSpan <= 1) {
      return selected.sourceLine;
    }
    const targetLine = selected.endLine > selected.sourceLine ? selected.endLine : next.sourceLine;
    const lineSpan = Math.max(0, targetLine - selected.sourceLine);
    if (lineSpan <= 0) {
      return selected.sourceLine;
    }
    const ratio = clamp01((normalizedY - selected.top) / visualSpan);
    return selected.sourceLine + Math.round(lineSpan * ratio);
  }
  function findSourceLineAtDocumentYWithFallback(liveAnchors, readFallbackAnchors, documentY) {
    if (!Number.isFinite(documentY)) {
      return null;
    }
    if (liveAnchors.length === 0) {
      return findSourceLineAtDocumentY(readFallbackAnchors(), documentY);
    }
    if (liveEdgeInterpolationMissing(liveAnchors, documentY)) {
      const fallbackAnchors = readFallbackAnchors();
      return findSourceLineAtDocumentY(fallbackAnchors, documentY);
    }
    return findSourceLineAtDocumentY(liveAnchors, documentY);
  }
  function findLastAnchorIndexAtOrBeforeLine(anchors, sourceLine) {
    let low = 0;
    let high = anchors.length - 1;
    let result = 0;
    while (low <= high) {
      const mid = low + Math.floor((high - low) / 2);
      if (anchors[mid].sourceLine <= sourceLine) {
        result = mid;
        low = mid + 1;
      } else {
        high = mid - 1;
      }
    }
    return result;
  }
  function findLastAnchorIndexAtOrBeforeTop(anchors, documentY) {
    let low = 0;
    let high = anchors.length - 1;
    let result = 0;
    while (low <= high) {
      const mid = low + Math.floor((high - low) / 2);
      if (anchors[mid].top <= documentY) {
        result = mid;
        low = mid + 1;
      } else {
        high = mid - 1;
      }
    }
    return result;
  }
  function liveEdgeInterpolationMissing(anchors, documentY) {
    if (anchors.length === 0) {
      return true;
    }
    const normalizedY = Math.max(0, documentY);
    const first = anchors[0];
    if (normalizedY < first.top) {
      return true;
    }
    const selectedIndex = findLastAnchorIndexAtOrBeforeTop(anchors, normalizedY);
    const selected = anchors[selectedIndex];
    const next = anchors[selectedIndex + 1] ?? null;
    return next === null && normalizedY > selected.top;
  }
  function parseNonNegativeInt2(value) {
    if (value === void 0 || value.trim() === "") {
      return null;
    }
    const parsed = Number.parseInt(value, 10);
    return Number.isFinite(parsed) && parsed >= 0 ? parsed : null;
  }
  function clamp01(value) {
    return Math.max(0, Math.min(1, value));
  }

  // RendererWeb/src/minimapCache.ts
  function captureMinimapSnapshot(input) {
    if (!input.minimapContent || input.minimapContent.childNodes.length === 0) {
      return null;
    }
    const nodes = Array.from(input.minimapContent.childNodes);
    const content = input.ownerDocument.createDocumentFragment();
    content.append(...nodes.map((node) => node.cloneNode(true)));
    return {
      content,
      documentHeight: input.documentHeight,
      lastPostedState: { ...input.lastPostedState },
      contentStyle: {
        width: input.minimapContent.style.width,
        transform: input.minimapContent.style.transform
      },
      viewportStyle: {
        height: input.minimapViewport?.style.height ?? "",
        transform: input.minimapViewport?.style.transform ?? ""
      }
    };
  }
  function restoreMinimapSnapshot(snapshot, input) {
    if (!input.minimapContent) {
      return null;
    }
    const contentNodeCount = snapshot.content.childNodes.length;
    input.minimapContent.replaceChildren(snapshot.content.cloneNode(true));
    input.minimapContent.style.width = snapshot.contentStyle.width;
    input.minimapContent.style.transform = snapshot.contentStyle.transform;
    if (input.minimapViewport) {
      input.minimapViewport.style.height = snapshot.viewportStyle.height;
      input.minimapViewport.style.transform = snapshot.viewportStyle.transform;
    }
    return {
      contentNodeCount,
      documentHeight: snapshot.documentHeight,
      lastPostedState: { ...snapshot.lastPostedState }
    };
  }

  // RendererWeb/src/topVisibleBlockIndex.ts
  var LIVE_DOCUMENT_BLOCK_SELECTOR = "body > main.mm-document [data-mm-block-index]";
  function collectLiveDocumentBlockElements(ownerDocument) {
    return Array.from(ownerDocument.querySelectorAll(LIVE_DOCUMENT_BLOCK_SELECTOR)).filter(hasVisibleBlockBox);
  }
  function findTopVisibleBlockIndexFromBlocks(blocks, scrollTop) {
    if (blocks.length === 0) {
      return null;
    }
    let lo = 0;
    let hi = blocks.length - 1;
    let firstAtOrBelowViewportTop = -1;
    while (lo <= hi) {
      const mid = lo + (hi - lo >> 1);
      const visibleMid = findNearestVisibleBlockBox(blocks, lo, mid, hi);
      if (visibleMid === null) {
        break;
      }
      if (visibleMid.top + visibleMid.height >= scrollTop) {
        firstAtOrBelowViewportTop = visibleMid.index;
        hi = visibleMid.index - 1;
      } else {
        lo = visibleMid.index + 1;
      }
    }
    const index = firstAtOrBelowViewportTop >= 0 ? firstAtOrBelowViewportTop : findLastVisibleBlockIndex(blocks);
    return index < 0 ? null : readBlockIndex(blocks[index]);
  }
  function readBlockIndex(block) {
    const raw = block.dataset["mmBlockIndex"];
    const parsed = raw === void 0 ? Number.NaN : Number.parseInt(raw, 10);
    return Number.isFinite(parsed) ? parsed : null;
  }
  function findNearestVisibleBlockBox(blocks, lo, mid, hi) {
    for (let index = mid; index >= lo; index--) {
      const box = readVisibleBlockBox(blocks[index], index);
      if (box !== null) {
        return box;
      }
    }
    for (let index = mid + 1; index <= hi; index++) {
      const box = readVisibleBlockBox(blocks[index], index);
      if (box !== null) {
        return box;
      }
    }
    return null;
  }
  function findLastVisibleBlockIndex(blocks) {
    for (let index = blocks.length - 1; index >= 0; index--) {
      if (hasVisibleBlockBox(blocks[index])) {
        return index;
      }
    }
    return -1;
  }
  function hasVisibleBlockBox(block) {
    return readVisibleBlockBox(block, 0) !== null;
  }
  function readVisibleBlockBox(block, index) {
    const height = block.offsetHeight;
    const top = blockDocumentTop(block);
    if (!Number.isFinite(height) || height < 0 || !Number.isFinite(top) || isDisplayNoneZeroBox(block, height)) {
      return null;
    }
    return { height, index, top };
  }
  function isDisplayNoneZeroBox(block, height) {
    if (height !== 0) {
      return false;
    }
    if (block.style.display === "none") {
      return true;
    }
    return getComputedStyle(block).display === "none";
  }
  function blockDocumentTop(block) {
    let top = 0;
    let current = block;
    while (current !== null) {
      if (!Number.isFinite(current.offsetTop)) {
        return Number.NaN;
      }
      top += current.offsetTop;
      const nextOffsetParent = current.offsetParent;
      current = nextOffsetParent instanceof HTMLElement ? nextOffsetParent : null;
    }
    return top;
  }

  // RendererWeb/src/sectionIntrinsicSize.ts
  var MODEL_GAP_PX = 44;
  var DEFAULT_LINE_HEIGHT_PX = 30;
  var DEFAULT_DISPLAY_MATH_CONTENT_PX = 120;
  var HEADING_CONTENT_HEIGHT_BY_LEVEL = {
    1: 56,
    2: 35,
    3: 32,
    4: 30,
    5: 28,
    6: 26
  };
  var DEFAULT_MIN_SAMPLES_PER_BUCKET = 3;
  function readIntrinsicSizeMetrics(main) {
    const styles = getComputedStyle(main);
    const fontSizePx = Number.parseFloat(styles.fontSize) || 18;
    const lineHeightPx = readLineHeightPx(styles.lineHeight, fontSizePx);
    const contentWidth = main.clientWidth || 820;
    const charsPerLine = Math.max(8, Math.floor(contentWidth / (fontSizePx * 0.61)));
    return { charsPerLine, lineHeightPx, fontSizePx };
  }
  function readLineHeightPx(lineHeight, fontSizePx) {
    if (lineHeight.endsWith("px")) {
      return Number.parseFloat(lineHeight) || fontSizePx * 1.6;
    }
    const ratio = Number.parseFloat(lineHeight);
    return Number.isFinite(ratio) && ratio > 0 ? fontSizePx * ratio : fontSizePx * 1.6;
  }
  function normalizeSectionKind(raw) {
    switch (raw) {
      case "heading":
      case "paragraph":
      case "quote":
      case "list":
      case "rule":
      case "code":
      case "table":
      case "image":
      case "math":
        return raw;
      default:
        return "unknown";
    }
  }
  function readSectionIntrinsicInputs(element) {
    const text = element.textContent ?? "";
    return {
      headingLevel: readHeadingLevel(element),
      listItemCount: element.querySelectorAll("li").length,
      newlineCount: countNewlines(text),
      tableRowCount: element.querySelectorAll("tr").length,
      textLength: text.length
    };
  }
  function readHeadingLevel(element) {
    const tag = element.tagName.toUpperCase();
    return /^H[1-6]$/.test(tag) ? Number.parseInt(tag.slice(1), 10) : 0;
  }
  function countNewlines(text) {
    let count = 0;
    for (let index = 0; index < text.length; index++) {
      if (text.charCodeAt(index) === 10) {
        count++;
      }
    }
    return count;
  }
  function readSectionIntrinsicCalibrationTarget(element, metrics) {
    const kind = normalizeSectionKind(element.dataset["mmBlockKind"]);
    const input = readSectionIntrinsicInputs(element);
    const sourceText = element.textContent ?? "";
    return {
      defaultHeight: estimateSectionIntrinsicHeight(kind, input, metrics, sourceText),
      input,
      kind,
      sourceText
    };
  }
  function estimateSectionIntrinsicHeight(kind, input, metrics, sourceText = "") {
    const wrappedLines = Math.max(1, Math.ceil(input.textLength / metrics.charsPerLine));
    switch (kind) {
      case "heading": {
        const level = input.headingLevel >= 1 && input.headingLevel <= 6 ? input.headingLevel : 2;
        const baseContentHeight = scaleDefaultPx(HEADING_CONTENT_HEIGHT_BY_LEVEL[level], metrics);
        const wrapExtraHeight = Math.max(0, wrappedLines - 1) * metrics.lineHeightPx * 1.15;
        return withModelGap(baseContentHeight + wrapExtraHeight);
      }
      case "paragraph":
        return withModelGap((wrappedLines * metrics.lineHeightPx + metrics.lineHeightPx * 0.6) * 0.95);
      case "code":
        return withModelGap((input.newlineCount + 1) * metrics.lineHeightPx * 0.95 + metrics.lineHeightPx * 1.4);
      case "quote":
        return withModelGap(wrappedLines * metrics.lineHeightPx + metrics.lineHeightPx * 0.9);
      case "list":
        return withModelGap((input.listItemCount || 1) * metrics.lineHeightPx * 1.3 + metrics.lineHeightPx * 0.5);
      case "table": {
        const rows = input.tableRowCount || 2;
        return withModelGap(rows * metrics.lineHeightPx * 1 + metrics.lineHeightPx * 0.8);
      }
      case "math": {
        const rowCount = countMathRows(sourceText);
        const baseContentHeight = scaleDefaultPx(DEFAULT_DISPLAY_MATH_CONTENT_PX, metrics);
        return withModelGap(baseContentHeight + Math.max(0, rowCount - 1) * metrics.lineHeightPx * 1.35);
      }
      case "image":
        return withModelGap(320);
      case "rule":
        return withModelGap(metrics.lineHeightPx);
      default:
        return withModelGap(Math.max(metrics.lineHeightPx, wrappedLines * metrics.lineHeightPx));
    }
  }
  function withModelGap(contentBoxHeight) {
    return contentBoxHeight + MODEL_GAP_PX;
  }
  function scaleDefaultPx(defaultPx, metrics) {
    return defaultPx * (metrics.lineHeightPx / DEFAULT_LINE_HEIGHT_PX);
  }
  function countMathRows(sourceText) {
    const rowSeparators = sourceText.match(/\\\\/g)?.length ?? 0;
    return Math.max(1, rowSeparators + 1);
  }
  var SectionIntrinsicCalibrator = class {
    constructor(options = {}) {
      this.buckets = /* @__PURE__ */ new Map();
      this.bucketKeyByBlockIndex = /* @__PURE__ */ new Map();
      this.minSamplesPerBucket = Math.max(1, Math.floor(options.minSamplesPerBucket ?? DEFAULT_MIN_SAMPLES_PER_BUCKET));
    }
    reset() {
      this.buckets.clear();
      this.bucketKeyByBlockIndex.clear();
    }
    recordSample(sample) {
      if (sample.measuredHeightPlaceholder === true || !Number.isFinite(sample.blockIndex) || !Number.isFinite(sample.measuredHeight) || sample.measuredHeight < 0 || !Number.isFinite(sample.defaultHeight) || sample.defaultHeight <= 0) {
        return false;
      }
      const bucketKey = sectionIntrinsicCalibrationBucketKey(sample.kind, sample.input, sample.sourceText ?? "");
      const previousBucketKey = this.bucketKeyByBlockIndex.get(sample.blockIndex);
      if (previousBucketKey !== void 0 && previousBucketKey !== bucketKey) {
        this.buckets.get(previousBucketKey)?.samplesByBlockIndex.delete(sample.blockIndex);
      }
      const bucket = this.readOrCreateBucket(bucketKey, sample.kind);
      const hadSample = bucket.samplesByBlockIndex.has(sample.blockIndex);
      bucket.samplesByBlockIndex.set(sample.blockIndex, sample.measuredHeight);
      this.bucketKeyByBlockIndex.set(sample.blockIndex, bucketKey);
      return !hadSample;
    }
    estimateHeight(kind, input, metrics, sourceText = "") {
      return this.estimateTargetHeight({
        defaultHeight: estimateSectionIntrinsicHeight(kind, input, metrics, sourceText),
        input,
        kind,
        sourceText
      });
    }
    estimateTargetHeight(target) {
      const bucket = this.buckets.get(sectionIntrinsicCalibrationBucketKey(
        target.kind,
        target.input,
        target.sourceText ?? ""
      ));
      if (bucket === void 0 || bucket.samplesByBlockIndex.size < this.minSamplesPerBucket) {
        return target.defaultHeight;
      }
      return median(Array.from(bucket.samplesByBlockIndex.values()));
    }
    getSummary() {
      const byKind = {};
      let bucketCount = 0;
      let calibratedBucketCount = 0;
      let sampleCount = 0;
      for (const bucket of this.buckets.values()) {
        const kindSummary = byKind[bucket.kind] ?? {
          calibratedBucketCount: 0,
          sampleCount: 0
        };
        const bucketSampleCount = bucket.samplesByBlockIndex.size;
        bucketCount++;
        sampleCount += bucketSampleCount;
        kindSummary.sampleCount += bucketSampleCount;
        if (bucketSampleCount >= this.minSamplesPerBucket) {
          calibratedBucketCount++;
          kindSummary.calibratedBucketCount++;
        }
        byKind[bucket.kind] = kindSummary;
      }
      return { bucketCount, calibratedBucketCount, sampleCount, byKind };
    }
    readOrCreateBucket(bucketKey, kind) {
      const existing = this.buckets.get(bucketKey);
      if (existing !== void 0) {
        return existing;
      }
      const created = {
        kind,
        samplesByBlockIndex: /* @__PURE__ */ new Map()
      };
      this.buckets.set(bucketKey, created);
      return created;
    }
  };
  function createSectionIntrinsicCalibrator(options = {}) {
    return new SectionIntrinsicCalibrator(options);
  }
  function sectionIntrinsicCalibrationBucketKey(kind, input, sourceText) {
    switch (kind) {
      case "heading": {
        const level = input.headingLevel >= 1 && input.headingLevel <= 6 ? input.headingLevel : 2;
        return `${kind}:level:${level}`;
      }
      case "paragraph":
      case "quote":
        return `${kind}:text:${bucketByThreshold(input.textLength, [80, 160, 320, 640, 1280, 2560])}`;
      case "math":
        return `${kind}:rows:${bucketByThreshold(countMathRows(sourceText), [1, 2, 4, 8, 16])}`;
      case "code":
        return `${kind}:lines:${bucketByThreshold(input.newlineCount + 1, [1, 3, 8, 16, 32, 64])}`;
      case "list":
        return `${kind}:items:${bucketByThreshold(input.listItemCount || 1, [1, 3, 6, 12, 24, 48])}`;
      case "table":
        return `${kind}:rows:${bucketByThreshold(input.tableRowCount || 2, [2, 4, 8, 16, 32, 64])}`;
      default:
        return kind;
    }
  }
  function bucketByThreshold(value, thresholds) {
    for (const threshold of thresholds) {
      if (value <= threshold) {
        return `le-${threshold}`;
      }
    }
    return `gt-${thresholds[thresholds.length - 1] ?? 0}`;
  }
  function median(values) {
    if (values.length === 0) {
      return 0;
    }
    const sorted = values.slice().sort((a, b) => a - b);
    const middle = Math.floor(sorted.length / 2);
    if (sorted.length % 2 === 1) {
      return sorted[middle];
    }
    return (sorted[middle - 1] + sorted[middle]) / 2;
  }

  // RendererWeb/src/documentWindow.ts
  var ESTIMATE_ERROR_KIND_ORDER = [
    "heading",
    "paragraph",
    "math",
    "code",
    "table",
    "list",
    "quote",
    "mermaid",
    "rule",
    "image",
    "unknown"
  ];
  var ESTIMATE_ERROR_WORST_OFFENDER_LIMIT = 5;
  var DEFAULT_RENDER_AHEAD = {
    aboveViewports: 1.5,
    belowViewports: 2,
    minAbovePx: 2400,
    minBelowPx: 3600
  };
  function effectiveHeight(entry) {
    return entry.measuredHeight ?? entry.estimatedHeight;
  }
  var DocumentWindowModel = class {
    constructor(entries, options = {}) {
      this.sectionIndexByBlockIndex = /* @__PURE__ */ new Map();
      this.containingSectionIndexByBlockIndex = /* @__PURE__ */ new Map();
      this.sectionIndexByHeadingAnchor = /* @__PURE__ */ new Map();
      this.sourceLineSpans = [];
      this.totalHeight = 0;
      this.leadingOffset = options.leadingOffset ?? 0;
      this.sections = entries.slice().sort((a, b) => a.sectionIndex - b.sectionIndex).map((entry) => ({ ...entry }));
      for (let index = 0; index < this.sections.length; index++) {
        const entry = this.sections[index];
        const metadata = readSectionModelEntryMetadata(entry);
        entry.containedBlockIndexes = metadata.containedBlockIndexes;
        entry.headingAnchors = metadata.headingAnchors;
        entry.sourceLineSpans = metadata.sourceLineSpans;
        if (!this.sectionIndexByBlockIndex.has(entry.blockIndex)) {
          this.sectionIndexByBlockIndex.set(entry.blockIndex, index);
        }
        for (const blockIndex of entry.containedBlockIndexes) {
          if (!this.containingSectionIndexByBlockIndex.has(blockIndex)) {
            this.containingSectionIndexByBlockIndex.set(blockIndex, index);
          }
        }
        for (const anchor of entry.headingAnchors) {
          if (!this.sectionIndexByHeadingAnchor.has(anchor)) {
            this.sectionIndexByHeadingAnchor.set(anchor, index);
          }
        }
        for (const span of entry.sourceLineSpans) {
          this.sourceLineSpans.push({ ...span, sectionIndex: index });
        }
      }
      this.sourceLineSpans.sort((left, right) => {
        const sourceComparison = left.sourceLine - right.sourceLine;
        return sourceComparison !== 0 ? sourceComparison : left.sectionIndex - right.sectionIndex;
      });
      this.refreshHeightModel();
    }
    getSectionCount() {
      return this.sections.length;
    }
    getTotalHeight() {
      return this.totalHeight;
    }
    sectionTop(sectionIndex) {
      return this.sections[sectionIndex]?.cumulativeTop ?? this.leadingOffset;
    }
    sectionEffectiveHeight(sectionIndex) {
      const entry = this.sections[sectionIndex];
      return entry ? effectiveHeight(entry) : 0;
    }
    getEntryByBlockIndex(blockIndex) {
      const sectionIndex = this.sectionIndexByBlockIndex.get(blockIndex);
      return sectionIndex === void 0 ? void 0 : this.sections[sectionIndex];
    }
    getEntryContainingBlockIndex(blockIndex) {
      const sectionIndex = this.containingSectionIndexByBlockIndex.get(blockIndex);
      return sectionIndex === void 0 ? void 0 : this.sections[sectionIndex];
    }
    getEntryByHeadingAnchor(anchor) {
      const normalized = normalizeHeadingAnchor(anchor);
      if (normalized.length === 0) {
        return void 0;
      }
      const sectionIndex = this.sectionIndexByHeadingAnchor.get(normalized);
      return sectionIndex === void 0 ? void 0 : this.sections[sectionIndex];
    }
    getEntryBySourceLine(sourceLine) {
      if (this.sourceLineSpans.length === 0 || !Number.isFinite(sourceLine)) {
        return void 0;
      }
      const normalizedLine = Math.max(0, Math.floor(sourceLine));
      let low = 0;
      let high = this.sourceLineSpans.length - 1;
      let selectedIndex = -1;
      while (low <= high) {
        const mid = low + Math.floor((high - low) / 2);
        if (this.sourceLineSpans[mid].sourceLine <= normalizedLine) {
          selectedIndex = mid;
          low = mid + 1;
        } else {
          high = mid - 1;
        }
      }
      const selected = selectedIndex >= 0 ? this.sourceLineSpans[selectedIndex] : this.sourceLineSpans[0];
      return selected === void 0 ? void 0 : this.sections[selected.sectionIndex];
    }
    getSourceLineAnchors() {
      return this.sourceLineSpans.map((span) => {
        const entry = this.sections[span.sectionIndex];
        return {
          blockIndex: entry.blockIndex,
          endLine: span.endLine,
          sectionIndex: span.sectionIndex,
          sourceLine: span.sourceLine,
          top: entry.cumulativeTop
        };
      });
    }
    getMinimapBlockProjection() {
      return this.sections.map((entry) => ({
        height: effectiveHeight(entry),
        headingLevel: entry.headingLevel,
        kind: entry.kind,
        top: entry.cumulativeTop
      }));
    }
    refreshHeightModel() {
      let cumulative = this.leadingOffset;
      for (const entry of this.sections) {
        entry.cumulativeTop = cumulative;
        cumulative += effectiveHeight(entry);
      }
      this.totalHeight = cumulative;
    }
    updateMeasuredHeightsByBlockIndex(updates) {
      let updatedCount = 0;
      let maxAbsDelta = 0;
      let totalDelta = 0;
      for (const update of updates) {
        const index = this.sectionIndexByBlockIndex.get(update.blockIndex);
        if (index === void 0) {
          continue;
        }
        const entry = this.sections[index];
        if (!Number.isFinite(update.measuredHeight) || update.measuredHeight < 0) {
          continue;
        }
        const previous = effectiveHeight(entry);
        entry.measuredHeight = update.measuredHeight;
        if (update.measuredHeightPlaceholder === true) {
          entry.measuredHeightPlaceholder = true;
        } else {
          delete entry.measuredHeightPlaceholder;
        }
        const delta = update.measuredHeight - previous;
        updatedCount++;
        maxAbsDelta = Math.max(maxAbsDelta, Math.abs(delta));
        totalDelta += delta;
      }
      if (updatedCount > 0) {
        this.refreshHeightModel();
      }
      return { maxAbsDelta, totalDelta, updatedCount };
    }
    recordIntrinsicSizeCalibrationSamples(calibrator) {
      let recordedCount = 0;
      for (const entry of this.sections) {
        if (entry.measuredHeight === void 0 || entry.intrinsicSize === void 0) {
          continue;
        }
        const sample = {
          ...entry.intrinsicSize,
          blockIndex: entry.blockIndex,
          measuredHeight: entry.measuredHeight
        };
        if (entry.measuredHeightPlaceholder === true) {
          Object.assign(sample, { measuredHeightPlaceholder: true });
        }
        if (calibrator.recordSample(sample)) {
          recordedCount++;
        }
      }
      return recordedCount;
    }
    updateEstimatedHeightsFromCalibration(calibrator) {
      let updatedCount = 0;
      let maxAbsDelta = 0;
      let totalDelta = 0;
      for (const entry of this.sections) {
        if (entry.intrinsicSize === void 0) {
          continue;
        }
        const nextHeight = calibrator.estimateTargetHeight(entry.intrinsicSize);
        if (!Number.isFinite(nextHeight) || nextHeight < 0) {
          continue;
        }
        const previous = entry.estimatedHeight;
        if (Object.is(previous, nextHeight)) {
          continue;
        }
        entry.estimatedHeight = nextHeight;
        const delta = nextHeight - previous;
        updatedCount++;
        maxAbsDelta = Math.max(maxAbsDelta, Math.abs(delta));
        totalDelta += delta;
      }
      if (updatedCount > 0) {
        this.refreshHeightModel();
      }
      return { maxAbsDelta, totalDelta, updatedCount };
    }
    sectionIndexAtDocumentY(y) {
      const count = this.sections.length;
      if (count === 0) {
        return 0;
      }
      let lo = 0;
      let hi = count - 1;
      let result = 0;
      while (lo <= hi) {
        const mid = lo + (hi - lo >> 1);
        const entry = this.sections[mid];
        if (entry.cumulativeTop <= y) {
          result = mid;
          lo = mid + 1;
        } else {
          hi = mid - 1;
        }
      }
      return result;
    }
    computeWindowRange(scrollTop, viewportHeight, config = DEFAULT_RENDER_AHEAD) {
      if (this.sections.length === 0) {
        return { start: 0, end: -1 };
      }
      const above = Math.max(config.minAbovePx, viewportHeight * config.aboveViewports);
      const below = Math.max(config.minBelowPx, viewportHeight * config.belowViewports);
      const topY = Math.max(0, scrollTop - above);
      const bottomY = scrollTop + viewportHeight + below;
      const start = this.sectionIndexAtDocumentY(topY);
      const end = Math.max(start, this.sectionIndexAtDocumentY(bottomY));
      return { start, end };
    }
    captureAnchor(scrollTop) {
      const firstTop = this.sections[0]?.cumulativeTop ?? this.leadingOffset;
      if (this.sections.length === 0 || scrollTop < firstTop) {
        return {
          blockIndex: -1,
          intraOffset: Math.max(0, scrollTop),
          sectionIndex: -1
        };
      }
      const sectionIndex = this.sectionIndexAtDocumentY(scrollTop);
      const entry = this.sections[sectionIndex];
      const top = entry?.cumulativeTop ?? this.leadingOffset;
      return {
        blockIndex: entry?.blockIndex ?? -1,
        intraOffset: Math.max(0, scrollTop - top),
        sectionIndex
      };
    }
    scrollTopForAnchor(anchor) {
      if (anchor.sectionIndex < 0 || anchor.blockIndex < 0) {
        return Math.max(0, anchor.intraOffset);
      }
      const byBlock = anchor.blockIndex >= 0 ? this.getEntryByBlockIndex(anchor.blockIndex) : void 0;
      const entry = byBlock ?? this.sections[anchor.sectionIndex];
      return (entry?.cumulativeTop ?? this.leadingOffset) + anchor.intraOffset;
    }
    computeSpacerHeights(range) {
      if (this.sections.length === 0 || range.end < range.start) {
        return {
          bottomSpacer: 0,
          topSpacer: 0,
          totalHeight: this.totalHeight,
          windowHeight: 0
        };
      }
      const start = Math.max(0, Math.min(range.start, this.sections.length - 1));
      const end = Math.max(start, Math.min(range.end, this.sections.length - 1));
      const windowTop = this.sectionTop(start);
      const topSpacer = Math.max(0, windowTop - this.leadingOffset);
      let windowHeight = 0;
      for (let index = start; index <= end; index++) {
        windowHeight += effectiveHeight(this.sections[index]);
      }
      return {
        bottomSpacer: Math.max(0, this.totalHeight - windowTop - windowHeight),
        topSpacer,
        totalHeight: this.totalHeight,
        windowHeight
      };
    }
  };
  function buildDocumentWindowModelsFromLiveBlocks(blocks, metrics, documentScrollHeight, options = {}) {
    const measuredEntries = readLiveSectionModelEntries(blocks, metrics, documentScrollHeight, true, options);
    const estimateEntries = measuredEntries.map((entry) => {
      const { measuredHeightPlaceholder: _placeholder, ...estimateEntry } = entry;
      return {
        ...estimateEntry,
        measuredHeight: void 0
      };
    });
    const leadingOffset = measuredEntries[0]?.cumulativeTop ?? 0;
    const measuredModel = new DocumentWindowModel(measuredEntries, { leadingOffset });
    const estimateOnlyModel = new DocumentWindowModel(estimateEntries, { leadingOffset });
    return {
      estimateHeightError: summarizeEstimateHeightErrors(estimateOnlyModel, measuredModel),
      estimateOnlyModel,
      measuredModel
    };
  }
  function collectLiveDocumentSectionElements(main) {
    return Array.from(main.children).filter((child) => child instanceof HTMLElement && child.hasAttribute("data-mm-block-index"));
  }
  function readLiveBlockMeasuredHeights(blocks, documentScrollHeight) {
    return readLiveBlockMeasurements(blocks, documentScrollHeight).map((measurement) => {
      const update = {
        blockIndex: measurement.blockIndex,
        measuredHeight: measurement.measuredHeight
      };
      if (measurement.measuredHeightPlaceholder) {
        update.measuredHeightPlaceholder = true;
      }
      return update;
    });
  }
  function readLiveBlockOffsetMeasuredHeights(blocks) {
    const geometry = readVisibleBlockGeometry(blocks);
    return geometry.map((item, index) => {
      const nextTop = geometry[index + 1]?.top ?? readNextSiblingDocumentTop(item.element);
      const measuredHeight = nextTop !== void 0 && nextTop > item.top ? nextTop - item.top : item.height;
      const update = {
        blockIndex: readBlockIndex2(item.element, item.sourceIndex),
        measuredHeight: Math.max(0, measuredHeight)
      };
      if (isContentVisibilityPlaceholderMeasurement(item)) {
        update.measuredHeightPlaceholder = true;
      }
      return update;
    });
  }
  function computeLiveBlockWindowRange(blocks, scrollTop, viewportHeight, config = DEFAULT_RENDER_AHEAD) {
    const visibleBlocks = readVisibleBlockGeometry(blocks);
    if (visibleBlocks.length === 0) {
      return { start: 0, end: -1 };
    }
    const above = Math.max(config.minAbovePx, viewportHeight * config.aboveViewports);
    const below = Math.max(config.minBelowPx, viewportHeight * config.belowViewports);
    const topY = Math.max(0, scrollTop - above);
    const bottomY = scrollTop + viewportHeight + below;
    let start = visibleBlocks.length - 1;
    let end = 0;
    let found = false;
    for (let index = 0; index < visibleBlocks.length; index++) {
      const block = visibleBlocks[index];
      const top = block.top;
      const bottom = top + block.height;
      if (bottom > topY && top <= bottomY) {
        if (!found) {
          start = index;
        }
        end = index;
        found = true;
      }
    }
    return found ? { start, end } : { start: 0, end: -1 };
  }
  function elementDocumentTop(element) {
    let top = 0;
    let current = element;
    while (current !== null) {
      if (!Number.isFinite(current.offsetTop)) {
        return Number.NaN;
      }
      top += current.offsetTop;
      const parent = current.offsetParent;
      current = parent instanceof HTMLElement ? parent : null;
    }
    return top;
  }
  function readNextSiblingDocumentTop(element) {
    let sibling = element.nextElementSibling;
    while (sibling instanceof HTMLElement) {
      const top = elementDocumentTop(sibling);
      if (Number.isFinite(top)) {
        return top;
      }
      sibling = sibling.nextElementSibling;
    }
    return void 0;
  }
  function summarizeEstimateHeightErrors(estimateOnlyModel, measuredModel) {
    const mutableBuckets = /* @__PURE__ */ new Map();
    for (const kind of ESTIMATE_ERROR_KIND_ORDER) {
      mutableBuckets.set(kind, {
        count: 0,
        kind,
        maxAbsError: 0,
        meanAbsError: 0,
        placeholderCount: 0,
        totalAbsError: 0,
        worstOffenders: []
      });
    }
    let count = 0;
    let totalAbsError = 0;
    let maxAbsError = 0;
    let placeholderCount = 0;
    const worstOffenders = [];
    for (const measuredEntry of measuredModel.sections) {
      if (measuredEntry.measuredHeight === void 0) {
        continue;
      }
      const kind = estimateErrorKind(measuredEntry);
      if (measuredEntry.measuredHeightPlaceholder === true) {
        const bucket2 = mutableBuckets.get(kind) ?? {
          count: 0,
          kind,
          maxAbsError: 0,
          meanAbsError: 0,
          placeholderCount: 0,
          totalAbsError: 0,
          worstOffenders: []
        };
        bucket2.placeholderCount++;
        mutableBuckets.set(kind, bucket2);
        placeholderCount++;
        continue;
      }
      const estimateEntry = estimateOnlyModel.getEntryByBlockIndex(measuredEntry.blockIndex);
      if (!estimateEntry) {
        continue;
      }
      const signedError = estimateEntry.estimatedHeight - measuredEntry.measuredHeight;
      const absError = Math.abs(signedError);
      const offender = {
        absError,
        blockIndex: measuredEntry.blockIndex,
        estimatedHeight: estimateEntry.estimatedHeight,
        kind,
        measuredHeight: measuredEntry.measuredHeight,
        sectionIndex: measuredEntry.sectionIndex,
        signedError
      };
      const bucket = mutableBuckets.get(kind) ?? {
        count: 0,
        kind,
        maxAbsError: 0,
        meanAbsError: 0,
        placeholderCount: 0,
        totalAbsError: 0,
        worstOffenders: []
      };
      bucket.count++;
      bucket.totalAbsError += absError;
      bucket.maxAbsError = Math.max(bucket.maxAbsError, absError);
      insertWorstOffender(bucket.worstOffenders, offender);
      mutableBuckets.set(kind, bucket);
      count++;
      totalAbsError += absError;
      maxAbsError = Math.max(maxAbsError, absError);
      insertWorstOffender(worstOffenders, offender);
    }
    const byKind = {};
    for (const [kind, bucket] of mutableBuckets) {
      byKind[kind] = {
        count: bucket.count,
        kind,
        maxAbsError: bucket.maxAbsError,
        meanAbsError: bucket.count === 0 ? 0 : bucket.totalAbsError / bucket.count,
        placeholderCount: bucket.placeholderCount,
        worstOffenders: bucket.worstOffenders
      };
    }
    return {
      byKind,
      count,
      maxAbsError,
      meanAbsError: count === 0 ? 0 : totalAbsError / count,
      placeholderCount,
      worstOffenders
    };
  }
  function readLiveSectionModelEntries(blocks, metrics, documentScrollHeight, measured, options) {
    return readLiveBlockMeasurements(blocks, documentScrollHeight).map((measurement, sectionIndex) => {
      const intrinsicSize = readSectionIntrinsicCalibrationTarget(measurement.element, metrics);
      const entry = {
        blockIndex: measurement.blockIndex,
        cumulativeTop: measurement.top,
        estimatedHeight: options.intrinsicSizeCalibrator?.estimateTargetHeight(intrinsicSize) ?? intrinsicSize.defaultHeight,
        hasMermaid: hasMermaidContent(measurement.element),
        headingLevel: readHeadingLevel2(measurement.element),
        html: measurement.element.outerHTML,
        intrinsicSize,
        kind: normalizeSectionKind(measurement.element.dataset["mmBlockKind"]),
        measuredHeight: measured ? measurement.measuredHeight : void 0,
        sectionIndex
      };
      if (measured && measurement.measuredHeightPlaceholder) {
        entry.measuredHeightPlaceholder = true;
      }
      return entry;
    });
  }
  function readLiveBlockMeasurements(blocks, documentScrollHeight) {
    const geometry = readVisibleBlockGeometry(blocks);
    const safeDocumentScrollHeight = Number.isFinite(documentScrollHeight) ? documentScrollHeight : 0;
    return geometry.map((item, index) => {
      const nextTop = geometry[index + 1]?.top;
      const measuredHeight = nextTop === void 0 ? Math.max(0, item.height, safeDocumentScrollHeight - item.top) : Math.max(0, nextTop - item.top);
      return {
        ...item,
        blockIndex: readBlockIndex2(item.element, item.sourceIndex),
        measuredHeight,
        measuredHeightPlaceholder: isContentVisibilityPlaceholderMeasurement(item)
      };
    });
  }
  function readVisibleBlockGeometry(blocks) {
    const geometry = [];
    for (let sourceIndex = 0; sourceIndex < blocks.length; sourceIndex++) {
      const element = blocks[sourceIndex];
      const top = elementDocumentTop(element);
      const height = element.offsetHeight;
      if (!Number.isFinite(top) || !Number.isFinite(height) || height <= 0) {
        continue;
      }
      geometry.push({ element, height, sourceIndex, top });
    }
    return geometry;
  }
  var CONTENT_VISIBILITY_PLACEHOLDER_TOLERANCE_PX = 1;
  function isContentVisibilityPlaceholderMeasurement(item) {
    if (readCssProperty(item.element, "content-visibility").trim() !== "auto") {
      return false;
    }
    const intrinsicSize = readContainIntrinsicBlockSizePx(item.element);
    if (intrinsicSize === null || Math.abs(item.height - intrinsicSize) > CONTENT_VISIBILITY_PLACEHOLDER_TOLERANCE_PX) {
      return false;
    }
    const viewport = readDocumentViewport(item.element);
    if (viewport === null) {
      return false;
    }
    const bottom = item.top + item.height;
    return bottom < viewport.top - CONTENT_VISIBILITY_PLACEHOLDER_TOLERANCE_PX || item.top > viewport.bottom + CONTENT_VISIBILITY_PLACEHOLDER_TOLERANCE_PX;
  }
  function readContainIntrinsicBlockSizePx(element) {
    const raw = readCssProperty(element, "contain-intrinsic-size");
    const matches = Array.from(raw.matchAll(/(-?\d+(?:\.\d+)?)px/g));
    const lastMatch = matches[matches.length - 1];
    if (!lastMatch) {
      return null;
    }
    const parsed = Number.parseFloat(lastMatch[1]);
    return Number.isFinite(parsed) && parsed >= 0 ? parsed : null;
  }
  function readCssProperty(element, propertyName) {
    const inlineValue = element.style.getPropertyValue(propertyName);
    if (inlineValue.trim().length > 0) {
      return inlineValue;
    }
    const view = element.ownerDocument.defaultView;
    return view?.getComputedStyle(element).getPropertyValue(propertyName) ?? "";
  }
  function readDocumentViewport(element) {
    const doc = element.ownerDocument;
    const view = doc.defaultView;
    const root = doc.scrollingElement ?? doc.documentElement;
    const top = Number.isFinite(root.scrollTop) ? root.scrollTop : view?.scrollY ?? 0;
    const height = root.clientHeight || view?.innerHeight || doc.documentElement.clientHeight;
    if (!Number.isFinite(top) || !Number.isFinite(height) || height <= 0) {
      return null;
    }
    return { bottom: top + height, top };
  }
  function estimateErrorKind(entry) {
    return entry.hasMermaid ? "mermaid" : entry.kind;
  }
  function insertWorstOffender(offenders, offender) {
    offenders.push(offender);
    offenders.sort((a, b) => b.absError - a.absError);
    if (offenders.length > ESTIMATE_ERROR_WORST_OFFENDER_LIMIT) {
      offenders.length = ESTIMATE_ERROR_WORST_OFFENDER_LIMIT;
    }
  }
  function hasMermaidContent(element) {
    return element.classList.contains("mm-mermaid") || element.querySelector("[data-mm-mermaid]") !== null;
  }
  function readBlockIndex2(element, fallback) {
    const raw = element.dataset["mmBlockIndex"];
    const parsed = raw === void 0 ? Number.NaN : Number.parseInt(raw, 10);
    return Number.isFinite(parsed) ? parsed : fallback;
  }
  function readHeadingLevel2(element) {
    const tag = element.tagName.toUpperCase();
    return /^H[1-6]$/.test(tag) ? Number.parseInt(tag.slice(1), 10) : 0;
  }
  function readSectionModelEntryMetadata(entry) {
    const parsed = entry.html ? readSectionHtmlMetadata(entry.html) : EMPTY_SECTION_METADATA;
    return {
      containedBlockIndexes: uniqueNumbers([
        entry.blockIndex,
        ...entry.containedBlockIndexes ?? [],
        ...parsed.containedBlockIndexes
      ]),
      headingAnchors: uniqueStrings([
        ...entry.headingAnchors ?? [],
        ...parsed.headingAnchors
      ]),
      sourceLineSpans: uniqueSourceLineSpans([
        ...entry.sourceLineSpans ?? [],
        ...parsed.sourceLineSpans
      ])
    };
  }
  var EMPTY_SECTION_METADATA = {
    containedBlockIndexes: [],
    headingAnchors: [],
    sourceLineSpans: []
  };
  function readSectionHtmlMetadata(html) {
    if (typeof document === "undefined") {
      return EMPTY_SECTION_METADATA;
    }
    const template = document.createElement("template");
    template.innerHTML = html;
    const elements = Array.from(template.content.querySelectorAll("*"));
    return readSectionElementMetadata(elements);
  }
  function readSectionElementMetadata(elements) {
    const containedBlockIndexes = [];
    const headingAnchors = [];
    const sourceLineSpans = [];
    for (const element of elements) {
      const blockIndex = parseFiniteInt(element.dataset["mmBlockIndex"]);
      if (blockIndex !== null) {
        containedBlockIndexes.push(blockIndex);
      }
      if (/^H[1-6]$/i.test(element.tagName) && element.id.trim().length > 0) {
        headingAnchors.push(element.id);
      }
      const sourceLine = parseNonNegativeInt3(element.dataset["mmSourceLine"]);
      if (sourceLine !== null) {
        const rawEndLine = parseNonNegativeInt3(element.dataset["mmSourceEndLine"]);
        sourceLineSpans.push({
          endLine: Math.max(sourceLine, rawEndLine ?? sourceLine),
          sourceLine
        });
      }
    }
    return {
      containedBlockIndexes,
      headingAnchors,
      sourceLineSpans
    };
  }
  function uniqueNumbers(values) {
    const result = [];
    const seen = /* @__PURE__ */ new Set();
    for (const value of values) {
      if (!Number.isFinite(value) || seen.has(value)) {
        continue;
      }
      seen.add(value);
      result.push(value);
    }
    return result;
  }
  function uniqueStrings(values) {
    const result = [];
    const seen = /* @__PURE__ */ new Set();
    for (const value of values) {
      if (value.length === 0 || seen.has(value)) {
        continue;
      }
      seen.add(value);
      result.push(value);
    }
    return result;
  }
  function uniqueSourceLineSpans(values) {
    const result = [];
    const seen = /* @__PURE__ */ new Set();
    for (const span of values) {
      if (!Number.isFinite(span.sourceLine) || !Number.isFinite(span.endLine)) {
        continue;
      }
      const sourceLine = Math.max(0, Math.floor(span.sourceLine));
      const endLine = Math.max(sourceLine, Math.floor(span.endLine));
      const key = `${sourceLine}:${endLine}`;
      if (seen.has(key)) {
        continue;
      }
      seen.add(key);
      result.push({ endLine, sourceLine });
    }
    return result;
  }
  function normalizeHeadingAnchor(anchor) {
    return anchor.startsWith("#") ? anchor.slice(1) : anchor;
  }
  function parseFiniteInt(value) {
    if (value === void 0 || value.trim() === "") {
      return null;
    }
    const parsed = Number.parseInt(value, 10);
    return Number.isFinite(parsed) ? parsed : null;
  }
  function parseNonNegativeInt3(value) {
    const parsed = parseFiniteInt(value);
    return parsed !== null && parsed >= 0 ? parsed : null;
  }

  // RendererWeb/src/virtualizationFlags.ts
  function readRendererBooleanFlag(input) {
    return isTrueFlagValue(readWindowFlag(input.ownerWindow, input.globalName)) || isTrueFlagValue(input.ownerDocument.documentElement.dataset[input.dataKey]) || isTrueFlagValue(readLocalStorageFlag(input.ownerWindow, input.storageName));
  }
  function readVirtualizationFlag(ownerWindow = window, ownerDocument = document) {
    return readRendererBooleanFlag({
      dataKey: "markmelloVirtualization",
      globalName: "MARKMELLO_VIRTUALIZATION",
      ownerDocument,
      ownerWindow,
      storageName: "MARKMELLO_VIRTUALIZATION"
    });
  }
  function readWindowFlag(ownerWindow, name) {
    return ownerWindow[name];
  }
  function readLocalStorageFlag(ownerWindow, name) {
    try {
      return ownerWindow.localStorage.getItem(name);
    } catch {
      return null;
    }
  }
  function isTrueFlagValue(value) {
    if (value === true) {
      return true;
    }
    if (typeof value !== "string") {
      return false;
    }
    switch (value.trim().toLowerCase()) {
      case "1":
      case "true":
      case "yes":
      case "on":
        return true;
      default:
        return false;
    }
  }

  // RendererWeb/src/virtualizedDocumentWindow.ts
  var TOP_SPACER = "top";
  var BOTTOM_SPACER = "bottom";
  var SPACER_CLASS = "mm-virtual-spacer";
  var EMPTY_HEIGHT_UPDATE = {
    maxAbsDelta: 0,
    totalDelta: 0,
    updatedCount: 0
  };
  function createVirtualizedDocumentWindowController(deps) {
    let currentRange = null;
    const renderAhead = deps.renderAhead ?? DEFAULT_RENDER_AHEAD;
    const renderRange = (range) => {
      const existingByBlockIndex = collectExistingSections(deps.main);
      const nodes = [];
      let insertedCount = 0;
      const topSpacer = createSpacer(deps.ownerWindow.document, TOP_SPACER);
      const bottomSpacer = createSpacer(deps.ownerWindow.document, BOTTOM_SPACER);
      const spacers = deps.model.computeSpacerHeights(range);
      topSpacer.style.height = `${Math.round(spacers.topSpacer)}px`;
      bottomSpacer.style.height = `${Math.round(spacers.bottomSpacer)}px`;
      nodes.push(topSpacer);
      for (let index = range.start; index <= range.end; index++) {
        const entry = deps.model.sections[index];
        if (!entry) {
          continue;
        }
        const existing = existingByBlockIndex.get(entry.blockIndex);
        if (existing) {
          nodes.push(existing);
          continue;
        }
        const created = createSectionNode(deps.ownerWindow.document, entry);
        if (created) {
          insertedCount++;
          nodes.push(created);
        }
      }
      nodes.push(bottomSpacer);
      deps.main.replaceChildren(...nodes);
      currentRange = { ...range };
      if (insertedCount > 0) {
        deps.prepareInsertedContent?.(deps.main);
      }
    };
    const computeRange = () => deps.model.computeWindowRange(deps.root.scrollTop, deps.root.clientHeight, renderAhead);
    const isSectionRendered = (sectionIndex) => currentRange !== null && sectionIndex >= currentRange.start && sectionIndex <= currentRange.end;
    const ensureRangeRendered = (requestedRange, options = {}) => {
      const range = normalizeRequestedRange(requestedRange, deps.model.getSectionCount());
      if (range === null) {
        return false;
      }
      if (options.force !== true && currentRange !== null && rangesEqual(currentRange, range)) {
        return false;
      }
      const anchor = options.preserveAnchor === false ? null : deps.model.captureAnchor(deps.root.scrollTop);
      renderRange(range);
      if (anchor !== null) {
        deps.root.scrollTop = deps.model.scrollTopForAnchor(anchor);
      }
      return true;
    };
    return {
      adoptRenderedHeights: (options = {}) => {
        const preserveSectionIndex = normalizeSectionIndex(options.preserveSectionIndex, deps.model.getSectionCount());
        const anchor = preserveSectionIndex === null ? deps.model.captureAnchor(deps.root.scrollTop) : null;
        const blocks = collectLiveDocumentSectionElements(deps.main);
        const updates = deps.readMeasuredHeights ? deps.readMeasuredHeights(blocks) : readLiveBlockOffsetMeasuredHeights(blocks);
        const result = deps.model.updateMeasuredHeightsByBlockIndex(updates);
        if (result.updatedCount === 0) {
          return EMPTY_HEIGHT_UPDATE;
        }
        if (preserveSectionIndex !== null) {
          deps.root.scrollTop = deps.model.sectionTop(preserveSectionIndex);
          renderRange(computeRange());
          deps.root.scrollTop = deps.model.sectionTop(preserveSectionIndex);
          return result;
        }
        deps.root.scrollTop = deps.model.scrollTopForAnchor(anchor);
        renderRange(computeRange());
        deps.root.scrollTop = deps.model.scrollTopForAnchor(anchor);
        return result;
      },
      ensureSectionRangeRendered: (start, end, options = {}) => ensureRangeRendered({ end, start }, options),
      ensureSectionRendered: (sectionIndex, options = {}) => ensureRangeRendered({ end: sectionIndex, start: sectionIndex }, options),
      getCurrentRange: () => currentRange === null ? null : { ...currentRange },
      isSectionRendered,
      updateWindowForScroll: (options = {}) => {
        const nextRange = computeRange();
        if (options.force !== true && currentRange !== null && rangesEqual(currentRange, nextRange)) {
          return false;
        }
        const anchor = deps.model.captureAnchor(deps.root.scrollTop);
        renderRange(nextRange);
        deps.root.scrollTop = deps.model.scrollTopForAnchor(anchor);
        return true;
      }
    };
  }
  function createFullDocumentFragmentFromWindowModel(ownerDocument, model) {
    const fragment = ownerDocument.createDocumentFragment();
    for (const entry of model.sections) {
      const created = createSectionNode(ownerDocument, entry);
      if (created) {
        fragment.append(created);
      }
    }
    return fragment;
  }
  function collectExistingSections(main) {
    const result = /* @__PURE__ */ new Map();
    for (const element of collectLiveDocumentSectionElements(main)) {
      const raw = element.dataset["mmBlockIndex"];
      const blockIndex = raw === void 0 ? Number.NaN : Number.parseInt(raw, 10);
      if (Number.isFinite(blockIndex)) {
        result.set(blockIndex, element);
      }
    }
    return result;
  }
  function createSpacer(ownerDocument, kind) {
    const spacer = ownerDocument.createElement("div");
    spacer.className = `${SPACER_CLASS} ${SPACER_CLASS}-${kind}`;
    spacer.dataset["mmVirtualSpacer"] = kind;
    spacer.setAttribute("aria-hidden", "true");
    spacer.style.display = "block";
    spacer.style.flex = "0 0 auto";
    spacer.style.pointerEvents = "none";
    return spacer;
  }
  function createSectionNode(ownerDocument, entry) {
    if (!entry.html) {
      return null;
    }
    const template = ownerDocument.createElement("template");
    template.innerHTML = entry.html;
    const firstElement = Array.from(template.content.childNodes).find((node) => node instanceof HTMLElement);
    return firstElement ?? null;
  }
  function rangesEqual(left, right) {
    return left.start === right.start && left.end === right.end;
  }
  function normalizeRequestedRange(range, sectionCount) {
    if (sectionCount <= 0 || !Number.isFinite(range.start) || !Number.isFinite(range.end)) {
      return null;
    }
    const rawStart = Math.floor(Math.min(range.start, range.end));
    const rawEnd = Math.floor(Math.max(range.start, range.end));
    const start = Math.max(0, Math.min(sectionCount - 1, rawStart));
    const end = Math.max(start, Math.min(sectionCount - 1, rawEnd));
    return { end, start };
  }
  function normalizeSectionIndex(sectionIndex, sectionCount) {
    if (sectionIndex === void 0 || sectionCount <= 0 || !Number.isFinite(sectionIndex)) {
      return null;
    }
    return Math.max(0, Math.min(sectionCount - 1, Math.floor(sectionIndex)));
  }

  // RendererWeb/src/virtualizationShadow.ts
  var SHADOW_FLAG_NAME = "MARKMELLO_VIRT_SHADOW";
  function readVirtualizationShadowFlag(ownerWindow = window, ownerDocument = document) {
    return readRendererBooleanFlag({
      dataKey: "markmelloVirtShadow",
      globalName: SHADOW_FLAG_NAME,
      ownerDocument,
      ownerWindow,
      storageName: SHADOW_FLAG_NAME
    });
  }
  function validateVirtualizationShadowGeometry(input) {
    const estimateModel = input.estimateModel ?? input.model;
    const predictedAnchor = input.model.captureAnchor(input.scrollTop);
    const predictedRange = input.model.computeWindowRange(
      input.scrollTop,
      input.viewportHeight,
      input.config ?? DEFAULT_RENDER_AHEAD
    );
    const actualRange = computeLiveBlockWindowRange(
      input.blocks,
      input.scrollTop,
      input.viewportHeight,
      input.config ?? DEFAULT_RENDER_AHEAD
    );
    const productionTopBlockIndex = input.productionTopBlockIndex ?? input.realTopBlockIndex;
    const productionBlocks = input.productionBlocks ?? input.blocks;
    const realTop = findBlockByIndex(input.blocks, input.realTopBlockIndex);
    const realTopSectionIndex = input.realTopBlockIndex === null ? null : input.model.getEntryByBlockIndex(input.realTopBlockIndex)?.sectionIndex ?? null;
    const productionTopSectionIndex = productionTopBlockIndex === null ? null : input.model.getEntryByBlockIndex(productionTopBlockIndex)?.sectionIndex ?? null;
    const productionTop = findBlockByIndex(productionBlocks, productionTopBlockIndex);
    const nestedTopVisibleAnchor = productionTopBlockIndex !== null && productionTop !== null && findBlockByIndex(input.blocks, productionTopBlockIndex) === null;
    const realTopDocumentTop = realTop === null ? null : elementDocumentTop(realTop);
    const realIntraOffset = realTop === null ? null : Math.max(0, input.scrollTop - realTopDocumentTop);
    const anchorBlockIndexMatches = blockIndexMatchesExpectedTop({
      model: input.model,
      predictedSectionIndex: predictedAnchor.sectionIndex,
      predictedTopBlockIndex: predictedAnchor.blockIndex >= 0 ? predictedAnchor.blockIndex : null,
      realIntraOffset,
      realTopBlockIndex: input.realTopBlockIndex
    });
    const intraOffsetDelta = realIntraOffset === null || predictedAnchor.blockIndex !== input.realTopBlockIndex ? null : predictedAnchor.intraOffset - realIntraOffset;
    const topOffsetDelta = realTopDocumentTop === null || realTopSectionIndex === null ? null : input.model.sectionTop(realTopSectionIndex) - realTopDocumentTop;
    const totalHeightDelta = input.model.getTotalHeight() - input.realScrollHeight;
    const estimatedTotalHeightDelta = estimateModel.getTotalHeight() - input.realScrollHeight;
    const windowStartDelta = predictedRange.start - actualRange.start;
    const windowEndDelta = predictedRange.end - actualRange.end;
    const maxAbsPxError = Math.max(
      Math.abs(totalHeightDelta),
      Math.abs(topOffsetDelta ?? 0),
      Math.abs(intraOffsetDelta ?? 0)
    );
    const maxAbsIndexDelta = Math.max(
      Math.abs(windowStartDelta),
      Math.abs(windowEndDelta)
    );
    const estimateHeightError = summarizeEstimateHeightErrors(estimateModel, input.model);
    return {
      actualWindowEnd: actualRange.end,
      actualWindowStart: actualRange.start,
      anchorBlockIndexMatches,
      elapsedMs: 0,
      estimatedTotalHeight: estimateModel.getTotalHeight(),
      estimatedTotalHeightDelta,
      estimateCalibration: input.estimateCalibration ?? emptyEstimateCalibrationSummary(),
      estimateHeightError,
      intraOffsetDelta,
      maxAbsError: maxAbsPxError,
      maxAbsIndexDelta,
      maxAbsPxError,
      nestedTopVisibleAnchor,
      predictedIntraOffset: predictedAnchor.intraOffset,
      predictedTopBlockIndex: predictedAnchor.blockIndex >= 0 ? predictedAnchor.blockIndex : null,
      predictedTopSectionIndex: predictedAnchor.sectionIndex,
      predictedTotalHeight: input.model.getTotalHeight(),
      predictedWindowEnd: predictedRange.end,
      predictedWindowStart: predictedRange.start,
      productionTopBlockIndex,
      productionTopSectionIndex,
      realIntraOffset,
      realScrollHeight: input.realScrollHeight,
      realTopBlockIndex: input.realTopBlockIndex,
      realTopSectionIndex,
      scrollHeightGrowth: 0,
      sectionCount: input.model.getSectionCount(),
      topOffsetDelta,
      totalHeightDelta,
      windowEndDelta,
      windowStartDelta
    };
  }
  function createVirtualizationShadowValidator(deps) {
    let model = null;
    let estimateModel = null;
    let scheduled = false;
    let validationCount = 0;
    let nestedTopVisibleAnchorCount = 0;
    let previousScrollHeight = null;
    let scrollHeightGrowth = 0;
    const intrinsicSizeCalibrator = createSectionIntrinsicCalibrator();
    const validateNow = () => {
      const startedAt = nowMs(deps.ownerWindow);
      if (deps.isDocumentFinal?.() === false) {
        deps.postPerfMark("mm-virt-shadow-validation-skipped", {
          reason: "progressive-append-pending"
        });
        return null;
      }
      const main = deps.ownerDocument.querySelector("main.mm-document");
      const root = deps.ownerDocument.scrollingElement ?? deps.ownerDocument.documentElement;
      const realScrollHeight = root.scrollHeight;
      if (previousScrollHeight !== null) {
        scrollHeightGrowth += Math.max(0, realScrollHeight - previousScrollHeight);
      }
      previousScrollHeight = realScrollHeight;
      const blocks = main ? collectLiveDocumentSectionElements(main) : [];
      if (!main || blocks.length === 0) {
        model = null;
        estimateModel = null;
        intrinsicSizeCalibrator.reset();
        previousScrollHeight = null;
        scrollHeightGrowth = 0;
        return null;
      }
      if (model === null || estimateModel === null) {
        const models = buildDocumentWindowModelsFromLiveBlocks(
          blocks,
          readIntrinsicSizeMetrics(main),
          realScrollHeight,
          { intrinsicSizeCalibrator }
        );
        model = models.measuredModel;
        estimateModel = models.estimateOnlyModel;
        if (model.getSectionCount() === 0) {
          model = null;
          estimateModel = null;
          intrinsicSizeCalibrator.reset();
          return null;
        }
        deps.postPerfMark("mm-virt-shadow-model-built", {
          estimatedTotalHeight: estimateModel.getTotalHeight(),
          estimateHeightError: models.estimateHeightError,
          sectionCount: model.getSectionCount(),
          totalHeight: model.getTotalHeight()
        });
      }
      const productionBlocks = collectLiveDocumentBlockElements(deps.ownerDocument);
      const validation = validateVirtualizationShadowGeometry({
        blocks,
        estimateModel,
        model,
        productionBlocks,
        productionTopBlockIndex: findTopVisibleBlockIndexFromBlocks(productionBlocks, root.scrollTop),
        realScrollHeight,
        realTopBlockIndex: findTopVisibleBlockIndexFromBlocks(blocks, root.scrollTop),
        scrollTop: root.scrollTop,
        viewportHeight: root.clientHeight
      });
      const adopted = model.updateMeasuredHeightsByBlockIndex(
        readLiveBlockMeasuredHeights(blocks, realScrollHeight)
      );
      const calibrationRecordedCount = model.recordIntrinsicSizeCalibrationSamples(intrinsicSizeCalibrator);
      const calibratedEstimate = estimateModel.updateEstimatedHeightsFromCalibration(intrinsicSizeCalibrator);
      const estimateCalibration = intrinsicSizeCalibrator.getSummary();
      validationCount++;
      if (validation.nestedTopVisibleAnchor) {
        nestedTopVisibleAnchorCount++;
      }
      const elapsedMs = Math.max(0, nowMs(deps.ownerWindow) - startedAt);
      const estimatedTotalHeight = estimateModel.getTotalHeight();
      const measuredValidation = {
        ...validation,
        elapsedMs,
        estimatedTotalHeight,
        estimatedTotalHeightDelta: estimatedTotalHeight - realScrollHeight,
        estimateCalibration,
        estimateHeightError: summarizeEstimateHeightErrors(estimateModel, model),
        scrollHeightGrowth
      };
      const detail = {
        ...measuredValidation,
        adoptedMaxAbsDelta: adopted.maxAbsDelta,
        adoptedTotalDelta: adopted.totalDelta,
        adoptedUpdatedCount: adopted.updatedCount,
        calibratedEstimateMaxAbsDelta: calibratedEstimate.maxAbsDelta,
        calibratedEstimateTotalDelta: calibratedEstimate.totalDelta,
        calibratedEstimateUpdatedCount: calibratedEstimate.updatedCount,
        calibrationRecordedCount,
        nestedTopVisibleAnchorCount,
        validationCount
      };
      deps.postPerfMark("mm-virt-shadow-validation", detail);
      deps.postDebugLog(
        `virt-shadow sections=${measuredValidation.sectionCount} totalDelta=${Math.round(measuredValidation.totalHeightDelta)} estimateDelta=${Math.round(measuredValidation.estimatedTotalHeightDelta)} topModel=${measuredValidation.predictedTopBlockIndex ?? "null"} topReal=${measuredValidation.realTopBlockIndex ?? "null"} topProd=${measuredValidation.productionTopBlockIndex ?? "null"} nested=${nestedTopVisibleAnchorCount}/${validationCount} topDelta=${measuredValidation.topOffsetDelta === null ? "null" : Math.round(measuredValidation.topOffsetDelta)} intraDelta=${measuredValidation.intraOffsetDelta === null ? "null" : Math.round(measuredValidation.intraOffsetDelta)} estimateMeanErr=${Math.round(measuredValidation.estimateHeightError.meanAbsError)} estimateMaxErr=${Math.round(measuredValidation.estimateHeightError.maxAbsError)} window=${measuredValidation.predictedWindowStart}..${measuredValidation.predictedWindowEnd}/${measuredValidation.actualWindowStart}..${measuredValidation.actualWindowEnd} maxPx=${Math.round(measuredValidation.maxAbsPxError)} maxIndex=${Math.round(measuredValidation.maxAbsIndexDelta)} scrollGrowth=${Math.round(measuredValidation.scrollHeightGrowth)} elapsedMs=${Math.round(elapsedMs)}`
      );
      return measuredValidation;
    };
    return {
      invalidate: () => {
        model = null;
        estimateModel = null;
        intrinsicSizeCalibrator.reset();
        previousScrollHeight = null;
        scrollHeightGrowth = 0;
      },
      schedule: () => {
        if (scheduled) {
          return;
        }
        scheduled = true;
        scheduleIdle(deps.ownerWindow, () => {
          scheduled = false;
          validateNow();
        });
      },
      validateNow
    };
  }
  function scheduleIdle(ownerWindow, callback) {
    const requestIdle = ownerWindow.requestIdleCallback;
    if (requestIdle) {
      requestIdle(callback, { timeout: 500 });
      return;
    }
    ownerWindow.setTimeout(callback, 120);
  }
  function findBlockByIndex(blocks, blockIndex) {
    if (blockIndex === null) {
      return null;
    }
    for (const block of blocks) {
      const raw = block.dataset["mmBlockIndex"];
      const parsed = raw === void 0 ? Number.NaN : Number.parseInt(raw, 10);
      if (parsed === blockIndex) {
        return block;
      }
    }
    return null;
  }
  function blockIndexMatchesExpectedTop(input) {
    if (input.predictedTopBlockIndex === input.realTopBlockIndex) {
      return true;
    }
    if (input.realTopBlockIndex === null || input.realIntraOffset !== 0) {
      return false;
    }
    const realTopEntry = input.model.getEntryByBlockIndex(input.realTopBlockIndex);
    return realTopEntry !== void 0 && input.predictedSectionIndex === realTopEntry.sectionIndex - 1;
  }
  function nowMs(ownerWindow) {
    const performanceNow = ownerWindow.performance?.now;
    return typeof performanceNow === "function" ? performanceNow.call(ownerWindow.performance) : Date.now();
  }
  function emptyEstimateCalibrationSummary() {
    return {
      bucketCount: 0,
      byKind: {},
      calibratedBucketCount: 0,
      sampleCount: 0
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
  var MODE_REVEAL_EASING = "cubic-bezier(0.215, 0.61, 0.355, 1)";
  var MODE_SETTLE_VIEWPORT_TOLERANCE = 2;
  var MODE_SETTLE_VIEWPORT_MAX_FRAMES = 18;
  var VIRTUALIZED_NAVIGATION_SETTLE_TOLERANCE_PX = 0.5;
  var VIRTUALIZED_NAVIGATION_SETTLE_STABLE_FRAMES = 2;
  var VIRTUALIZED_NAVIGATION_SETTLE_MAX_FRAMES = 120;
  var minimapMode = "off";
  var hasReceivedHostPreferences = false;
  var hasInitialLayoutSettled = false;
  var minimapViewportFrameRequested = false;
  var minimapRefreshTimer;
  var minimapContentRefreshTimer;
  var minimapDeferredContentRefreshHandle = null;
  var progressiveMinimapRefreshGeneration = 0;
  var cachedGeometryRefreshTimer;
  var mermaidCacheResumeTimer;
  var resizeReactFrameRequested = false;
  var modeToggleProbeFrameRequested = false;
  var modeToggleSettleSequence = 0;
  var modeToggleProbeTransactionGeneration;
  var modeRevealPrepared = false;
  var modeRevealShield = null;
  var documentRevealShield = null;
  var minimapRoot = null;
  var minimapContent = null;
  var minimapViewport = null;
  var currentMinimapLayout = null;
  var minimapDragging = false;
  var minimapDragStartClientY = null;
  var minimapDragStartScrollTop = 0;
  var minimapDragMode = "tentative";
  var minimapDragGrabOffset = 0;
  var MINIMAP_DRAG_THRESHOLD_PX = 4;
  var minimapSourceReady = false;
  var mermaidRenderGeneration = 0;
  var mermaidLazyObserver = null;
  var mermaidLazyRenderQueue = Promise.resolve();
  var themeMermaidRefreshGeneration = 0;
  var themeMermaidRefreshTimer;
  var themeAppliedAckGeneration = 0;
  var initialRenderPipelineGeneration = 0;
  var initialRenderPipelineCompleted = false;
  var firstPrefsBootstrapSuppressedByLoadGeneration = null;
  var postReadyEnhancementsCompleted = false;
  var currentController = null;
  var MERMAID_PER_DIAGRAM_TIMEOUT_MS = 3e3;
  var MERMAID_WATCHDOG_MS = 15e3;
  var MERMAID_EAGER_VIEWPORT_MARGIN_PX = 700;
  var MERMAID_LAZY_ROOT_MARGIN_PX = 1400;
  var THEME_MERMAID_REFRESH_DELAY_MS = 160;
  var THEME_APPLIED_ACK_FALLBACK_MS = 120;
  var POST_LAYOUT_READY_EDIT_PREVIEW_DELAY_MS = 120;
  var widthResizerVisibility = "on-hover";
  var viewerChromeEnabled = false;
  var documentScrollEnabled = true;
  var wheelProxyEnabled = false;
  var findBarController = null;
  var widthHandleRoot = null;
  var widthHandleDragging = false;
  var widthHandleStartClientX = 0;
  var widthHandleStartMaxWidth = 0;
  var pendingWidthDragDeltaX = 0;
  var widthHandleDragStartLeft = 0;
  var widthHandleDragHitArea = 24;
  var widthHandleDragMinimapReservedWidth = 0;
  var widthDragFrameRequested = false;
  var widthDragApplyFrameRequested = false;
  var widthDragPerfStartTime;
  var widthDragPerfMoveEvents = 0;
  var widthDragPerfMovePosts = 0;
  var widthDragPerfApplyFrames = 0;
  var widthDragPerfMaxApplyMs = 0;
  var widthDragPerfStartMaxWidth = 0;
  var widthDragPerfLastMaxWidth = 0;
  var layoutReadyGeneration = 0;
  var layoutReadyTimer;
  var postLayoutReadyWorkQueue = [];
  var lastPostedMinimapState = { hasPosted: false, visible: false, reservedWidth: 0 };
  var minimapPolicy = null;
  var sourceLineAnchors = [];
  var pendingSourceLineTarget = null;
  var previewSourceLineFrameRequested = false;
  var suppressPreviewSourceLineEmit = false;
  var suppressPreviewSourceLineSequence = 0;
  var lastPostedPreviewSourceLine = null;
  var liveDocumentBlockElements = [];
  var liveDocumentBlockElementsStale = true;
  var virtualizationEnabled = readVirtualizationFlag(window, document);
  var virtualizationShadowEnabled = readVirtualizationShadowFlag(window, document);
  var virtualizationShadowValidator = null;
  var virtualizationShadowDocumentFinal = true;
  var virtualizedDocumentWindowController = null;
  var virtualizedDocumentWindowModel = null;
  var virtualizedFindProvider = null;
  var virtualizedIntrinsicCalibrator = createSectionIntrinsicCalibrator();
  var virtualizedMeasureFrameRequested = false;
  var virtualizedCalibrationHandle = null;
  var virtualizedProgrammaticNavigationInProgress = false;
  var virtualizedProgrammaticNavigationGeneration = 0;
  var virtualizedProgrammaticNavigationPostSettleTarget = null;
  var virtualizedWindowMathController = null;
  var PROCESSED_DOCUMENT_CACHE_LIMIT = 4;
  function cloneHeadingPayload(heading) {
    return {
      ...heading,
      segments: heading.segments.map((segment) => ({ ...segment }))
    };
  }
  var processedDocumentCache = /* @__PURE__ */ new Map();
  var currentDocumentCacheKey = null;
  var currentDocumentRenderId = null;
  var restoredCachedLayoutState = null;
  var restoredCachedHeadings = null;
  var restoredCachedMinimapSnapshot = null;
  var processedDocumentCacheCloneGeneration = 0;
  var processedDocumentCacheCloneHandle = null;
  var lastExtractedHeadings = [];
  var lastKnownLayoutState = {
    scrollTop: 0,
    scrollHeight: 0,
    clientHeight: 0,
    topBlockIndex: null
  };
  function hashDocumentHtml(html) {
    let hash = 2166136261;
    for (let index = 0; index < html.length; index++) {
      hash ^= html.charCodeAt(index);
      hash = Math.imul(hash, 16777619);
    }
    return (hash >>> 0).toString(16).padStart(8, "0");
  }
  function createProcessedDocumentCacheKey(html, theme) {
    return `${theme}|${html.length}|${hashDocumentHtml(html)}`;
  }
  function getCachedProcessedDocumentFragment(cacheKey) {
    const cached = processedDocumentCache.get(cacheKey);
    if (cached === void 0) {
      return void 0;
    }
    processedDocumentCache.delete(cacheKey);
    processedDocumentCache.set(cacheKey, cached);
    restoredCachedLayoutState = { ...cached.layoutState };
    restoredCachedHeadings = cached.headings.map(cloneHeadingPayload);
    restoredCachedMinimapSnapshot = cached.minimapSnapshot;
    return cached.fragment.cloneNode(true);
  }
  function setCurrentProcessedDocumentCacheKey(cacheKey) {
    currentDocumentCacheKey = cacheKey;
  }
  function cancelProcessedDocumentCacheClone() {
    if (!processedDocumentCacheCloneHandle) {
      return;
    }
    const handle = processedDocumentCacheCloneHandle;
    processedDocumentCacheCloneHandle = null;
    if (handle.kind === "idle") {
      window.cancelIdleCallback?.(handle.id);
    } else {
      window.clearTimeout(handle.id);
    }
  }
  function captureCurrentProcessedDocumentCacheEntry(mode) {
    const main = document.querySelector("main.mm-document");
    if (!main || main.childNodes.length === 0) {
      return null;
    }
    const virtualizedFullFragment = virtualizationEnabled && virtualizedDocumentWindowModel !== null ? createFullDocumentFragmentFromWindowModel(document, virtualizedDocumentWindowModel) : null;
    const sourceNodes = Array.from(virtualizedFullFragment?.childNodes ?? main.childNodes);
    const fragment = document.createDocumentFragment();
    if (mode === "clone" || virtualizedFullFragment !== null) {
      const clones = sourceNodes.map((node) => node.cloneNode(true));
      for (let index = 0; index < sourceNodes.length; index++) {
        const live = sourceNodes[index];
        const clone = clones[index];
        if (live instanceof HTMLElement && clone instanceof HTMLElement) {
          const settledHeight = live.offsetHeight;
          if (settledHeight > 0) {
            clone.style.containIntrinsicSize = `auto ${settledHeight}px`;
          }
        }
      }
      fragment.append(...clones);
    } else {
      const settledHeights = sourceNodes.map((node) => node instanceof HTMLElement ? node.offsetHeight : 0);
      for (let index = 0; index < sourceNodes.length; index++) {
        const node = sourceNodes[index];
        const settledHeight = settledHeights[index] ?? 0;
        if (node instanceof HTMLElement && settledHeight > 0) {
          node.style.containIntrinsicSize = `auto ${settledHeight}px`;
        }
      }
      fragment.append(...sourceNodes);
    }
    const minimapSnapshot = captureMinimapSnapshot({
      ownerDocument: document,
      minimapContent,
      minimapViewport,
      documentHeight: minimapDocumentHeight,
      lastPostedState: lastPostedMinimapState
    });
    return {
      fragment,
      nodeCount: sourceNodes.length,
      layoutState: { ...lastKnownLayoutState },
      headings: lastExtractedHeadings.map(cloneHeadingPayload),
      minimapSnapshot
    };
  }
  function storeProcessedDocumentCacheEntry(cacheKey, entry) {
    processedDocumentCache.delete(cacheKey);
    processedDocumentCache.set(cacheKey, entry);
    while (processedDocumentCache.size > PROCESSED_DOCUMENT_CACHE_LIMIT) {
      const oldest = processedDocumentCache.keys().next().value;
      if (oldest === void 0) {
        break;
      }
      processedDocumentCache.delete(oldest);
    }
  }
  function cachedFragmentIsBehindLiveDocument(cached) {
    const main = document.querySelector("main.mm-document");
    if (!main) {
      return false;
    }
    if (main.childNodes.length > cached.nodeCount) {
      return true;
    }
    const liveHeadingCount = main.querySelectorAll("h1,h2,h3,h4,h5,h6").length;
    if (liveHeadingCount > cached.headings.length) {
      return true;
    }
    return cached.minimapSnapshot === null && minimapContent !== null && minimapContent.childNodes.length > 0;
  }
  function refreshProcessedDocumentCacheState(cacheKey, markName) {
    const cached = processedDocumentCache.get(cacheKey);
    if (cached === void 0) {
      return false;
    }
    if (cachedFragmentIsBehindLiveDocument(cached)) {
      return false;
    }
    const minimapSnapshot = captureMinimapSnapshot({
      ownerDocument: document,
      minimapContent,
      minimapViewport,
      documentHeight: minimapDocumentHeight,
      lastPostedState: lastPostedMinimapState
    });
    processedDocumentCache.delete(cacheKey);
    processedDocumentCache.set(cacheKey, {
      ...cached,
      layoutState: { ...lastKnownLayoutState },
      headings: lastExtractedHeadings.map(cloneHeadingPayload),
      minimapSnapshot
    });
    postPerfMark(markName, {
      entries: processedDocumentCache.size,
      nodeCount: cached.nodeCount
    });
    return true;
  }
  function scheduleCurrentProcessedDocumentCacheClone(delayMs = 240) {
    const cacheKey = currentDocumentCacheKey;
    if (!cacheKey || !initialRenderPipelineCompleted || !postReadyEnhancementsCompleted) {
      return;
    }
    const generation = ++processedDocumentCacheCloneGeneration;
    cancelProcessedDocumentCacheClone();
    const run = () => {
      if (generation !== processedDocumentCacheCloneGeneration || currentDocumentCacheKey !== cacheKey) {
        return;
      }
      processedDocumentCacheCloneHandle = null;
      const entry = captureCurrentProcessedDocumentCacheEntry("clone");
      if (!entry) {
        return;
      }
      storeProcessedDocumentCacheEntry(cacheKey, entry);
      postPerfMark("mm-document-cache-prestore", {
        entries: processedDocumentCache.size,
        nodeCount: entry.nodeCount
      });
    };
    const requestIdle = window.requestIdleCallback;
    if (requestIdle) {
      processedDocumentCacheCloneHandle = {
        kind: "idle",
        id: requestIdle(run, { timeout: Math.max(delayMs, 1200) })
      };
    } else {
      processedDocumentCacheCloneHandle = {
        kind: "timeout",
        id: window.setTimeout(run, delayMs)
      };
    }
  }
  function preserveCurrentProcessedDocument() {
    if (!currentDocumentCacheKey || !initialRenderPipelineCompleted || !postReadyEnhancementsCompleted) {
      return;
    }
    const cacheKey = currentDocumentCacheKey;
    cancelProcessedDocumentCacheClone();
    if (refreshProcessedDocumentCacheState(cacheKey, "mm-document-cache-refresh")) {
      currentDocumentCacheKey = null;
      return;
    }
    const entry = captureCurrentProcessedDocumentCacheEntry("move");
    if (!entry) {
      return;
    }
    storeProcessedDocumentCacheEntry(cacheKey, entry);
    currentDocumentCacheKey = null;
    postPerfMark("mm-document-cache-store", {
      entries: processedDocumentCache.size,
      nodeCount: entry.nodeCount
    });
  }
  function applyViewerChromeState() {
    document.documentElement.dataset.mmChrome = viewerChromeEnabled ? "on" : "off";
  }
  function applyDocumentScrollState() {
    document.documentElement.dataset.mmDocumentScroll = documentScrollEnabled ? "on" : "off";
    if (!documentScrollEnabled) {
      window.scrollTo({ left: 0, top: 0, behavior: "instant" });
    }
  }
  function clampModeRevealDuration(durationMs) {
    return typeof durationMs === "number" && Number.isFinite(durationMs) ? Math.max(0, Math.min(600, Math.round(durationMs))) : 0;
  }
  function getModeRevealTarget() {
    return document.querySelector("main.mm-document");
  }
  function getRevealShieldBackground(theme = getCurrentTheme()) {
    const bodyBackground = window.getComputedStyle(document.body).backgroundColor;
    if (bodyBackground && bodyBackground !== "rgba(0, 0, 0, 0)" && bodyBackground !== "transparent") {
      return bodyBackground;
    }
    return theme === "dark" ? "#11100d" : "#ffffff";
  }
  function getModeRevealShieldBackground() {
    return getRevealShieldBackground();
  }
  function getThemeRevealShieldBackground(theme) {
    if (theme === "dark") {
      return "#11100d";
    }
    if (theme === "classic-white") {
      return "#ffffff";
    }
    return "#fcfaf6";
  }
  function ensureModeRevealShield() {
    if (modeRevealShield && modeRevealShield.isConnected) {
      return modeRevealShield;
    }
    modeRevealShield = document.createElement("div");
    modeRevealShield.className = "mm-mode-reveal-shield";
    modeRevealShield.setAttribute("aria-hidden", "true");
    modeRevealShield.style.position = "fixed";
    modeRevealShield.style.inset = "0";
    modeRevealShield.style.zIndex = "2147483647";
    modeRevealShield.style.pointerEvents = "none";
    document.body.append(modeRevealShield);
    postPerfMark("mm-mode-reveal-shield-created", {
      viewportWidth: window.innerWidth,
      viewportHeight: window.innerHeight
    });
    return modeRevealShield;
  }
  function clearModeRevealShield() {
    if (modeRevealShield) {
      postPerfMark("mm-mode-reveal-shield-cleared", {
        connected: modeRevealShield.isConnected,
        opacity: modeRevealShield.style.opacity
      });
    }
    modeRevealShield?.remove();
    modeRevealShield = null;
  }
  function ensureDocumentRevealShield() {
    if (documentRevealShield && documentRevealShield.isConnected) {
      return documentRevealShield;
    }
    documentRevealShield = document.createElement("div");
    documentRevealShield.className = "mm-document-reveal-shield";
    documentRevealShield.setAttribute("aria-hidden", "true");
    documentRevealShield.style.position = "fixed";
    documentRevealShield.style.inset = "0";
    documentRevealShield.style.zIndex = "2147483646";
    documentRevealShield.style.pointerEvents = "none";
    document.body.append(documentRevealShield);
    postPerfMark("mm-document-reveal-shield-created", {
      viewportWidth: window.innerWidth,
      viewportHeight: window.innerHeight
    });
    return documentRevealShield;
  }
  function clearDocumentRevealShield() {
    if (documentRevealShield) {
      postPerfMark("mm-document-reveal-shield-cleared", {
        connected: documentRevealShield.isConnected,
        opacity: documentRevealShield.style.opacity
      });
    }
    documentRevealShield?.remove();
    documentRevealShield = null;
  }
  function prepareDocumentReveal(durationMs, theme) {
    const shield = ensureDocumentRevealShield();
    shield.style.background = theme ? getThemeRevealShieldBackground(theme) : getRevealShieldBackground();
    shield.style.opacity = "1";
    shield.style.transition = "none";
    postPerfMark("mm-document-reveal-shield-prepared", {
      durationMs: clampModeRevealDuration(durationMs),
      viewportWidth: window.innerWidth,
      viewportHeight: window.innerHeight,
      connected: shield.isConnected
    });
  }
  function startDocumentReveal(durationMs) {
    postPerfMark("mm-document-reveal-start", {
      durationMs: clampModeRevealDuration(durationMs),
      hasShield: documentRevealShield !== null,
      shieldConnected: documentRevealShield?.isConnected ?? false
    });
    const duration = clampModeRevealDuration(durationMs);
    if (duration <= 0) {
      clearDocumentRevealShield();
      return;
    }
    if (documentRevealShield) {
      void documentRevealShield.offsetWidth;
      documentRevealShield.style.transition = `opacity ${duration}ms ${MODE_REVEAL_EASING}`;
      documentRevealShield.style.opacity = "0";
    }
    window.setTimeout(clearDocumentRevealShield, duration);
  }
  function prepareModeReveal(durationMs) {
    modeRevealPrepared = true;
    const shield = ensureModeRevealShield();
    shield.style.background = getModeRevealShieldBackground();
    shield.style.opacity = "1";
    shield.style.transition = "none";
    postPerfMark("mm-mode-reveal-shield-prepared", {
      durationMs: clampModeRevealDuration(durationMs),
      viewportWidth: window.innerWidth,
      viewportHeight: window.innerHeight,
      connected: shield.isConnected
    });
    const target = getModeRevealTarget();
    if (!target) {
      postPerfMark("mm-mode-reveal-prepare-missing-target");
      return;
    }
    const duration = clampModeRevealDuration(durationMs);
    target.style.transition = "none";
    target.style.opacity = "1";
    target.style.transform = duration > 0 ? "translateY(4px)" : "";
    target.style.willChange = duration > 0 ? "transform" : "";
  }
  function startModeReveal(durationMs) {
    modeRevealPrepared = false;
    const target = getModeRevealTarget();
    postPerfMark("mm-mode-reveal-start", {
      durationMs: clampModeRevealDuration(durationMs),
      hasShield: modeRevealShield !== null,
      shieldConnected: modeRevealShield?.isConnected ?? false,
      hasTarget: target !== null
    });
    if (!target) {
      clearModeRevealShield();
      return;
    }
    const duration = clampModeRevealDuration(durationMs);
    if (duration <= 0) {
      clearModeRevealShield();
      target.style.transition = "none";
      target.style.opacity = "1";
      target.style.transform = "";
      target.style.willChange = "";
      return;
    }
    void target.offsetWidth;
    target.style.transition = `transform ${duration}ms ${MODE_REVEAL_EASING}`;
    target.style.opacity = "1";
    target.style.transform = "translateY(0)";
    if (modeRevealShield) {
      void modeRevealShield.offsetWidth;
      modeRevealShield.style.transition = `opacity ${duration}ms ${MODE_REVEAL_EASING}`;
      modeRevealShield.style.opacity = "0";
    }
    window.setTimeout(() => {
      if (target.style.transition.includes("transform")) {
        target.style.transition = "";
        target.style.transform = "";
        target.style.willChange = "";
      }
      clearModeRevealShield();
    }, duration);
  }
  function postHostMessage(message) {
    const serialized = JSON.stringify(message);
    if (hostWindow.chrome?.webview) {
      hostWindow.chrome.webview.postMessage(message);
      return;
    }
    hostWindow.invokeCSharpAction?.(serialized);
  }
  function postDebugLog(text) {
    postHostMessage({ type: "debug-log", text });
  }
  function postPerfMark(name, detail) {
    const message = { type: "perf-mark", name };
    if (detail !== void 0) {
      try {
        message.detail = JSON.stringify(detail);
      } catch {
      }
    }
    postHostMessage(message);
  }
  function countFailedInSet(nodes) {
    let count = 0;
    for (const node of nodes) {
      if (node.dataset["mmMathRendered"] === "failed") count++;
    }
    return count;
  }
  function hasUnrenderedDocumentMath() {
    return document.querySelector(".mm-document [data-tex]:not([data-mm-math-rendered])") !== null;
  }
  function renderMath2() {
    emitMark("mm-render-math-start", { mathCount: document.querySelectorAll("[data-tex]").length });
    const katex = hostWindow.katex ?? void 0;
    const controller = renderMath({ katex, documentRoot: document });
    const phaseBDocumentCacheKey = currentDocumentCacheKey;
    const initialVisualSettleReady = virtualizationEnabled ? controller.allMathRendered.then(() => {
      if (phaseBDocumentCacheKey !== currentDocumentCacheKey || controller.isCancelled()) {
        return;
      }
      if (getModelMinimapSource() !== null && minimapSourceReady) {
        syncModelMinimapCloneMetadata();
        updateMinimapViewport({ skipVisibilityUpdate: true });
      }
    }) : schedulePhaseBRebuild({
      allMathRendered: controller.allMathRendered,
      getCurrentDocumentHeight: () => (document.scrollingElement ?? document.documentElement).scrollHeight,
      getCachedDocumentHeight: () => minimapDocumentHeight,
      refresh: (phase) => {
        if (phaseBDocumentCacheKey !== currentDocumentCacheKey || controller.isCancelled()) {
          return;
        }
        refreshMinimapContent(phase);
      }
    });
    const readinessController = {
      ...controller,
      initialVisualSettleReady
    };
    currentController = readinessController;
    controller.initialVisibleReady.then(() => {
      emitMark("mm-initial-visible-ready", {
        visibleCount: controller.initialVisibleNodes.size,
        failedCount: countFailedInSet(controller.initialVisibleNodes)
      });
      postPerfMark("mm-initial-visible-ready", {
        visibleCount: controller.initialVisibleNodes.size,
        failedCount: countFailedInSet(controller.initialVisibleNodes)
      });
      refreshInitialVisibleMinimapContent();
      hasInitialLayoutSettled = true;
      updateWidthHandlePositionForCurrentLayout();
      invalidateSourceLineAnchors();
    });
    controller.allMathRendered.then(() => {
      invalidateSourceLineAnchors();
      const allMathNodes = Array.from(document.querySelectorAll("[data-tex]"));
      emitMark("mm-all-math-rendered", {
        totalCount: controller.totalMathCount,
        failedCount: countFailedInSet(allMathNodes),
        cancelled: controller.isCancelled()
      });
    });
    return readinessController;
  }
  function getCurrentTheme() {
    const theme = document.documentElement.dataset.theme;
    return theme === "dark" || theme === "classic-white" ? theme : "light";
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
  async function renderMermaidNodes(allNodes, mermaid, perfMarkName = "mm-mermaid-visible-first") {
    if (allNodes.length === 0) return;
    const generation = ++mermaidRenderGeneration;
    mermaidLazyRenderQueue = Promise.resolve();
    const viewportHeight = getViewportHeightForMermaid();
    const eagerNodes = allNodes.filter((node) => isMermaidNodeNearViewport(node, viewportHeight, MERMAID_EAGER_VIEWPORT_MARGIN_PX));
    const eagerSet = new Set(eagerNodes);
    const lazyNodes = allNodes.filter((node) => !eagerSet.has(node));
    postPerfMark(perfMarkName, {
      total: allNodes.length,
      eager: eagerNodes.length,
      lazy: lazyNodes.length
    });
    installLazyMermaidObserver(lazyNodes, generation, mermaid);
    if (eagerNodes.length === 0) return;
    let eagerBudgetExpired = false;
    const watchdog = window.setTimeout(() => {
      eagerBudgetExpired = true;
    }, MERMAID_WATCHDOG_MS);
    try {
      for (const node of eagerNodes) {
        await renderMermaidNode(node, generation, () => mermaidRenderGeneration, mermaid, MERMAID_PER_DIAGRAM_TIMEOUT_MS);
        if (eagerBudgetExpired || generation !== mermaidRenderGeneration) return;
      }
    } finally {
      window.clearTimeout(watchdog);
    }
  }
  async function renderMermaid() {
    disconnectMermaidLazyObserver();
    const mermaid = hostWindow.mermaid;
    if (!mermaid) return;
    const allNodes = Array.from(document.querySelectorAll("pre.mm-mermaid"));
    await renderMermaidNodes(allNodes, mermaid);
  }
  function scheduleCachedMermaidResume(hasMermaid) {
    if (hasMermaid === false) {
      return;
    }
    const cacheKey = currentDocumentCacheKey;
    window.clearTimeout(mermaidCacheResumeTimer);
    mermaidCacheResumeTimer = window.setTimeout(() => {
      mermaidCacheResumeTimer = void 0;
      if (cacheKey !== currentDocumentCacheKey) {
        return;
      }
      disconnectMermaidLazyObserver();
      const mermaid = hostWindow.mermaid;
      if (!mermaid) {
        postPerfMark("mm-mermaid-cache-resume-skipped", { reason: "no-mermaid-api" });
        return;
      }
      const missingNodes = Array.from(document.querySelectorAll("pre.mm-mermaid:not(.is-rendered)"));
      if (missingNodes.length === 0) {
        postPerfMark("mm-mermaid-cache-resume-skipped", { reason: "all-rendered" });
        return;
      }
      void renderMermaidNodes(missingNodes, mermaid, "mm-mermaid-cache-resume");
    }, 0);
  }
  function scheduleProgressiveDeferredEnhancements(message) {
    const renderId = message.renderId;
    const run = () => {
      if (renderId !== void 0 && currentDocumentRenderId !== null && renderId !== currentDocumentRenderId) {
        postPerfMark("mm-progressive-enhancements-stale", {
          renderId,
          currentRenderId: currentDocumentRenderId
        });
        return;
      }
      postPerfMark("mm-progressive-enhancements-start", {
        renderId: renderId ?? null
      });
      renderMath2();
      scheduleCachedMermaidResume(message.hasMermaid);
      scheduleCurrentProcessedDocumentCacheClone(1200);
      postPerfMark("mm-progressive-enhancements-end", {
        renderId: renderId ?? null
      });
    };
    const requestIdle = window.requestIdleCallback;
    if (requestIdle) {
      requestIdle(run, { timeout: 4e3 });
      return;
    }
    window.setTimeout(run, 800);
  }
  function getViewportHeightForMermaid() {
    const root = document.scrollingElement ?? document.documentElement;
    return root.clientHeight || window.innerHeight || 0;
  }
  function disconnectMermaidLazyObserver() {
    mermaidLazyObserver?.disconnect();
    mermaidLazyObserver = null;
  }
  function installLazyMermaidObserver(nodes, generation, mermaid) {
    if (nodes.length === 0) return;
    postPerfMark("mm-mermaid-lazy-observe", {
      total: nodes.length,
      rootMarginPx: MERMAID_LAZY_ROOT_MARGIN_PX
    });
    if (typeof window.IntersectionObserver !== "function") {
      for (const node of nodes) {
        enqueueLazyMermaidRender(node, generation, mermaid);
      }
      return;
    }
    mermaidLazyObserver = new IntersectionObserver((entries) => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue;
        const node = entry.target;
        mermaidLazyObserver?.unobserve(node);
        enqueueLazyMermaidRender(node, generation, mermaid);
      }
    }, {
      root: null,
      rootMargin: `${MERMAID_LAZY_ROOT_MARGIN_PX}px 0px ${MERMAID_LAZY_ROOT_MARGIN_PX}px 0px`,
      threshold: 0
    });
    for (const node of nodes) {
      mermaidLazyObserver.observe(node);
    }
  }
  function enqueueLazyMermaidRender(node, generation, mermaid) {
    if (generation !== mermaidRenderGeneration) return;
    const marker = String(generation);
    if (node.dataset.mmMermaidRenderQueued === marker) return;
    node.dataset.mmMermaidRenderQueued = marker;
    mermaidLazyRenderQueue = mermaidLazyRenderQueue.catch(() => void 0).then(async () => {
      if (generation !== mermaidRenderGeneration) return;
      postPerfMark("mm-mermaid-lazy-render-start");
      await renderMermaidNode(node, generation, () => mermaidRenderGeneration, mermaid, MERMAID_PER_DIAGRAM_TIMEOUT_MS);
      if (generation === mermaidRenderGeneration) {
        postPerfMark("mm-mermaid-lazy-render-end");
        scheduleCurrentProcessedDocumentCacheClone();
      }
    });
  }
  function renderCodeBlocks(root = document) {
    const hljs = hostWindow.hljs;
    if (!hljs) return;
    const nodes = Array.from(root.querySelectorAll("code[data-mm-code], code[data-mm-mermaid]"));
    for (const node of nodes) {
      if (node.dataset["mmHighlighted"] === "true") {
        continue;
      }
      const langClass = Array.from(node.classList).find((c) => c.startsWith("language-"));
      const rawLang = langClass?.slice("language-".length);
      const normalized = normalizeHljsLanguage(rawLang);
      if (!hljs.getLanguage(normalized)) {
        node.dataset["mmHighlighted"] = "true";
        continue;
      }
      if (langClass && langClass !== `language-${normalized}`) {
        node.classList.remove(langClass);
        node.classList.add(`language-${normalized}`);
      }
      try {
        hljs.highlightElement(node);
      } catch {
      }
      node.dataset["mmHighlighted"] = "true";
    }
  }
  function deferPostReadyEnhancements(work) {
    postLayoutReadyWorkQueue.push({ generation: layoutReadyGeneration, work });
  }
  function postPostReadyEnhancementsComplete(renderId, hasMermaid, hasHljs) {
    postReadyEnhancementsCompleted = true;
    const message = {
      type: "post-ready-enhancements-complete",
      hasMermaid: hasMermaid === true,
      hasHljs: hasHljs === true
    };
    if (renderId !== void 0) {
      message.renderId = renderId;
    }
    postHostMessage(message);
    scheduleCurrentProcessedDocumentCacheClone();
  }
  function hasMermaidNodes() {
    return document.querySelector("pre.mm-mermaid") !== null;
  }
  function scheduleThemeMermaidRefresh(theme) {
    const generation = ++themeMermaidRefreshGeneration;
    ++mermaidRenderGeneration;
    if (themeMermaidRefreshTimer !== void 0) {
      window.clearTimeout(themeMermaidRefreshTimer);
      themeMermaidRefreshTimer = void 0;
    }
    if (!hostWindow.mermaid || !hasMermaidNodes()) {
      postPerfMark("mm-theme-mermaid-refresh-skipped", {
        theme,
        reason: hostWindow.mermaid ? "no-mermaid-nodes" : "no-mermaid-api"
      });
      return;
    }
    postPerfMark("mm-theme-mermaid-refresh-scheduled", {
      theme,
      delayMs: THEME_MERMAID_REFRESH_DELAY_MS
    });
    themeMermaidRefreshTimer = window.setTimeout(() => {
      themeMermaidRefreshTimer = void 0;
      if (generation !== themeMermaidRefreshGeneration) {
        return;
      }
      postPerfMark("mm-theme-mermaid-refresh-start", { theme });
      void renderMermaid().finally(() => {
        if (generation === themeMermaidRefreshGeneration) {
          postPerfMark("mm-theme-mermaid-refresh-end", { theme });
        }
      });
    }, THEME_MERMAID_REFRESH_DELAY_MS);
  }
  function appendProgressiveDocumentHtml(message) {
    if (message.renderId !== void 0 && currentDocumentRenderId !== null && message.renderId !== currentDocumentRenderId) {
      postPerfMark("mm-progressive-append-stale", {
        renderId: message.renderId,
        currentRenderId: currentDocumentRenderId
      });
      return;
    }
    const main = document.querySelector("main.mm-document");
    if (!main || message.html.length === 0) {
      return;
    }
    const isFinal = message.isFinal !== false;
    postPerfMark("mm-progressive-append-start", {
      htmlLength: message.html.length,
      renderId: message.renderId ?? null,
      isFinal
    });
    const template = document.createElement("template");
    template.innerHTML = message.html;
    if (message.hasHljs !== false) {
      renderCodeBlocks(template.content);
    }
    main.append(template.content);
    virtualizationShadowDocumentFinal = isFinal;
    invalidateTopVisibleBlockIndexCache();
    if (!isFinal) {
      postPerfMark("mm-progressive-append-end", {
        htmlLength: message.html.length,
        renderId: message.renderId ?? null,
        isFinal: false
      });
      return;
    }
    if (typeof message.cacheKey === "string" && message.cacheKey.length > 0) {
      setCurrentProcessedDocumentCacheKey(message.cacheKey);
    }
    ensureChromeNodes(false, { refreshMinimap: false });
    invalidateSourceLineAnchors();
    postPerfMark("mm-progressive-append-end", {
      htmlLength: message.html.length,
      renderId: message.renderId ?? null,
      isFinal: true
    });
    queueProgressiveMinimapAppendRefresh(message);
    scheduleProgressiveDeferredEnhancements(message);
  }
  function postThemeAppliedAfterPaint(theme, requestId) {
    if (requestId === void 0 || !Number.isFinite(requestId) || requestId <= 0) {
      return;
    }
    const generation = ++themeAppliedAckGeneration;
    let posted = false;
    const postAck = () => {
      if (posted || generation !== themeAppliedAckGeneration) {
        return;
      }
      posted = true;
      postHostMessage({ type: "theme-applied", theme, requestId });
    };
    window.requestAnimationFrame(() => window.requestAnimationFrame(postAck));
    window.setTimeout(postAck, THEME_APPLIED_ACK_FALLBACK_MS);
  }
  function handleThemeChange(theme, requestId) {
    postPerfMark("mm-theme-change-start", { theme });
    applyTheme(theme);
    initMermaidWithTheme(theme);
    postPerfMark("mm-theme-change-applied", { theme });
    postThemeAppliedAfterPaint(theme, requestId);
    scheduleThemeMermaidRefresh(theme);
  }
  function getScrollState() {
    const root = document.scrollingElement ?? document.documentElement;
    return {
      scrollTop: root.scrollTop,
      scrollHeight: root.scrollHeight,
      clientHeight: root.clientHeight
    };
  }
  function invalidateTopVisibleBlockIndexCache() {
    liveDocumentBlockElements = [];
    liveDocumentBlockElementsStale = true;
    invalidateVirtualizationShadowModel();
  }
  function refreshTopVisibleBlockIndexCache() {
    liveDocumentBlockElements = collectLiveDocumentBlockElements(document);
    liveDocumentBlockElementsStale = false;
  }
  function getLiveDocumentBlockElements() {
    if (liveDocumentBlockElementsStale) {
      refreshTopVisibleBlockIndexCache();
    }
    return liveDocumentBlockElements;
  }
  function findTopVisibleBlockIndex() {
    const root = document.scrollingElement ?? document.documentElement;
    return findTopVisibleBlockIndexFromBlocks(getLiveDocumentBlockElements(), root.scrollTop);
  }
  function getVirtualizationShadowValidator() {
    if (virtualizationShadowValidator === null) {
      virtualizationShadowValidator = createVirtualizationShadowValidator({
        ownerDocument: document,
        ownerWindow: window,
        isDocumentFinal: () => virtualizationShadowDocumentFinal,
        postDebugLog,
        postPerfMark
      });
    }
    return virtualizationShadowValidator;
  }
  function invalidateVirtualizationShadowModel() {
    virtualizationShadowValidator?.invalidate();
  }
  function scheduleVirtualizationShadowValidation() {
    if (!virtualizationShadowEnabled) {
      return;
    }
    getVirtualizationShadowValidator().schedule();
  }
  function getDocumentScrollRoot() {
    return document.scrollingElement ?? document.documentElement;
  }
  function readVirtualizedFindContext() {
    return {
      controller: virtualizedDocumentWindowController,
      main: document.querySelector("main.mm-document"),
      model: virtualizedDocumentWindowModel,
      ownerWindow: window,
      renderId: currentDocumentRenderId,
      root: getDocumentScrollRoot(),
      virtualizationEnabled
    };
  }
  function isVirtualizedProgrammaticNavigationInProgress() {
    return virtualizationEnabled && virtualizedProgrammaticNavigationInProgress;
  }
  function resolveVirtualizedNavigationTargetSectionIndex(descriptor) {
    const model = virtualizedDocumentWindowModel;
    if (model === null) {
      return null;
    }
    const entry = (() => {
      switch (descriptor.kind) {
        case "block":
          return model.getEntryContainingBlockIndex(descriptor.blockIndex);
        case "heading-anchor":
          return model.getEntryByHeadingAnchor(descriptor.anchor);
        case "source-line":
          return model.getEntryBySourceLine(descriptor.sourceLine);
        case "document-y":
          return model.sections[model.sectionIndexAtDocumentY(descriptor.documentY)];
        case "section":
          return model.sections[descriptor.sectionIndex];
        case "find-match":
          return descriptor.blockIndex === void 0 ? void 0 : model.getEntryContainingBlockIndex(descriptor.blockIndex);
      }
    })();
    if (entry === void 0) {
      return null;
    }
    const sectionIndex = model.sections.findIndex((candidate) => candidate.blockIndex === entry.blockIndex);
    return sectionIndex < 0 ? null : sectionIndex;
  }
  function readVirtualizedProgrammaticNavigationTargetContext(descriptor) {
    const model = virtualizedDocumentWindowModel;
    const controller = virtualizedDocumentWindowController;
    const main = document.querySelector("main.mm-document");
    if (model === null || controller === null || main === null) {
      return null;
    }
    const resolution = resolveWindowTarget(model, descriptor);
    if (resolution === null) {
      return null;
    }
    return readWindowTargetContext({
      controller,
      main,
      model,
      ownerWindow: window
    }, resolution);
  }
  function readElementDocumentTop(element) {
    return element.getBoundingClientRect().top + getDocumentScrollRoot().scrollTop;
  }
  function readVirtualizedTargetLocalOffset(context, descriptor) {
    const sectionElement = context.element;
    if (sectionElement === null) {
      return 0;
    }
    if (descriptor.kind === "source-line") {
      refreshSourceLineAnchors();
      const sourceLineTop = findScrollTopForSourceLine(sourceLineAnchors, descriptor.sourceLine);
      if (sourceLineTop !== null) {
        return Math.max(0, sourceLineTop - readElementDocumentTop(sectionElement));
      }
    }
    const targetElement = context.targetElement ?? sectionElement;
    return Math.max(
      0,
      targetElement.getBoundingClientRect().top - sectionElement.getBoundingClientRect().top
    );
  }
  function computeVirtualizedProgrammaticNavigationScrollTop(context, descriptor, viewportOffsetY) {
    const targetLocalOffset = readVirtualizedTargetLocalOffset(context, descriptor);
    const normalizedViewportOffset = Number.isFinite(viewportOffsetY) ? Math.max(0, viewportOffsetY) : 0;
    return Math.max(0, context.sectionTop + targetLocalOffset - normalizedViewportOffset);
  }
  function applyVirtualizedProgrammaticNavigationContext(context, descriptor, viewportOffsetY) {
    if (descriptor.kind === "source-line") {
      pendingSourceLineTarget = descriptor.sourceLine;
      suppressPreviewSourceLinePost();
    }
    getDocumentScrollRoot().scrollTop = computeVirtualizedProgrammaticNavigationScrollTop(
      context,
      descriptor,
      viewportOffsetY
    );
    return true;
  }
  function applyVirtualizedProgrammaticNavigationTarget(input) {
    const context = readVirtualizedProgrammaticNavigationTargetContext(input.descriptor);
    if (context === null) {
      return false;
    }
    return applyVirtualizedProgrammaticNavigationContext(context, input.descriptor, input.viewportOffsetY);
  }
  function landVirtualizedProgrammaticNavigation(input) {
    startVirtualizedProgrammaticNavigationSettle({
      descriptor: input.descriptor,
      initialContext: input.context,
      viewportOffsetY: input.viewportOffsetY
    });
  }
  function rememberVirtualizedProgrammaticNavigationPostSettleTarget(input) {
    virtualizedProgrammaticNavigationPostSettleTarget = input;
  }
  function clearVirtualizedProgrammaticNavigationPostSettleTarget() {
    virtualizedProgrammaticNavigationPostSettleTarget = null;
  }
  function alignVirtualizedProgrammaticNavigationPostSettleTarget() {
    const target = virtualizedProgrammaticNavigationPostSettleTarget;
    if (target !== null) {
      applyVirtualizedProgrammaticNavigationTarget(target);
    }
  }
  function getVirtualizedProgrammaticNavigationPostSettleSectionIndex() {
    const target = virtualizedProgrammaticNavigationPostSettleTarget;
    return target === null ? null : resolveVirtualizedNavigationTargetSectionIndex(target.descriptor);
  }
  function startVirtualizedProgrammaticNavigationSettle(input) {
    if (!virtualizationEnabled || virtualizedDocumentWindowModel === null || virtualizedDocumentWindowController === null) {
      return;
    }
    const generation = ++virtualizedProgrammaticNavigationGeneration;
    virtualizedProgrammaticNavigationInProgress = true;
    const root = getDocumentScrollRoot();
    if (input.initialContext !== void 0) {
      applyVirtualizedProgrammaticNavigationContext(input.initialContext, input.descriptor, input.viewportOffsetY);
    }
    let lastScrollTop = root.scrollTop;
    let stableFrames = 0;
    let frameCount = 0;
    const finish = () => {
      if (generation !== virtualizedProgrammaticNavigationGeneration) {
        return;
      }
      const model = virtualizedDocumentWindowModel;
      const controller = virtualizedDocumentWindowController;
      if (model === null || controller === null) {
        return;
      }
      rememberVirtualizedProgrammaticNavigationPostSettleTarget(input);
      updateVirtualizedWindowForScroll({ force: true });
      if (!applyVirtualizedProgrammaticNavigationTarget(input)) {
        virtualizedProgrammaticNavigationInProgress = false;
        return;
      }
      updateMinimapViewport({ skipVisibilityUpdate: true });
      postScroll();
      window.requestAnimationFrame(() => {
        if (generation === virtualizedProgrammaticNavigationGeneration) {
          virtualizedProgrammaticNavigationInProgress = false;
        }
      });
    };
    const tick = () => {
      if (generation !== virtualizedProgrammaticNavigationGeneration) {
        return;
      }
      const currentScrollTop = root.scrollTop;
      if (Math.abs(currentScrollTop - lastScrollTop) <= VIRTUALIZED_NAVIGATION_SETTLE_TOLERANCE_PX) {
        stableFrames++;
      } else {
        stableFrames = 0;
        lastScrollTop = currentScrollTop;
      }
      frameCount++;
      if (stableFrames >= VIRTUALIZED_NAVIGATION_SETTLE_STABLE_FRAMES || frameCount >= VIRTUALIZED_NAVIGATION_SETTLE_MAX_FRAMES) {
        finish();
        return;
      }
      window.requestAnimationFrame(tick);
    };
    window.requestAnimationFrame(tick);
  }
  function cancelVirtualizedCalibration() {
    if (virtualizedCalibrationHandle === null) {
      return;
    }
    if (virtualizedCalibrationHandle.kind === "idle") {
      window.cancelIdleCallback?.(virtualizedCalibrationHandle.id);
    } else {
      window.clearTimeout(virtualizedCalibrationHandle.id);
    }
    virtualizedCalibrationHandle = null;
  }
  function resetVirtualizedDocumentWindow(resetCalibrator = true) {
    cancelVirtualizedCalibration();
    virtualizedWindowMathController?.cancel();
    virtualizedWindowMathController = null;
    virtualizedDocumentWindowController = null;
    virtualizedDocumentWindowModel = null;
    virtualizedMeasureFrameRequested = false;
    virtualizedProgrammaticNavigationInProgress = false;
    virtualizedProgrammaticNavigationGeneration++;
    virtualizedProgrammaticNavigationPostSettleTarget = null;
    if (resetCalibrator) {
      virtualizedIntrinsicCalibrator.reset();
    }
  }
  function refreshVirtualizedFindHighlights() {
    virtualizedFindProvider?.refreshVisibleHighlights();
  }
  function prepareVirtualizedInsertedContent(root) {
    renderCodeBlocks(root);
    virtualizedWindowMathController?.cancel();
    const mathController = renderMath({
      katex: hostWindow.katex ?? void 0,
      documentRoot: root
    });
    virtualizedWindowMathController = mathController;
    const scheduleAfterRichContent = () => {
      if (virtualizedWindowMathController !== mathController || mathController.isCancelled()) {
        return;
      }
      invalidateSourceLineAnchors();
      refreshVirtualizedFindHighlights();
      scheduleVirtualizedMeasuredHeightAdoption();
    };
    mathController.initialVisibleReady.then(scheduleAfterRichContent, scheduleAfterRichContent);
    mathController.allMathRendered.then(scheduleAfterRichContent, scheduleAfterRichContent);
    const mermaid = hostWindow.mermaid;
    if (!mermaid) {
      return;
    }
    const mermaidNodes = Array.from(root.querySelectorAll("pre.mm-mermaid"));
    if (mermaidNodes.length === 0) {
      return;
    }
    disconnectMermaidLazyObserver();
    void renderMermaidNodes(mermaidNodes, mermaid, "mm-mermaid-virt-window").finally(scheduleAfterRichContent);
  }
  function initializeVirtualizedDocumentWindow() {
    if (!virtualizationEnabled) {
      return;
    }
    const main = document.querySelector("main.mm-document");
    if (!main) {
      resetVirtualizedDocumentWindow(false);
      return;
    }
    const blocks = collectLiveDocumentSectionElements(main);
    if (blocks.length === 0) {
      resetVirtualizedDocumentWindow(false);
      return;
    }
    const root = getDocumentScrollRoot();
    const models = buildDocumentWindowModelsFromLiveBlocks(
      blocks,
      readIntrinsicSizeMetrics(main),
      root.scrollHeight,
      { intrinsicSizeCalibrator: virtualizedIntrinsicCalibrator }
    );
    virtualizedDocumentWindowModel = models.estimateOnlyModel;
    virtualizedDocumentWindowController = createVirtualizedDocumentWindowController({
      main,
      model: virtualizedDocumentWindowModel,
      ownerWindow: window,
      prepareInsertedContent: prepareVirtualizedInsertedContent,
      root
    });
    virtualizedDocumentWindowController.updateWindowForScroll();
    refreshVirtualizedFindHighlights();
    invalidateTopVisibleBlockIndexCache();
    postPerfMark("mm-virt-window-built", {
      estimateMeanAbsError: models.estimateHeightError.meanAbsError,
      sectionCount: virtualizedDocumentWindowModel.getSectionCount(),
      totalHeight: virtualizedDocumentWindowModel.getTotalHeight()
    });
    scheduleVirtualizedMeasuredHeightAdoption();
  }
  function updateVirtualizedWindowForScroll(options = {}) {
    if (!virtualizationEnabled || virtualizedDocumentWindowController === null) {
      return;
    }
    if (options.force !== true && isVirtualizedProgrammaticNavigationInProgress()) {
      return;
    }
    if (virtualizedDocumentWindowController.updateWindowForScroll(options)) {
      invalidateTopVisibleBlockIndexCache();
      invalidateSourceLineAnchors();
      refreshVirtualizedFindHighlights();
      scheduleVirtualizedMeasuredHeightAdoption();
    }
  }
  function scheduleVirtualizedMeasuredHeightAdoption() {
    if (!virtualizationEnabled || virtualizedDocumentWindowController === null || virtualizedMeasureFrameRequested) {
      return;
    }
    virtualizedMeasureFrameRequested = true;
    window.requestAnimationFrame(() => {
      virtualizedMeasureFrameRequested = false;
      adoptVirtualizedRenderedHeights();
    });
  }
  function adoptVirtualizedRenderedHeights() {
    if (!virtualizationEnabled || virtualizedDocumentWindowController === null) {
      return;
    }
    if (isVirtualizedProgrammaticNavigationInProgress()) {
      scheduleVirtualizedMeasuredHeightAdoption();
      return;
    }
    const preserveSectionIndex = getVirtualizedProgrammaticNavigationPostSettleSectionIndex();
    const result = virtualizedDocumentWindowController.adoptRenderedHeights(
      preserveSectionIndex === null ? void 0 : { preserveSectionIndex }
    );
    if (result.updatedCount === 0) {
      return;
    }
    invalidateTopVisibleBlockIndexCache();
    invalidateSourceLineAnchors();
    refreshVirtualizedFindHighlights();
    if (getModelMinimapSource() !== null && minimapSourceReady) {
      syncModelMinimapCloneMetadata();
      updateMinimapViewport({ skipVisibilityUpdate: true });
    }
    alignVirtualizedProgrammaticNavigationPostSettleTarget();
    scheduleVirtualizedCalibration();
    postPerfMark("mm-virt-window-height-adopted", {
      maxAbsDelta: result.maxAbsDelta,
      totalHeight: virtualizedDocumentWindowModel?.getTotalHeight() ?? null,
      totalDelta: result.totalDelta,
      updatedCount: result.updatedCount
    });
  }
  function scheduleVirtualizedCalibration() {
    if (!virtualizationEnabled || virtualizedDocumentWindowModel === null || virtualizedCalibrationHandle !== null) {
      return;
    }
    const run = () => {
      virtualizedCalibrationHandle = null;
      runVirtualizedCalibration();
    };
    const requestIdle = window.requestIdleCallback;
    if (requestIdle) {
      virtualizedCalibrationHandle = { kind: "idle", id: requestIdle(run, { timeout: 1500 }) };
      return;
    }
    virtualizedCalibrationHandle = { kind: "timeout", id: window.setTimeout(run, 250) };
  }
  function runVirtualizedCalibration() {
    const model = virtualizedDocumentWindowModel;
    const controller = virtualizedDocumentWindowController;
    if (!virtualizationEnabled || model === null || controller === null) {
      return;
    }
    if (isVirtualizedProgrammaticNavigationInProgress()) {
      scheduleVirtualizedCalibration();
      return;
    }
    const root = getDocumentScrollRoot();
    const preserveSectionIndex = getVirtualizedProgrammaticNavigationPostSettleSectionIndex();
    const anchor = preserveSectionIndex === null ? model.captureAnchor(root.scrollTop) : null;
    const recordedCount = model.recordIntrinsicSizeCalibrationSamples(virtualizedIntrinsicCalibrator);
    if (recordedCount === 0) {
      return;
    }
    const result = model.updateEstimatedHeightsFromCalibration(virtualizedIntrinsicCalibrator);
    if (result.updatedCount === 0) {
      return;
    }
    if (preserveSectionIndex !== null) {
      root.scrollTop = model.sectionTop(preserveSectionIndex);
    } else {
      root.scrollTop = model.scrollTopForAnchor(anchor);
    }
    controller.updateWindowForScroll({ force: true });
    if (preserveSectionIndex !== null) {
      root.scrollTop = model.sectionTop(preserveSectionIndex);
      alignVirtualizedProgrammaticNavigationPostSettleTarget();
    } else {
      root.scrollTop = model.scrollTopForAnchor(anchor);
    }
    invalidateTopVisibleBlockIndexCache();
    invalidateSourceLineAnchors();
    postPerfMark("mm-virt-window-calibrated", {
      maxAbsDelta: result.maxAbsDelta,
      recordedCount,
      totalDelta: result.totalDelta,
      updatedCount: result.updatedCount
    });
  }
  function postScroll() {
    const scrollState = getScrollState();
    const topBlockIndex = findTopVisibleBlockIndex();
    lastKnownLayoutState = { ...scrollState, topBlockIndex };
    recordScrollIpc();
    postHostMessage({
      type: "scroll",
      ...scrollState,
      topBlockIndex
    });
    scheduleVirtualizationShadowValidation();
  }
  function refreshSourceLineAnchors() {
    sourceLineAnchors = readSourceLineAnchors(document);
  }
  function readVirtualizedModelSourceLineAnchors() {
    return virtualizedDocumentWindowModel?.getSourceLineAnchors().map((anchor) => ({
      endLine: anchor.endLine,
      sourceLine: anchor.sourceLine,
      top: anchor.top
    })) ?? [];
  }
  function scrollToSourceLineInCurrentWindow(sourceLine) {
    if (!Number.isFinite(sourceLine) || sourceLine < 0) {
      return;
    }
    if (sourceLineAnchors.length === 0) {
      refreshSourceLineAnchors();
    }
    const scrollTop = findScrollTopForSourceLine(sourceLineAnchors, sourceLine);
    if (scrollTop === null) {
      return;
    }
    pendingSourceLineTarget = sourceLine;
    suppressPreviewSourceLinePost();
    window.scrollTo({
      left: 0,
      top: Math.max(0, scrollTop - getViewportAnchorY()),
      behavior: "instant"
    });
  }
  function scrollToSourceLine(sourceLine) {
    if (!Number.isFinite(sourceLine) || sourceLine < 0) {
      return;
    }
    if (!virtualizationEnabled) {
      scrollToSourceLineInCurrentWindow(sourceLine);
      return;
    }
    const main = document.querySelector("main.mm-document");
    if (main === null || virtualizedDocumentWindowModel === null || virtualizedDocumentWindowController === null) {
      scrollToSourceLineInCurrentWindow(sourceLine);
      return;
    }
    void renderWindowTargetThenAct({
      action: (context) => {
        pendingSourceLineTarget = sourceLine;
        suppressPreviewSourceLinePost();
        landVirtualizedProgrammaticNavigation({
          context,
          descriptor: { kind: "source-line", sourceLine },
          viewportOffsetY: getViewportAnchorY()
        });
      },
      actionKind: "navigate",
      controller: virtualizedDocumentWindowController,
      descriptor: { kind: "source-line", sourceLine },
      legacyAction: () => {
        scrollToSourceLineInCurrentWindow(sourceLine);
      },
      main,
      model: virtualizedDocumentWindowModel,
      ownerWindow: window,
      root: getDocumentScrollRoot(),
      virtualizationEnabled: true
    });
  }
  function invalidateSourceLineAnchors() {
    sourceLineAnchors = [];
    if (pendingSourceLineTarget !== null) {
      const target = pendingSourceLineTarget;
      if (virtualizationEnabled) {
        if (isVirtualizedProgrammaticNavigationInProgress()) {
          return;
        }
        window.requestAnimationFrame(() => {
          if (pendingSourceLineTarget === target) {
            alignVirtualizedProgrammaticNavigationPostSettleTarget();
          }
        });
        return;
      }
      window.requestAnimationFrame(() => {
        if (pendingSourceLineTarget === target) {
          scrollToSourceLine(target);
        }
      });
    }
  }
  function suppressPreviewSourceLinePost() {
    const sequence = ++suppressPreviewSourceLineSequence;
    suppressPreviewSourceLineEmit = true;
    window.requestAnimationFrame(() => {
      window.requestAnimationFrame(() => {
        if (sequence === suppressPreviewSourceLineSequence) {
          suppressPreviewSourceLineEmit = false;
        }
      });
    });
  }
  function queuePreviewSourceLinePost() {
    if (suppressPreviewSourceLineEmit || !documentScrollEnabled || previewSourceLineFrameRequested) {
      return;
    }
    previewSourceLineFrameRequested = true;
    window.requestAnimationFrame(() => {
      previewSourceLineFrameRequested = false;
      if (suppressPreviewSourceLineEmit || !documentScrollEnabled) {
        return;
      }
      pendingSourceLineTarget = null;
      if (virtualizationEnabled) {
        updateVirtualizedWindowForScroll();
      }
      if (sourceLineAnchors.length === 0) {
        refreshSourceLineAnchors();
      }
      const documentY = window.scrollY + getViewportAnchorY();
      const sourceLine = virtualizationEnabled && virtualizedDocumentWindowModel !== null ? findSourceLineAtDocumentYWithFallback(
        sourceLineAnchors,
        readVirtualizedModelSourceLineAnchors,
        documentY
      ) : findSourceLineAtDocumentY(sourceLineAnchors, documentY);
      if (sourceLine === null || sourceLine === lastPostedPreviewSourceLine) {
        return;
      }
      lastPostedPreviewSourceLine = sourceLine;
      postHostMessage({ type: "preview-source-line", sourceLine });
    });
  }
  function getViewportAnchorY() {
    const viewportHeight = Math.max(0, window.innerHeight);
    if (viewportHeight <= 0) {
      return 24;
    }
    if (viewportHeight <= 48) {
      return viewportHeight * 0.5;
    }
    return Math.max(24, Math.min(viewportHeight * 0.38, viewportHeight - 24));
  }
  function postLayoutReady(renderId) {
    try {
      const scrollState = getScrollState();
      const topBlockIndex = findTopVisibleBlockIndex();
      lastKnownLayoutState = { ...scrollState, topBlockIndex };
      recordScrollIpc();
      postHostMessage({
        type: "scroll",
        ...scrollState,
        topBlockIndex
      });
      postHostMessage({
        type: "layout-ready",
        ...scrollState,
        renderId
      });
      postPerfMark("mm-layout-ready");
      flushPostLayoutReadyWork();
    } catch (error) {
      postPerfMark("mm-layout-ready-post-error", {
        message: error instanceof Error ? error.message : String(error)
      });
      throw error;
    }
  }
  function postCachedLayoutReady() {
    const cachedLayoutState = restoredCachedLayoutState;
    restoredCachedLayoutState = null;
    const layoutState = cachedLayoutState !== null ? { ...cachedLayoutState } : { ...getScrollState(), topBlockIndex: findTopVisibleBlockIndex() };
    lastKnownLayoutState = { ...layoutState };
    recordScrollIpc();
    postHostMessage({
      type: "scroll",
      scrollTop: layoutState.scrollTop,
      scrollHeight: layoutState.scrollHeight,
      clientHeight: layoutState.clientHeight,
      topBlockIndex: layoutState.topBlockIndex
    });
    postHostMessage({
      type: "layout-ready",
      scrollTop: layoutState.scrollTop,
      scrollHeight: layoutState.scrollHeight,
      clientHeight: layoutState.clientHeight,
      cached: true,
      renderId: currentDocumentRenderId
    });
    postPerfMark("mm-layout-ready", { cached: true });
    flushPostLayoutReadyWork();
    if (cachedLayoutState !== null) {
      queueCachedGeometryRefresh(cachedLayoutState.topBlockIndex);
    }
  }
  function queueCachedGeometryRefresh(topBlockIndex) {
    const cacheKey = currentDocumentCacheKey;
    window.clearTimeout(cachedGeometryRefreshTimer);
    cachedGeometryRefreshTimer = window.setTimeout(() => {
      cachedGeometryRefreshTimer = void 0;
      if (cacheKey !== currentDocumentCacheKey) {
        return;
      }
      const scrollState = getScrollState();
      const layoutState = { ...scrollState, topBlockIndex };
      lastKnownLayoutState = { ...layoutState };
      recordScrollIpc();
      postHostMessage({
        type: "scroll",
        scrollTop: layoutState.scrollTop,
        scrollHeight: layoutState.scrollHeight,
        clientHeight: layoutState.clientHeight,
        topBlockIndex: layoutState.topBlockIndex
      });
    }, 180);
  }
  function flushPostLayoutReadyWork() {
    if (postLayoutReadyWorkQueue.length === 0) {
      return;
    }
    const flushGeneration = layoutReadyGeneration;
    const workItems = postLayoutReadyWorkQueue.filter((item) => item.generation === flushGeneration);
    postLayoutReadyWorkQueue = postLayoutReadyWorkQueue.filter((item) => item.generation !== flushGeneration);
    const delayMs = viewerChromeEnabled ? 0 : POST_LAYOUT_READY_EDIT_PREVIEW_DELAY_MS;
    if (delayMs > 0) {
      postPerfMark("post-ready-enhancements-deferred", { delayMs, viewerChromeEnabled });
    }
    window.setTimeout(() => {
      if (flushGeneration !== layoutReadyGeneration) {
        return;
      }
      for (const item of workItems) {
        item.work();
      }
    }, delayMs);
  }
  function restoreCachedScrollPosition() {
    const layoutState = restoredCachedLayoutState ?? lastKnownLayoutState;
    window.scrollTo({
      left: 0,
      top: layoutState.scrollTop,
      behavior: "instant"
    });
    updateVirtualizedWindowForScroll({ force: true });
  }
  function scheduleLayoutReady(skipFrameWait = false) {
    const generation = ++layoutReadyGeneration;
    const scheduledRenderId = currentDocumentRenderId;
    let completed = false;
    let posted = false;
    let frameFallbackTimer;
    if (layoutReadyTimer !== void 0) {
      window.clearTimeout(layoutReadyTimer);
    }
    const post = (path) => {
      if (posted || generation !== layoutReadyGeneration) {
        return;
      }
      posted = true;
      if (frameFallbackTimer !== void 0) {
        window.clearTimeout(frameFallbackTimer);
        frameFallbackTimer = void 0;
      }
      if (path === "frame-fallback") {
        postPerfMark("mm-layout-ready-frame-fallback", { generation });
      }
      postLayoutReady(scheduledRenderId);
    };
    const complete = () => {
      if (completed || generation !== layoutReadyGeneration) {
        return;
      }
      completed = true;
      if (layoutReadyTimer !== void 0) {
        window.clearTimeout(layoutReadyTimer);
        layoutReadyTimer = void 0;
      }
      if (skipFrameWait) {
        postPerfMark("mm-layout-ready-frame-wait-skipped");
        post("skip-frame-wait");
        return;
      }
      frameFallbackTimer = window.setTimeout(() => {
        post("frame-fallback");
      }, 120);
      window.requestAnimationFrame(() => {
        window.requestAnimationFrame(() => {
          if (generation === layoutReadyGeneration) {
            post("raf");
          } else {
            postPerfMark("mm-layout-ready-frame-stale", { generation, current: layoutReadyGeneration });
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
  function readPixelValue(value) {
    const parsed = Number.parseFloat(value ?? "");
    return Number.isFinite(parsed) && parsed > 0 ? parsed : 0;
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
    widthHandleRoot.hidden = !hasReceivedHostPreferences || !viewerChromeEnabled || !hasInitialLayoutSettled;
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
    const documentStyle = getComputedStyle(documentElement);
    const documentPaddingRight = Number.parseFloat(documentStyle.paddingRight) || 0;
    const clampedLeft = calculateWidthHandleLeft({
      documentRight: documentRect.right,
      documentPaddingRight,
      hitArea,
      minimapReservedWidth,
      viewportWidth: window.innerWidth
    });
    widthHandleRoot.style.left = `${Math.round(clampedLeft)}px`;
  }
  function updateWidthHandlePositionFromCssModel(minimapVisible) {
    ensureWidthHandle();
    if (!widthHandleRoot) {
      return;
    }
    widthHandleRoot.hidden = !hasReceivedHostPreferences || !viewerChromeEnabled || !hasInitialLayoutSettled;
    if (widthHandleRoot.hidden) {
      return;
    }
    const documentElement = document.querySelector(".mm-document");
    if (!documentElement) {
      widthHandleRoot.hidden = true;
      return;
    }
    const hitArea = readRootPixelVariable("--mm-width-handle-hit-area", 24);
    const basePadding = readRootPixelVariable("--mm-document-base-padding-x", 72);
    const minimapReservedWidth = minimapVisible ? readConfiguredMinimapReservedWidth() : 0;
    const inlineMaxWidth = Number.parseFloat(
      document.documentElement.style.getPropertyValue("--mm-document-max-width")
    );
    const documentMaxWidth = Number.isFinite(inlineMaxWidth) && inlineMaxWidth > 0 ? inlineMaxWidth : lastAppliedReadingPreferences?.maxWidth ?? readRootPixelVariable("--mm-document-max-width", 820);
    const borderBoxWidth = Math.min(
      Math.max(0, window.innerWidth),
      Math.max(0, documentMaxWidth + minimapReservedWidth)
    );
    const documentRight = (Math.max(0, window.innerWidth) + borderBoxWidth) / 2;
    const clampedLeft = calculateWidthHandleLeft({
      documentRight,
      documentPaddingRight: basePadding + minimapReservedWidth,
      hitArea,
      minimapReservedWidth,
      viewportWidth: window.innerWidth
    });
    widthHandleRoot.style.left = `${Math.round(clampedLeft)}px`;
  }
  function readDocumentMaxWidthFromCssModel() {
    const inlineMaxWidth = Number.parseFloat(
      document.documentElement.style.getPropertyValue("--mm-document-max-width")
    );
    return Number.isFinite(inlineMaxWidth) && inlineMaxWidth > 0 ? inlineMaxWidth : lastAppliedReadingPreferences?.maxWidth ?? readRootPixelVariable("--mm-document-max-width", 820);
  }
  function calculateDocumentContentWidthFromCssModel(minimapVisible) {
    const basePadding = readRootPixelVariable("--mm-document-base-padding-x", 72);
    const minimapReservedWidth = minimapVisible ? readConfiguredMinimapReservedWidth() : 0;
    const borderBoxWidth = Math.min(
      Math.max(0, window.innerWidth),
      Math.max(0, readDocumentMaxWidthFromCssModel() + minimapReservedWidth)
    );
    return Math.max(1, borderBoxWidth - basePadding * 2 - minimapReservedWidth);
  }
  function isPolicyHeavyMinimapDocument() {
    return isPolicyHeavyMinimapHeight(minimapDocumentHeight);
  }
  function updateWidthHandlePositionForCurrentLayout() {
    if (isPolicyHeavyMinimapDocument()) {
      updateWidthHandlePositionFromCssModel(minimapRoot ? !minimapRoot.hidden : false);
      return;
    }
    updateWidthHandlePosition();
  }
  function captureWidthHandleDragGeometry() {
    if (!widthHandleRoot) {
      widthHandleDragStartLeft = 0;
      widthHandleDragHitArea = 24;
      widthHandleDragMinimapReservedWidth = 0;
      return;
    }
    const inlineLeft = Number.parseFloat(widthHandleRoot.style.left);
    widthHandleDragStartLeft = Number.isFinite(inlineLeft) ? inlineLeft : widthHandleRoot.getBoundingClientRect().left;
    widthHandleDragHitArea = readRootPixelVariable("--mm-width-handle-hit-area", 24);
    widthHandleDragMinimapReservedWidth = getCurrentMinimapReservedWidth();
  }
  function updateWidthHandleDragPreviewPosition(previewMaxWidth) {
    if (!widthHandleRoot) {
      return;
    }
    const widthDelta = previewMaxWidth - widthHandleStartMaxWidth;
    const targetLeft = clampWidthHandleLeft({
      candidateLeft: widthHandleDragStartLeft + widthDelta / 2,
      hitArea: widthHandleDragHitArea,
      minimapReservedWidth: widthHandleDragMinimapReservedWidth,
      viewportWidth: window.innerWidth
    });
    widthHandleRoot.style.transform = `translateX(${Math.round(targetLeft - widthHandleDragStartLeft)}px)`;
  }
  function postWidthDragMove() {
    if (widthDragFrameRequested) {
      return;
    }
    widthDragFrameRequested = true;
    window.requestAnimationFrame(() => {
      widthDragFrameRequested = false;
      widthDragPerfMovePosts++;
      postHostMessage({ type: "width-drag", phase: "move", deltaX: pendingWidthDragDeltaX });
    });
  }
  function resetWidthDragPerf(startMaxWidth) {
    widthDragPerfStartTime = typeof performance !== "undefined" ? performance.now() : void 0;
    widthDragPerfMoveEvents = 0;
    widthDragPerfMovePosts = 0;
    widthDragPerfApplyFrames = 0;
    widthDragPerfMaxApplyMs = 0;
    widthDragPerfStartMaxWidth = startMaxWidth;
    widthDragPerfLastMaxWidth = startMaxWidth;
  }
  function completeWidthDragPerf(reason, deltaX) {
    const now = typeof performance !== "undefined" ? performance.now() : void 0;
    const durationMs = widthDragPerfStartTime !== void 0 && now !== void 0 ? Math.max(0, now - widthDragPerfStartTime) : 0;
    postPerfMark(`mm-width-drag-${reason}`, {
      durationMs: Number(durationMs.toFixed(1)),
      moveEvents: widthDragPerfMoveEvents,
      movePosts: widthDragPerfMovePosts,
      applyFrames: widthDragPerfApplyFrames,
      maxApplyMs: Number(widthDragPerfMaxApplyMs.toFixed(1)),
      deltaX: Number(deltaX.toFixed(1)),
      startMaxWidth: Number(widthDragPerfStartMaxWidth.toFixed(1)),
      finalMaxWidth: Number(widthDragPerfLastMaxWidth.toFixed(1)),
      minimapVisible: minimapRoot ? !minimapRoot.hidden : false,
      minimapMode
    });
    widthDragPerfStartTime = void 0;
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
    captureWidthHandleDragGeometry();
    resetWidthDragPerf(widthHandleStartMaxWidth);
    postPerfMark("mm-width-drag-start", {
      startMaxWidth: Number(widthHandleStartMaxWidth.toFixed(1)),
      minimapVisible: minimapRoot ? !minimapRoot.hidden : false,
      minimapMode
    });
    widthHandleRoot.classList.add(WIDTH_HANDLE_DRAGGING_CLASS);
    widthHandleRoot.setPointerCapture(event.pointerId);
    postHostMessage({ type: "width-drag", phase: "start", deltaX: 0 });
    event.preventDefault();
  }
  function handleWidthHandlePointerMove(event) {
    if (!widthHandleDragging) {
      return;
    }
    widthDragPerfMoveEvents++;
    pendingWidthDragDeltaX = event.clientX - widthHandleStartClientX;
    scheduleWidthDragApply();
    postWidthDragMove();
    event.preventDefault();
  }
  function scheduleWidthDragApply() {
    if (widthDragApplyFrameRequested) {
      return;
    }
    widthDragApplyFrameRequested = true;
    window.requestAnimationFrame(() => {
      widthDragApplyFrameRequested = false;
      if (!widthHandleDragging) {
        return;
      }
      const applyStart = typeof performance !== "undefined" ? performance.now() : void 0;
      const previewMaxWidth = Math.max(hostMinMaxWidth, widthHandleStartMaxWidth + 2 * pendingWidthDragDeltaX);
      widthDragPerfLastMaxWidth = previewMaxWidth;
      document.documentElement.style.setProperty("--mm-document-max-width", `${previewMaxWidth}px`);
      updateWidthHandleDragPreviewPosition(previewMaxWidth);
      if (applyStart !== void 0 && typeof performance !== "undefined") {
        const duration = Math.max(0, performance.now() - applyStart);
        widthDragPerfMaxApplyMs = Math.max(widthDragPerfMaxApplyMs, duration);
      }
      widthDragPerfApplyFrames++;
    });
  }
  function handleWidthHandlePointerUp(event) {
    if (!widthHandleDragging) {
      return;
    }
    const deltaX = event.clientX - widthHandleStartClientX;
    widthHandleDragging = false;
    widthHandleRoot?.classList.remove(WIDTH_HANDLE_DRAGGING_CLASS);
    if (widthHandleRoot) {
      widthHandleRoot.style.transform = "";
    }
    try {
      widthHandleRoot?.releasePointerCapture(event.pointerId);
    } catch {
    }
    if (lastAppliedReadingPreferences !== null) {
      const inlineMaxWidth = parseFloat(
        document.documentElement.style.getPropertyValue("--mm-document-max-width")
      );
      if (Number.isFinite(inlineMaxWidth) && inlineMaxWidth > 0) {
        lastAppliedReadingPreferences = {
          ...lastAppliedReadingPreferences,
          maxWidth: inlineMaxWidth
        };
      }
    }
    updateWidthHandlePosition();
    queueMinimapViewportUpdate();
    completeWidthDragPerf("end", deltaX);
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
    if (widthHandleRoot) {
      widthHandleRoot.style.transform = "";
    }
    if (lastAppliedReadingPreferences !== null) {
      const inlineMaxWidth = parseFloat(
        document.documentElement.style.getPropertyValue("--mm-document-max-width")
      );
      if (Number.isFinite(inlineMaxWidth) && inlineMaxWidth > 0) {
        lastAppliedReadingPreferences = {
          ...lastAppliedReadingPreferences,
          maxWidth: inlineMaxWidth
        };
      }
    }
    updateWidthHandlePosition();
    queueMinimapViewportUpdate();
    completeWidthDragPerf("cancel", pendingWidthDragDeltaX);
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
  function getDocumentScrollMetrics() {
    const root = document.scrollingElement ?? document.documentElement;
    return {
      documentHeight: root.scrollHeight,
      viewportHeight: root.clientHeight
    };
  }
  function getModelMinimapSource() {
    return virtualizationEnabled ? virtualizedDocumentWindowModel : null;
  }
  function syncModelMinimapCloneMetadata() {
    const model = getModelMinimapSource();
    if (model === null) {
      return;
    }
    minimapDocumentHeight = model.getTotalHeight();
    const clone = minimapContent?.querySelector(".mm-document[data-mm-minimap-source='model-fragment']");
    if (clone === void 0 || clone === null) {
      return;
    }
    clone.dataset["mmModelMinimapSectionCount"] = String(model.getSectionCount());
    clone.dataset["mmModelMinimapTotalHeight"] = String(model.getTotalHeight());
  }
  function getCurrentMinimapDocumentHeight() {
    return getModelMinimapSource()?.getTotalHeight() ?? getDocumentScrollMetrics().documentHeight;
  }
  function shouldBuildDetailedMinimapContent() {
    const source = document.querySelector(".mm-document");
    const metrics = getDocumentScrollMetrics();
    const documentHeight = getModelMinimapSource()?.getTotalHeight() ?? metrics.documentHeight;
    const viewportHeight = metrics.viewportHeight;
    if (!source) {
      return { allowed: false, reason: "no-source", documentHeight };
    }
    if (!hasReceivedHostPreferences) {
      return { allowed: false, reason: "host-prefs-missing", documentHeight };
    }
    if (!viewerChromeEnabled) {
      return { allowed: false, reason: "chrome-off", documentHeight };
    }
    if (minimapMode === "off") {
      return { allowed: false, reason: "mode-off", documentHeight };
    }
    if (!minimapPolicy) {
      return { allowed: false, reason: "policy-missing", documentHeight };
    }
    if (viewportHeight <= 0 || documentHeight <= viewportHeight) {
      return { allowed: false, reason: "not-scrollable", documentHeight };
    }
    if (minimapMode === "auto" && documentHeight > minimapPolicy.maxDetailedDocumentHeight) {
      return { allowed: false, reason: "auto-heavy", documentHeight };
    }
    return { allowed: true, documentHeight };
  }
  function isPolicyHeavyMinimapHeight(documentHeight) {
    return minimapPolicy !== null && documentHeight > minimapPolicy.maxDetailedDocumentHeight;
  }
  function sanitizeMinimapCloneTree(root) {
    root.querySelectorAll("*").forEach((node) => {
      const isHtml = node.namespaceURI === "http://www.w3.org/1999/xhtml" || node.namespaceURI === null;
      if (isHtml && node.hasAttribute("id")) node.removeAttribute("id");
      const tag = node.tagName;
      if (tag === "A" || tag === "BUTTON" || tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") {
        node.setAttribute("tabindex", "-1");
        node.removeAttribute("href");
      }
    });
  }
  function cloneDocumentElementForMinimap(source, sourceStyle) {
    const clone = source.cloneNode(true);
    minimapSourceReady = true;
    clone.removeAttribute("id");
    clone.setAttribute("aria-hidden", "true");
    clone.inert = true;
    clone.style.paddingTop = sourceStyle.paddingTop;
    clone.style.paddingRight = "0";
    clone.style.paddingBottom = sourceStyle.paddingBottom;
    clone.style.paddingLeft = "0";
    sanitizeMinimapCloneTree(clone);
    return clone;
  }
  function cloneDocumentForMinimap() {
    const source = document.querySelector(".mm-document");
    if (!source) {
      minimapSourceReady = false;
      return null;
    }
    return cloneDocumentElementForMinimap(source, getComputedStyle(source));
  }
  function cloneModelDocumentForMinimap(model) {
    const liveSource = document.querySelector(".mm-document");
    if (!liveSource) {
      minimapSourceReady = false;
      return null;
    }
    const source = document.createElement(liveSource.localName);
    source.className = liveSource.className;
    source.dataset["mmMinimapSource"] = "model-fragment";
    source.dataset["mmModelMinimapSectionCount"] = String(model.getSectionCount());
    source.dataset["mmModelMinimapTotalHeight"] = String(model.getTotalHeight());
    source.append(createFullDocumentFragmentFromWindowModel(document, model));
    const clone = cloneDocumentElementForMinimap(source, getComputedStyle(liveSource));
    clone.dataset["mmModelMinimapSectionCount"] = String(model.getSectionCount());
    clone.dataset["mmModelMinimapTotalHeight"] = String(model.getTotalHeight());
    return clone;
  }
  function refreshMinimapContent(phase = "A") {
    cancelDeferredMinimapContentRefresh();
    emitMark("mm-minimap-refresh-start", { phase });
    postPerfMark("mm-minimap-refresh-start", { phase });
    ensureMinimap();
    if (!minimapContent || !minimapRoot) {
      emitMark("mm-minimap-refresh-end", { phase, skipped: "no-mount" });
      postPerfMark("mm-minimap-refresh-end", { phase, skipped: "no-mount" });
      return;
    }
    const buildDecision = shouldBuildDetailedMinimapContent();
    if (!buildDecision.allowed) {
      minimapSourceReady = false;
      minimapDocumentHeight = buildDecision.documentHeight;
      currentMinimapLayout = null;
      minimapContent.replaceChildren();
      updateMinimapVisibility(true);
      emitMark("mm-minimap-refresh-skipped", {
        phase,
        reason: buildDecision.reason ?? "not-allowed",
        documentHeight: buildDecision.documentHeight
      });
      postPerfMark("mm-minimap-refresh-skipped", {
        phase,
        reason: buildDecision.reason ?? "not-allowed",
        documentHeight: buildDecision.documentHeight
      });
      return;
    }
    currentMinimapLayout = null;
    const model = getModelMinimapSource();
    const clone = model === null ? cloneDocumentForMinimap() : cloneModelDocumentForMinimap(model);
    if (!clone) {
      emitMark("mm-minimap-refresh-end", { phase, skipped: "no-source" });
      postPerfMark("mm-minimap-refresh-end", { phase, skipped: "no-source" });
      return;
    }
    const root = document.scrollingElement ?? document.documentElement;
    minimapDocumentHeight = model === null ? root.scrollHeight : model.getTotalHeight();
    if (isPolicyHeavyMinimapDocument()) {
      minimapContent.style.width = `${calculateDocumentContentWidthFromCssModel(true)}px`;
    }
    minimapContent.replaceChildren(clone);
    syncModelMinimapCloneMetadata();
    updateMinimapVisibility(true);
    updateMinimapViewport({ skipVisibilityUpdate: true });
    const source = model === null ? "live-dom" : "model-fragment";
    emitMark("mm-minimap-refresh-end", { phase, documentHeight: minimapDocumentHeight, source });
    postPerfMark("mm-minimap-refresh-end", { phase, documentHeight: minimapDocumentHeight, source });
    scheduleCurrentProcessedDocumentCacheClone();
  }
  function ensureDetailedMinimapContentForVisiblePath(phase = "A") {
    if (minimapSourceReady || !shouldBuildDetailedMinimapContent().allowed) {
      return;
    }
    if (minimapContentRefreshTimer !== void 0) {
      window.clearTimeout(minimapContentRefreshTimer);
      minimapContentRefreshTimer = void 0;
    }
    refreshMinimapContent(phase);
  }
  function refreshInitialVisibleMinimapContent() {
    if (!minimapSourceReady) {
      refreshMinimapContent("A");
      return;
    }
    minimapDocumentHeight = getCurrentMinimapDocumentHeight();
    updateMinimapVisibility(true);
    updateMinimapViewport();
    emitMark("mm-minimap-refresh-skipped", {
      phase: "A",
      reason: "initial-source-ready",
      documentHeight: minimapDocumentHeight
    });
    postPerfMark("mm-minimap-refresh-skipped", {
      phase: "A",
      reason: "initial-source-ready",
      documentHeight: minimapDocumentHeight
    });
  }
  function postCachedMinimapState(state2) {
    ensureMinimap();
    if (!minimapRoot) {
      return;
    }
    const visible = state2.hasPosted && state2.visible;
    const reservedWidth = visible ? Math.max(0, state2.reservedWidth) : 0;
    minimapRoot.hidden = !visible;
    document.body.classList.toggle(MINIMAP_VISIBLE_CLASS, visible);
    lastPostedMinimapState = { hasPosted: true, visible, reservedWidth };
    postHostMessage({ type: "minimap-state", visible, reservedWidth });
  }
  function restoreCachedMinimapContent() {
    const snapshot = restoredCachedMinimapSnapshot;
    restoredCachedMinimapSnapshot = null;
    if (!snapshot) {
      return false;
    }
    ensureMinimap();
    const restored = restoreMinimapSnapshot(snapshot, { minimapContent, minimapViewport });
    if (!restored) {
      return false;
    }
    minimapDocumentHeight = restored.documentHeight;
    minimapSourceReady = true;
    postCachedMinimapState(restored.lastPostedState);
    emitMark("mm-minimap-cache-hit", {
      documentHeight: restored.documentHeight,
      nodeCount: restored.contentNodeCount
    });
    postPerfMark("mm-minimap-cache-hit", {
      documentHeight: restored.documentHeight,
      nodeCount: restored.contentNodeCount
    });
    const refreshGeneration = layoutReadyGeneration;
    window.requestAnimationFrame(() => {
      if (refreshGeneration !== layoutReadyGeneration) {
        return;
      }
      updateMinimapVisibility(true);
      updateMinimapViewport();
      updateWidthHandlePositionForCurrentLayout();
    });
    return true;
  }
  var activeHeadingObserver = null;
  var lastPostedActiveHeadingId = null;
  function addHeadingSegment(segments, kind, text) {
    if (!text) {
      return;
    }
    const previous = segments.length > 0 ? segments[segments.length - 1] : void 0;
    if (previous?.kind === kind) {
      previous.text += text;
      return;
    }
    segments.push({ kind, text });
  }
  function extractHeadingSegments(root) {
    const segments = [];
    const visit = (node) => {
      if (node.nodeType === Node.TEXT_NODE) {
        addHeadingSegment(segments, "text", node.textContent);
        return;
      }
      if (!(node instanceof Element)) {
        return;
      }
      if (node instanceof HTMLElement && node.classList.contains("math-inline")) {
        addHeadingSegment(segments, "math", node.dataset.tex ?? node.getAttribute("data-tex") ?? node.textContent);
        return;
      }
      node.childNodes.forEach(visit);
    };
    root.childNodes.forEach(visit);
    return segments;
  }
  function readHeadingPayload(node, metadata = {}) {
    const id = node.id;
    if (!id) {
      return null;
    }
    const tag = node.tagName.toUpperCase();
    const level = Number.parseInt(tag.slice(1), 10);
    if (!Number.isFinite(level) || level < 1 || level > 6) {
      return null;
    }
    const segments = extractHeadingSegments(node);
    const text = segments.length > 0 ? segments.map((segment) => segment.text).join("").trim() : (node.textContent ?? "").trim();
    const heading = { id, level, text, segments };
    const includeModelMetadata = metadata.blockIndex !== void 0 || metadata.sectionIndex !== void 0;
    const blockIndex = includeModelMetadata ? readClosestBlockIndex(node) ?? metadata.blockIndex : void 0;
    if (blockIndex !== void 0) {
      heading.blockIndex = blockIndex;
    }
    if (metadata.sectionIndex !== void 0) {
      heading.sectionIndex = metadata.sectionIndex;
    }
    return heading;
  }
  function readLiveHeadingNodes(main) {
    return Array.from(
      main.querySelectorAll("h1, h2, h3, h4, h5, h6")
    );
  }
  function readLiveHeadingPayloads(main) {
    const nodes = readLiveHeadingNodes(main);
    return {
      headings: nodes.map((node) => readHeadingPayload(node)).filter((heading) => heading !== null),
      nodes
    };
  }
  function readModelHeadingPayloads(model) {
    const headings = [];
    for (const entry of model.sections) {
      if (!entry.html) {
        continue;
      }
      const template = document.createElement("template");
      template.innerHTML = entry.html;
      const nodes = Array.from(
        template.content.querySelectorAll("h1, h2, h3, h4, h5, h6")
      );
      for (const node of nodes) {
        const heading = readHeadingPayload(node, {
          blockIndex: entry.blockIndex,
          sectionIndex: entry.sectionIndex
        });
        if (heading !== null) {
          headings.push(heading);
        }
      }
    }
    return headings;
  }
  function readClosestBlockIndex(node) {
    const block = node.closest("[data-mm-block-index]");
    const raw = block?.dataset["mmBlockIndex"];
    if (raw === void 0 || raw.trim() === "") {
      return void 0;
    }
    const parsed = Number.parseInt(raw, 10);
    return Number.isFinite(parsed) ? parsed : void 0;
  }
  function extractAndPostHeadings() {
    const main = document.querySelector("main.mm-document");
    if (!main) {
      postHostMessage({ type: "headings-updated", headings: [] });
      lastExtractedHeadings = [];
      lastPostedActiveHeadingId = null;
      return;
    }
    const live = readLiveHeadingPayloads(main);
    const headings = virtualizationEnabled && virtualizedDocumentWindowModel !== null ? readModelHeadingPayloads(virtualizedDocumentWindowModel) : live.headings;
    lastExtractedHeadings = headings.map(cloneHeadingPayload);
    postHostMessage({ type: "headings-updated", headings });
    rebuildActiveHeadingObserver(live.nodes.filter((n) => !!n.id));
  }
  function postCachedHeadings() {
    const cachedHeadings = restoredCachedHeadings;
    restoredCachedHeadings = null;
    if (cachedHeadings === null || cachedHeadings.length === 0) {
      extractAndPostHeadings();
      return;
    }
    const headings = cachedHeadings.map(cloneHeadingPayload);
    lastExtractedHeadings = headings.map(cloneHeadingPayload);
    postHostMessage({ type: "headings-updated", headings });
    if (activeHeadingObserver) {
      activeHeadingObserver.disconnect();
      activeHeadingObserver = null;
    }
    lastPostedActiveHeadingId = null;
    const rebuildGeneration = layoutReadyGeneration;
    window.setTimeout(() => {
      if (rebuildGeneration !== layoutReadyGeneration) {
        return;
      }
      const main = document.querySelector("main.mm-document");
      if (!main) {
        return;
      }
      const nodes = Array.from(
        main.querySelectorAll("h1, h2, h3, h4, h5, h6")
      );
      rebuildActiveHeadingObserver(nodes.filter((n) => !!n.id));
    }, 750);
  }
  function rebuildActiveHeadingObserver(headingNodes) {
    if (activeHeadingObserver) {
      activeHeadingObserver.disconnect();
      activeHeadingObserver = null;
    }
    lastPostedActiveHeadingId = null;
    if (headingNodes.length === 0) {
      return;
    }
    const inViewport = /* @__PURE__ */ new Set();
    const callback = (entries) => {
      for (const entry of entries) {
        const target = entry.target;
        if (entry.isIntersecting) {
          inViewport.add(target);
        } else {
          inViewport.delete(target);
        }
      }
      let active = null;
      for (const node of headingNodes) {
        const rect = node.getBoundingClientRect();
        if (rect.top <= 10) {
          active = node;
        } else {
          break;
        }
      }
      if (active === null) {
        active = headingNodes[0] ?? null;
      }
      if (active === null) {
        return;
      }
      const id = active.id;
      if (id && id !== lastPostedActiveHeadingId) {
        lastPostedActiveHeadingId = id;
        postHostMessage({ type: "active-heading-changed", id });
      }
    };
    activeHeadingObserver = new IntersectionObserver(callback, {
      rootMargin: "0px 0px -85% 0px",
      threshold: [0, 1]
    });
    for (const node of headingNodes) {
      activeHeadingObserver.observe(node);
    }
    window.requestAnimationFrame(() => {
      let active = null;
      for (const node of headingNodes) {
        const rect = node.getBoundingClientRect();
        if (rect.top <= 10) {
          active = node;
        } else {
          break;
        }
      }
      if (active === null) {
        active = headingNodes[0] ?? null;
      }
      if (active && active.id && active.id !== lastPostedActiveHeadingId) {
        lastPostedActiveHeadingId = active.id;
        postHostMessage({ type: "active-heading-changed", id: active.id });
      }
    });
  }
  function shouldShowMinimap() {
    const metrics = getDocumentScrollMetrics();
    const documentHeight = getCurrentMinimapDocumentHeight();
    const viewportHeight = metrics.viewportHeight;
    if (!hasReceivedHostPreferences || !minimapPolicy || !viewerChromeEnabled || !minimapSourceReady || minimapMode === "off" || viewportHeight <= 0 || documentHeight <= viewportHeight) {
      return false;
    }
    if (minimapMode === "on") {
      return true;
    }
    if (documentHeight > minimapPolicy.maxDetailedDocumentHeight) {
      return false;
    }
    return window.innerWidth >= minimapPolicy.minHostWidth && documentHeight >= viewportHeight * minimapPolicy.minScrollableViewportRatio;
  }
  function updateMinimapVisibility(forcePostState = false) {
    ensureMinimap();
    if (!minimapRoot) {
      return false;
    }
    const wasVisible = !minimapRoot.hidden;
    const hadClass = document.body.classList.contains(MINIMAP_VISIBLE_CLASS);
    const visible = shouldShowMinimap();
    minimapRoot.hidden = !visible;
    document.body.classList.toggle(MINIMAP_VISIBLE_CLASS, visible);
    postMinimapState(visible, forcePostState);
    const changed = wasVisible !== visible || hadClass !== visible;
    if (changed) {
      updateWidthHandlePositionForCurrentLayout();
    }
    return changed;
  }
  function readConfiguredMinimapReservedWidth() {
    const minimapGap = readRootPixelVariable("--mm-minimap-gap", 0);
    const configuredMinimapWidth = readRootPixelVariable("--mm-minimap-width", 0);
    if (configuredMinimapWidth > 0) {
      return Math.max(0, configuredMinimapWidth + minimapGap * 2);
    }
    return 0;
  }
  function getCurrentMinimapReservedWidth() {
    if (!minimapRoot || minimapRoot.hidden) {
      return 0;
    }
    const configuredReservedWidth = readConfiguredMinimapReservedWidth();
    if (configuredReservedWidth > 0) {
      return configuredReservedWidth;
    }
    const minimapGap = readRootPixelVariable("--mm-minimap-gap", 0);
    const minimapWidth = minimapRoot.getBoundingClientRect().width;
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
  function postTransactionMinimapSettled(transactionGeneration) {
    if (!Number.isFinite(transactionGeneration) || transactionGeneration <= 0) {
      return;
    }
    ensureDetailedMinimapContentForVisiblePath();
    updateMinimapVisibility(true);
    updateMinimapViewport({ skipVisibilityUpdate: true });
    const visible = minimapRoot ? !minimapRoot.hidden : false;
    const reservedWidth = visible ? getCurrentMinimapReservedWidth() : 0;
    postHostMessage({
      type: "minimap-settled",
      transactionGeneration,
      visible,
      reservedWidth
    });
  }
  function cloneSpaceTop(el, container) {
    let y = 0;
    let n = el;
    while (n && n !== container) {
      y += n.offsetTop;
      n = n.offsetParent;
    }
    return n === container ? y : null;
  }
  function cloneYForDocBlock(docBlock, clone, rect, clientY) {
    const idx = docBlock.dataset["mmBlockIndex"];
    if (idx === void 0) return null;
    const cln = clone.querySelector(`[data-mm-block-index="${idx}"]`);
    if (!cln) return null;
    const top = cloneSpaceTop(cln, clone);
    if (top === null) return null;
    const offset = clientY - rect.top;
    const contribution = offset <= 0 ? offset : rect.height > 0 ? offset / rect.height * cln.offsetHeight : 0;
    return top + contribution;
  }
  function getDocumentViewportTopCloneY(clone) {
    const docRoot = document.querySelector("body > main.mm-document");
    if (!docRoot) return null;
    for (const b of Array.from(docRoot.querySelectorAll("[data-mm-block-index]"))) {
      const r = b.getBoundingClientRect();
      if (r.height > 0 && r.bottom >= 0) {
        const y = cloneYForDocBlock(b, clone, r, 0);
        if (y !== null) return y;
      }
    }
    return null;
  }
  function cloneBlockAtCloneY(clone, y) {
    let prev = null;
    let prevTop = 0;
    for (const b of Array.from(clone.querySelectorAll("[data-mm-block-index]"))) {
      const top = cloneSpaceTop(b, clone);
      if (top === null) continue;
      const h = b.offsetHeight;
      if (y < top) return { block: b, mode: "gap", value: y - top };
      if (y < top + h) return { block: b, mode: "frac", value: h > 0 ? (y - top) / h : 0 };
      prev = b;
      prevTop = top;
    }
    if (prev) return { block: prev, mode: "tail", value: y - (prevTop + prev.offsetHeight) };
    return null;
  }
  function docScrollTopForCloneY(root, y) {
    if (!minimapContent) return null;
    const hit = cloneBlockAtCloneY(minimapContent, y);
    if (!hit) return null;
    const idx = hit.block.dataset["mmBlockIndex"];
    if (idx === void 0) return null;
    const blockIndex = Number.parseInt(idx, 10);
    const docBlock = document.querySelector(`body > main.mm-document [data-mm-block-index="${idx}"]`);
    let scrollTop;
    if (docBlock) {
      const r = docBlock.getBoundingClientRect();
      const contribution = hit.mode === "gap" ? hit.value : hit.mode === "tail" ? r.height + hit.value : hit.value * r.height;
      scrollTop = root.scrollTop + r.top + contribution;
    } else if (virtualizationEnabled && virtualizedDocumentWindowModel !== null && Number.isFinite(blockIndex)) {
      const entry = virtualizedDocumentWindowModel.getEntryContainingBlockIndex(blockIndex);
      if (entry === void 0) {
        return null;
      }
      const sectionHeight = virtualizedDocumentWindowModel.sectionEffectiveHeight(entry.sectionIndex);
      const contribution = hit.mode === "gap" ? hit.value : hit.mode === "tail" ? sectionHeight + hit.value : hit.value * sectionHeight;
      scrollTop = entry.cumulativeTop + contribution;
    } else {
      return null;
    }
    const maxScrollTop = Math.max(0, root.scrollHeight - root.clientHeight);
    return Math.max(0, Math.min(maxScrollTop, scrollTop));
  }
  function updateMinimapViewport(options = {}) {
    ensureMinimap();
    if (!minimapRoot || !minimapContent || !minimapViewport) {
      return;
    }
    if (options.skipVisibilityUpdate !== true) {
      updateMinimapVisibility();
    }
    if (minimapRoot.hidden) {
      currentMinimapLayout = null;
      return;
    }
    const root = document.scrollingElement ?? document.documentElement;
    const knownPolicyHeavyDocument = isPolicyHeavyMinimapDocument();
    const documentScrollHeight = root.scrollHeight;
    const policyHeavyDocument = knownPolicyHeavyDocument || minimapPolicy !== null && documentScrollHeight > minimapPolicy.maxDetailedDocumentHeight;
    const source = policyHeavyDocument ? null : document.querySelector(".mm-document");
    if (!policyHeavyDocument && !source) {
      return;
    }
    const minimapWidth = policyHeavyDocument ? readRootPixelVariable("--mm-minimap-width", 136) : minimapRoot.clientWidth;
    const minimapHeight = policyHeavyDocument ? Math.max(0, window.innerHeight - 128) : minimapRoot.clientHeight;
    const documentWidth = policyHeavyDocument ? calculateDocumentContentWidthFromCssModel(!minimapRoot.hidden) : (() => {
      const sourceElement = source;
      const sourceStyle = getComputedStyle(sourceElement);
      return calculateMinimapDocumentWidth({
        borderBoxWidth: sourceElement.clientWidth || sourceElement.getBoundingClientRect().width,
        paddingLeft: readPixelValue(sourceStyle.paddingLeft),
        paddingRight: readPixelValue(sourceStyle.paddingRight)
      });
    })();
    const viewportHeight = policyHeavyDocument ? Math.max(0, window.innerHeight) : root.clientHeight;
    if (minimapHeight <= 0 || minimapWidth <= 0 || documentScrollHeight <= 0 || viewportHeight <= 0) {
      return;
    }
    const nextContentWidth = `${documentWidth}px`;
    if (minimapContent.style.width !== nextContentWidth) {
      minimapContent.style.width = nextContentWidth;
    }
    const measuredContentHeight = minimapContent.scrollHeight;
    const contentHeight = measuredContentHeight > 0 ? measuredContentHeight : documentScrollHeight;
    let layout;
    const anchorTopY = getDocumentViewportTopCloneY(minimapContent);
    if (anchorTopY !== null) {
      layout = calculateMinimapViewportLayout({
        minimapWidth,
        minimapHeight,
        documentWidth,
        documentHeight: contentHeight,
        viewportHeight,
        scrollTop: anchorTopY
      });
    } else {
      const realMaxScrollTop = Math.max(0, documentScrollHeight - viewportHeight);
      const scrollProgress = realMaxScrollTop > 0 ? Math.min(1, Math.max(0, root.scrollTop / realMaxScrollTop)) : 0;
      const contentScrollTop = scrollProgress * Math.max(0, contentHeight - viewportHeight);
      layout = calculateMinimapViewportLayout({
        minimapWidth,
        minimapHeight,
        documentWidth,
        documentHeight: contentHeight,
        viewportHeight,
        scrollTop: contentScrollTop
      });
    }
    if (!layout) {
      currentMinimapLayout = null;
      return;
    }
    currentMinimapLayout = layout;
    minimapContent.style.transform = layout.transform;
    minimapViewport.style.transform = `translateY(${layout.thumbTop}px)`;
    minimapViewport.style.height = `${layout.thumbHeight}px`;
  }
  function getCurrentMinimapThumbTravel() {
    if (currentMinimapLayout) {
      return Math.max(1, currentMinimapLayout.thumbTravel);
    }
    const minimapHeight = minimapRoot?.clientHeight ?? 0;
    return Math.max(1, minimapHeight - 22);
  }
  function scrollFromMinimapClientY(clientY) {
    if (!minimapRoot) {
      return;
    }
    const root = document.scrollingElement ?? document.documentElement;
    const rect = minimapRoot.getBoundingClientRect();
    const minimapY = Math.max(0, Math.min(rect.height, clientY - rect.top));
    if (currentMinimapLayout && minimapContent) {
      const cloneYTarget = (minimapY - currentMinimapLayout.contentTranslateY) / currentMinimapLayout.scale;
      const firstTarget = docScrollTopForCloneY(root, cloneYTarget);
      if (firstTarget !== null) {
        window.scrollTo({ top: firstTarget, behavior: "instant" });
        let attempts = 0;
        const refine = () => {
          if (++attempts > 3) return;
          const next = docScrollTopForCloneY(root, cloneYTarget);
          if (next !== null && Math.abs(next - root.scrollTop) > 2) {
            window.scrollTo({ top: next, behavior: "instant" });
            window.requestAnimationFrame(refine);
          }
        };
        window.requestAnimationFrame(refine);
        return;
      }
    }
    const thumbTravel = getCurrentMinimapThumbTravel();
    const maxScrollTop = Math.max(0, root.scrollHeight - root.clientHeight);
    const targetScrollTop = Math.min(minimapY, thumbTravel) / thumbTravel * maxScrollTop;
    const clamped = Math.max(0, Math.min(maxScrollTop, targetScrollTop));
    window.scrollTo({ top: clamped, behavior: "instant" });
  }
  function scrollToProgress(progressPercent) {
    const root = document.scrollingElement ?? document.documentElement;
    const maximum = Math.max(0, root.scrollHeight - root.clientHeight);
    const progress = Number.isFinite(progressPercent) ? Math.max(0, Math.min(100, progressPercent)) : 0;
    window.scrollTo({ top: maximum * (progress / 100), behavior: "instant" });
  }
  function handleMinimapPointerDown(event) {
    minimapDragging = true;
    minimapDragStartClientY = event.clientY;
    const root = document.scrollingElement ?? document.documentElement;
    minimapDragStartScrollTop = root.scrollTop;
    minimapDragMode = "tentative";
    minimapDragGrabOffset = 0;
    if (minimapRoot && minimapViewport) {
      const rootTop = minimapRoot.getBoundingClientRect().top;
      const thumbTop = minimapViewport.getBoundingClientRect().top - rootTop;
      minimapDragGrabOffset = event.clientY - rootTop - thumbTop;
    }
    minimapRoot?.setPointerCapture(event.pointerId);
    event.preventDefault();
  }
  function handleMinimapPointerMove(event) {
    if (!minimapDragging || minimapDragStartClientY === null) {
      return;
    }
    const delta = event.clientY - minimapDragStartClientY;
    if (minimapDragMode === "tentative" && Math.abs(delta) < MINIMAP_DRAG_THRESHOLD_PX) {
      return;
    }
    minimapDragMode = "panning";
    const root = document.scrollingElement ?? document.documentElement;
    const maxScrollTop = Math.max(0, root.scrollHeight - root.clientHeight);
    if (minimapRoot && minimapViewport && currentMinimapLayout && minimapContent && currentMinimapLayout.thumbSlope > 0) {
      const rootTop = minimapRoot.getBoundingClientRect().top;
      const desiredThumbTop = event.clientY - rootTop - minimapDragGrabOffset;
      const cloneY = desiredThumbTop / currentMinimapLayout.thumbSlope;
      const target = docScrollTopForCloneY(root, cloneY);
      if (target !== null) {
        window.scrollTo({ top: Math.max(0, Math.min(maxScrollTop, target)), behavior: "instant" });
        const pinnedTop = Math.max(0, Math.min(currentMinimapLayout.thumbTravel, desiredThumbTop));
        minimapViewport.style.transform = `translateY(${pinnedTop}px)`;
        event.preventDefault();
        return;
      }
    }
    const thumbTravel = getCurrentMinimapThumbTravel();
    const scrollDelta = delta * (maxScrollTop / thumbTravel);
    const clampedScrollTop = Math.max(0, Math.min(maxScrollTop, minimapDragStartScrollTop + scrollDelta));
    window.scrollTo({ top: clampedScrollTop, behavior: "instant" });
    event.preventDefault();
  }
  function handleMinimapPointerUp(event) {
    if (!minimapDragging) {
      return;
    }
    const wasTap = minimapDragMode === "tentative";
    minimapDragging = false;
    minimapDragStartClientY = null;
    minimapDragMode = "tentative";
    try {
      minimapRoot?.releasePointerCapture(event.pointerId);
    } catch {
    }
    if (wasTap) {
      scrollFromMinimapClientY(event.clientY);
    }
  }
  function queueMinimapViewportUpdate(perfMarkName) {
    if (minimapViewportFrameRequested) {
      return;
    }
    minimapViewportFrameRequested = true;
    window.requestAnimationFrame(() => {
      minimapViewportFrameRequested = false;
      updateMinimapVisibility();
      updateMinimapViewport();
      if (perfMarkName) {
        postPerfMark(perfMarkName);
      }
    });
  }
  function queueMinimapRefreshAfterLayoutSettles() {
    window.clearTimeout(minimapRefreshTimer);
    minimapRefreshTimer = window.setTimeout(() => {
      queueMinimapViewportUpdate();
    }, MINIMAP_REFRESH_DEBOUNCE_MS);
  }
  function cancelDeferredMinimapContentRefresh(invalidate = true) {
    if (invalidate) {
      ++progressiveMinimapRefreshGeneration;
    }
    const handle = minimapDeferredContentRefreshHandle;
    minimapDeferredContentRefreshHandle = null;
    if (!handle) {
      return;
    }
    if (handle.kind === "idle") {
      window.cancelIdleCallback?.(handle.id);
    } else {
      window.clearTimeout(handle.id);
    }
  }
  function queueMinimapContentRefreshAfterLayoutSettles(phase = "A") {
    cancelDeferredMinimapContentRefresh();
    window.clearTimeout(minimapContentRefreshTimer);
    minimapContentRefreshTimer = window.setTimeout(() => {
      minimapContentRefreshTimer = void 0;
      refreshMinimapContent(phase);
    }, MINIMAP_REFRESH_DEBOUNCE_MS);
  }
  function queueProgressiveMinimapAppendRefresh(message) {
    if (message.html.length === 0) {
      return;
    }
    const generation = ++progressiveMinimapRefreshGeneration;
    const renderId = message.renderId;
    cancelDeferredMinimapContentRefresh(false);
    const run = () => {
      minimapDeferredContentRefreshHandle = null;
      if (generation !== progressiveMinimapRefreshGeneration) {
        return;
      }
      if (renderId !== void 0 && currentDocumentRenderId !== null && renderId !== currentDocumentRenderId) {
        postPerfMark("mm-minimap-progressive-append-stale", {
          renderId,
          currentRenderId: currentDocumentRenderId
        });
        return;
      }
      emitMark("mm-minimap-progressive-append-start", { renderId: renderId ?? null });
      postPerfMark("mm-minimap-progressive-append-start", { renderId: renderId ?? null });
      refreshMinimapContent("A");
      emitMark("mm-minimap-progressive-append-end", {
        renderId: renderId ?? null,
        documentHeight: minimapDocumentHeight
      });
      postPerfMark("mm-minimap-progressive-append-end", {
        renderId: renderId ?? null,
        documentHeight: minimapDocumentHeight
      });
    };
    const requestIdle = window.requestIdleCallback;
    if (requestIdle) {
      minimapDeferredContentRefreshHandle = {
        kind: "idle",
        id: requestIdle(run, { timeout: 1200 })
      };
      return;
    }
    minimapDeferredContentRefreshHandle = {
      kind: "timeout",
      id: window.setTimeout(run, 160)
    };
  }
  function scheduleResizeReactions() {
    if (resizeReactFrameRequested) {
      return;
    }
    if (modeRevealPrepared) {
      return;
    }
    resizeReactFrameRequested = true;
    window.requestAnimationFrame(() => {
      resizeReactFrameRequested = false;
      if (widthHandleDragging) {
        return;
      }
      updateWidthHandlePositionForCurrentLayout();
      queueMinimapViewportUpdate();
    });
  }
  var lastAppliedReadingPreferences = null;
  var pendingReadingPreferences = null;
  var pendingReadingPreferencesSkipFrameWait = false;
  var applyPrefsFrameRequested = false;
  var RENDERER_FALLBACK_MIN_MAX_WIDTH = 320;
  var hostMinMaxWidth = RENDERER_FALLBACK_MIN_MAX_WIDTH;
  var heavyLiveUpdateTimer;
  var HEAVY_LIVE_UPDATE_DEBOUNCE_MS = 80;
  function normalizeFontFamilyMode(value) {
    if (value === "sans" || value === "mono") return value;
    return "serif";
  }
  function applyReadingPreferences(message) {
    if (typeof message.minMaxWidth === "number" && Number.isFinite(message.minMaxWidth) && message.minMaxWidth > 0) {
      hostMinMaxWidth = message.minMaxWidth;
    }
    const next = {
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
    pendingReadingPreferences = next;
    pendingReadingPreferencesSkipFrameWait = pendingReadingPreferencesSkipFrameWait || message.skipFrameWait === true;
    if (!next.viewerChromeEnabled) {
      viewerChromeEnabled = false;
      applyViewerChromeState();
      updateMinimapVisibility(true);
      updateWidthHandlePositionForCurrentLayout();
    }
    if (applyPrefsFrameRequested) return;
    applyPrefsFrameRequested = true;
    requestAnimationFrame(flushPendingReadingPreferences);
  }
  function flushPendingReadingPreferences() {
    applyPrefsFrameRequested = false;
    const next = pendingReadingPreferences;
    const skipFrameWait = pendingReadingPreferencesSkipFrameWait;
    pendingReadingPreferences = null;
    pendingReadingPreferencesSkipFrameWait = false;
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
      if (!viewerChromeEnabled) {
        updateMinimapVisibility(true);
        updateWidthHandlePositionForCurrentLayout();
      } else {
        updateWidthHandlePositionForCurrentLayout();
      }
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
    const layoutAffectingChange = fontFamilyChanged || fontSizeChanged || lineHeightChanged || maxWidthChanged || minimapModeChanged || viewerChromeChanged;
    if (layoutAffectingChange) {
      invalidateVirtualizationShadowModel();
      if (!minimapSourceReady && shouldBuildDetailedMinimapContent().allowed) {
        queueMinimapContentRefreshAfterLayoutSettles();
      } else {
        scheduleHeavyLiveUpdate();
      }
    }
    const suppressFirstPrefsBootstrap = !hadHostPreferences && firstPrefsBootstrapSuppressedByLoadGeneration === initialRenderPipelineGeneration;
    if (!hadHostPreferences) {
      firstPrefsBootstrapSuppressedByLoadGeneration = null;
    }
    if (!hadHostPreferences && !suppressFirstPrefsBootstrap) {
      const pipelineGeneration = ++initialRenderPipelineGeneration;
      void runInitialRenderPipeline({
        getCurrentTheme,
        applyTheme,
        initMermaidWithTheme,
        renderMath: renderMath2,
        renderMermaid,
        renderCodeBlocks,
        deferPostReadyWork: deferPostReadyEnhancements,
        scheduleLayoutReady: () => {
          initialRenderPipelineCompleted = true;
          scheduleLayoutReady(skipFrameWait);
        },
        postPerfMark,
        notifyPostReadyEnhancementsComplete: () => {
          postPostReadyEnhancementsComplete(currentDocumentRenderId ?? void 0, void 0, void 0);
        },
        isCurrent: () => pipelineGeneration === initialRenderPipelineGeneration
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
    if (message.type === "host-shortcuts-reset") {
      resetHostShortcutsForModeSwitch?.();
      return;
    }
    if (message.type === "theme") {
      if (initialRenderPipelineCompleted) {
        void handleThemeChange(message.theme, message.requestId);
      } else {
        document.documentElement.dataset.theme = message.theme;
        postThemeAppliedAfterPaint(message.theme, message.requestId);
      }
      return;
    }
    if (message.type === "minimap-policy") {
      minimapPolicy = message.minimapPolicy;
      if (!minimapSourceReady && shouldBuildDetailedMinimapContent().allowed) {
        queueMinimapContentRefreshAfterLayoutSettles();
      } else {
        queueMinimapViewportUpdate();
      }
      return;
    }
    if (message.type === "reading-preferences") {
      applyReadingPreferences(message);
      return;
    }
    if (message.type === "scroll-to") {
      if (!virtualizationEnabled) {
        document.getElementById(message.anchor)?.scrollIntoView({ block: "start" });
        return;
      }
      const main = document.querySelector("main.mm-document");
      if (main === null || virtualizedDocumentWindowModel === null || virtualizedDocumentWindowController === null) {
        document.getElementById(message.anchor)?.scrollIntoView({ block: "start" });
        return;
      }
      void renderWindowTargetThenAct({
        action: (context) => {
          landVirtualizedProgrammaticNavigation({
            context,
            descriptor: { anchor: message.anchor, kind: "heading-anchor" },
            viewportOffsetY: 0
          });
        },
        actionKind: "navigate",
        controller: virtualizedDocumentWindowController,
        descriptor: { anchor: message.anchor, kind: "heading-anchor" },
        legacyAction: () => {
          document.getElementById(message.anchor)?.scrollIntoView({ block: "start" });
        },
        main,
        model: virtualizedDocumentWindowModel,
        ownerWindow: window,
        root: getDocumentScrollRoot(),
        virtualizationEnabled: true
      });
      return;
    }
    if (message.type === "scroll-to-heading") {
      if (!virtualizationEnabled) {
        const target = document.getElementById(message.id);
        if (target) {
          target.scrollIntoView({ behavior: "smooth", block: "start" });
        }
        return;
      }
      const main = document.querySelector("main.mm-document");
      if (main === null || virtualizedDocumentWindowModel === null || virtualizedDocumentWindowController === null) {
        const target = document.getElementById(message.id);
        if (target) {
          target.scrollIntoView({ behavior: "smooth", block: "start" });
        }
        return;
      }
      const descriptor = { anchor: message.id, kind: "heading-anchor" };
      void renderWindowTargetThenAct({
        action: (context) => {
          landVirtualizedProgrammaticNavigation({
            context,
            descriptor,
            viewportOffsetY: 0
          });
        },
        actionKind: "navigate",
        controller: virtualizedDocumentWindowController,
        descriptor,
        legacyAction: () => {
          const target = document.getElementById(message.id);
          if (target) {
            target.scrollIntoView({ behavior: "smooth", block: "start" });
          }
        },
        main,
        model: virtualizedDocumentWindowModel,
        ownerWindow: window,
        root: getDocumentScrollRoot(),
        virtualizationEnabled: true
      });
      return;
    }
    if (message.type === "scroll-to-source-line") {
      scrollToSourceLine(message.sourceLine);
      return;
    }
    if (message.type === "find-results") {
      virtualizedFindProvider?.handleFindResults(message);
      return;
    }
    if (message.type === "open-find-bar") {
      findBarController?.toggle();
      return;
    }
    if (message.type === "host-scrollbar") {
      document.documentElement.dataset.mmHostScrollbar = message.active ? "on" : "off";
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
      if (!virtualizationEnabled) {
        const target = document.querySelector(
          `[data-mm-block-index="${message.blockIndex}"]`
        );
        if (target) {
          target.scrollIntoView({ block: "start", behavior: "instant" });
        }
        return;
      }
      const main = document.querySelector("main.mm-document");
      if (main === null || virtualizedDocumentWindowModel === null || virtualizedDocumentWindowController === null) {
        const target = document.querySelector(
          `[data-mm-block-index="${message.blockIndex}"]`
        );
        if (target) {
          target.scrollIntoView({ block: "start", behavior: "instant" });
        }
        return;
      }
      const descriptor = { blockIndex: message.blockIndex, kind: "block" };
      void renderWindowTargetThenAct({
        action: (context) => {
          landVirtualizedProgrammaticNavigation({
            context,
            descriptor,
            viewportOffsetY: 0
          });
        },
        actionKind: "navigate",
        controller: virtualizedDocumentWindowController,
        descriptor,
        legacyAction: () => {
          const target = document.querySelector(
            `[data-mm-block-index="${message.blockIndex}"]`
          );
          if (target) {
            target.scrollIntoView({ block: "start", behavior: "instant" });
          }
        },
        main,
        model: virtualizedDocumentWindowModel,
        ownerWindow: window,
        root: getDocumentScrollRoot(),
        virtualizationEnabled: true
      });
      return;
    }
    if (message.type === "load-document") {
      currentDocumentRenderId = message.renderId ?? null;
      const loadMessage = { html: message.html };
      if (message.documentName !== void 0) {
        loadMessage.documentName = message.documentName;
      }
      if (message.theme !== void 0) {
        loadMessage.theme = message.theme;
      }
      if (message.renderId !== void 0) {
        loadMessage.renderId = message.renderId;
      }
      if (message.skipFrameWait !== void 0) {
        loadMessage.skipFrameWait = message.skipFrameWait;
      }
      if (message.hasMermaid !== void 0) {
        loadMessage.hasMermaid = message.hasMermaid;
      }
      if (message.hasHljs !== void 0) {
        loadMessage.hasHljs = message.hasHljs;
      }
      if (message.cacheKey === null) {
        loadMessage.cacheKey = null;
      } else if (typeof message.cacheKey === "string" && message.cacheKey.length > 0) {
        loadMessage.cacheKey = message.cacheKey;
      } else {
        loadMessage.cacheKey = createProcessedDocumentCacheKey(
          message.html,
          message.theme ?? getCurrentTheme()
        );
      }
      applyLoadDocument(loadMessage, buildLoadDocumentDeps());
      return;
    }
    if (message.type === "append-document") {
      appendProgressiveDocumentHtml(message);
      return;
    }
    if (message.type === "load-cached-document") {
      currentDocumentRenderId = message.renderId ?? null;
      const loadMessage = {
        cacheKey: message.cacheKey
      };
      if (message.documentName !== void 0) {
        loadMessage.documentName = message.documentName;
      }
      if (message.theme !== void 0) {
        loadMessage.theme = message.theme;
      }
      if (message.renderId !== void 0) {
        loadMessage.renderId = message.renderId;
      }
      if (message.skipFrameWait !== void 0) {
        loadMessage.skipFrameWait = message.skipFrameWait;
      }
      if (message.hasMermaid !== void 0) {
        loadMessage.hasMermaid = message.hasMermaid;
      }
      if (message.hasHljs !== void 0) {
        loadMessage.hasHljs = message.hasHljs;
      }
      applyLoadDocument(loadMessage, buildLoadDocumentDeps());
      return;
    }
    if (message.type === "clear-document") {
      currentDocumentRenderId = null;
      clearModeRevealShield();
      clearDocumentState(buildLoadDocumentDeps());
      return;
    }
    if (message.type === "invalidate-document-cache-key") {
      currentDocumentCacheKey = null;
      cancelProcessedDocumentCacheClone();
      return;
    }
    if (message.type === "set-task-checkbox") {
      const box = document.querySelector(
        `input.mm-task-checkbox[data-task-line="${message.line}"]`
      );
      if (box) {
        box.checked = message.checked;
      }
      return;
    }
    if (message.type === "mode-settle-probe") {
      postPerfMark("mm-mode-settle-probe-received");
      applyModeSettleProbePreferences(message);
      const transactionGeneration = readModeSettleTransactionGeneration(message);
      if (modeToggleProbeFrameRequested) {
        if (transactionGeneration === void 0 || modeToggleProbeTransactionGeneration !== void 0 && transactionGeneration <= modeToggleProbeTransactionGeneration) {
          postPerfMark("mm-mode-settle-probe-duplicate");
          return;
        }
        postPerfMark("mm-mode-settle-probe-superseded", {
          previousGeneration: modeToggleProbeTransactionGeneration,
          transactionGeneration
        });
      }
      modeToggleProbeFrameRequested = true;
      modeToggleProbeTransactionGeneration = transactionGeneration;
      const settleSequence = ++modeToggleSettleSequence;
      const isCurrentProbe = () => settleSequence === modeToggleSettleSequence;
      const postModeToggleSettleAck = () => {
        if (!isCurrentProbe()) {
          return;
        }
        postPerfMark("mm-mode-settle-chrome-ready");
        modeToggleProbeFrameRequested = false;
        modeToggleProbeTransactionGeneration = void 0;
        if (transactionGeneration === void 0) {
          postHostMessage({ type: "mode-toggle-settled" });
        } else {
          postHostMessage({ type: "mode-toggle-settled", transactionGeneration });
        }
      };
      flushPendingReadingPreferences();
      ensureDetailedMinimapContentForVisiblePath();
      if (message.skipFrameWait === true) {
        postPerfMark("mm-mode-settle-frame-wait-skipped", {
          transactionGeneration
        });
        postModeToggleSettleAck();
        return;
      }
      const completeModeToggleSettleAfterPaint = () => {
        if (!isCurrentProbe()) {
          return;
        }
        updateMinimapViewport();
        updateWidthHandlePositionForCurrentLayout();
        window.requestAnimationFrame(() => {
          if (!isCurrentProbe()) {
            return;
          }
          postPerfMark("mm-mode-settle-post-chrome-paint");
          postModeToggleSettleAck();
        });
      };
      const settleAfterViewportReady = (attempt) => {
        if (!isCurrentProbe()) {
          return;
        }
        if (!isModeSettleViewportReady(message) && attempt < MODE_SETTLE_VIEWPORT_MAX_FRAMES) {
          postPerfMark("mm-mode-settle-viewport-wait", {
            attempt,
            width: window.innerWidth,
            height: window.innerHeight,
            expectedWidth: message.viewportWidth,
            expectedHeight: message.viewportHeight
          });
          window.requestAnimationFrame(() => settleAfterViewportReady(attempt + 1));
          return;
        }
        postPerfMark("mm-mode-settle-first-raf");
        flushPendingReadingPreferences();
        ensureDetailedMinimapContentForVisiblePath();
        updateMinimapVisibility();
        updateMinimapViewport();
        updateWidthHandlePositionForCurrentLayout();
        if (!viewerChromeEnabled) {
          completeModeToggleSettleAfterPaint();
          return;
        }
        window.requestAnimationFrame(() => {
          if (!isCurrentProbe()) {
            return;
          }
          postPerfMark("mm-mode-settle-second-raf");
          flushPendingReadingPreferences();
          ensureDetailedMinimapContentForVisiblePath();
          updateMinimapVisibility();
          updateMinimapViewport();
          updateWidthHandlePositionForCurrentLayout();
          completeModeToggleSettleAfterPaint();
        });
      };
      window.requestAnimationFrame(() => settleAfterViewportReady(0));
      return;
    }
    if (message.type === "mode-reveal-prepare") {
      prepareModeReveal(message.durationMs);
      return;
    }
    if (message.type === "mode-reveal-start") {
      startModeReveal(message.durationMs);
      return;
    }
    if (message.type === "document-reveal-prepare") {
      prepareDocumentReveal(message.durationMs, message.theme);
      return;
    }
    if (message.type === "document-reveal-start") {
      startDocumentReveal(message.durationMs);
      return;
    }
    if (message.type === "minimap-settle-probe") {
      postTransactionMinimapSettled(message.transactionGeneration);
      return;
    }
  }
  function isModeSettleViewportReady(message) {
    const widthReady = typeof message.viewportWidth !== "number" || !Number.isFinite(message.viewportWidth) || message.viewportWidth <= 0 || Math.abs(window.innerWidth - message.viewportWidth) <= MODE_SETTLE_VIEWPORT_TOLERANCE;
    const heightReady = typeof message.viewportHeight !== "number" || !Number.isFinite(message.viewportHeight) || message.viewportHeight <= 0 || Math.abs(window.innerHeight - message.viewportHeight) <= MODE_SETTLE_VIEWPORT_TOLERANCE;
    return widthReady && heightReady;
  }
  function readModeSettleTransactionGeneration(message) {
    if (typeof message.transactionGeneration !== "number" || !Number.isFinite(message.transactionGeneration) || message.transactionGeneration <= 0) {
      return void 0;
    }
    return message.transactionGeneration;
  }
  function applyModeSettleProbePreferences(message) {
    if (typeof message.fontSize !== "number" || typeof message.lineHeight !== "number" || typeof message.maxWidth !== "number" || message.minimapMode === void 0) {
      return;
    }
    const preferences = {
      type: "reading-preferences",
      fontSize: message.fontSize,
      lineHeight: message.lineHeight,
      maxWidth: message.maxWidth,
      minimapMode: message.minimapMode
    };
    if (message.minMaxWidth !== void 0) {
      preferences.minMaxWidth = message.minMaxWidth;
    }
    if (message.fontFamily !== void 0) {
      preferences.fontFamily = message.fontFamily;
    }
    if (message.viewerChromeEnabled !== void 0) {
      preferences.viewerChromeEnabled = message.viewerChromeEnabled;
    }
    if (message.documentScrollEnabled !== void 0) {
      preferences.documentScrollEnabled = message.documentScrollEnabled;
    }
    if (message.wheelProxyEnabled !== void 0) {
      preferences.wheelProxyEnabled = message.wheelProxyEnabled;
    }
    if (message.widthResizerVisibility !== void 0) {
      preferences.widthResizerVisibility = message.widthResizerVisibility;
    }
    if (message.skipFrameWait !== void 0) {
      preferences.skipFrameWait = message.skipFrameWait;
    }
    applyReadingPreferences(preferences);
  }
  function resetModuleGlobalsForLoadDocument() {
    ++initialRenderPipelineGeneration;
    ++processedDocumentCacheCloneGeneration;
    ++progressiveMinimapRefreshGeneration;
    cancelProcessedDocumentCacheClone();
    cancelDeferredMinimapContentRefresh(false);
    initialRenderPipelineCompleted = false;
    firstPrefsBootstrapSuppressedByLoadGeneration = null;
    postReadyEnhancementsCompleted = false;
    currentController?.cancel();
    currentController = null;
    ++layoutReadyGeneration;
    if (layoutReadyTimer !== void 0) {
      window.clearTimeout(layoutReadyTimer);
      layoutReadyTimer = void 0;
    }
    if (minimapContentRefreshTimer !== void 0) {
      window.clearTimeout(minimapContentRefreshTimer);
      minimapContentRefreshTimer = void 0;
    }
    if (cachedGeometryRefreshTimer !== void 0) {
      window.clearTimeout(cachedGeometryRefreshTimer);
      cachedGeometryRefreshTimer = void 0;
    }
    if (mermaidCacheResumeTimer !== void 0) {
      window.clearTimeout(mermaidCacheResumeTimer);
      mermaidCacheResumeTimer = void 0;
    }
    postLayoutReadyWorkQueue = [];
    if (themeMermaidRefreshTimer !== void 0) {
      window.clearTimeout(themeMermaidRefreshTimer);
      themeMermaidRefreshTimer = void 0;
    }
    ++themeMermaidRefreshGeneration;
    disconnectMermaidLazyObserver();
    mermaidLazyRenderQueue = Promise.resolve();
    ++mermaidRenderGeneration;
    minimapDocumentHeight = 0;
    lastPostedMinimapState = { hasPosted: false, visible: false, reservedWidth: 0 };
    minimapSourceReady = false;
    currentMinimapLayout = null;
    hasInitialLayoutSettled = false;
    resetVirtualizedDocumentWindow();
    findBarController?.close();
    if (activeHeadingObserver) {
      activeHeadingObserver.disconnect();
      activeHeadingObserver = null;
    }
    lastPostedActiveHeadingId = null;
    sourceLineAnchors = [];
    invalidateTopVisibleBlockIndexCache();
    previewSourceLineFrameRequested = false;
    suppressPreviewSourceLineEmit = false;
    lastPostedPreviewSourceLine = null;
    pendingSourceLineTarget = null;
  }
  function ensureChromeNodes(useCachedDocumentState = false, options = {}) {
    ensureMinimap();
    ensureWidthHandle();
    ensureDropOverlay();
    if (options.allowVirtualization === false) {
      resetVirtualizedDocumentWindow(false);
    } else {
      initializeVirtualizedDocumentWindow();
    }
    refreshTopVisibleBlockIndexCache();
    updateWidthHandlePositionForCurrentLayout();
    if (options.refreshMinimap === false) {
      updateMinimapVisibility(true);
      updateMinimapViewport({ skipVisibilityUpdate: true });
    } else if (!useCachedDocumentState || !restoreCachedMinimapContent()) {
      refreshMinimapContent("A");
    }
    if (useCachedDocumentState) {
      postCachedHeadings();
    } else {
      extractAndPostHeadings();
    }
  }
  async function runLoadDocumentInitialRenderPipeline(hasMermaid, skipFrameWait, renderId, hasHljs, suppressFirstPrefsBootstrap = false) {
    const pipelineGeneration = ++initialRenderPipelineGeneration;
    firstPrefsBootstrapSuppressedByLoadGeneration = suppressFirstPrefsBootstrap ? pipelineGeneration : null;
    await runInitialRenderPipeline({
      getCurrentTheme,
      applyTheme,
      initMermaidWithTheme,
      renderMath: renderMath2,
      renderMermaid,
      renderCodeBlocks,
      deferPostReadyWork: deferPostReadyEnhancements,
      scheduleLayoutReady: () => {
        initialRenderPipelineCompleted = true;
        scheduleLayoutReady(skipFrameWait === true);
        postHostMessage({
          type: "document-ready",
          mathCount: document.querySelectorAll("[data-tex]").length
        });
      },
      hasMermaid,
      postPerfMark,
      notifyPostReadyEnhancementsComplete: () => {
        postPostReadyEnhancementsComplete(renderId, hasMermaid, hasHljs);
      },
      isCurrent: () => pipelineGeneration === initialRenderPipelineGeneration
    });
  }
  function buildLoadDocumentDeps() {
    return {
      // PE r2 item G — accept the per-document `hasMermaid` so the pipeline
      // skips mermaid init+render for docs without mermaid blocks. Undefined
      // passes through to the pipeline's `!== false` default, preserving the
      // pre-G behavior for any caller that doesn't carry the flag.
      runInitialRenderPipeline: (hasMermaid, skipFrameWait, renderId, hasHljs, ownsCompleteFreshBody) => runLoadDocumentInitialRenderPipeline(
        hasMermaid,
        skipFrameWait,
        renderId,
        hasHljs,
        ownsCompleteFreshBody === true
      ),
      cancelCurrentMathController: () => {
        currentController?.cancel();
      },
      resetModuleGlobals: resetModuleGlobalsForLoadDocument,
      scrollWindowToTop: () => {
        suppressPreviewSourceLinePost();
        window.scrollTo({ left: 0, top: 0, behavior: "instant" });
      },
      // Mirror selected renderer-side perf marks into the host's
      // [renderer-perf] stream. Only `mm-load-document` is bridged from this
      // path per round-2 plan item C; other marks are bridged at their own
      // emission sites in renderer.ts so the bridging is colocated with the
      // semantic anchor rather than centralized here.
      emitMark: (name, detail) => {
        emitMark(name, detail);
        if (name === "mm-load-document" || name === "mm-load-document-cache-hit" || name === "mm-load-document-cache-miss") {
          postPerfMark(name, detail ?? void 0);
        }
      },
      ensureChromeNodes,
      applyTheme,
      debugLog: postDebugLog,
      preserveCurrentDocumentCache: preserveCurrentProcessedDocument,
      getCachedDocumentFragment: getCachedProcessedDocumentFragment,
      setCurrentDocumentCacheKey: setCurrentProcessedDocumentCacheKey,
      restoreCachedScrollPosition,
      completeCachedDocumentLoad: (renderId, hasMermaid, hasHljs, skipFrameWait) => {
        if (hasUnrenderedDocumentMath()) {
          void runLoadDocumentInitialRenderPipeline(hasMermaid, skipFrameWait, renderId, hasHljs, false);
          return;
        }
        initialRenderPipelineCompleted = true;
        hasInitialLayoutSettled = true;
        postReadyEnhancementsCompleted = true;
        postHostMessage({
          type: "document-ready",
          mathCount: document.querySelectorAll("[data-tex]").length
        });
        postCachedLayoutReady();
        postPostReadyEnhancementsComplete(renderId, hasMermaid, hasHljs);
        scheduleCachedMermaidResume(hasMermaid);
      },
      notifyDocumentCacheMiss: (renderId, cacheKey) => {
        const message = {
          type: "document-cache-miss"
        };
        if (renderId !== void 0) {
          message.renderId = renderId;
        }
        if (cacheKey !== void 0) {
          message.cacheKey = cacheKey;
        }
        postHostMessage(message);
      }
    };
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
        // actual relative path inside a temp-folder file URI — useless to
        // the host because the host needs to resolve against the ORIGINAL
        // markdown source directory. Send the raw attribute value too so
        // the host can pick the right one for resolution.
        type: "link-clicked",
        href: target.dataset.mmHref ?? target.getAttribute("href") ?? target.href,
        button: event.button,
        ctrlKey: event.ctrlKey,
        shiftKey: event.shiftKey,
        altKey: event.altKey,
        metaKey: event.metaKey
      });
    });
  }
  function wireTaskCheckboxes() {
    document.addEventListener("change", (event) => {
      const target = event.target;
      if (!(target instanceof HTMLInputElement) || !target.classList.contains("mm-task-checkbox")) {
        return;
      }
      const lineAttr = target.getAttribute("data-task-line");
      if (lineAttr === null) {
        return;
      }
      const line = Number.parseInt(lineAttr, 10);
      if (Number.isNaN(line)) {
        return;
      }
      const key = target.getAttribute("data-task-key");
      postHostMessage({ type: "task-toggle", line, checked: target.checked, key });
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
      if (event.ctrlKey || event.metaKey) {
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
  var resetHostShortcutsForModeSwitch;
  function wireHostShortcuts() {
    let editModeShortcutDown = false;
    let editModeShortcutResetTimer;
    const resetEditModeShortcut = () => {
      editModeShortcutDown = false;
      window.clearTimeout(editModeShortcutResetTimer);
    };
    resetHostShortcutsForModeSwitch = resetEditModeShortcut;
    const keepEditModeShortcutHeld = () => {
      editModeShortcutDown = true;
      window.clearTimeout(editModeShortcutResetTimer);
      editModeShortcutResetTimer = window.setTimeout(resetEditModeShortcut, 1e3);
    };
    const hostShortcuts = /* @__PURE__ */ new Set([
      "ctrl+1",
      "ctrl+2",
      "ctrl+3",
      "ctrl+4",
      "ctrl+5",
      "ctrl+6",
      "ctrl+7",
      "ctrl+8",
      "ctrl+9",
      "ctrl+e",
      "ctrl+o",
      "ctrl+s",
      "ctrl+shift+s",
      "ctrl+n",
      "ctrl+r",
      "ctrl+t",
      "f5",
      "escape"
    ]);
    window.addEventListener(
      "keydown",
      (event) => {
        const key = event.key.toLowerCase();
        const combo = (event.ctrlKey || event.metaKey ? "ctrl+" : "") + (event.shiftKey ? "shift+" : "") + (event.altKey ? "alt+" : "") + key;
        if (!hostShortcuts.has(combo)) {
          return;
        }
        event.preventDefault();
        event.stopPropagation();
        if (combo === "ctrl+e") {
          if (editModeShortcutDown || event.repeat) {
            keepEditModeShortcutHeld();
            return;
          }
          keepEditModeShortcutHeld();
        }
        postHostMessage({ type: "host-shortcut", combo });
      },
      { capture: true }
    );
    window.addEventListener(
      "keyup",
      (event) => {
        const key = event.key.toLowerCase();
        if (key === "e" || !event.ctrlKey && !event.metaKey) {
          resetEditModeShortcut();
        }
      },
      { capture: true }
    );
    window.addEventListener("blur", resetEditModeShortcut);
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
  function wireFindBar() {
    virtualizedFindProvider = virtualizationEnabled ? createVirtualizedFindProvider({
      postHostMessage,
      readContext: readVirtualizedFindContext
    }) : null;
    findBarController = createFindBar(virtualizedFindProvider ?? void 0);
    window.addEventListener(
      "keydown",
      (event) => {
        const isFindCombo = (event.ctrlKey || event.metaKey) && !event.shiftKey && !event.altKey && event.key.toLowerCase() === "f";
        if (isFindCombo) {
          event.preventDefault();
          event.stopPropagation();
          findBarController?.toggle();
          return;
        }
        if (event.key === "Escape" && findBarController?.isOpen === true) {
          event.preventDefault();
          event.stopPropagation();
          findBarController.close();
        }
      },
      { capture: true }
    );
  }
  var contextMenuPending = false;
  function wireSaveAsPageChromeSuppress() {
    document.addEventListener("contextmenu", () => {
      contextMenuPending = true;
    });
    window.addEventListener("blur", () => {
      if (contextMenuPending) {
        document.body.classList.add("mm-saving");
      }
    });
    window.addEventListener("focus", () => {
      contextMenuPending = false;
      document.body.classList.remove("mm-saving");
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
    postPerfMark("mm-doc-loaded");
    requestAnimationFrame(() => {
      emitMark("mm-doc-painted");
      postPerfMark("mm-doc-painted");
    });
    installLongTaskObserver();
    applyViewerChromeState();
    applyDocumentScrollState();
    wireLinks();
    wireTaskCheckboxes();
    wireViewerInteraction();
    wireWheelProxy();
    wireFileDrop();
    wireFindBar();
    wireHostShortcuts();
    wireSaveAsPageChromeSuppress();
    postHostMessage({
      type: "document-ready",
      mathCount: document.querySelectorAll("[data-tex]").length
    });
    postScroll();
    const documentElement = document.querySelector(".mm-document");
    if (documentElement) {
      const resizeObserver = new ResizeObserver(() => {
        if (widthHandleDragging) {
          return;
        }
        queueMinimapRefreshAfterLayoutSettles();
        scheduleResizeReactions();
        invalidateSourceLineAnchors();
        scheduleVirtualizedMeasuredHeightAdoption();
        window.requestAnimationFrame(postScroll);
      });
      resizeObserver.observe(documentElement);
      resizeObserver.observe(document.body);
    }
    document.fonts?.ready.then(() => {
      queueMinimapRefreshAfterLayoutSettles();
      invalidateSourceLineAnchors();
      scheduleVirtualizedMeasuredHeightAdoption();
    }).catch(() => void 0);
  });
  var queuePostScroll = createScrollCoalescer({
    postScroll: () => {
      updateVirtualizedWindowForScroll();
      postScroll();
      queueMinimapViewportUpdate();
    },
    schedule: (cb) => {
      window.requestAnimationFrame(cb);
    }
  });
  document.addEventListener("scroll", () => {
    const programmaticNavigationScroll = isVirtualizedProgrammaticNavigationInProgress();
    if (!programmaticNavigationScroll) {
      clearVirtualizedProgrammaticNavigationPostSettleTarget();
    }
    queuePostScroll();
    queuePreviewSourceLinePost();
  }, { passive: true });
  hostWindow.chrome?.webview?.addEventListener?.("message", (event) => handleHostMessage(event.data));
  window.addEventListener("message", (event) => handleHostMessage(event.data));
  window.addEventListener("resize", () => {
    scheduleResizeReactions();
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
  window.__mmRendererLoad = (msg) => handleHostMessage(msg);
})();
