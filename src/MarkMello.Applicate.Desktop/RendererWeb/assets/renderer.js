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
  var MERMAID_PROXY_LIFECYCLE_OWNER = /* @__PURE__ */ Symbol("mm-mermaid-proxy-lifecycle-owner");
  function isMermaidNodeNearViewport(node, viewportHeight, marginPx) {
    const rect = node.getBoundingClientRect();
    return rect.bottom >= -marginPx && rect.top <= viewportHeight + marginPx;
  }
  async function renderMermaidNode(node, generation, getCurrentGeneration, mermaid, perDiagramTimeoutMs, options) {
    const codeEl = node.querySelector("code[data-mm-mermaid]");
    if (!codeEl) return;
    const source = codeEl.textContent ?? "";
    const lifecycleClaim = options?.manageVirtualizedProxyLifecycle === true ? claimProxyLifecycle(node) : null;
    let timeoutHandle;
    try {
      const id = `mm-mermaid-${generation}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
      const timeoutPromise = new Promise((_, reject) => {
        timeoutHandle = setTimeout(() => reject(new Error("mermaid render timeout")), perDiagramTimeoutMs);
      });
      const { svg } = await Promise.race([mermaid.render(id, source), timeoutPromise]);
      if (getCurrentGeneration() !== generation || lifecycleClaim !== null && (!node.isConnected || !ownsLifecycleClaim(node, lifecycleClaim))) {
        if (lifecycleClaim !== null) {
          resetOwnedProxyLifecycle(node, lifecycleClaim);
        }
        return;
      }
      let svgHost = node.nextElementSibling;
      if (!svgHost || !svgHost.classList.contains("mm-mermaid-svg")) {
        svgHost = node.ownerDocument.createElement("div");
        svgHost.className = "mm-mermaid-svg";
        node.after(svgHost);
      }
      if (lifecycleClaim !== null) {
        lifecycleClaim.proxy = svgHost;
        svgHost[MERMAID_PROXY_LIFECYCLE_OWNER] = lifecycleClaim;
        removeExtraAdjacentProxies(svgHost);
      }
      svgHost.innerHTML = svg;
      node.classList.add("is-rendered");
      if (lifecycleClaim !== null) {
        lifecycleClaim.state = "ready";
      }
    } catch {
      if (lifecycleClaim !== null) {
        resetOwnedProxyLifecycle(node, lifecycleClaim);
        return;
      }
      if (getCurrentGeneration() !== generation) return;
      node.classList.remove("is-rendered");
      const sibling = node.nextElementSibling;
      if (sibling?.classList.contains("mm-mermaid-svg")) sibling.remove();
    } finally {
      if (timeoutHandle !== void 0) clearTimeout(timeoutHandle);
    }
  }
  function readReadyMermaidProxy(source) {
    if (!source.matches("pre.mm-mermaid.is-rendered") || !source.isConnected) {
      return null;
    }
    const claim = source[MERMAID_PROXY_LIFECYCLE_OWNER];
    const proxy = source.nextElementSibling;
    if (claim === void 0 || claim.state !== "ready" || claim.source !== source || !(proxy instanceof HTMLElement) || claim.proxy !== proxy || proxy[MERMAID_PROXY_LIFECYCLE_OWNER] !== claim || !proxy.isConnected || proxy.parentElement !== source.parentElement || !proxy.classList.contains("mm-mermaid-svg") || proxy.hasAttribute("data-mm-block-index") || proxy.nextElementSibling?.classList.contains("mm-mermaid-svg")) {
      return null;
    }
    const sourceHeight = source.offsetHeight;
    const proxyHeight = proxy.offsetHeight;
    const sourceStyle = readComputedStyle(source);
    const proxyStyle = readComputedStyle(proxy);
    const sourceIsHiddenOrZeroBox = Number.isFinite(sourceHeight) && (sourceStyle?.display === "none" || sourceHeight <= 0);
    const proxyIsVisible = proxyStyle?.display !== "none" && proxyStyle?.visibility !== "hidden" && proxyStyle?.visibility !== "collapse";
    if (!sourceIsHiddenOrZeroBox || !Number.isFinite(proxyHeight) || proxyHeight <= 0 || !proxyIsVisible) {
      return null;
    }
    return proxy;
  }
  function reclaimClonedMermaidProxyLifecycles(root) {
    const sources = root.querySelectorAll("pre.mm-mermaid.is-rendered");
    for (const source of sources) {
      if (readReadyMermaidProxy(source) !== null) {
        continue;
      }
      const proxy = source.nextElementSibling;
      const hasValidAdjacency = proxy instanceof HTMLElement && proxy.parentElement === source.parentElement && proxy.classList.contains("mm-mermaid-svg") && !proxy.hasAttribute("data-mm-block-index") && !proxy.nextElementSibling?.classList.contains("mm-mermaid-svg");
      if (hasValidAdjacency) {
        const claim = {
          proxy,
          source,
          state: "ready"
        };
        source[MERMAID_PROXY_LIFECYCLE_OWNER] = claim;
        proxy[MERMAID_PROXY_LIFECYCLE_OWNER] = claim;
        continue;
      }
      source.classList.remove("is-rendered");
      let sibling = source.nextElementSibling;
      while (sibling instanceof HTMLElement && sibling.classList.contains("mm-mermaid-svg")) {
        const nextSibling = sibling.nextElementSibling;
        sibling.remove();
        sibling = nextSibling;
      }
    }
  }
  function claimProxyLifecycle(node) {
    const previousClaim = node[MERMAID_PROXY_LIFECYCLE_OWNER];
    if (previousClaim !== void 0) {
      previousClaim.state = "superseded";
    }
    const claim = {
      proxy: null,
      source: node,
      state: "pending"
    };
    node[MERMAID_PROXY_LIFECYCLE_OWNER] = claim;
    let sibling = node.nextElementSibling;
    while (sibling instanceof HTMLElement && sibling.classList.contains("mm-mermaid-svg")) {
      sibling[MERMAID_PROXY_LIFECYCLE_OWNER] = claim;
      sibling = sibling.nextElementSibling;
    }
    return claim;
  }
  function ownsLifecycleClaim(node, claim) {
    return claim.state === "pending" && claim.source === node && node[MERMAID_PROXY_LIFECYCLE_OWNER] === claim;
  }
  function resetOwnedProxyLifecycle(node, claim) {
    const ownedNode = node;
    if (ownedNode[MERMAID_PROXY_LIFECYCLE_OWNER] !== claim) {
      return;
    }
    claim.state = "superseded";
    delete ownedNode[MERMAID_PROXY_LIFECYCLE_OWNER];
    node.classList.remove("is-rendered");
    let sibling = node.nextElementSibling;
    while (sibling instanceof HTMLElement && sibling.classList.contains("mm-mermaid-svg")) {
      const nextSibling = sibling.nextElementSibling;
      if (sibling[MERMAID_PROXY_LIFECYCLE_OWNER] === claim) {
        sibling.remove();
      }
      sibling = nextSibling;
    }
  }
  function readComputedStyle(element) {
    return element.ownerDocument.defaultView?.getComputedStyle(element) ?? null;
  }
  function removeExtraAdjacentProxies(proxy) {
    let sibling = proxy.nextElementSibling;
    while (sibling instanceof HTMLElement && sibling.classList.contains("mm-mermaid-svg")) {
      const nextSibling = sibling.nextElementSibling;
      sibling.remove();
      sibling = nextSibling;
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
      const finish2 = () => {
        if (resolved) return;
        resolved = true;
        if (timeout !== void 0) {
          window.clearTimeout(timeout);
        }
        resolve();
      };
      if (typeof window.requestAnimationFrame === "function") {
        timeout = window.setTimeout(finish2, MATH_RENDER_FRAME_FALLBACK_MS);
        window.requestAnimationFrame(finish2);
        return;
      }
      timeout = window.setTimeout(finish2, 0);
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

  // RendererWeb/src/findVisibleText.ts
  var FIND_VISIBLE_SKIP_TAGS = /* @__PURE__ */ new Set(["SCRIPT", "STYLE", "NOSCRIPT", "ASIDE"]);
  var FIND_VISIBLE_SKIP_CLASSES = /* @__PURE__ */ new Set([
    "mm-minimap",
    "mm-minimap-viewport",
    "mm-width-handle",
    "mm-drop-overlay",
    "katex-mathml",
    "mm-find-bar"
  ]);
  var FIND_VISIBLE_SKIP_SELECTOR = "pre.mm-mermaid.is-rendered";
  function shouldSkipVisibleTextSubtree(element) {
    if (FIND_VISIBLE_SKIP_TAGS.has(element.tagName)) {
      return true;
    }
    for (const className of FIND_VISIBLE_SKIP_CLASSES) {
      if (element.classList.contains(className)) {
        return true;
      }
    }
    return element.matches?.(FIND_VISIBLE_SKIP_SELECTOR) === true;
  }
  function walkVisibleTextNodes(root) {
    const out = [];
    const visit = (node) => {
      if (node.nodeType === Node.ELEMENT_NODE) {
        const element = node;
        if (shouldSkipVisibleTextSubtree(element)) {
          return;
        }
      } else if (node.nodeType === Node.TEXT_NODE) {
        out.push(node);
        return;
      }
      for (const child of Array.from(node.childNodes)) {
        visit(child);
      }
    };
    visit(root);
    return out;
  }
  async function walkVisibleTextNodesSliced(root, options, visitTextNode) {
    const now = options.now ?? (() => performance.now());
    const stack = [root];
    let sliceActive = false;
    let sliceStart = 0;
    const beginOrContinueWork = async () => {
      if (!sliceActive) {
        if (options.shouldCancel("before-work")) {
          return true;
        }
        sliceStart = now();
        sliceActive = true;
        return false;
      }
      if (now() - sliceStart < options.sliceBudgetMs) {
        return false;
      }
      await options.yieldControl();
      if (options.shouldCancel("after-yield")) {
        return true;
      }
      if (options.shouldCancel("before-work")) {
        return true;
      }
      sliceStart = now();
      return false;
    };
    while (stack.length > 0) {
      if (await beginOrContinueWork()) {
        return "cancelled";
      }
      const node = stack.pop();
      if (node.nodeType === Node.ELEMENT_NODE) {
        const element = node;
        if (shouldSkipVisibleTextSubtree(element)) {
          continue;
        }
      } else if (node.nodeType === Node.TEXT_NODE) {
        visitTextNode(node);
        continue;
      }
      for (let index = node.childNodes.length - 1; index >= 0; index--) {
        const child = node.childNodes.item(index);
        if (child !== null) {
          stack.push(child);
        }
      }
    }
    return "complete";
  }

  // RendererWeb/src/findBar.ts
  var FIND_BAR_CLASS = "mm-find-bar";
  var FIND_INPUT_CLASS = "mm-find-input";
  var FIND_COUNT_CLASS = "mm-find-count";
  var FIND_BTN_CLASS = "mm-find-btn";
  var FIND_DEBOUNCE_MS = 150;
  var HIGHLIGHT_ALL = "mm-find-all";
  var HIGHLIGHT_CURRENT = "mm-find-current";
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
    for (const node of walkVisibleTextNodes(root)) {
      for (const [start, end] of findCaseInsensitiveMatchOffsets(node.nodeValue ?? "", needle)) {
        const range = document.createRange();
        range.setStart(node, start);
        range.setEnd(node, end);
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

  // RendererWeb/src/renderedFindProjection.ts
  var RENDERED_FIND_TEXT_DOMAIN = "rendered-dom-v1";
  var RENDERED_FIND_SCHEMA_VERSION = 1;
  var RENDERED_FIND_MAX_MESSAGE_UTF8_BYTES = 262144;
  var RENDERED_FIND_MAX_MESSAGE_CODE_UNITS = 262144;
  var RENDERED_FIND_MAX_CHUNK_PARTS = 4096;
  var RENDERED_FIND_MAX_TEXT_PART_CODE_UNITS = 65536;
  var RENDERED_FIND_MAX_PROJECTION_CODE_UNITS = 16777216;
  var RENDERED_FIND_MAX_SEMANTIC_SEGMENTS = 524288;
  var RENDERED_FIND_MAX_TRANSFER_PARTS = 1048576;
  var RENDERED_FIND_PRODUCER_SLICE_BUDGET_MS = 7;
  async function publishRenderedFindProjection(options) {
    if (options.shouldCancel()) {
      return "cancelled";
    }
    const readiness = await options.readiness;
    if (readiness !== "not-needed" && readiness !== "ready" && readiness !== "ready-with-failures" || options.shouldCancel()) {
      return "cancelled";
    }
    const projectionOptions = {
      shouldCancel: () => options.shouldCancel(),
      yieldControl: options.yieldControl
    };
    if (options.now !== void 0) {
      projectionOptions.now = options.now;
    }
    const projection = await createRenderedFindProjection(options.root(), projectionOptions);
    if (projection.status === "cancelled" || options.shouldCancel()) {
      return "cancelled";
    }
    const transferOptions = {
      emit: (message) => {
        options.emit(message);
      },
      projectionRevision: options.projectionRevision,
      renderId: options.renderId,
      shouldCancel: () => options.shouldCancel(),
      yieldControl: options.yieldControl
    };
    if (options.now !== void 0) {
      transferOptions.now = options.now;
    }
    return emitRenderedFindProjectionTransfer(projection.segments, transferOptions);
  }
  async function createRenderedFindProjection(root, options = {}) {
    const segments = [];
    const blockLengths = /* @__PURE__ */ new Map();
    const walkOptions = {
      shouldCancel: options.shouldCancel ?? (() => false),
      sliceBudgetMs: RENDERED_FIND_PRODUCER_SLICE_BUDGET_MS,
      yieldControl: options.yieldControl ?? (async () => {
      })
    };
    const result = await walkVisibleTextNodesSliced(root, options.now === void 0 ? walkOptions : { ...walkOptions, now: options.now }, (node) => {
      const text = node.nodeValue ?? "";
      if (text.length === 0) {
        return;
      }
      const block = node.parentElement?.closest("[data-mm-block-index]");
      if (block === null || block === void 0) {
        return;
      }
      const blockIndexText = block.dataset.mmBlockIndex;
      if (blockIndexText === void 0) {
        return;
      }
      const blockIndex = Number.parseInt(blockIndexText, 10);
      if (!Number.isSafeInteger(blockIndex) || blockIndex < 0) {
        return;
      }
      const blockLocalStart = blockLengths.get(blockIndex) ?? 0;
      segments.push({
        blockIndex,
        blockLocalStart,
        segmentCodeUnitLength: text.length,
        segmentOrdinal: segments.length,
        text
      });
      blockLengths.set(blockIndex, blockLocalStart + text.length);
    });
    if (result === "cancelled") {
      return { segments: [], status: "cancelled" };
    }
    return { segments, status: "complete" };
  }
  function createRenderedFindDomainBeginMessage(input) {
    return {
      renderId: input.renderId,
      schemaVersion: RENDERED_FIND_SCHEMA_VERSION,
      textDomain: RENDERED_FIND_TEXT_DOMAIN,
      type: "find-domain-begin"
    };
  }
  function createRenderedFindTextIndexChunkMessage(input) {
    return {
      chunkIndex: input.chunkIndex,
      parts: input.parts,
      projectionRevision: input.projectionRevision,
      renderId: input.renderId,
      schemaVersion: RENDERED_FIND_SCHEMA_VERSION,
      textDomain: RENDERED_FIND_TEXT_DOMAIN,
      transferId: transferId(input.renderId, input.projectionRevision),
      type: "find-text-index-chunk"
    };
  }
  function measureRenderedFindMessage(message) {
    const serialized = JSON.stringify(message);
    return {
      codeUnits: serialized.length,
      utf8Bytes: new TextEncoder().encode(serialized).length
    };
  }
  function assertRenderedFindMessageWithinLimits(message) {
    const measurement = measureRenderedFindMessage(message);
    if (measurement.codeUnits > RENDERED_FIND_MAX_MESSAGE_CODE_UNITS) {
      throw new Error(`rendered find message exceeds UTF-16 limit: ${measurement.codeUnits}`);
    }
    if (measurement.utf8Bytes > RENDERED_FIND_MAX_MESSAGE_UTF8_BYTES) {
      throw new Error(`rendered find message exceeds UTF-8 limit: ${measurement.utf8Bytes}`);
    }
  }
  async function emitRenderedFindProjectionTransfer(segments, options) {
    const now = options.now ?? (() => performance.now());
    if (segments.length > RENDERED_FIND_MAX_SEMANTIC_SEGMENTS) {
      throw new Error(`rendered find projection exceeds semantic segment limit: ${segments.length}`);
    }
    const plan = await buildTransferPlan(segments, options, now);
    if (plan.status === "cancelled") {
      return "cancelled";
    }
    const start = createStartMessage({
      chunkCount: plan.chunks.length,
      partCount: plan.partCount,
      projectionRevision: options.projectionRevision,
      renderId: options.renderId,
      semanticSegmentCount: segments.length,
      totalCodeUnits: plan.totalCodeUnits
    });
    const complete = createCompleteMessage({
      chunkCount: plan.chunks.length,
      partCount: plan.partCount,
      projectionRevision: options.projectionRevision,
      renderId: options.renderId,
      semanticSegmentCount: segments.length,
      totalCodeUnits: plan.totalCodeUnits
    });
    const messages = [start, ...plan.chunks, complete];
    for (const message of messages) {
      if (options.shouldCancel("before-slice")) {
        return "cancelled";
      }
      const sliceStart = now();
      if (options.shouldCancel("before-post")) {
        return "cancelled";
      }
      assertRenderedFindMessageWithinLimits(message);
      options.emit(message);
      await options.yieldControl();
      if (options.shouldCancel("after-yield")) {
        return "cancelled";
      }
    }
    return "complete";
  }
  async function buildTransferPlan(segments, options, now) {
    const chunks = [];
    let pending = [];
    let partCount = 0;
    let totalCodeUnits = 0;
    let sliceActive = false;
    let sliceStart = 0;
    const beginOrContinuePackingSlice = async () => {
      if (!sliceActive) {
        if (options.shouldCancel("before-slice")) {
          return true;
        }
        sliceStart = now();
        sliceActive = true;
        return false;
      }
      if (now() - sliceStart < RENDERED_FIND_PRODUCER_SLICE_BUDGET_MS) {
        return false;
      }
      await options.yieldControl();
      if (options.shouldCancel("after-yield")) {
        return true;
      }
      if (options.shouldCancel("before-slice")) {
        return true;
      }
      sliceStart = now();
      return false;
    };
    const flush = () => {
      chunks.push(createRenderedFindTextIndexChunkMessage({
        chunkIndex: chunks.length,
        parts: pending,
        projectionRevision: options.projectionRevision,
        renderId: options.renderId
      }));
      pending = [];
    };
    const appendPart = (part) => {
      const candidate = [...pending, part];
      if (candidate.length > RENDERED_FIND_MAX_CHUNK_PARTS) {
        flush();
        pending.push(part);
        return;
      }
      const candidateMessage = createRenderedFindTextIndexChunkMessage({
        chunkIndex: chunks.length,
        parts: candidate,
        projectionRevision: options.projectionRevision,
        renderId: options.renderId
      });
      const measurement = measureRenderedFindMessage(candidateMessage);
      if (pending.length > 0 && (measurement.codeUnits > RENDERED_FIND_MAX_MESSAGE_CODE_UNITS || measurement.utf8Bytes > RENDERED_FIND_MAX_MESSAGE_UTF8_BYTES)) {
        flush();
        pending.push(part);
        return;
      }
      if (pending.length === 0 && (measurement.codeUnits > RENDERED_FIND_MAX_MESSAGE_CODE_UNITS || measurement.utf8Bytes > RENDERED_FIND_MAX_MESSAGE_UTF8_BYTES)) {
        throw new Error("rendered find text part cannot fit within one message");
      }
      pending = candidate;
    };
    for (const segment of segments) {
      if (await beginOrContinuePackingSlice()) {
        return { status: "cancelled" };
      }
      const text = segment.text;
      const segmentLength = segment.segmentCodeUnitLength;
      totalCodeUnits += segmentLength;
      if (totalCodeUnits > RENDERED_FIND_MAX_PROJECTION_CODE_UNITS) {
        throw new Error(`rendered find projection exceeds total UTF-16 limit: ${totalCodeUnits}`);
      }
      for (let offset = 0; offset < text.length; offset += RENDERED_FIND_MAX_TEXT_PART_CODE_UNITS) {
        if (await beginOrContinuePackingSlice()) {
          return { status: "cancelled" };
        }
        appendPart({
          blockIndex: segment.blockIndex,
          blockLocalStart: segment.blockLocalStart,
          partOffset: offset,
          segmentCodeUnitLength: segmentLength,
          segmentOrdinal: segment.segmentOrdinal,
          text: text.slice(offset, offset + RENDERED_FIND_MAX_TEXT_PART_CODE_UNITS)
        });
        partCount++;
        if (partCount > RENDERED_FIND_MAX_TRANSFER_PARTS) {
          throw new Error(`rendered find projection exceeds transfer part limit: ${partCount}`);
        }
      }
    }
    if (pending.length > 0) {
      flush();
    }
    return {
      chunks,
      partCount,
      status: "ready",
      totalCodeUnits
    };
  }
  function createStartMessage(input) {
    return {
      chunkCount: input.chunkCount,
      partCount: input.partCount,
      projectionRevision: input.projectionRevision,
      renderId: input.renderId,
      schemaVersion: RENDERED_FIND_SCHEMA_VERSION,
      semanticSegmentCount: input.semanticSegmentCount,
      textDomain: RENDERED_FIND_TEXT_DOMAIN,
      totalCodeUnits: input.totalCodeUnits,
      transferId: transferId(input.renderId, input.projectionRevision),
      type: "find-text-index-start"
    };
  }
  function createCompleteMessage(input) {
    return {
      chunkCount: input.chunkCount,
      partCount: input.partCount,
      projectionRevision: input.projectionRevision,
      renderId: input.renderId,
      schemaVersion: RENDERED_FIND_SCHEMA_VERSION,
      semanticSegmentCount: input.semanticSegmentCount,
      textDomain: RENDERED_FIND_TEXT_DOMAIN,
      totalCodeUnits: input.totalCodeUnits,
      transferId: transferId(input.renderId, input.projectionRevision),
      type: "find-text-index-complete"
    };
  }
  function transferId(renderId, projectionRevision) {
    return `${renderId}:${projectionRevision}`;
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
    getPendingRenderedContentEntryIndexes() {
      const pendingIndexes = [];
      for (let index = 0; index < this.sections.length; index++) {
        const entry = this.sections[index];
        if (readRenderedContentHtmlStats(entry.html).pendingMathCount > 0) {
          pendingIndexes.push(index);
        }
      }
      return pendingIndexes;
    }
    adoptRenderedSectionHtml(updates) {
      let updatedCount = 0;
      for (const update of updates) {
        const entry = this.sections[update.sectionIndex];
        if (entry === void 0 || typeof update.html !== "string") {
          continue;
        }
        if (entry.html === update.html) {
          continue;
        }
        entry.html = update.html;
        updatedCount++;
      }
      return {
        pendingMathCount: this.countPendingRenderedContentMath(),
        updatedCount
      };
    }
    commitRenderedFormulaFragment(sectionIndex, renderedHtml, result) {
      const entry = this.sections[sectionIndex];
      if (entry === void 0 || typeof renderedHtml !== "string" || !isRenderedFormulaFragmentResultConsistent(renderedHtml, result)) {
        return {
          changed: false,
          pendingMathCount: this.countPendingRenderedContentMath()
        };
      }
      const changed = entry.html !== renderedHtml;
      if (changed) {
        entry.html = renderedHtml;
      }
      return {
        changed,
        pendingMathCount: this.countPendingRenderedContentMath()
      };
    }
    getRenderedContentState() {
      const summary = this.readRenderedContentSummary();
      if (summary.totalMathCount === 0) {
        return "not-needed";
      }
      if (summary.pendingMathCount > 0) {
        return "unprepared";
      }
      return summary.failedMathCount > 0 ? "ready-with-failures" : "ready";
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
        const occupiedNonContentHeight = update.occupiedNonContentHeight;
        if (typeof occupiedNonContentHeight === "number" && Number.isFinite(occupiedNonContentHeight)) {
          entry.occupiedNonContentHeight = occupiedNonContentHeight;
        }
        if (update.measuredHeightPlaceholder === true) {
          continue;
        }
        if (!Number.isFinite(update.measuredHeight) || update.measuredHeight < 0) {
          continue;
        }
        const previous = effectiveHeight(entry);
        entry.measuredHeight = update.measuredHeight;
        if (update.geometryOwner === void 0) {
          delete entry.geometryOwner;
        } else {
          entry.geometryOwner = update.geometryOwner;
        }
        delete entry.measuredHeightPlaceholder;
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
        if (entry.measuredHeightPlaceholder === true) {
          continue;
        }
        if (entry.geometryOwner === "mermaid-proxy") {
          continue;
        }
        const sample = {
          ...entry.intrinsicSize,
          blockIndex: entry.blockIndex,
          measuredHeight: entry.measuredHeight
        };
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
      if (entry === void 0) {
        return this.leadingOffset;
      }
      const intraOffset = Math.max(
        0,
        Math.min(anchor.intraOffset, Math.max(0, effectiveHeight(entry) - 0.5))
      );
      return entry.cumulativeTop + intraOffset;
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
    countPendingRenderedContentMath() {
      return this.readRenderedContentSummary().pendingMathCount;
    }
    readRenderedContentSummary() {
      const summary = {
        failedMathCount: 0,
        pendingMathCount: 0,
        totalMathCount: 0
      };
      for (const entry of this.sections) {
        const stats = readRenderedContentHtmlStats(entry.html);
        summary.failedMathCount += stats.failedMathCount;
        summary.pendingMathCount += stats.pendingMathCount;
        summary.totalMathCount += stats.totalMathCount;
      }
      return summary;
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
      if (measurement.geometryOwner !== void 0) {
        update.geometryOwner = measurement.geometryOwner;
      }
      if (measurement.measuredHeightPlaceholder) {
        update.measuredHeightPlaceholder = true;
      }
      if (measurement.occupiedNonContentHeight !== void 0) {
        update.occupiedNonContentHeight = measurement.occupiedNonContentHeight;
      }
      return update;
    });
  }
  function readRenderedContentHtmlStats(html) {
    if (typeof html !== "string" || !html.includes("data-tex")) {
      return EMPTY_RENDERED_CONTENT_HTML_STATS;
    }
    if (typeof document === "undefined") {
      return {
        failedMathCount: 0,
        pendingMathCount: 1,
        totalMathCount: 1
      };
    }
    const template = document.createElement("template");
    template.innerHTML = html;
    const mathNodes = Array.from(template.content.querySelectorAll("[data-tex]"));
    let failedMathCount = 0;
    let pendingMathCount = 0;
    for (const node of mathNodes) {
      const state2 = node.dataset["mmMathRendered"];
      if (state2 === "failed") {
        failedMathCount++;
      } else if (state2 !== "true") {
        pendingMathCount++;
      }
    }
    return {
      failedMathCount,
      pendingMathCount,
      totalMathCount: mathNodes.length
    };
  }
  var EMPTY_RENDERED_CONTENT_HTML_STATS = {
    failedMathCount: 0,
    pendingMathCount: 0,
    totalMathCount: 0
  };
  function isRenderedFormulaFragmentResultConsistent(renderedHtml, result) {
    const stats = readRenderedContentHtmlStats(renderedHtml);
    if (stats.pendingMathCount > 0) {
      return false;
    }
    return result.status === "ready-with-failures" ? stats.failedMathCount > 0 : stats.failedMathCount === 0;
  }
  function readLiveBlockOffsetMeasuredHeights(blocks) {
    const geometry = readVisibleBlockGeometry(blocks);
    return geometry.map((item, index) => {
      const nextItem = geometry[index + 1];
      const nextTop = hasInvalidRenderedMermaidBetween(blocks, item.sourceIndex, nextItem?.sourceIndex) ? readNextSiblingDocumentTop(item.boxElement) : nextItem?.top ?? readNextSiblingDocumentTop(item.boxElement);
      const measuredHeight = nextTop !== void 0 && nextTop > item.top ? nextTop - item.top : item.height;
      const update = {
        blockIndex: readBlockIndex(item.semanticElement, item.sourceIndex),
        measuredHeight: Math.max(0, measuredHeight)
      };
      if (item.geometryOwner !== void 0) {
        update.geometryOwner = item.geometryOwner;
      }
      if (isContentVisibilityPlaceholderMeasurement(item)) {
        update.measuredHeightPlaceholder = true;
      }
      const occupiedNonContentHeight = readOccupiedNonContentHeight(item, update.measuredHeight);
      if (occupiedNonContentHeight !== null) {
        update.occupiedNonContentHeight = occupiedNonContentHeight;
      } else if (item.geometryOwner !== "mermaid-proxy") {
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
      if (measuredEntry.measuredHeight === void 0) {
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
      const semanticElement = measurement.semanticElement;
      const intrinsicSize = readSectionIntrinsicCalibrationTarget(semanticElement, metrics);
      const entry = {
        blockIndex: measurement.blockIndex,
        cumulativeTop: measurement.top,
        estimatedHeight: options.intrinsicSizeCalibrator?.estimateTargetHeight(intrinsicSize) ?? intrinsicSize.defaultHeight,
        hasMermaid: hasMermaidContent(semanticElement),
        headingLevel: readHeadingLevel2(semanticElement),
        html: semanticElement.outerHTML,
        intrinsicSize,
        kind: normalizeSectionKind(semanticElement.dataset["mmBlockKind"]),
        measuredHeight: measured ? measurement.measuredHeight : void 0,
        sectionIndex
      };
      if (measurement.geometryOwner !== void 0) {
        entry.geometryOwner = measurement.geometryOwner;
      }
      if (measurement.occupiedNonContentHeight !== void 0) {
        entry.occupiedNonContentHeight = measurement.occupiedNonContentHeight;
      }
      if (measured && measurement.measuredHeightPlaceholder) {
        entry.measuredHeight = void 0;
        entry.measuredHeightPlaceholder = true;
      }
      return entry;
    });
  }
  function readLiveBlockMeasurements(blocks, documentScrollHeight) {
    const geometry = readVisibleBlockGeometry(blocks);
    const safeDocumentScrollHeight = Number.isFinite(documentScrollHeight) ? documentScrollHeight : 0;
    return geometry.map((item, index) => {
      const nextItem = geometry[index + 1];
      const invalidMermaidBoundary = hasInvalidRenderedMermaidBetween(
        blocks,
        item.sourceIndex,
        nextItem?.sourceIndex
      );
      const nextTop = invalidMermaidBoundary ? readNextSiblingDocumentTop(item.boxElement) : nextItem?.top;
      const measuredHeight = nextTop !== void 0 && nextTop > item.top ? Math.max(0, nextTop - item.top) : invalidMermaidBoundary ? Math.max(0, item.height) : Math.max(0, item.height, safeDocumentScrollHeight - item.top);
      const measurement = {
        ...item,
        blockIndex: readBlockIndex(item.semanticElement, item.sourceIndex),
        measuredHeight,
        measuredHeightPlaceholder: isContentVisibilityPlaceholderMeasurement(item)
      };
      const occupiedNonContentHeight = readOccupiedNonContentHeight(item, measuredHeight);
      if (occupiedNonContentHeight !== null) {
        measurement.occupiedNonContentHeight = occupiedNonContentHeight;
      } else if (item.geometryOwner !== "mermaid-proxy") {
        measurement.measuredHeightPlaceholder = true;
      }
      return measurement;
    });
  }
  function readVisibleBlockGeometry(blocks) {
    const geometry = [];
    for (let sourceIndex = 0; sourceIndex < blocks.length; sourceIndex++) {
      const semanticElement = blocks[sourceIndex];
      const mermaidProxy = readReadyMermaidProxy(semanticElement);
      if (semanticElement.matches("pre.mm-mermaid.is-rendered") && mermaidProxy === null) {
        continue;
      }
      const boxElement = mermaidProxy ?? semanticElement;
      const top = elementDocumentTop(boxElement);
      const height = boxElement.offsetHeight;
      if (!Number.isFinite(top) || !Number.isFinite(height) || height <= 0) {
        continue;
      }
      const item = {
        boxElement,
        height,
        semanticElement,
        sourceIndex,
        top
      };
      if (mermaidProxy !== null) {
        item.geometryOwner = "mermaid-proxy";
      }
      geometry.push(item);
    }
    return geometry;
  }
  function hasInvalidRenderedMermaidBetween(blocks, sourceIndex, nextSourceIndex) {
    const end = nextSourceIndex ?? blocks.length;
    for (let index = sourceIndex + 1; index < end; index++) {
      const candidate = blocks[index];
      if (candidate?.matches("pre.mm-mermaid.is-rendered") && readReadyMermaidProxy(candidate) === null) {
        return true;
      }
    }
    return false;
  }
  var CONTENT_VISIBILITY_PLACEHOLDER_TOLERANCE_PX = 1;
  function isContentVisibilityPlaceholderMeasurement(item) {
    if (readCssProperty(item.boxElement, "content-visibility").trim() !== "auto") {
      return false;
    }
    const viewport = readDocumentViewport(item.boxElement);
    if (viewport === null) {
      return false;
    }
    const bottom = item.top + item.height;
    return bottom <= viewport.top + CONTENT_VISIBILITY_PLACEHOLDER_TOLERANCE_PX || item.top >= viewport.bottom - CONTENT_VISIBILITY_PLACEHOLDER_TOLERANCE_PX;
  }
  function readOccupiedNonContentHeight(item, occupiedHeight) {
    if (item.geometryOwner === "mermaid-proxy") {
      return null;
    }
    if (!Number.isFinite(occupiedHeight)) {
      return null;
    }
    const contentBoxHeight = readContentBoxContributionHeight(item);
    if (contentBoxHeight === null) {
      return null;
    }
    const occupiedNonContentHeight = occupiedHeight - contentBoxHeight;
    return Number.isFinite(occupiedNonContentHeight) ? occupiedNonContentHeight : null;
  }
  function readContentBoxContributionHeight(item) {
    if (isContentVisibilityPlaceholderMeasurement(item)) {
      return readContainIntrinsicBlockSizePx(item.boxElement);
    }
    const blockAxisNonContent = readBlockAxisPaddingBorderHeightPx(item.boxElement);
    if (blockAxisNonContent === null) {
      return null;
    }
    const contentBoxHeight = item.height - blockAxisNonContent;
    return Number.isFinite(contentBoxHeight) && contentBoxHeight >= 0 ? contentBoxHeight : null;
  }
  function readBlockAxisPaddingBorderHeightPx(element) {
    const styles = element.ownerDocument.defaultView?.getComputedStyle(element);
    if (!styles) {
      return 0;
    }
    let total = 0;
    for (const propertyName of ["padding-top", "padding-bottom", "border-top-width", "border-bottom-width"]) {
      const value = readCssPixelLength(styles.getPropertyValue(propertyName));
      if (value === null) {
        return null;
      }
      total += value;
    }
    return total;
  }
  function readCssPixelLength(raw) {
    const value = raw.trim();
    if (value === "" || value === "0") {
      return 0;
    }
    if (!value.endsWith("px")) {
      return null;
    }
    const parsed = Number.parseFloat(value);
    return Number.isFinite(parsed) ? parsed : null;
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
  function readBlockIndex(element, fallback) {
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
      const sourceLine = parseNonNegativeInt(element.dataset["mmSourceLine"]);
      if (sourceLine !== null) {
        const rawEndLine = parseNonNegativeInt(element.dataset["mmSourceEndLine"]);
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
  function parseNonNegativeInt(value) {
    const parsed = parseFiniteInt(value);
    return parsed !== null && parsed >= 0 ? parsed : null;
  }

  // RendererWeb/src/virtualizedDocumentWindow.ts
  var TOP_SPACER = "top";
  var BOTTOM_SPACER = "bottom";
  var SPACER_CLASS = "mm-virtual-spacer";
  var REALIZATION_FRAME_BUDGET = 120;
  var REALIZATION_QUARANTINE_CYCLES = 3;
  var REALIZATION_TRACE_IDS = {
    expired: "mm-virt-realization-expired",
    quarantined: "mm-virt-realization-quarantined"
  };
  var EMPTY_HEIGHT_UPDATE = {
    maxAbsDelta: 0,
    totalDelta: 0,
    updatedCount: 0
  };
  function createVirtualizedDocumentWindowController(deps) {
    let currentRange = null;
    let windowMountGeneration = 0;
    const renderAhead = deps.renderAhead ?? DEFAULT_RENDER_AHEAD;
    const realizationTracker = deps.realization?.enabled === true ? createRealizationTracker(deps) : null;
    const renderRange = (range) => {
      const mountGeneration = ++windowMountGeneration;
      const geometryWork = deps.beginWindowGeometryWork?.(mountGeneration) ?? null;
      const existingByBlockIndex = collectExistingSections(deps.main);
      const nodes = [];
      let insertedCount = 0;
      let repairedMermaidCount = 0;
      const repairedMermaidBlockIndexes = /* @__PURE__ */ new Set();
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
          if (appendSectionUnit(nodes, existing.source, existing.proxy, entry)) {
            repairedMermaidCount++;
            repairedMermaidBlockIndexes.add(entry.blockIndex);
          }
          continue;
        }
        const created = createSectionNode(deps.ownerWindow.document, entry);
        if (created) {
          insertedCount++;
          appendSectionUnit(nodes, created, null, entry);
        }
      }
      nodes.push(bottomSpacer);
      try {
        deps.main.replaceChildren(...nodes);
        geometryWork?.mutated();
        currentRange = { ...range };
        const mountedBlocks = collectLiveDocumentSectionElements(deps.main);
        reconcileMountedNonContentMetadata(deps.model, mountedBlocks, repairedMermaidBlockIndexes);
        realizationTracker?.syncMountedSections(mountedBlocks, mountGeneration);
        deps.onWindowMounted?.(mountGeneration);
        if (insertedCount > 0 || repairedMermaidCount > 0) {
          deps.prepareInsertedContent?.(deps.main, mountGeneration);
        }
      } finally {
        geometryWork?.end();
      }
    };
    const computeRange = (scrollTop = deps.root.scrollTop) => deps.model.computeWindowRange(scrollTop, deps.root.clientHeight, renderAhead);
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
        options.operation?.requestScrollTop(
          deps.model.scrollTopForAnchor(anchor),
          "target-window-reanchor"
        );
      }
      return true;
    };
    return {
      adoptRenderedHeights: (options = {}) => {
        const preserveSectionIndex = normalizeSectionIndex(options.preserveSectionIndex, deps.model.getSectionCount());
        const reanchor = options.reanchor !== false;
        const anchor = preserveSectionIndex === null ? deps.model.captureAnchor(deps.root.scrollTop) : null;
        const blocks = collectLiveDocumentSectionElements(deps.main);
        const liveAnchor = preserveSectionIndex === null ? captureReadingAnchor(blocks) : null;
        const updates = deps.readMeasuredHeights ? deps.readMeasuredHeights(blocks) : readLiveBlockOffsetMeasuredHeights(blocks);
        const result = deps.model.updateMeasuredHeightsByBlockIndex(
          realizationTracker?.filterRealizedUpdates(blocks, updates) ?? updates
        );
        if (result.updatedCount === 0) {
          return EMPTY_HEIGHT_UPDATE;
        }
        if (result.maxAbsDelta <= Number.EPSILON && Math.abs(result.totalDelta) <= Number.EPSILON) {
          return result;
        }
        const desiredScrollTop = preserveSectionIndex !== null ? deps.model.sectionTop(preserveSectionIndex) : scrollTopForReadingAnchor(deps.model, liveAnchor) ?? deps.model.scrollTopForAnchor(anchor);
        renderRange(computeRange(desiredScrollTop));
        if (reanchor) {
          options.operation?.requestScrollTop(desiredScrollTop, "measured-height-adoption");
        }
        return result;
      },
      dispose: () => {
        realizationTracker?.dispose();
      },
      ensureSectionRangeRendered: (start, end, options = {}) => ensureRangeRendered({ end, start }, options),
      ensureSectionRendered: (sectionIndex, options = {}) => ensureRangeRendered({ end: sectionIndex, start: sectionIndex }, options),
      getCurrentRange: () => currentRange === null ? null : { ...currentRange },
      isSectionRendered,
      recensusRealizationWatches: () => realizationTracker?.recensusRealizationWatches() ?? true,
      updateWindowForScroll: (options = {}) => {
        const nextRange = computeRange(options.desiredScrollTop ?? deps.root.scrollTop);
        if (options.force !== true && currentRange !== null && rangesEqual(currentRange, nextRange)) {
          return false;
        }
        const anchor = deps.model.captureAnchor(options.desiredScrollTop ?? deps.root.scrollTop);
        renderRange(nextRange);
        options.operation?.requestScrollTop(
          deps.model.scrollTopForAnchor(anchor),
          "scroll-window-reanchor"
        );
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
        result.set(blockIndex, {
          proxy: readReadyMermaidProxy(element),
          source: element
        });
      }
    }
    return result;
  }
  function appendSectionUnit(nodes, source, proxy, entry) {
    nodes.push(source);
    if (proxy !== null) {
      source.style.removeProperty("contain-intrinsic-size");
      nodes.push(proxy);
      return false;
    }
    if (!source.matches("pre.mm-mermaid.is-rendered")) {
      return false;
    }
    source.classList.remove("is-rendered");
    writeIntrinsicSizeStamp(source, entry);
    return true;
  }
  function captureReadingAnchor(blocks) {
    for (const block of blocks) {
      const blockIndex = readBlockIndex2(block);
      if (blockIndex === null) {
        continue;
      }
      const boxElement = readReadyMermaidProxy(block) ?? block;
      const rect = boxElement.getBoundingClientRect();
      const ownerDocument = boxElement.ownerDocument;
      const viewportHeight = ownerDocument.scrollingElement?.clientHeight || ownerDocument.defaultView?.innerHeight || 0;
      if (!Number.isFinite(rect.top) || !Number.isFinite(rect.height) || rect.height <= 0 || !Number.isFinite(rect.bottom) || rect.bottom <= 0 || Number.isFinite(viewportHeight) && viewportHeight > 0 && rect.top >= viewportHeight) {
        continue;
      }
      return {
        blockIndex,
        intraOffsetPx: Math.max(0, Math.min(-rect.top, Math.max(0, rect.height - 0.5)))
      };
    }
    return null;
  }
  function scrollTopForReadingAnchor(model, anchor) {
    if (anchor === null || !Number.isFinite(anchor.intraOffsetPx)) {
      return null;
    }
    const entry = model.getEntryByBlockIndex(anchor.blockIndex);
    if (entry === void 0) {
      return 0;
    }
    return model.scrollTopForAnchor({
      blockIndex: entry.blockIndex,
      intraOffset: anchor.intraOffsetPx,
      sectionIndex: entry.sectionIndex
    });
  }
  function readBlockIndex2(element) {
    const raw = element.dataset["mmBlockIndex"];
    if (raw === void 0 || raw.trim() === "") {
      return null;
    }
    const parsed = Number.parseInt(raw, 10);
    return Number.isFinite(parsed) ? parsed : null;
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
    if (firstElement) {
      if (firstElement.matches("pre.mm-mermaid.is-rendered")) {
        firstElement.classList.remove("is-rendered");
      }
      writeIntrinsicSizeStamp(firstElement, entry);
    }
    return firstElement ?? null;
  }
  function writeIntrinsicSizeStamp(element, entry) {
    const stamp = readIntrinsicSizeStamp(entry);
    if (stamp === null) {
      element.style.removeProperty("contain-intrinsic-size");
      return;
    }
    element.style.containIntrinsicSize = `auto ${stamp}px`;
  }
  function readIntrinsicSizeStamp(entry) {
    const occupiedNonContentHeight = entry.occupiedNonContentHeight;
    if (!Number.isFinite(occupiedNonContentHeight)) {
      return null;
    }
    const stamp = Math.max(0, effectiveHeight(entry) - occupiedNonContentHeight);
    return Number.isFinite(stamp) ? stamp : null;
  }
  function reconcileMountedNonContentMetadata(model, blocks, excludedBlockIndexes) {
    const updates = readLiveBlockOffsetMeasuredHeights(blocks);
    for (const update of updates) {
      if (excludedBlockIndexes.has(update.blockIndex)) {
        continue;
      }
      const block = blocks.find((candidate) => readBlockIndex2(candidate) === update.blockIndex);
      if (update.geometryOwner === "mermaid-proxy") {
        block?.style.removeProperty("contain-intrinsic-size");
        continue;
      }
      const occupiedNonContentHeight = update.occupiedNonContentHeight;
      if (typeof occupiedNonContentHeight !== "number" || !Number.isFinite(occupiedNonContentHeight)) {
        continue;
      }
      const entry = model.getEntryByBlockIndex(update.blockIndex);
      if (entry === void 0) {
        continue;
      }
      entry.occupiedNonContentHeight = occupiedNonContentHeight;
      if (block !== void 0) {
        writeIntrinsicSizeStamp(block, entry);
      }
    }
  }
  function createRealizationTracker(deps) {
    const watches = /* @__PURE__ */ new Map();
    let currentMountGeneration = 0;
    let disposed = false;
    const eventOptions = { capture: true };
    const documentEpoch = deps.documentEpoch;
    const isCurrentDocument = () => documentEpoch === void 0 || deps.isCurrentDocumentEpoch === void 0 || deps.isCurrentDocumentEpoch(documentEpoch);
    const handleContentVisibilityStateChange = (event) => {
      if (!isCurrentDocument()) {
        return;
      }
      const target = event.target;
      if (!(target instanceof HTMLElement)) {
        return;
      }
      const watch = watches.get(target);
      if (watch === void 0 || watch.element !== target || !deps.main.contains(target)) {
        return;
      }
      if (watch.state === "quarantined-nonconvergent") {
        return;
      }
      const stateEvent = event;
      if (stateEvent.skipped === true) {
        watch.skipped = true;
        watch.readyMeasuredHeight = null;
        watch.stableFrameCount = 0;
        watch.lastOffsetHeight = null;
        watch.lastOccupiedHeight = null;
        watch.state = watch.state === "real-ready" ? "realized-then-skipped" : "placeholder-not-intersecting";
        return;
      }
      watch.skipped = false;
      watch.frameBudget = REALIZATION_FRAME_BUDGET;
      watch.stableFrameCount = 0;
      watch.lastOffsetHeight = null;
      watch.lastOccupiedHeight = null;
      watch.readyMeasuredHeight = null;
      watch.state = "event-observed-settling";
      scheduleSample(watch);
    };
    deps.main.addEventListener("contentvisibilityautostatechange", handleContentVisibilityStateChange, eventOptions);
    const dispose = () => {
      if (disposed) {
        return;
      }
      disposed = true;
      watches.clear();
      deps.main.removeEventListener("contentvisibilityautostatechange", handleContentVisibilityStateChange, eventOptions);
    };
    const syncMountedSections = (blocks, mountGeneration) => {
      if (disposed) {
        return;
      }
      currentMountGeneration = mountGeneration;
      for (const block of blocks) {
        if (readReadyMermaidProxy(block) !== null) {
          watches.delete(block);
          continue;
        }
        if (!isContentVisibilityAutoOwner(block)) {
          watches.delete(block);
          continue;
        }
        const blockIndex = readBlockIndex2(block);
        if (blockIndex === null) {
          continue;
        }
        const existing = watches.get(block);
        if (existing !== void 0) {
          existing.blockIndex = blockIndex;
          existing.mountGeneration = currentMountGeneration;
          if (existing.state === "placeholder-not-intersecting" || existing.state === "realized-then-skipped") {
            existing.state = isStrictlyIntersecting(block) ? "intersecting-await-event" : "placeholder-not-intersecting";
          }
          continue;
        }
        watches.set(block, {
          blockIndex,
          element: block,
          frameBudget: REALIZATION_FRAME_BUDGET,
          frameRequested: false,
          lastOccupiedHeight: null,
          lastOffsetHeight: null,
          mountGeneration: currentMountGeneration,
          nonconvergentCycles: 0,
          readyMeasuredHeight: null,
          skipped: true,
          stableFrameCount: 0,
          state: isStrictlyIntersecting(block) ? "intersecting-await-event" : "placeholder-not-intersecting"
        });
      }
      for (const [element, watch] of watches) {
        if (watch.mountGeneration !== currentMountGeneration || !deps.main.contains(element)) {
          watches.delete(element);
        }
      }
    };
    const filterRealizedUpdates = (blocks, updates) => {
      const accepted = [];
      const blocksByBlockIndex = mapBlocksByBlockIndex(blocks);
      for (const update of updates) {
        const block = blocksByBlockIndex.get(update.blockIndex);
        if (block === void 0 || block === null || readBlockIndex2(block) !== update.blockIndex) {
          continue;
        }
        if (update.geometryOwner === "mermaid-proxy") {
          const proxy = readReadyMermaidProxy(block);
          if (proxy !== null && deps.main.contains(proxy)) {
            accepted.push(update);
          }
          continue;
        }
        const watch = watches.get(block);
        if (watch === void 0) {
          accepted.push(update);
          continue;
        }
        if (watch.element !== block || watch.blockIndex !== update.blockIndex || watch.mountGeneration !== currentMountGeneration || !deps.main.contains(block) || watch.state !== "real-ready" || watch.readyMeasuredHeight === null) {
          continue;
        }
        if (isStrictlyIntersecting(block)) {
          watch.readyMeasuredHeight = Math.max(0, update.measuredHeight);
        }
        const acceptedUpdate = {
          ...update,
          measuredHeight: watch.readyMeasuredHeight
        };
        delete acceptedUpdate.measuredHeightPlaceholder;
        accepted.push(acceptedUpdate);
      }
      return accepted;
    };
    function scheduleSample(watch) {
      if (disposed || watch.frameRequested || watch.state !== "event-observed-settling") {
        return;
      }
      const expectedGeneration = watch.mountGeneration;
      watch.frameRequested = true;
      deps.ownerWindow.requestAnimationFrame(() => {
        watch.frameRequested = false;
        if (disposed || !isCurrentDocument() || watch.mountGeneration !== expectedGeneration || watches.get(watch.element) !== watch || !deps.main.contains(watch.element)) {
          return;
        }
        sampleWatch(watch);
      });
    }
    function sampleWatch(watch) {
      if (watch.skipped || watch.state !== "event-observed-settling") {
        return;
      }
      const sample = readRealizationSample(watch.element);
      if (sample === null) {
        expireOrContinue(watch);
        return;
      }
      const offsetStable = watch.lastOffsetHeight === null || Math.abs(sample.offsetHeight - watch.lastOffsetHeight) <= 1;
      const occupiedStable = watch.lastOccupiedHeight === null || Math.abs(sample.occupiedHeight - watch.lastOccupiedHeight) <= 1;
      watch.stableFrameCount = offsetStable && occupiedStable ? watch.stableFrameCount + 1 : 1;
      watch.lastOffsetHeight = sample.offsetHeight;
      watch.lastOccupiedHeight = sample.occupiedHeight;
      if (watch.stableFrameCount >= 2) {
        if (Math.abs(sample.offsetHeight - sample.fallbackBorderBoxHeight) <= 1) {
          watch.state = "event-equal-fallback-noop";
          watch.readyMeasuredHeight = null;
          deps.onRealizationReady?.(watch.mountGeneration);
          return;
        }
        if (Math.abs(sample.offsetHeight - sample.fallbackBorderBoxHeight) > 1) {
          watch.state = "real-ready";
          watch.readyMeasuredHeight = Math.max(0, sample.occupiedHeight);
          deps.onRealizationReady?.(watch.mountGeneration);
          return;
        }
      }
      expireOrContinue(watch);
    }
    function expireOrContinue(watch) {
      watch.frameBudget--;
      if (watch.frameBudget <= 0) {
        watch.nonconvergentCycles++;
        watch.state = "expired-nonconvergent";
        watch.readyMeasuredHeight = null;
        deps.trace?.({
          id: REALIZATION_TRACE_IDS.expired,
          details: {
            blockIndex: watch.blockIndex,
            cycles: watch.nonconvergentCycles
          }
        });
        return;
      }
      scheduleSample(watch);
    }
    const recensusRealizationWatches = () => {
      if (disposed || !isCurrentDocument()) {
        return false;
      }
      syncMountedSections(collectLiveDocumentSectionElements(deps.main), currentMountGeneration);
      let ready = true;
      for (const watch of watches.values()) {
        const intersecting = isStrictlyIntersecting(watch.element);
        if (intersecting && watch.state === "expired-nonconvergent") {
          if (watch.nonconvergentCycles < REALIZATION_QUARANTINE_CYCLES) {
            watch.frameBudget = REALIZATION_FRAME_BUDGET;
            watch.stableFrameCount = 0;
            watch.lastOffsetHeight = null;
            watch.lastOccupiedHeight = null;
            watch.readyMeasuredHeight = null;
            watch.state = "event-observed-settling";
            scheduleSample(watch);
            ready = false;
          } else {
            watch.state = "quarantined-nonconvergent";
            deps.trace?.({
              id: REALIZATION_TRACE_IDS.quarantined,
              details: {
                blockIndex: watch.blockIndex,
                cycles: watch.nonconvergentCycles,
                mountGeneration: watch.mountGeneration
              }
            });
          }
          continue;
        }
        if (intersecting && watch.state !== "real-ready" && watch.state !== "event-equal-fallback-noop" && watch.state !== "quarantined-nonconvergent") {
          if (watch.state === "placeholder-not-intersecting" || watch.state === "realized-then-skipped") {
            watch.state = "intersecting-await-event";
          }
          ready = false;
        }
      }
      return ready;
    };
    return {
      dispose,
      filterRealizedUpdates,
      recensusRealizationWatches,
      syncMountedSections
    };
  }
  function mapBlocksByBlockIndex(blocks) {
    const blocksByBlockIndex = /* @__PURE__ */ new Map();
    for (const block of blocks) {
      const blockIndex = readBlockIndex2(block);
      if (blockIndex === null) {
        continue;
      }
      blocksByBlockIndex.set(
        blockIndex,
        blocksByBlockIndex.has(blockIndex) ? null : block
      );
    }
    return blocksByBlockIndex;
  }
  function readRealizationSample(element) {
    const offsetHeight = element.offsetHeight;
    const occupiedHeight = readOccupiedHeight(element);
    const fallbackBorderBoxHeight = readFallbackBorderBoxHeight(element);
    if (!Number.isFinite(offsetHeight) || occupiedHeight === null || !Number.isFinite(occupiedHeight) || fallbackBorderBoxHeight === null) {
      return null;
    }
    return { fallbackBorderBoxHeight, occupiedHeight, offsetHeight };
  }
  function readOccupiedHeight(element) {
    const top = elementDocumentTop(element);
    const nextTop = readNextSiblingTop(element);
    if (!Number.isFinite(top) || nextTop === null || !Number.isFinite(nextTop) || nextTop <= top) {
      return null;
    }
    return nextTop - top;
  }
  function readNextSiblingTop(element) {
    let sibling = element.nextElementSibling;
    while (sibling instanceof HTMLElement) {
      const top = elementDocumentTop(sibling);
      if (Number.isFinite(top)) {
        return top;
      }
      sibling = sibling.nextElementSibling;
    }
    return null;
  }
  function readFallbackBorderBoxHeight(element) {
    const intrinsicSize = readContainIntrinsicBlockSizePx2(element);
    const nonContent = readBlockAxisPaddingBorderHeightPx2(element);
    if (intrinsicSize === null || nonContent === null) {
      return null;
    }
    return intrinsicSize + nonContent;
  }
  function isContentVisibilityAutoOwner(element) {
    const inlineValue = element.style.getPropertyValue("content-visibility");
    if (inlineValue.trim().length > 0) {
      return inlineValue.trim() === "auto";
    }
    return element.ownerDocument.defaultView?.getComputedStyle(element).getPropertyValue("content-visibility").trim() === "auto";
  }
  function readContainIntrinsicBlockSizePx2(element) {
    const raw = element.style.getPropertyValue("contain-intrinsic-size") || element.ownerDocument.defaultView?.getComputedStyle(element).getPropertyValue("contain-intrinsic-size") || "";
    const matches = Array.from(raw.matchAll(/(-?\d+(?:\.\d+)?)px/g));
    const lastMatch = matches[matches.length - 1];
    if (!lastMatch) {
      return null;
    }
    const parsed = Number.parseFloat(lastMatch[1]);
    return Number.isFinite(parsed) && parsed >= 0 ? parsed : null;
  }
  function readBlockAxisPaddingBorderHeightPx2(element) {
    const styles = element.ownerDocument.defaultView?.getComputedStyle(element);
    if (!styles) {
      return 0;
    }
    let total = 0;
    for (const propertyName of ["padding-top", "padding-bottom", "border-top-width", "border-bottom-width"]) {
      const value = readCssPixelLength2(styles.getPropertyValue(propertyName));
      if (value === null) {
        return null;
      }
      total += value;
    }
    return total;
  }
  function readCssPixelLength2(raw) {
    const value = raw.trim();
    if (value === "" || value === "0") {
      return 0;
    }
    if (!value.endsWith("px")) {
      return null;
    }
    const parsed = Number.parseFloat(value);
    return Number.isFinite(parsed) ? parsed : null;
  }
  function isStrictlyIntersecting(element) {
    const root = element.ownerDocument.scrollingElement ?? element.ownerDocument.documentElement;
    const top = elementDocumentTop(element);
    const height = element.offsetHeight;
    const viewportTop = Number.isFinite(root.scrollTop) ? root.scrollTop : 0;
    const viewportHeight = root.clientHeight || element.ownerDocument.defaultView?.innerHeight || 0;
    if (!Number.isFinite(top) || !Number.isFinite(height) || !Number.isFinite(viewportHeight) || viewportHeight <= 0) {
      return false;
    }
    return top < viewportTop + viewportHeight && top + height > viewportTop;
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

  // RendererWeb/src/windowTargetResolver.ts
  async function renderWindowTargetThenAct(input) {
    const model = input.model;
    const controller = input.controller;
    if (!input.virtualizationEnabled) {
      return input.legacyAction();
    }
    const operation = input.operation;
    if (model === null || controller === null || operation === void 0) {
      return input.legacyAction();
    }
    const resolution = resolveWindowTarget(model, input.descriptor);
    if (resolution === null) {
      return input.legacyAction();
    }
    const originalAnchor = input.actionKind === "query" ? captureReadingAnchor(collectLiveDocumentSectionElements(input.main)) : null;
    const originalRange = input.actionKind === "query" ? controller.getCurrentRange() : null;
    let didRender = false;
    let actionResult;
    try {
      const delivered = await deliverOperationFrame(operation, () => {
        didRender = ensureResolutionRendered(controller, resolution, operation);
        if (!operation.isCurrent()) {
          return;
        }
        actionResult = input.action(readWindowTargetContext(input, resolution));
        if (input.actionKind === "query" && didRender) {
          operation.requestScrollTop(
            scrollTopForReadingAnchor(model, originalAnchor) ?? 0,
            "query-anchor-preserve"
          );
        }
      });
      if (!delivered || !operation.isCurrent()) {
        return void 0;
      }
      const result = await actionResult;
      if (!operation.isCurrent()) {
        return void 0;
      }
      return result;
    } finally {
      if (input.actionKind === "query" && didRender && operation.isCurrent()) {
        await restoreReadingAnchor({
          controller,
          model,
          operation,
          originalAnchor,
          originalRange
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
  function ensureResolutionRendered(controller, resolution, operation) {
    if (resolution.range.start === resolution.range.end && controller.isSectionRendered(resolution.range.start)) {
      return false;
    }
    const options = { operation, preserveAnchor: false };
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
        return findElementByIdWithinSection(ownerWindow, sectionElement, anchor);
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
  function findElementByIdWithinSection(ownerWindow, sectionElement, id) {
    if (sectionElement.id === id) {
      return sectionElement;
    }
    for (const element of Array.from(sectionElement.querySelectorAll("[id]"))) {
      if (element instanceof ownerWindow.HTMLElement && element.id === id) {
        return element;
      }
    }
    return null;
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
      const start = parseNonNegativeInt2(element.dataset["mmSourceLine"]);
      if (start === null) {
        continue;
      }
      const end = Math.max(start, parseNonNegativeInt2(element.dataset["mmSourceEndLine"]) ?? start);
      if (normalizedLine >= start && normalizedLine <= end) {
        return element;
      }
    }
    return null;
  }
  async function restoreReadingAnchor(input) {
    await deliverOperationFrame(input.operation, () => {
      if (input.originalRange !== null) {
        input.controller.ensureSectionRangeRendered(input.originalRange.start, input.originalRange.end, {
          force: true,
          operation: input.operation,
          preserveAnchor: false
        });
      } else if (input.originalAnchor !== null) {
        const entry = input.model.getEntryByBlockIndex(input.originalAnchor.blockIndex);
        if (entry !== void 0) {
          input.controller.ensureSectionRendered(entry.sectionIndex, {
            force: true,
            operation: input.operation,
            preserveAnchor: false
          });
        }
      }
      input.operation.requestScrollTop(
        scrollTopForReadingAnchor(input.model, input.originalAnchor) ?? 0,
        "query-anchor-restore"
      );
    });
  }
  function deliverOperationFrame(operation, work) {
    if (!operation.isCurrent()) {
      return Promise.resolve(false);
    }
    return new Promise((resolve, reject) => {
      const scheduled = operation.scheduleFrameTransaction(() => {
        if (!operation.isCurrent()) {
          resolve(false);
          return;
        }
        try {
          work();
          resolve(true);
        } catch (error) {
          reject(error);
          throw error;
        }
      });
      if (!scheduled) {
        resolve(false);
      }
    });
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
  function parseNonNegativeInt2(value) {
    if (value === void 0 || value.trim() === "") {
      return null;
    }
    const parsed = Number.parseInt(value, 10);
    return Number.isFinite(parsed) && parsed >= 0 ? parsed : null;
  }

  // RendererWeb/src/virtualizedFindProvider.ts
  var FIND_RANGE_REAIM_FRAME_LIMIT = 3;
  var FIND_SCROLL_EPSILON = 0.5;
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
        textDomain: RENDERED_FIND_TEXT_DOMAIN,
        type: "find-query"
      };
      if (context.renderId !== null) {
        request.renderId = context.renderId;
      }
      latestRequestId = request.requestId;
      deps.postHostMessage(request);
    };
    const handleFindResults = (message) => {
      if (message.stale === true || message.requestId !== latestRequestId || message.query !== currentQuery || message.textDomain !== RENDERED_FIND_TEXT_DOMAIN) {
        return;
      }
      const context = deps.readContext();
      if (context.renderId !== null && message.renderId !== void 0 && message.renderId !== null && message.renderId !== context.renderId) {
        return;
      }
      if (message.status !== "ready") {
        resetResults(currentQuery);
        return;
      }
      matches = message.matches.filter(isUsableDescriptor).filter(hasUsableRenderedOffsetWhenLive).slice().sort((left, right) => left.ordinal - right.ordinal);
      totalCount = Math.max(0, Math.floor(message.totalCount));
      currentIndex = matches.length === 0 ? -1 : 0;
      paintVisibleHighlights();
      if (currentIndex >= 0) {
        const sequence = ++navigationSequence;
        void renderMatchThenAct(matches[currentIndex], sequence);
      }
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
      const operation = context.virtualizationEnabled ? context.beginNavigationOperation() : null;
      if (!context.virtualizationEnabled || operation === null) {
        if (sequence !== navigationSequence) {
          return;
        }
        const currentRange = paintVisibleHighlights();
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
          if (sequence !== navigationSequence || !operation.isCurrent()) {
            return;
          }
          paintVisibleHighlights();
          requestElementLanding(context, operation, element ?? targetElement);
          return scheduleRangeReaim(context, operation, () => {
            if (sequence !== navigationSequence || !operation.isCurrent()) {
              return null;
            }
            return paintVisibleHighlights();
          });
        },
        actionKind: "navigate",
        controller: context.controller,
        descriptor,
        legacyAction: () => {
          if (sequence !== navigationSequence || !operation.isCurrent()) {
            return;
          }
          return new Promise((resolve) => {
            const scheduled = operation.scheduleFrameTransaction(() => {
              if (sequence !== navigationSequence || !operation.isCurrent()) {
                resolve();
                return;
              }
              paintVisibleHighlights();
              requestElementLanding(
                context,
                operation,
                findLiveTopLevelBlockElement(match.blockIndex) ?? findLiveBlockElement(match.blockIndex)
              );
              void scheduleRangeReaim(context, operation, () => {
                if (sequence !== navigationSequence || !operation.isCurrent()) {
                  return null;
                }
                return paintVisibleHighlights();
              }).then(resolve);
            });
            if (!scheduled) {
              resolve();
            }
          });
        },
        main: context.main ?? document.body,
        model: context.model,
        operation,
        ownerWindow: context.ownerWindow,
        root: context.root,
        virtualizationEnabled: context.virtualizationEnabled
      });
      await pendingRender;
      if (!operation.isCurrent()) {
        return;
      }
      if (sequence === navigationSequence) {
        updateStatus();
      }
      context.completeNavigationOperation(operation);
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
  function hasUsableRenderedOffsetWhenLive(match) {
    const block = findLiveBlockElement(match.blockIndex);
    return block === null || rangeFromBlockLocalOffset(block, match.blockLocalOffset, match.length) !== null;
  }
  function resolveLiveRangeForMatch(match) {
    const block = findLiveBlockElement(match.blockIndex);
    if (block === null) {
      return null;
    }
    return rangeFromBlockLocalOffset(block, match.blockLocalOffset, match.length);
  }
  function findLiveBlockElement(blockIndex) {
    return document.querySelector(`body > main.mm-document [data-mm-block-index="${blockIndex}"]`);
  }
  function findLiveTopLevelBlockElement(blockIndex) {
    return findLiveBlockElement(blockIndex)?.closest("main.mm-document > *") ?? null;
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
  function visibleTextNodes(root) {
    return walkVisibleTextNodes(root);
  }
  function requestRangeLanding(context, operation, range) {
    if (range === null || !operation.isCurrent()) {
      return false;
    }
    const rect = range.getBoundingClientRect();
    if (rect.height <= 0 && rect.width <= 0) {
      return false;
    }
    return requestLandingForRect(context, operation, rect);
  }
  function requestElementLanding(context, operation, element) {
    if (element === null || !operation.isCurrent()) {
      return false;
    }
    return requestLandingForRect(context, operation, element.getBoundingClientRect());
  }
  function requestLandingForRect(context, operation, rect) {
    const target = context.root.scrollTop + rect.top - Math.max(0, (context.root.clientHeight - Math.max(0, rect.height)) / 2);
    const scrollTop = Math.max(0, target);
    if (Math.abs(scrollTop - context.root.scrollTop) <= FIND_SCROLL_EPSILON || !operation.isCurrent()) {
      return false;
    }
    operation.requestScrollTop(scrollTop, "find-navigation");
    return true;
  }
  function scheduleRangeReaim(context, operation, readRange) {
    return new Promise((resolve) => {
      let attempts = 0;
      const scheduleNext = () => {
        if (!operation.isCurrent() || attempts >= FIND_RANGE_REAIM_FRAME_LIMIT) {
          resolve();
          return;
        }
        const scheduled = operation.scheduleFrameTransaction(() => {
          if (!operation.isCurrent()) {
            resolve();
            return;
          }
          attempts++;
          const range = readRange();
          const requested = requestRangeLanding(context, operation, range);
          if ((requested || range === null) && attempts < FIND_RANGE_REAIM_FRAME_LIMIT) {
            scheduleNext();
            return;
          }
          resolve();
        });
        if (!scheduled) {
          resolve();
        }
      };
      scheduleNext();
    });
  }

  // RendererWeb/src/sourceLineSync.ts
  var SOURCE_LINE_ANCHOR_SELECTOR = "[data-mm-source-line]";
  function readSourceLineAnchors(root = document, scrollY = window.scrollY) {
    const anchors = [];
    for (const element of Array.from(root.querySelectorAll(SOURCE_LINE_ANCHOR_SELECTOR))) {
      const sourceLine = parseNonNegativeInt3(element.dataset["mmSourceLine"]);
      if (sourceLine === null) {
        continue;
      }
      const endLine = parseNonNegativeInt3(element.dataset["mmSourceEndLine"]) ?? sourceLine;
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
  function parseNonNegativeInt3(value) {
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
    return index < 0 ? null : readBlockIndex3(blocks[index]);
  }
  function readBlockIndex3(block) {
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

  // RendererWeb/src/modelRenderedContent.ts
  var DEFAULT_MODEL_RENDERED_CONTENT_TIME_BUDGET_MS = 7;
  async function prepareDocumentWindowModelRenderedContent(model, deps) {
    const shouldContinue = deps.shouldContinue ?? (() => true);
    const pendingEntryIndexes = model.getPendingRenderedContentEntryIndexes();
    let renderedMathCount = 0;
    let failedMathCount = 0;
    let attemptedSectionCount = 0;
    let committedSectionCount = 0;
    if (pendingEntryIndexes.length === 0) {
      return finish({
        attemptedSectionCount,
        cancelled: false,
        committedSectionCount,
        deps,
        failedMathCount,
        model,
        renderedMathCount,
        skippedNoKatex: false,
        status: model.getRenderedContentState(),
        type: "complete"
      });
    }
    if (!deps.katex) {
      return finish({
        attemptedSectionCount,
        cancelled: false,
        committedSectionCount,
        deps,
        failedMathCount,
        model,
        renderedMathCount,
        skippedNoKatex: true,
        status: "unavailable",
        type: "skipped-no-katex"
      });
    }
    for (const sectionIndex of pendingEntryIndexes) {
      if (!shouldContinue()) {
        return finish({
          attemptedSectionCount,
          cancelled: true,
          committedSectionCount,
          deps,
          failedMathCount,
          model,
          renderedMathCount,
          skippedNoKatex: false,
          status: "cancelled",
          type: "cancelled"
        });
      }
      const entry = model.sections[sectionIndex];
      const section = entry === void 0 ? null : readPendingRenderedSection(entry, deps.ownerDocument);
      if (section === null || section.pendingMathNodes.length === 0) {
        continue;
      }
      attemptedSectionCount++;
      let queue;
      queue = new MathRenderQueue({
        katex: deps.katex,
        now: deps.now ?? readNow,
        timeBudgetMs: deps.timeBudgetMs ?? DEFAULT_MODEL_RENDERED_CONTENT_TIME_BUDGET_MS,
        yield: async () => {
          await deps.yield();
          if (!shouldContinue()) {
            queue?.cancel();
          }
        }
      });
      const unsubscribe = queue.onTaskComplete((node) => {
        renderedMathCount++;
      });
      for (const node of section.pendingMathNodes) {
        queue.enqueue(readMathRenderTask(node), "low");
      }
      try {
        await queue.start();
      } finally {
        unsubscribe();
      }
      if (!shouldContinue() || section.allMathNodes.some((node) => !isTerminalMathState(node.dataset["mmMathRendered"]))) {
        return finish({
          attemptedSectionCount,
          cancelled: true,
          committedSectionCount,
          deps,
          failedMathCount,
          model,
          renderedMathCount,
          skippedNoKatex: false,
          status: "cancelled",
          type: "cancelled"
        });
      }
      const sectionFailedCount = section.allMathNodes.filter((node) => node.dataset["mmMathRendered"] === "failed").length;
      const renderedHtml = serializeRenderedSection(section.template);
      const status = sectionFailedCount > 0 ? "ready-with-failures" : "ready";
      const commit = model.commitRenderedFormulaFragment(sectionIndex, renderedHtml, { status });
      if (commit.changed) {
        committedSectionCount++;
        failedMathCount += sectionFailedCount;
      }
      deps.onProgress?.({
        committed: commit.changed,
        failedMathCount,
        pendingMathCount: commit.pendingMathCount,
        renderedMathCount,
        sectionIndex,
        status: model.getRenderedContentState(),
        type: "progress"
      });
    }
    return finish({
      attemptedSectionCount,
      cancelled: false,
      committedSectionCount,
      deps,
      failedMathCount,
      model,
      renderedMathCount,
      skippedNoKatex: false,
      status: model.getRenderedContentState(),
      type: "complete"
    });
  }
  function readPendingRenderedSection(entry, ownerDocument) {
    if (typeof entry.html !== "string" || !entry.html.includes("data-tex")) {
      return null;
    }
    const template = ownerDocument.createElement("template");
    template.innerHTML = entry.html;
    const allMathNodes = Array.from(template.content.querySelectorAll("[data-tex]"));
    const pendingMathNodes = allMathNodes.filter((node) => !isTerminalMathState(node.dataset["mmMathRendered"]));
    return { allMathNodes, pendingMathNodes, template };
  }
  function readMathRenderTask(node) {
    return {
      displayMode: node.classList.contains("math-display"),
      node,
      tex: node.dataset["tex"] ?? ""
    };
  }
  function serializeRenderedSection(template) {
    const firstElement = template.content.firstElementChild;
    return firstElement instanceof HTMLElement ? firstElement.outerHTML : template.innerHTML;
  }
  function finish(args) {
    const pendingMathCount = countPendingMath(args.model, args.deps.ownerDocument);
    const completed = args.status === "not-needed" || args.status === "ready" || args.status === "ready-with-failures";
    args.deps.onProgress?.({
      failedMathCount: args.failedMathCount,
      pendingMathCount,
      renderedMathCount: args.renderedMathCount,
      status: args.status,
      type: args.type
    });
    return {
      attemptedSectionCount: args.attemptedSectionCount,
      cancelled: args.cancelled,
      committedSectionCount: args.committedSectionCount,
      completed,
      failedMathCount: args.failedMathCount,
      pendingMathCount,
      renderedMathCount: args.renderedMathCount,
      skippedNoKatex: args.skippedNoKatex,
      status: args.status
    };
  }
  function countPendingMath(model, ownerDocument) {
    let pendingMathCount = 0;
    for (const entry of model.sections) {
      const section = readPendingRenderedSection(entry, ownerDocument);
      pendingMathCount += section?.pendingMathNodes.length ?? 0;
    }
    return pendingMathCount;
  }
  function readNow() {
    return typeof performance === "undefined" ? Date.now() : performance.now();
  }

  // RendererWeb/src/virtualizationFlags.ts
  function readRendererBooleanFlag(input) {
    return isTrueFlagValue(readWindowFlag(input.ownerWindow, input.globalName)) || isTrueFlagValue(input.ownerDocument.documentElement.dataset[input.dataKey]) || input.storageName !== void 0 && isTrueFlagValue(readLocalStorageFlag(input.ownerWindow, input.storageName));
  }
  function readVirtualizationFlag(ownerWindow = window, ownerDocument = document) {
    return readRendererBooleanFlag({
      dataKey: "markmelloVirtualization",
      globalName: "MARKMELLO_VIRTUALIZATION",
      ownerDocument,
      ownerWindow
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

  // RendererWeb/src/scrollOwnershipControlPlane.ts
  var GEOMETRY_SETTLED_EVENT = "mm-virt-geometry-settled";
  var SCROLL_OWNERSHIP_TRACE_IDS = {
    geometryMutated: "mm-virt-geometry-mutated",
    geometrySettled: GEOMETRY_SETTLED_EVENT,
    geometryWorkEnd: "mm-virt-geometry-work-end",
    geometryWorkStart: "mm-virt-geometry-work-start",
    frameTransactionRejected: "mm-virt-scroll-frame-transaction-rejected",
    leaseAcquired: "mm-virt-scroll-lease-acquired",
    leaseReleased: "mm-virt-scroll-lease-released",
    leaseSuperseded: "mm-virt-scroll-lease-superseded",
    observerDeliveryFailed: "mm-virt-observer-delivery-failed",
    retiredEchoQuarantined: "mm-virt-scroll-retired-echo-quarantined",
    settleTimeout: "mm-virt-geometry-settle-timeout",
    staleLease: "mm-virt-stale-callback-dropped",
    staleTicket: "mm-virt-stale-callback-dropped",
    unattributedMovement: "mm-virt-scroll-unattributed-movement",
    watchdogPaused: "mm-virt-geometry-watchdog-paused",
    watchdogResumed: "mm-virt-geometry-watchdog-resumed",
    writeCommitted: "mm-virt-scroll-write-committed",
    writeRejected: "mm-virt-scroll-write-rejected",
    writeRequest: "mm-virt-scroll-write-request"
  };
  var LEASE_BRAND = /* @__PURE__ */ Symbol("mm-virt-scroll-lease");
  var GEOMETRY_TICKET_BRAND = /* @__PURE__ */ Symbol("mm-virt-geometry-ticket");
  var DEFAULT_DELIVERED_FRAME_BUDGET = 120;
  var MAX_RETIRED_ECHOES = 4;
  var RETIRED_ECHO_DELIVERED_FRAME_TTL = 2;
  var SELF_ECHO_TOLERANCE_PX = 0.5;
  function createScrollOwnershipControlPlane(deps) {
    const deliveredFrameBudget = readDeliveredFrameBudget(deps.deliveredFrameBudget);
    let activeLease = null;
    let activeSupersessionSource = null;
    let deferredAcquisition = null;
    let disposed = false;
    let documentEpoch = 1;
    let expectedEcho = null;
    let frameSerial = 0;
    let frameTransaction = null;
    let geometryEpoch = 0;
    let lastEmittedPayload = null;
    let lastEmittedRevision = 0;
    let nextGeometryTicketId = 1;
    let operationEpoch = 0;
    let pendingWrite = null;
    let pendingTraceFailures = 0;
    let quietCandidate = null;
    let retiredEchoes = [];
    let scheduledFrame = null;
    let settleEmission = 0;
    let settleRevision = 0;
    let watchdogDeliveredFrames = 0;
    const geometryTickets = /* @__PURE__ */ new Map();
    const waiters = /* @__PURE__ */ new Set();
    const createTraceEvent = (id, details) => {
      const event = {
        documentEpoch,
        frame: frameSerial,
        geometryEpoch,
        id,
        operationEpoch
      };
      if (details !== void 0) {
        event.details = details;
      }
      return event;
    };
    const deliverTrace = (event) => {
      if (deps.trace === void 0) {
        return true;
      }
      try {
        deps.trace(event);
        return true;
      } catch {
        return false;
      }
    };
    const trace = (id, details) => {
      if (deps.trace === void 0) {
        return;
      }
      if (pendingTraceFailures > 0 && id !== SCROLL_OWNERSHIP_TRACE_IDS.observerDeliveryFailed) {
        const failures = pendingTraceFailures;
        pendingTraceFailures = 0;
        if (!deliverTrace(createTraceEvent(SCROLL_OWNERSHIP_TRACE_IDS.observerDeliveryFailed, {
          channel: "trace",
          failures
        }))) {
          pendingTraceFailures = failures + 1;
        }
      }
      if (!deliverTrace(createTraceEvent(id, details))) {
        pendingTraceFailures++;
      }
    };
    const invalidateSettleCandidate = () => {
      settleRevision++;
      quietCandidate = null;
    };
    const pruneRetiredEchoes = () => {
      retiredEchoes = retiredEchoes.filter((echo) => echo.documentEpoch === documentEpoch && echo.expiresAfterFrame >= frameSerial);
    };
    const retireEcho = (echo) => {
      if (!Number.isFinite(echo.value)) {
        return;
      }
      pruneRetiredEchoes();
      retiredEchoes.push({
        documentEpoch,
        expiresAfterFrame: frameSerial + RETIRED_ECHO_DELIVERED_FRAME_TTL,
        operationEpoch: echo.lease.operationEpoch,
        value: echo.value
      });
      if (retiredEchoes.length > MAX_RETIRED_ECHOES) {
        retiredEchoes.splice(0, retiredEchoes.length - MAX_RETIRED_ECHOES);
      }
      trace(SCROLL_OWNERSHIP_TRACE_IDS.retiredEchoQuarantined, {
        retiredOperationEpoch: echo.lease.operationEpoch,
        value: echo.value
      });
    };
    const consumeRetiredEcho = (value) => {
      if (!Number.isFinite(value)) {
        return null;
      }
      pruneRetiredEchoes();
      for (let index = retiredEchoes.length - 1; index >= 0; index--) {
        const echo = retiredEchoes[index];
        if (Math.abs(value - echo.value) <= SELF_ECHO_TOLERANCE_PX) {
          retiredEchoes.splice(index, 1);
          return echo;
        }
      }
      return null;
    };
    const holds = (lease, expectedGeometryEpoch) => {
      if (disposed || activeLease !== lease || lease.documentEpoch !== documentEpoch || lease.operationEpoch !== operationEpoch) {
        return false;
      }
      return expectedGeometryEpoch === void 0 || Number.isFinite(expectedGeometryEpoch) && expectedGeometryEpoch === geometryEpoch;
    };
    const traceWrite = (id, writer, before, after, supersessionSource, reason) => {
      const details = {
        after,
        before,
        supersessionSource,
        writer
      };
      if (reason !== void 0) {
        details["reason"] = reason;
      }
      trace(id, details);
    };
    const rejectPendingWrite = (reason) => {
      const pending = pendingWrite;
      pendingWrite = null;
      if (pending !== null) {
        pending.resolve({ reason, status: "rejected" });
        traceWrite(
          SCROLL_OWNERSHIP_TRACE_IDS.writeRejected,
          pending.writer,
          finiteOrNull(deps.root.scrollTop),
          null,
          pending.supersessionSource,
          reason
        );
      }
    };
    const cancelWaiters = (reason, predicate = () => true) => {
      for (const waiter of [...waiters]) {
        if (!predicate(waiter)) {
          continue;
        }
        waiters.delete(waiter);
        waiter.resolve({ reason, status: "canceled" });
      }
    };
    const cancelDeferred = (reason) => {
      const deferred = deferredAcquisition;
      deferredAcquisition = null;
      deferred?.resolve({ reason, status: "canceled" });
    };
    const cancelDeferredForOperation = (reason) => {
      switch (reason) {
        case "disposed":
        case "document-invalidated":
        case "non-converged":
        case "programmatic-supersession":
        case "user-supersession":
          cancelDeferred(reason);
          break;
        case "invalid-after-emission":
        case "stale-document":
          break;
      }
    };
    const clearActiveOperation = (reason, waiterReason, supersessionSource) => {
      const previous = activeLease;
      if (previous === null) {
        return null;
      }
      activeLease = null;
      activeSupersessionSource = null;
      if (pendingWrite?.lease === previous) {
        rejectPendingWrite(reason);
      }
      if (frameTransaction?.lease === previous) {
        frameTransaction = null;
      }
      if (expectedEcho?.lease === previous) {
        retireEcho(expectedEcho);
        expectedEcho = null;
      }
      cancelWaiters(waiterReason, (waiter) => waiter.operationEpoch === previous.operationEpoch);
      cancelDeferredForOperation(waiterReason);
      trace(SCROLL_OWNERSHIP_TRACE_IDS.leaseSuperseded, {
        owner: previous.owner,
        supersessionSource
      });
      invalidateSettleCandidate();
      return previous;
    };
    const createLease = (owner, supersessionSource = null) => {
      operationEpoch++;
      watchdogDeliveredFrames = 0;
      const lease = Object.freeze({
        [LEASE_BRAND]: true,
        documentEpoch,
        geometryEpoch,
        operationEpoch,
        owner
      });
      activeLease = lease;
      activeSupersessionSource = supersessionSource;
      trace(SCROLL_OWNERSHIP_TRACE_IDS.leaseAcquired, { owner });
      return lease;
    };
    const drainDeferredAcquisition = () => {
      if (disposed || activeLease !== null || deferredAcquisition === null) {
        return;
      }
      const deferred = deferredAcquisition;
      deferredAcquisition = null;
      deferred.resolve({
        lease: createLease(deferred.owner, "deferred-maintenance"),
        status: "acquired"
      });
    };
    const hasSettlementBlocker = () => {
      if (geometryTickets.size > 0 || pendingWrite !== null || frameTransaction !== null || expectedEcho !== null) {
        return true;
      }
      if (deps.prepareGeometrySettleCandidate === void 0) {
        return false;
      }
      try {
        return deps.prepareGeometrySettleCandidate() !== true;
      } catch {
        trace(SCROLL_OWNERSHIP_TRACE_IDS.observerDeliveryFailed, {
          channel: "geometry-settle-census",
          failures: 1
        });
        return true;
      }
    };
    const needsSettlementProgress = () => hasSettlementBlocker() || waiters.size > 0 || settleRevision > lastEmittedRevision;
    const ensureFrame = () => {
      if (disposed || scheduledFrame !== null || !needsSettlementProgress()) {
        return;
      }
      trace(SCROLL_OWNERSHIP_TRACE_IDS.watchdogPaused, { reason: "awaiting-delivered-frame" });
      scheduledFrame = deps.requestFrame(deliverFrame);
    };
    const emitSettled = () => {
      if (hasSettlementBlocker()) {
        quietCandidate = null;
        return false;
      }
      const candidateMatches = quietCandidate !== null && quietCandidate.documentEpoch === documentEpoch && quietCandidate.geometryEpoch === geometryEpoch && quietCandidate.revision === settleRevision;
      if (candidateMatches && quietCandidate !== null) {
        quietCandidate = { ...quietCandidate, stableFrames: quietCandidate.stableFrames + 1 };
      } else {
        quietCandidate = { documentEpoch, geometryEpoch, revision: settleRevision, stableFrames: 1 };
      }
      if (quietCandidate.stableFrames < 2) {
        return false;
      }
      const payload = { documentEpoch, geometryEpoch };
      settleEmission++;
      lastEmittedPayload = payload;
      lastEmittedRevision = settleRevision;
      quietCandidate = null;
      watchdogDeliveredFrames = 0;
      for (const waiter of [...waiters]) {
        if (waiter.documentEpoch !== documentEpoch || settleEmission <= waiter.afterEmission) {
          continue;
        }
        waiters.delete(waiter);
        waiter.resolve({ emission: settleEmission, payload, status: "settled" });
      }
      try {
        deps.emitGeometrySettled(payload);
      } catch {
        trace(SCROLL_OWNERSHIP_TRACE_IDS.observerDeliveryFailed, {
          channel: "geometry-settled-emitter",
          failures: 1
        });
      }
      trace(SCROLL_OWNERSHIP_TRACE_IDS.geometrySettled);
      return true;
    };
    const failNonConvergence = () => {
      trace(SCROLL_OWNERSHIP_TRACE_IDS.settleTimeout, {
        deliveredFrames: watchdogDeliveredFrames,
        pendingGeometryWork: geometryTickets.size
      });
      clearActiveOperation("non-converged", "non-converged", "geometry-settle-timeout");
      cancelDeferred("non-converged");
      cancelWaiters("non-converged");
      geometryTickets.clear();
      frameTransaction = null;
      expectedEcho = null;
      rejectPendingWrite("non-converged");
      quietCandidate = null;
      lastEmittedRevision = settleRevision;
      watchdogDeliveredFrames = 0;
    };
    const commitPendingWrite = (lease) => {
      const pending = pendingWrite;
      if (pending === null || pending.lease !== lease) {
        return;
      }
      pendingWrite = null;
      if (!holds(lease)) {
        pending.resolve({ reason: "stale-lease", status: "rejected" });
        traceWrite(
          SCROLL_OWNERSHIP_TRACE_IDS.writeRejected,
          pending.writer,
          finiteOrNull(deps.root.scrollTop),
          null,
          pending.supersessionSource,
          "stale-lease"
        );
        return;
      }
      const maxScrollTop = readMaxScrollTop(deps.root);
      if (maxScrollTop === null) {
        pending.resolve({ reason: "non-finite-root-range", status: "rejected" });
        traceWrite(
          SCROLL_OWNERSHIP_TRACE_IDS.writeRejected,
          pending.writer,
          finiteOrNull(deps.root.scrollTop),
          null,
          pending.supersessionSource,
          "non-finite-root-range"
        );
        return;
      }
      const value = clamp(pending.requestedTarget, 0, maxScrollTop);
      const before = deps.root.scrollTop;
      const expectation = { lease, value };
      expectedEcho = expectation;
      try {
        deps.root.scrollTop = value;
      } catch {
        expectedEcho = null;
        pending.resolve({ reason: "root-write-failed", status: "rejected" });
        traceWrite(
          SCROLL_OWNERSHIP_TRACE_IDS.writeRejected,
          pending.writer,
          finiteOrNull(before),
          null,
          pending.supersessionSource,
          "root-write-failed"
        );
        clearActiveOperation("root-write-failed", "programmatic-supersession", "root-write-failed");
        return;
      }
      const actual = deps.root.scrollTop;
      if (!Number.isFinite(actual)) {
        if (expectedEcho === expectation) {
          expectedEcho = null;
        }
        pending.resolve({ reason: "root-write-failed", status: "rejected" });
        traceWrite(
          SCROLL_OWNERSHIP_TRACE_IDS.writeRejected,
          pending.writer,
          finiteOrNull(before),
          null,
          pending.supersessionSource,
          "non-finite-root-result"
        );
        clearActiveOperation("root-write-failed", "programmatic-supersession", "root-write-failed");
        return;
      }
      if (expectedEcho === expectation) {
        expectedEcho = Number.isFinite(before) && Math.abs(actual - before) <= SELF_ECHO_TOLERANCE_PX ? null : { lease, value: actual };
      }
      pending.resolve({ status: "committed", value: actual });
      traceWrite(
        SCROLL_OWNERSHIP_TRACE_IDS.writeCommitted,
        pending.writer,
        finiteOrNull(before),
        actual,
        pending.supersessionSource
      );
    };
    function deliverFrame(_timestamp) {
      scheduledFrame = null;
      if (disposed) {
        return;
      }
      frameSerial++;
      pruneRetiredEchoes();
      trace(SCROLL_OWNERSHIP_TRACE_IDS.watchdogResumed, { reason: "frame-delivered" });
      const transaction = frameTransaction;
      frameTransaction = null;
      if (transaction !== null && holds(transaction.lease)) {
        try {
          transaction.work();
        } catch {
          trace(SCROLL_OWNERSHIP_TRACE_IDS.frameTransactionRejected, { reason: "frame-work-failed" });
          clearActiveOperation(
            "programmatic-supersession",
            "programmatic-supersession",
            "frame-work-failed"
          );
        }
        if (holds(transaction.lease)) {
          commitPendingWrite(transaction.lease);
        }
      }
      const emitted = emitSettled();
      if (!emitted && needsSettlementProgress()) {
        watchdogDeliveredFrames++;
        if (watchdogDeliveredFrames >= deliveredFrameBudget) {
          failNonConvergence();
        }
      }
      ensureFrame();
    }
    const acquire = (owner, policy) => {
      if (disposed) {
        return {
          ready: Promise.resolve({ reason: "disposed", status: "canceled" }),
          status: "deferred"
        };
      }
      if (activeLease === null) {
        return { lease: createLease(owner), status: "acquired" };
      }
      if (policy === "defer") {
        cancelDeferred("coalesced");
        let resolve;
        const ready = new Promise((completed) => {
          resolve = completed;
        });
        deferredAcquisition = { owner, resolve };
        return { ready, status: "deferred" };
      }
      const asUser = policy === "supersede-as-user";
      clearActiveOperation(
        asUser ? "user-supersession" : "programmatic-supersession",
        asUser ? "user-supersession" : "programmatic-supersession",
        owner
      );
      cancelDeferred(asUser ? "user-supersession" : "programmatic-supersession");
      return { lease: createLease(owner, owner), status: "acquired" };
    };
    const joinMaintenance = (owner) => {
      if (disposed) {
        return null;
      }
      if (activeLease !== null) {
        return { lease: activeLease, ownsLease: false };
      }
      return { lease: createLease(owner), ownsLease: true };
    };
    const release = (lease) => {
      if (!holds(lease)) {
        trace(SCROLL_OWNERSHIP_TRACE_IDS.staleLease, {
          capturedOperationEpoch: lease.operationEpoch,
          reason: "stale-release"
        });
        return false;
      }
      activeLease = null;
      activeSupersessionSource = null;
      if (pendingWrite?.lease === lease) {
        rejectPendingWrite("released");
      }
      if (frameTransaction?.lease === lease) {
        frameTransaction = null;
      }
      if (expectedEcho?.lease === lease) {
        retireEcho(expectedEcho);
        expectedEcho = null;
      }
      trace(SCROLL_OWNERSHIP_TRACE_IDS.leaseReleased, { owner: lease.owner });
      drainDeferredAcquisition();
      return true;
    };
    const write = (lease, request) => {
      const receiptBase = {
        afterEmission: settleEmission,
        documentEpoch: lease.documentEpoch,
        operationEpoch: lease.operationEpoch
      };
      const rejected = (reason) => {
        traceWrite(
          SCROLL_OWNERSHIP_TRACE_IDS.writeRejected,
          request.writer,
          finiteOrNull(deps.root.scrollTop),
          null,
          activeSupersessionSource,
          reason
        );
        return {
          ...receiptBase,
          result: Promise.resolve({ reason, status: "rejected" })
        };
      };
      if (disposed) {
        return rejected("disposed");
      }
      if (!holds(lease)) {
        return rejected("stale-lease");
      }
      if (!Number.isFinite(request.target)) {
        return rejected("non-finite-target");
      }
      const maxScrollTop = readMaxScrollTop(deps.root);
      if (maxScrollTop === null) {
        return rejected("non-finite-root-range");
      }
      if (pendingWrite !== null) {
        rejectPendingWrite("coalesced");
      }
      if (expectedEcho?.lease === lease) {
        retireEcho(expectedEcho);
        expectedEcho = null;
      }
      let resolve;
      const result = new Promise((completed) => {
        resolve = completed;
      });
      pendingWrite = {
        requestedTarget: request.target,
        lease,
        resolve,
        supersessionSource: activeSupersessionSource,
        writer: request.writer
      };
      invalidateSettleCandidate();
      traceWrite(
        SCROLL_OWNERSHIP_TRACE_IDS.writeRequest,
        request.writer,
        finiteOrNull(deps.root.scrollTop),
        pendingWrite.requestedTarget,
        pendingWrite.supersessionSource
      );
      return { ...receiptBase, result };
    };
    const scheduleFrameTransaction = (lease, work) => {
      if (!holds(lease)) {
        trace(SCROLL_OWNERSHIP_TRACE_IDS.staleLease, {
          capturedOperationEpoch: lease.operationEpoch,
          reason: "stale-frame-transaction"
        });
        return false;
      }
      if (frameTransaction !== null) {
        trace(SCROLL_OWNERSHIP_TRACE_IDS.writeRejected, { reason: "frame-transaction-already-scheduled" });
        return false;
      }
      frameTransaction = { lease, work };
      invalidateSettleCandidate();
      ensureFrame();
      return true;
    };
    const supersedeByUser = (source) => {
      if (disposed) {
        return;
      }
      clearActiveOperation("user-supersession", "user-supersession", source);
      cancelDeferred("user-supersession");
      cancelWaiters("user-supersession");
      operationEpoch++;
      watchdogDeliveredFrames = 0;
      invalidateSettleCandidate();
    };
    const classifyNativeScroll = (value, source = "native-scroll") => {
      const expected = expectedEcho;
      if (expected !== null && Number.isFinite(value) && Math.abs(value - expected.value) <= SELF_ECHO_TOLERANCE_PX) {
        expectedEcho = null;
        ensureFrame();
        return { expected: expected.value, kind: "self-echo", value };
      }
      const retired = consumeRetiredEcho(value);
      if (retired !== null) {
        trace(SCROLL_OWNERSHIP_TRACE_IDS.staleLease, {
          reason: "retired-self-echo",
          retiredOperationEpoch: retired.operationEpoch,
          value
        });
        return { expected: retired.value, kind: "stale-self-echo", value };
      }
      if (expected !== null) {
        trace(SCROLL_OWNERSHIP_TRACE_IDS.unattributedMovement, {
          expected: expected.value,
          value: Number.isFinite(value) ? value : 0
        });
        clearActiveOperation(
          "programmatic-supersession",
          "programmatic-supersession",
          "unattributed-external-movement"
        );
        cancelDeferred("programmatic-supersession");
        operationEpoch++;
        return { expected: expected.value, kind: "unattributed-failure", value };
      }
      if (!Number.isFinite(value)) {
        trace(SCROLL_OWNERSHIP_TRACE_IDS.unattributedMovement, { expected: null, value: 0 });
        clearActiveOperation(
          "programmatic-supersession",
          "programmatic-supersession",
          "non-finite-native-scroll"
        );
        cancelDeferred("programmatic-supersession");
        operationEpoch++;
        return { expected: null, kind: "unattributed-failure", value };
      }
      supersedeByUser(source);
      return { kind: "user-supersession", value };
    };
    const beginGeometryWork = (source, capturedDocumentEpoch = documentEpoch, capturedMountGeneration) => {
      if (disposed || !isCurrentDocumentEpoch(capturedDocumentEpoch)) {
        trace(SCROLL_OWNERSHIP_TRACE_IDS.staleTicket, {
          capturedDocumentEpoch,
          reason: "stale-geometry-start"
        });
        return null;
      }
      const ticket = Object.freeze({
        [GEOMETRY_TICKET_BRAND]: true,
        documentEpoch: capturedDocumentEpoch,
        id: nextGeometryTicketId++,
        mountGeneration: Number.isSafeInteger(capturedMountGeneration) ? capturedMountGeneration : null,
        source
      });
      geometryTickets.set(ticket.id, ticket);
      invalidateSettleCandidate();
      trace(SCROLL_OWNERSHIP_TRACE_IDS.geometryWorkStart, {
        mountGeneration: ticket.mountGeneration,
        source,
        ticket: ticket.id
      });
      ensureFrame();
      return ticket;
    };
    const readCurrentTicket = (ticket) => {
      const current = geometryTickets.get(ticket.id);
      if (disposed || current !== ticket || ticket.documentEpoch !== documentEpoch || ticket[GEOMETRY_TICKET_BRAND] !== true) {
        trace(SCROLL_OWNERSHIP_TRACE_IDS.staleTicket, {
          capturedDocumentEpoch: ticket.documentEpoch,
          reason: "stale-geometry-ticket",
          ticket: ticket.id
        });
        return null;
      }
      return current;
    };
    const geometryMutated = (ticket) => {
      const current = readCurrentTicket(ticket);
      if (current === null) {
        return false;
      }
      geometryEpoch++;
      invalidateSettleCandidate();
      trace(SCROLL_OWNERSHIP_TRACE_IDS.geometryMutated, {
        mountGeneration: current.mountGeneration,
        source: current.source,
        ticket: current.id
      });
      ensureFrame();
      return true;
    };
    const endGeometryWork = (ticket) => {
      const current = readCurrentTicket(ticket);
      if (current === null) {
        return false;
      }
      geometryTickets.delete(current.id);
      trace(SCROLL_OWNERSHIP_TRACE_IDS.geometryWorkEnd, {
        mountGeneration: current.mountGeneration,
        source: current.source,
        ticket: current.id
      });
      ensureFrame();
      return true;
    };
    const waitForGeometrySettled = (capturedDocumentEpoch, afterEmission = 0) => {
      if (disposed) {
        return Promise.resolve({ reason: "disposed", status: "canceled" });
      }
      if (!isCurrentDocumentEpoch(capturedDocumentEpoch)) {
        return Promise.resolve({ reason: "stale-document", status: "canceled" });
      }
      if (!Number.isSafeInteger(afterEmission) || afterEmission < 0) {
        return Promise.resolve({ reason: "invalid-after-emission", status: "canceled" });
      }
      if (lastEmittedPayload !== null && lastEmittedPayload.documentEpoch === documentEpoch && lastEmittedRevision === settleRevision && settleEmission > afterEmission && !hasSettlementBlocker()) {
        return Promise.resolve({
          emission: settleEmission,
          payload: lastEmittedPayload,
          status: "settled"
        });
      }
      if (settleRevision === lastEmittedRevision) {
        invalidateSettleCandidate();
      }
      let resolve;
      const result = new Promise((completed) => {
        resolve = completed;
      });
      waiters.add({
        afterEmission,
        documentEpoch: capturedDocumentEpoch,
        operationEpoch: activeLease?.operationEpoch ?? operationEpoch,
        resolve
      });
      ensureFrame();
      return result;
    };
    const invalidateDocument = () => {
      if (disposed) {
        return;
      }
      if (scheduledFrame !== null) {
        deps.cancelFrame(scheduledFrame);
        scheduledFrame = null;
      }
      clearActiveOperation(
        "document-invalidated",
        "document-invalidated",
        "document-invalidated"
      );
      cancelDeferred("document-invalidated");
      cancelWaiters("document-invalidated");
      rejectPendingWrite("document-invalidated");
      geometryTickets.clear();
      frameTransaction = null;
      expectedEcho = null;
      retiredEchoes = [];
      documentEpoch++;
      geometryEpoch = 0;
      quietCandidate = null;
      settleRevision++;
      lastEmittedRevision = settleRevision;
      lastEmittedPayload = null;
      watchdogDeliveredFrames = 0;
    };
    const dispose = () => {
      if (disposed) {
        return;
      }
      if (scheduledFrame !== null) {
        deps.cancelFrame(scheduledFrame);
        scheduledFrame = null;
      }
      clearActiveOperation("disposed", "disposed", "disposed");
      cancelDeferred("disposed");
      cancelWaiters("disposed");
      rejectPendingWrite("disposed");
      geometryTickets.clear();
      frameTransaction = null;
      expectedEcho = null;
      retiredEchoes = [];
      quietCandidate = null;
      disposed = true;
    };
    const isCurrentDocumentEpoch = (epoch) => !disposed && Number.isSafeInteger(epoch) && epoch === documentEpoch;
    return {
      acquire,
      beginGeometryWork,
      captureDocumentEpoch: () => documentEpoch,
      captureGeometryEpoch: () => geometryEpoch,
      classifyNativeScroll,
      dispose,
      endGeometryWork,
      geometryMutated,
      holds,
      invalidateDocument,
      isCurrentDocumentEpoch,
      joinMaintenance,
      release,
      scheduleFrameTransaction,
      supersedeByUser,
      waitForGeometrySettled,
      write
    };
  }
  function readDeliveredFrameBudget(input) {
    if (input === void 0) {
      return DEFAULT_DELIVERED_FRAME_BUDGET;
    }
    if (!Number.isSafeInteger(input) || input <= 0) {
      throw new RangeError("deliveredFrameBudget must be a positive safe integer");
    }
    return input;
  }
  function readMaxScrollTop(root) {
    if (!Number.isFinite(root.scrollHeight) || !Number.isFinite(root.clientHeight) || root.scrollHeight < 0 || root.clientHeight < 0) {
      return null;
    }
    const maxScrollTop = Math.max(0, root.scrollHeight - root.clientHeight);
    return Number.isFinite(maxScrollTop) ? maxScrollTop : null;
  }
  function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
  }
  function finiteOrNull(value) {
    return Number.isFinite(value) ? value : null;
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
  var VIRTUALIZED_NAVIGATION_CORRECTION_TOLERANCE_PX = 2;
  var VIRTUALIZED_NAVIGATION_CORRECTION_MAX_PASSES = 3;
  var VIRTUALIZED_NAVIGATION_CORRECTION_MIN_SHRINK_PX = 0.5;
  var minimapMode = "off";
  var hasReceivedHostPreferences = false;
  var hasInitialLayoutSettled = false;
  var minimapViewportFrameRequested = false;
  var minimapRefreshTimer;
  var minimapContentRefreshTimer;
  var minimapDeferredContentRefreshHandle = null;
  var progressiveDeferredEnhancementHandle = null;
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
  var scrollOwnershipControlPlane = virtualizationEnabled ? createScrollOwnershipControlPlane({
    cancelFrame: (handle) => window.cancelAnimationFrame(handle),
    emitGeometrySettled: (payload) => {
      document.dispatchEvent(new CustomEvent(GEOMETRY_SETTLED_EVENT, { detail: payload }));
    },
    prepareGeometrySettleCandidate: () => virtualizedDocumentWindowController?.recensusRealizationWatches() ?? true,
    requestFrame: (callback) => window.requestAnimationFrame(callback),
    root: getDocumentScrollRoot(),
    trace: (event) => postPerfMark(event.id, {
      ...event.details,
      documentEpoch: event.documentEpoch,
      frame: event.frame,
      geometryEpoch: event.geometryEpoch,
      operationEpoch: event.operationEpoch
    })
  }) : null;
  if (scrollOwnershipControlPlane !== null) {
    const virtualizationRoot = getDocumentScrollRoot();
    if (virtualizationRoot instanceof HTMLElement) {
      virtualizationRoot.dataset.mmVirtualizationActive = "true";
    } else {
      virtualizationRoot.setAttribute("data-mm-virtualization-active", "true");
    }
    window.addEventListener("pagehide", () => {
      finishMinimapScrollOperation();
      cancelPendingVirtualizedMaintenance("teardown");
      finishCachedScrollRestore?.("canceled", "teardown");
      cancelProcessedDocumentCacheClone();
      cancelProgressiveDeferredEnhancements();
      cancelDeferredMinimapContentRefresh(false);
      cancelMinimapRefreshAfterLayoutSettles();
      cancelHeavyLiveUpdate();
      if (minimapContentRefreshTimer !== void 0) {
        window.clearTimeout(minimapContentRefreshTimer);
        minimapContentRefreshTimer = void 0;
      }
      cancelModelRenderedContentCoordinator("teardown");
      resetVirtualizedDocumentWindow(false);
      scrollOwnershipControlPlane.dispose();
      getDocumentScrollRoot().removeAttribute("data-mm-virtualization-active");
    }, { once: true });
  }
  var virtualizationShadowEnabled = readVirtualizationShadowFlag(window, document);
  var virtualizationShadowValidator = null;
  var virtualizationShadowDocumentFinal = true;
  var virtualizedDocumentWindowController = null;
  var virtualizedDocumentWindowModel = null;
  var virtualizedFindProvider = null;
  var virtualizedIntrinsicCalibrator = createSectionIntrinsicCalibrator();
  var virtualizedMeasureFrameRequested = false;
  var virtualizedMeasuredHeightGeometryTicket = null;
  var virtualizedMeasuredHeightTerminalSubscribers = /* @__PURE__ */ new Set();
  var virtualizedCalibrationHandle = null;
  var virtualizedCalibrationGeometryTicket = null;
  var virtualizedWindowMountGeneration = 0;
  var virtualizedWindowFontGeometryTicket = null;
  var virtualizedProgrammaticNavigationInProgress = false;
  var virtualizedProgrammaticNavigationGeneration = 0;
  var virtualizedProgrammaticNavigationExternalShiftCount = 0;
  var virtualizedProgrammaticNavigationPostSettleTarget = null;
  var virtualizedProgrammaticNavigationOperation = null;
  var minimapScrollOperation = null;
  var virtualizedWriteReceipts = /* @__PURE__ */ new Map();
  var cachedScrollRestoreCompletion = null;
  var finishCachedScrollRestore = null;
  var virtualizedWindowMathController = null;
  var modelRenderedContentCoordinatorState = null;
  var modelRenderedContentLeaseSerial = 0;
  var minimapRenderedContentLease = null;
  var renderedFindContentLease = null;
  var renderedFindProjectionGeneration = 0;
  var renderedFindProjectionRenderId = null;
  var renderedFindProjectionRevision = 0;
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
    const restoredRenderedContentState = validateCachedRenderedContentState(cached);
    restoredCachedLayoutState = { ...cached.layoutState };
    restoredCachedHeadings = cached.headings.map(cloneHeadingPayload);
    restoredCachedMinimapSnapshot = restoredRenderedContentState === cached.renderedContentState ? cached.minimapSnapshot : null;
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
  function isProcessedDocumentMinimapSnapshotEligible(renderedContentState) {
    return renderedContentState === null || renderedContentState === "not-needed" || renderedContentState === "ready" || renderedContentState === "ready-with-failures";
  }
  function getCurrentProcessedDocumentRenderedContentState() {
    return virtualizationEnabled && virtualizedDocumentWindowModel !== null ? virtualizedDocumentWindowModel.getRenderedContentState() : null;
  }
  function readRenderedContentStateFromFragment(fragment) {
    const mathNodes = Array.from(fragment.querySelectorAll("[data-tex]"));
    if (mathNodes.length === 0) {
      return "not-needed";
    }
    let failedCount = 0;
    for (const node of mathNodes) {
      const state2 = node.dataset["mmMathRendered"];
      if (state2 !== "true" && state2 !== "failed") {
        return "unprepared";
      }
      if (state2 === "failed") {
        failedCount++;
      }
    }
    return failedCount > 0 ? "ready-with-failures" : "ready";
  }
  function validateCachedRenderedContentState(cached) {
    if (cached.renderedContentState === null) {
      return null;
    }
    const fragmentState = readRenderedContentStateFromFragment(cached.fragment);
    if (cached.renderedContentState === "not-needed") {
      return fragmentState === "not-needed" ? cached.renderedContentState : "unprepared";
    }
    if ((cached.renderedContentState === "ready" || cached.renderedContentState === "ready-with-failures") && fragmentState !== cached.renderedContentState) {
      return "unprepared";
    }
    return cached.renderedContentState;
  }
  function isCurrentModelFragmentMinimapSnapshot(snapshot) {
    const root = snapshot.content.firstElementChild;
    return root instanceof HTMLElement && root.dataset["mmMinimapSource"] === "model-fragment" && snapshot.content.querySelector("[data-tex]") === null;
  }
  function captureProcessedDocumentMinimapPayload(renderedContentState) {
    let minimapSnapshot = null;
    if (isProcessedDocumentMinimapSnapshotEligible(renderedContentState)) {
      const captured = captureMinimapSnapshot({
        ownerDocument: document,
        minimapContent,
        minimapViewport,
        documentHeight: minimapDocumentHeight,
        lastPostedState: lastPostedMinimapState
      });
      minimapSnapshot = renderedContentState === null || captured === null || isCurrentModelFragmentMinimapSnapshot(captured) ? captured : null;
    }
    return {
      minimapSnapshot,
      renderedContentState
    };
  }
  function captureCurrentProcessedDocumentCacheEntry(mode) {
    const main = document.querySelector("main.mm-document");
    if (!main || main.childNodes.length === 0) {
      return null;
    }
    const virtualizedLayoutState = virtualizationEnabled ? {
      readingAnchor: captureCurrentVirtualizedReadingAnchor(),
      settledGeometryEpoch: scrollOwnershipControlPlane.captureGeometryEpoch()
    } : null;
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
    const minimapPayload = captureProcessedDocumentMinimapPayload(
      getCurrentProcessedDocumentRenderedContentState()
    );
    return {
      fragment,
      nodeCount: sourceNodes.length,
      layoutState: virtualizedLayoutState !== null ? {
        ...lastKnownLayoutState,
        ...virtualizedLayoutState
      } : { ...lastKnownLayoutState },
      headings: lastExtractedHeadings.map(cloneHeadingPayload),
      renderedContentState: minimapPayload.renderedContentState,
      minimapSnapshot: minimapPayload.minimapSnapshot
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
    return cached.minimapSnapshot === null && isProcessedDocumentMinimapSnapshotEligible(getCurrentProcessedDocumentRenderedContentState()) && minimapContent !== null && minimapContent.childNodes.length > 0;
  }
  function refreshProcessedDocumentCacheState(cacheKey, markName) {
    const cached = processedDocumentCache.get(cacheKey);
    if (cached === void 0) {
      return false;
    }
    if (cachedFragmentIsBehindLiveDocument(cached)) {
      return false;
    }
    const minimapPayload = captureProcessedDocumentMinimapPayload(
      getCurrentProcessedDocumentRenderedContentState()
    );
    processedDocumentCache.delete(cacheKey);
    processedDocumentCache.set(cacheKey, {
      ...cached,
      layoutState: virtualizationEnabled ? {
        ...lastKnownLayoutState,
        readingAnchor: captureCurrentVirtualizedReadingAnchor(),
        settledGeometryEpoch: scrollOwnershipControlPlane.captureGeometryEpoch()
      } : { ...lastKnownLayoutState },
      headings: lastExtractedHeadings.map(cloneHeadingPayload),
      renderedContentState: minimapPayload.renderedContentState,
      minimapSnapshot: minimapPayload.minimapSnapshot
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
    const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    cancelProcessedDocumentCacheClone();
    const run = () => {
      if (generation !== processedDocumentCacheCloneGeneration || currentDocumentCacheKey !== cacheKey || documentEpoch !== void 0 && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
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
      if (virtualizationEnabled) {
        scheduleVirtualizedStandaloneOperation("scroll-disabled-reset", "supersede-as-user", (operation) => {
          operation.requestScrollTop(0, "scroll-disabled-reset");
        });
      } else {
        window.scrollTo({ left: 0, top: 0, behavior: "instant" });
      }
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
  function isTerminalModelRenderedContentStatus(status) {
    return status === "not-needed" || status === "ready" || status === "ready-with-failures";
  }
  function readModelRenderedContentConsumers(state2) {
    return Array.from(new Set(state2.leases.values())).sort();
  }
  function postModelRenderedContentMark(state2, name, detail = {}) {
    postPerfMark(name, {
      ...detail,
      activeLeaseCount: state2.leases.size,
      consumers: readModelRenderedContentConsumers(state2),
      documentEpoch: state2.documentEpoch
    });
  }
  function isCurrentModelRenderedContentState(state2) {
    return state2.model === virtualizedDocumentWindowModel && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(state2.documentEpoch) === true;
  }
  function postModelRenderedContentCancellation(state2, reason) {
    if (state2.cancelMarkPosted) {
      return;
    }
    state2.cancelMarkPosted = true;
    postModelRenderedContentMark(state2, "mm-model-rendered-content-cancel", {
      reason,
      status: state2.model.getRenderedContentState()
    });
  }
  function cancelModelRenderedContentState(state2, reason) {
    state2.cancelled = true;
    state2.cancelReason = reason;
    postModelRenderedContentCancellation(state2, reason);
  }
  function cancelModelRenderedContentCoordinator(reason) {
    renderedFindProjectionGeneration++;
    minimapRenderedContentLease = null;
    renderedFindContentLease = null;
    const state2 = modelRenderedContentCoordinatorState;
    if (state2 !== null) {
      state2.leases.clear();
      if (state2.promise !== null && state2.model.getRenderedContentState() === "unprepared") {
        cancelModelRenderedContentState(state2, reason);
      }
    }
    modelRenderedContentCoordinatorState = null;
  }
  function yieldModelRenderedContentWork() {
    return new Promise((resolve) => {
      window.requestAnimationFrame(() => resolve());
    });
  }
  function handleModelRenderedContentEvent(state2, event) {
    if (event.type === "progress") {
      postModelRenderedContentMark(state2, "mm-model-rendered-content-progress", {
        committed: event.committed,
        failedMathCount: event.failedMathCount,
        pendingMathCount: event.pendingMathCount,
        renderedMathCount: event.renderedMathCount,
        sectionIndex: event.sectionIndex,
        status: event.status
      });
      return;
    }
    const detail = {
      failedMathCount: event.failedMathCount,
      pendingMathCount: event.pendingMathCount,
      renderedMathCount: event.renderedMathCount,
      status: event.status
    };
    if (event.type === "complete") {
      postModelRenderedContentMark(state2, "mm-model-rendered-content-end", detail);
    } else if (event.type === "skipped-no-katex") {
      postModelRenderedContentMark(state2, "mm-model-rendered-content-skipped-no-katex", detail);
    } else {
      postModelRenderedContentCancellation(state2, state2.cancelReason ?? "cancelled");
    }
  }
  function ensureModelRenderedContentJob(state2) {
    if (state2.promise !== null) {
      return state2.promise;
    }
    const runSerial = ++state2.runSerial;
    state2.cancelled = false;
    state2.cancelReason = null;
    state2.cancelMarkPosted = false;
    postModelRenderedContentMark(state2, "mm-model-rendered-content-start", {
      status: state2.model.getRenderedContentState()
    });
    const katex = hostWindow.katex;
    const promise = prepareDocumentWindowModelRenderedContent(state2.model, {
      katex,
      now: () => performance.now(),
      onProgress: (event) => handleModelRenderedContentEvent(state2, event),
      ownerDocument: document,
      shouldContinue: () => !state2.cancelled && state2.leases.size > 0 && isCurrentModelRenderedContentState(state2),
      yield: yieldModelRenderedContentWork
    }).then((result) => {
      if (modelRenderedContentCoordinatorState === state2 && state2.runSerial === runSerial) {
        state2.promise = null;
        if (result.completed) {
          scheduleCurrentProcessedDocumentCacheClone();
        }
      }
      return result.status;
    }, (error) => {
      cancelModelRenderedContentState(state2, `error:${String(error)}`);
      if (modelRenderedContentCoordinatorState === state2 && state2.runSerial === runSerial) {
        state2.promise = null;
      }
      return "cancelled";
    });
    state2.promise = promise;
    return promise;
  }
  function getCurrentModelRenderedContentState(model, documentEpoch) {
    const existing = modelRenderedContentCoordinatorState;
    if (existing !== null) {
      if (existing.model === model && existing.documentEpoch === documentEpoch) {
        return existing;
      }
      cancelModelRenderedContentState(existing, "stale-model");
    }
    const state2 = {
      cancelMarkPosted: false,
      cancelReason: null,
      cancelled: false,
      documentEpoch,
      leases: /* @__PURE__ */ new Map(),
      model,
      promise: null,
      runSerial: 0
    };
    modelRenderedContentCoordinatorState = state2;
    return state2;
  }
  function acquireCurrentModelRenderedContentLease(consumer) {
    const model = virtualizedDocumentWindowModel;
    const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    if (!virtualizationEnabled || model === null || documentEpoch === void 0 || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
      return null;
    }
    const modelStatus = model.getRenderedContentState();
    if (modelStatus !== "unprepared") {
      return {
        consumer,
        documentEpoch,
        model,
        readiness: Promise.resolve(modelStatus),
        release: () => {
        }
      };
    }
    const state2 = getCurrentModelRenderedContentState(model, documentEpoch);
    const leaseId = ++modelRenderedContentLeaseSerial;
    state2.leases.set(leaseId, consumer);
    const readiness = ensureModelRenderedContentJob(state2);
    let released = false;
    return {
      consumer,
      documentEpoch,
      model,
      readiness,
      release: () => {
        if (released) {
          return;
        }
        released = true;
        state2.leases.delete(leaseId);
        if (state2.promise !== null && state2.leases.size === 0 && state2.model.getRenderedContentState() === "unprepared") {
          cancelModelRenderedContentState(state2, "last-lease-released");
        }
      }
    };
  }
  function releaseMinimapRenderedContentLease() {
    const lease = minimapRenderedContentLease;
    minimapRenderedContentLease = null;
    lease?.release();
  }
  function releaseRenderedFindContentLease() {
    const lease = renderedFindContentLease;
    renderedFindContentLease = null;
    lease?.release();
  }
  function startRenderedFindProjectionForCurrentModel() {
    const model = virtualizedDocumentWindowModel;
    const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    const renderId = currentDocumentRenderId;
    if (!virtualizationEnabled || model === null || documentEpoch === void 0 || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true || renderId === null || !Number.isSafeInteger(renderId) || renderId <= 0) {
      return;
    }
    if (renderedFindProjectionRenderId !== renderId) {
      renderedFindProjectionRenderId = renderId;
      renderedFindProjectionRevision = 0;
    }
    const projectionRevision = ++renderedFindProjectionRevision;
    const generation = ++renderedFindProjectionGeneration;
    postHostMessage(createRenderedFindDomainBeginMessage({ renderId }));
    releaseRenderedFindContentLease();
    const lease = acquireCurrentModelRenderedContentLease("rendered-find-projection");
    if (lease === null) {
      return;
    }
    renderedFindContentLease = lease;
    const shouldCancel = () => generation !== renderedFindProjectionGeneration || model !== virtualizedDocumentWindowModel || renderId !== currentDocumentRenderId || lease.documentEpoch !== documentEpoch || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true;
    void publishRenderedFindProjection({
      emit: (message) => {
        postHostMessage(message);
      },
      projectionRevision,
      readiness: lease.readiness,
      renderId,
      root: () => createFullDocumentFragmentFromWindowModel(document, model),
      shouldCancel,
      yieldControl: yieldModelRenderedContentWork
    }).then((status) => {
      postPerfMark("mm-find-projection-terminal", {
        projectionRevision,
        renderId,
        status
      });
    }).catch((error) => {
      postPerfMark("mm-find-projection-failed", {
        projectionRevision,
        renderId,
        reason: String(error)
      });
    }).finally(() => {
      if (renderedFindContentLease === lease) {
        renderedFindContentLease = null;
        lease.release();
      }
    });
  }
  function getLiveDocumentRoot() {
    return document.querySelector("body > main.mm-document");
  }
  function readLiveDocumentMathNodes() {
    return Array.from(getLiveDocumentRoot()?.querySelectorAll("[data-tex]") ?? []);
  }
  function getLiveDocumentMathCount() {
    return readLiveDocumentMathNodes().length;
  }
  function findLiveDocumentElementById(id) {
    const main = getLiveDocumentRoot();
    if (main === null) {
      return null;
    }
    if (main.id === id) {
      return main;
    }
    for (const element of Array.from(main.querySelectorAll("[id]"))) {
      if (element.id === id) {
        return element;
      }
    }
    return null;
  }
  function findLiveDocumentBlockElement(blockIndex) {
    const main = getLiveDocumentRoot();
    if (main === null || !Number.isFinite(blockIndex)) {
      return null;
    }
    for (const element of Array.from(main.querySelectorAll("[data-mm-block-index]"))) {
      if (Number.parseInt(element.dataset["mmBlockIndex"] ?? "", 10) === blockIndex) {
        return element;
      }
    }
    return null;
  }
  function countFailedInSet(nodes) {
    let count = 0;
    for (const node of nodes) {
      if (node.dataset["mmMathRendered"] === "failed") count++;
    }
    return count;
  }
  function hasUnrenderedDocumentMath() {
    return (getLiveDocumentRoot()?.querySelector("[data-tex]:not([data-mm-math-rendered])") ?? null) !== null;
  }
  function renderMath2() {
    emitMark("mm-render-math-start", { mathCount: getLiveDocumentMathCount() });
    const katex = hostWindow.katex ?? void 0;
    const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    const geometryTicket = virtualizationEnabled ? beginVirtualizedGeometryWork("document-math") : null;
    const controller = renderMath({ katex, documentRoot: getLiveDocumentRoot() ?? document });
    const phaseBDocumentCacheKey = currentDocumentCacheKey;
    const initialVisualSettleReady = virtualizationEnabled ? controller.allMathRendered.then(() => {
      if (documentEpoch !== void 0 && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true || phaseBDocumentCacheKey !== currentDocumentCacheKey || controller.isCancelled()) {
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
      if (documentEpoch !== void 0 && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
        return;
      }
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
      if (documentEpoch !== void 0 && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
        return;
      }
      invalidateSourceLineAnchors();
      const allMathNodes = readLiveDocumentMathNodes();
      emitMark("mm-all-math-rendered", {
        totalCount: controller.totalMathCount,
        failedCount: countFailedInSet(allMathNodes),
        cancelled: controller.isCancelled()
      });
    });
    const finishGeometryWork = () => {
      if (geometryTicket !== null && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(geometryTicket.documentEpoch) === true) {
        mutateVirtualizedGeometry(geometryTicket);
        scheduleVirtualizedMeasuredHeightAdoption();
      }
      endVirtualizedGeometryWork(geometryTicket);
    };
    void controller.allMathRendered.then(finishGeometryWork, finishGeometryWork);
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
  async function renderMermaidNodes(allNodes, mermaid, perfMarkName = "mm-mermaid-visible-first", geometrySource = "mermaid-eager", geometryMountGeneration) {
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
    installLazyMermaidObserver(lazyNodes, generation, mermaid, geometryMountGeneration);
    if (eagerNodes.length === 0) return;
    const geometryTicket = virtualizationEnabled ? beginVirtualizedGeometryWork(geometrySource, geometryMountGeneration) : null;
    let eagerBudgetExpired = false;
    const watchdog = window.setTimeout(() => {
      eagerBudgetExpired = true;
    }, MERMAID_WATCHDOG_MS);
    try {
      for (const node of eagerNodes) {
        await renderMermaidNode(
          node,
          generation,
          () => mermaidRenderGeneration,
          mermaid,
          MERMAID_PER_DIAGRAM_TIMEOUT_MS,
          virtualizationEnabled ? { manageVirtualizedProxyLifecycle: true } : void 0
        );
        if (eagerBudgetExpired || generation !== mermaidRenderGeneration) return;
      }
    } finally {
      window.clearTimeout(watchdog);
      if (geometryTicket !== null && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(geometryTicket.documentEpoch) === true && (geometryMountGeneration === void 0 || geometryMountGeneration === virtualizedWindowMountGeneration)) {
        mutateVirtualizedGeometry(geometryTicket);
        scheduleVirtualizedMeasuredHeightAdoption();
      }
      endVirtualizedGeometryWork(geometryTicket);
    }
  }
  async function renderMermaid() {
    disconnectMermaidLazyObserver();
    const mermaid = hostWindow.mermaid;
    if (!mermaid) return;
    const allNodes = Array.from(getLiveDocumentRoot()?.querySelectorAll("pre.mm-mermaid") ?? []);
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
      const missingNodes = Array.from(
        getLiveDocumentRoot()?.querySelectorAll("pre.mm-mermaid:not(.is-rendered)") ?? []
      );
      if (missingNodes.length === 0) {
        postPerfMark("mm-mermaid-cache-resume-skipped", { reason: "all-rendered" });
        return;
      }
      void renderMermaidNodes(missingNodes, mermaid, "mm-mermaid-cache-resume");
    }, 0);
  }
  function cancelProgressiveDeferredEnhancements() {
    const handle = progressiveDeferredEnhancementHandle;
    progressiveDeferredEnhancementHandle = null;
    if (handle === null) {
      return;
    }
    if (handle.kind === "idle") {
      window.cancelIdleCallback?.(handle.id);
    } else {
      window.clearTimeout(handle.id);
    }
  }
  function scheduleProgressiveDeferredEnhancements(message) {
    if (virtualizationEnabled) {
      cancelProgressiveDeferredEnhancements();
    }
    const renderId = message.renderId;
    const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    const run = () => {
      if (virtualizationEnabled) {
        progressiveDeferredEnhancementHandle = null;
      }
      if (documentEpoch !== void 0 && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
        return;
      }
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
      const id2 = requestIdle(run, { timeout: 4e3 });
      if (virtualizationEnabled) {
        progressiveDeferredEnhancementHandle = { kind: "idle", id: id2 };
      }
      return;
    }
    const id = window.setTimeout(run, 800);
    if (virtualizationEnabled) {
      progressiveDeferredEnhancementHandle = { kind: "timeout", id };
    }
  }
  function getViewportHeightForMermaid() {
    const root = document.scrollingElement ?? document.documentElement;
    return root.clientHeight || window.innerHeight || 0;
  }
  function disconnectMermaidLazyObserver() {
    mermaidLazyObserver?.disconnect();
    mermaidLazyObserver = null;
  }
  function installLazyMermaidObserver(nodes, generation, mermaid, mountGeneration) {
    if (nodes.length === 0) return;
    postPerfMark("mm-mermaid-lazy-observe", {
      total: nodes.length,
      rootMarginPx: MERMAID_LAZY_ROOT_MARGIN_PX
    });
    if (typeof window.IntersectionObserver !== "function") {
      for (const node of nodes) {
        enqueueLazyMermaidRender(node, generation, mermaid, mountGeneration);
      }
      return;
    }
    mermaidLazyObserver = new IntersectionObserver((entries) => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue;
        const node = entry.target;
        mermaidLazyObserver?.unobserve(node);
        enqueueLazyMermaidRender(node, generation, mermaid, mountGeneration);
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
  function enqueueLazyMermaidRender(node, generation, mermaid, mountGeneration) {
    if (generation !== mermaidRenderGeneration || mountGeneration !== void 0 && mountGeneration !== virtualizedWindowMountGeneration) return;
    const marker = String(generation);
    if (node.dataset.mmMermaidRenderQueued === marker) return;
    node.dataset.mmMermaidRenderQueued = marker;
    const geometryTicket = virtualizationEnabled ? beginVirtualizedGeometryWork("lazy-mermaid", mountGeneration) : null;
    mermaidLazyRenderQueue = mermaidLazyRenderQueue.catch(() => void 0).then(async () => {
      try {
        if (generation !== mermaidRenderGeneration || mountGeneration !== void 0 && mountGeneration !== virtualizedWindowMountGeneration) return;
        postPerfMark("mm-mermaid-lazy-render-start");
        await renderMermaidNode(
          node,
          generation,
          () => mermaidRenderGeneration,
          mermaid,
          MERMAID_PER_DIAGRAM_TIMEOUT_MS,
          virtualizationEnabled ? { manageVirtualizedProxyLifecycle: true } : void 0
        );
        if (generation === mermaidRenderGeneration && (mountGeneration === void 0 || mountGeneration === virtualizedWindowMountGeneration)) {
          postPerfMark("mm-mermaid-lazy-render-end");
          scheduleCurrentProcessedDocumentCacheClone();
          mutateVirtualizedGeometry(geometryTicket);
          scheduleVirtualizedMeasuredHeightAdoption();
        }
      } finally {
        endVirtualizedGeometryWork(geometryTicket);
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
    return (getLiveDocumentRoot()?.querySelector("pre.mm-mermaid") ?? null) !== null;
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
  var virtualizedMaintenanceByOwner = /* @__PURE__ */ new Map();
  var virtualizedMaintenanceReleaseHolds = /* @__PURE__ */ new Map();
  var virtualizedMaintenanceDeferredPromotionOwners = /* @__PURE__ */ new Set();
  var virtualizedMaintenanceCancellationBatchDepth = 0;
  var virtualizedMaintenanceRequestSerial = 0;
  var pendingInitialVirtualizedWindowWork = null;
  function beginVirtualizedGeometryWork(source, mountGeneration = virtualizedWindowMountGeneration) {
    const plane = scrollOwnershipControlPlane;
    if (plane === null) {
      return null;
    }
    return plane.beginGeometryWork(source, plane.captureDocumentEpoch(), mountGeneration);
  }
  function mutateVirtualizedGeometry(ticket) {
    return ticket !== null && scrollOwnershipControlPlane?.geometryMutated(ticket) === true;
  }
  function endVirtualizedGeometryWork(ticket) {
    return ticket !== null && scrollOwnershipControlPlane?.endGeometryWork(ticket) === true;
  }
  async function waitForCurrentVirtualizedGeometry(operation, afterEmission) {
    const plane = scrollOwnershipControlPlane;
    if (plane === null || !operation.isCurrent()) {
      return { reason: "programmatic-supersession", status: "canceled" };
    }
    return plane.waitForGeometrySettled(operation.documentEpoch, afterEmission);
  }
  async function awaitConfirmedVirtualizedGeometry(operation, nominal) {
    const plane = scrollOwnershipControlPlane;
    if (plane === null || !operation.isCurrent()) {
      return { reason: "programmatic-supersession", status: "canceled" };
    }
    const documentEpoch = operation.documentEpoch;
    const afterEmission = nominal.emission;
    const confirmation = await plane.waitForGeometrySettled(documentEpoch, afterEmission);
    if (confirmation.status === "canceled") {
      return confirmation;
    }
    if (confirmation.payload.geometryEpoch === nominal.payload.geometryEpoch && plane.holds(operation.lease, confirmation.payload.geometryEpoch)) {
      return { confirmation, status: "confirmed" };
    }
    return { settlement: confirmation, status: "changed" };
  }
  function consumePendingInitialVirtualizedWindow(operation) {
    const work = pendingInitialVirtualizedWindowWork;
    pendingInitialVirtualizedWindowWork = null;
    if (work === null || !operation.isCurrent()) {
      return false;
    }
    work(operation);
    return true;
  }
  function createVirtualizedScrollOperation(lease) {
    const plane = scrollOwnershipControlPlane;
    if (plane === null) {
      return null;
    }
    return {
      documentEpoch: lease.documentEpoch,
      operationEpoch: lease.operationEpoch,
      lease,
      isCurrent: () => plane.isCurrentDocumentEpoch(lease.documentEpoch) && plane.holds(lease),
      requestScrollTop: (target, writer) => {
        if (!plane.isCurrentDocumentEpoch(lease.documentEpoch) || !plane.holds(lease)) {
          return;
        }
        const receipt = plane.write(lease, { target, writer });
        virtualizedWriteReceipts.set(lease.operationEpoch, receipt);
        void receipt.result.then(() => {
          if (virtualizedWriteReceipts.get(lease.operationEpoch) === receipt) {
            virtualizedWriteReceipts.delete(lease.operationEpoch);
          }
        });
      },
      scheduleFrameTransaction: (work) => plane.scheduleFrameTransaction(lease, work)
    };
  }
  function acquireVirtualizedScrollOperation(owner, policy) {
    const maintenanceCutoff = virtualizedMaintenanceRequestSerial;
    const acquired = scrollOwnershipControlPlane?.acquire(owner, policy);
    if (acquired?.status !== "acquired") {
      return null;
    }
    const operation = createVirtualizedScrollOperation(acquired.lease);
    if (operation !== null && policy !== "defer") {
      cancelVirtualizedMaintenanceThrough(
        maintenanceCutoff,
        policy === "supersede-as-user" ? "user-supersession" : "programmatic-supersession"
      );
    }
    return operation;
  }
  function releaseVirtualizedScrollOperationAfterWrite(operation) {
    const plane = scrollOwnershipControlPlane;
    if (plane === null) {
      return;
    }
    const receipt = virtualizedWriteReceipts.get(operation.operationEpoch);
    if (receipt === void 0) {
      if (operation.isCurrent()) {
        releaseVirtualizedScrollOperation(operation);
      }
      return;
    }
    void receipt.result.then(() => {
      if (operation.isCurrent()) {
        releaseVirtualizedScrollOperation(operation);
      }
    });
  }
  function releaseVirtualizedScrollOperation(operation) {
    const plane = scrollOwnershipControlPlane;
    const hold = virtualizedMaintenanceReleaseHolds.get(operation.operationEpoch);
    if (hold !== void 0 && hold.requestSerials.size > 0) {
      hold.releaseRequested = true;
      return true;
    }
    if (plane === null || !plane.release(operation.lease)) {
      return false;
    }
    return true;
  }
  function scheduleVirtualizedStandaloneOperation(owner, policy, work) {
    const operation = acquireVirtualizedScrollOperation(owner, policy);
    if (operation === null) {
      return null;
    }
    const scheduled = operation.scheduleFrameTransaction(() => {
      if (!operation.isCurrent()) {
        return;
      }
      work(operation);
      releaseVirtualizedScrollOperationAfterWrite(operation);
    });
    if (!scheduled) {
      releaseVirtualizedScrollOperation(operation);
      return null;
    }
    return operation;
  }
  function scheduleExistingVirtualizedOperation(operation, work, releaseAfterWrite = false) {
    const scheduled = operation.scheduleFrameTransaction(() => {
      if (!operation.isCurrent()) {
        return;
      }
      work();
      if (releaseAfterWrite) {
        releaseVirtualizedScrollOperationAfterWrite(operation);
      }
    });
    if (!scheduled && releaseAfterWrite && operation.isCurrent()) {
      releaseVirtualizedScrollOperation(operation);
    }
    return scheduled;
  }
  function scheduleVirtualizedElementLanding(operation, element, writer, viewportOffsetY = 0) {
    if (element === null) {
      if (operation.isCurrent()) {
        releaseVirtualizedScrollOperation(operation);
      }
      return false;
    }
    return scheduleExistingVirtualizedOperation(operation, () => {
      const target = readElementDocumentTop(element) - Math.max(0, viewportOffsetY);
      operation.requestScrollTop(target, writer);
    }, true);
  }
  function virtualizedMaintenanceDetail(request) {
    return {
      documentEpoch: request.documentEpoch,
      owner: request.owner,
      requestId: request.requestId,
      requestSerial: request.requestId.requestSerial,
      workRevision: request.workRevision
    };
  }
  function postVirtualizedMaintenanceEvent(name, request, detail = {}) {
    postPerfMark(name, {
      ...virtualizedMaintenanceDetail(request),
      ...detail
    });
  }
  function isLiveVirtualizedMaintenanceRequest(request) {
    if (request.terminal !== null || request.phase === "terminal") {
      return false;
    }
    const slot = virtualizedMaintenanceByOwner.get(request.owner);
    return slot?.active === request || slot?.successor === request;
  }
  function isActiveVirtualizedMaintenanceRequest(request) {
    return isLiveVirtualizedMaintenanceRequest(request) && virtualizedMaintenanceByOwner.get(request.owner)?.active === request;
  }
  function createVirtualizedMaintenanceRequest(owner, documentEpoch, work, onTerminal) {
    const requestSerial = ++virtualizedMaintenanceRequestSerial;
    return {
      binding: null,
      documentEpoch,
      executionCount: 0,
      owner,
      onTerminal,
      phase: "pending",
      requestId: Object.freeze({ documentEpoch, requestSerial }),
      retryFrame: null,
      terminal: null,
      work,
      workRevision: 1
    };
  }
  function postVirtualizedMaintenanceRequested(request) {
    postVirtualizedMaintenanceEvent("mm-virt-maintenance-requested", request);
  }
  function coalesceVirtualizedMaintenanceRequest(request, work, onTerminal) {
    if (!isLiveVirtualizedMaintenanceRequest(request)) {
      return;
    }
    const replacedTerminal = request.onTerminal;
    request.onTerminal = onTerminal;
    request.work = work;
    request.workRevision++;
    replacedTerminal?.({ reason: "coalesced", status: "canceled" });
    postVirtualizedMaintenanceEvent("mm-virt-maintenance-coalesced", request);
  }
  function registerVirtualizedMaintenanceReleaseHold(request, binding) {
    let hold = virtualizedMaintenanceReleaseHolds.get(binding.operationEpoch);
    if (hold === void 0) {
      hold = {
        operation: binding.operation,
        releaseRequested: false,
        requestSerials: /* @__PURE__ */ new Set()
      };
      virtualizedMaintenanceReleaseHolds.set(binding.operationEpoch, hold);
    }
    hold.requestSerials.add(request.requestId.requestSerial);
  }
  function detachVirtualizedMaintenanceBinding(request, terminal) {
    const binding = request.binding;
    if (binding === null) {
      return null;
    }
    if (binding.ownsLease) {
      if (terminal.status === "completed") {
        return { afterWrite: true, operation: binding.operation };
      }
      if (terminal.status === "canceled" && binding.operation.isCurrent()) {
        return { afterWrite: false, operation: binding.operation };
      }
      return null;
    }
    const hold = virtualizedMaintenanceReleaseHolds.get(binding.operationEpoch);
    if (hold === void 0) {
      return null;
    }
    hold.requestSerials.delete(request.requestId.requestSerial);
    if (hold.requestSerials.size > 0) {
      return null;
    }
    virtualizedMaintenanceReleaseHolds.delete(binding.operationEpoch);
    return hold.releaseRequested && hold.operation.isCurrent() ? { afterWrite: true, operation: hold.operation } : null;
  }
  function promoteVirtualizedMaintenanceSuccessor(owner) {
    const slot = virtualizedMaintenanceByOwner.get(owner);
    if (slot === void 0 || slot.active !== null) {
      return;
    }
    const successor = slot.successor;
    slot.successor = null;
    if (successor === null) {
      virtualizedMaintenanceByOwner.delete(owner);
      return;
    }
    slot.active = successor;
    attemptVirtualizedMaintenance(successor);
  }
  function flushVirtualizedMaintenancePromotions() {
    if (virtualizedMaintenanceCancellationBatchDepth !== 0) {
      return;
    }
    const owners = [...virtualizedMaintenanceDeferredPromotionOwners];
    virtualizedMaintenanceDeferredPromotionOwners.clear();
    for (const owner of owners) {
      promoteVirtualizedMaintenanceSuccessor(owner);
    }
  }
  function finishVirtualizedMaintenance(request, status, reason) {
    if (request.terminal !== null || request.phase === "terminal") {
      return false;
    }
    const terminal = Object.freeze({ reason, status });
    request.terminal = terminal;
    request.phase = "terminal";
    const onTerminal = request.onTerminal;
    request.onTerminal = null;
    if (request.retryFrame !== null) {
      window.cancelAnimationFrame(request.retryFrame);
      request.retryFrame = null;
    }
    const releaseAction = detachVirtualizedMaintenanceBinding(request, terminal);
    const slot = virtualizedMaintenanceByOwner.get(request.owner);
    if (slot?.active === request) {
      slot.active = null;
    } else if (slot?.successor === request) {
      slot.successor = null;
    }
    if (slot !== void 0 && slot.active === null && slot.successor === null) {
      virtualizedMaintenanceByOwner.delete(request.owner);
    }
    postPerfMark("mm-virt-maintenance-terminal", {
      ...virtualizedMaintenanceDetail(request),
      executionCount: request.executionCount,
      reason,
      status
    });
    onTerminal?.(terminal);
    if (releaseAction !== null) {
      if (releaseAction.afterWrite) {
        releaseVirtualizedScrollOperationAfterWrite(releaseAction.operation);
      } else {
        releaseVirtualizedScrollOperation(releaseAction.operation);
      }
    }
    if (slot?.active === null && slot.successor !== null) {
      if (status === "failed") {
        finishVirtualizedMaintenance(slot.successor, "canceled", reason);
      } else if (virtualizedMaintenanceCancellationBatchDepth === 0) {
        promoteVirtualizedMaintenanceSuccessor(request.owner);
      } else {
        virtualizedMaintenanceDeferredPromotionOwners.add(request.owner);
      }
    }
    return true;
  }
  function cancelVirtualizedMaintenanceRequests(predicate, reason) {
    const selected = [];
    for (const slot of virtualizedMaintenanceByOwner.values()) {
      for (const request of [slot.active, slot.successor]) {
        if (request !== null && isLiveVirtualizedMaintenanceRequest(request) && predicate(request)) {
          selected.push(request);
        }
      }
    }
    virtualizedMaintenanceCancellationBatchDepth++;
    try {
      for (const request of selected) {
        finishVirtualizedMaintenance(request, "canceled", reason);
      }
    } finally {
      virtualizedMaintenanceCancellationBatchDepth--;
      flushVirtualizedMaintenancePromotions();
    }
  }
  function cancelPendingVirtualizedMaintenance(reason) {
    cancelVirtualizedMaintenanceRequests(() => true, reason);
  }
  function cancelVirtualizedMaintenanceThrough(cutoff, reason) {
    cancelVirtualizedMaintenanceRequests(
      (request) => request.requestId.requestSerial <= cutoff,
      reason
    );
  }
  function scheduleVirtualizedMaintenanceRetry(request) {
    if (request.retryFrame !== null || !isActiveVirtualizedMaintenanceRequest(request) || request.phase === "terminal") {
      return;
    }
    request.phase = "retry-pending";
    request.retryFrame = window.requestAnimationFrame(() => {
      request.retryFrame = null;
      if (!isActiveVirtualizedMaintenanceRequest(request)) {
        return;
      }
      if (scrollOwnershipControlPlane?.isCurrentDocumentEpoch(request.documentEpoch) !== true) {
        finishVirtualizedMaintenance(request, "canceled", "stale-document");
        return;
      }
      request.phase = "pending";
      attemptVirtualizedMaintenance(request);
    });
    postVirtualizedMaintenanceEvent("mm-virt-maintenance-retry", request, {
      reason: "frame-transaction-occupied"
    });
  }
  function deliverVirtualizedMaintenance(request, operation) {
    const binding = request.binding;
    if (!isActiveVirtualizedMaintenanceRequest(request) || request.phase !== "frame-scheduled" || binding === null || binding.operation !== operation) {
      return;
    }
    if (!operation.isCurrent() || operation.documentEpoch !== request.documentEpoch) {
      finishVirtualizedMaintenance(request, "canceled", "stale-operation");
      return;
    }
    if (request.executionCount !== 0) {
      finishVirtualizedMaintenance(request, "failed", "execution-count-invariant");
      return;
    }
    request.phase = "executing";
    request.executionCount = 1;
    const work = request.work;
    try {
      work(operation);
    } catch (error) {
      finishVirtualizedMaintenance(request, "failed", "frame-work-failed");
      throw error;
    }
    finishVirtualizedMaintenance(request, "completed", "delivered");
  }
  function attemptVirtualizedMaintenance(request) {
    const plane = scrollOwnershipControlPlane;
    if (plane === null || !isActiveVirtualizedMaintenanceRequest(request) || !plane.isCurrentDocumentEpoch(request.documentEpoch)) {
      if (isLiveVirtualizedMaintenanceRequest(request)) {
        finishVirtualizedMaintenance(request, "canceled", "stale-document");
      }
      return;
    }
    const joined = plane.joinMaintenance(request.owner);
    if (joined === null) {
      finishVirtualizedMaintenance(request, "canceled", "lease-unavailable");
      return;
    }
    const operation = createVirtualizedScrollOperation(joined.lease);
    if (operation === null) {
      finishVirtualizedMaintenance(request, "canceled", "operation-unavailable");
      return;
    }
    const scheduled = operation.scheduleFrameTransaction(() => {
      deliverVirtualizedMaintenance(request, operation);
    });
    if (!scheduled) {
      if (joined.ownsLease && operation.isCurrent()) {
        releaseVirtualizedScrollOperation(operation);
      }
      scheduleVirtualizedMaintenanceRetry(request);
      return;
    }
    const binding = Object.freeze({
      operation,
      operationEpoch: operation.operationEpoch,
      ownsLease: joined.ownsLease
    });
    request.binding = binding;
    request.phase = "frame-scheduled";
    if (!binding.ownsLease) {
      registerVirtualizedMaintenanceReleaseHold(request, binding);
    }
    postVirtualizedMaintenanceEvent("mm-virt-maintenance-bound", request, {
      operationEpoch: binding.operationEpoch,
      ownsLease: binding.ownsLease
    });
  }
  function scheduleVirtualizedMaintenance(owner, work, onTerminal = null) {
    const plane = scrollOwnershipControlPlane;
    if (plane === null) {
      return false;
    }
    const documentEpoch = plane.captureDocumentEpoch();
    const staleSlot = virtualizedMaintenanceByOwner.get(owner);
    if (staleSlot !== void 0 && [staleSlot.active, staleSlot.successor].some((request2) => request2 !== null && request2.documentEpoch !== documentEpoch)) {
      cancelVirtualizedMaintenanceRequests(
        (request2) => request2.owner === owner && request2.documentEpoch !== documentEpoch,
        "stale-document"
      );
    }
    let slot = virtualizedMaintenanceByOwner.get(owner);
    if (slot?.active !== null && slot?.active !== void 0) {
      if (slot.active.phase === "executing") {
        if (slot.successor !== null) {
          coalesceVirtualizedMaintenanceRequest(slot.successor, work, onTerminal);
          return true;
        }
        const successor = createVirtualizedMaintenanceRequest(owner, documentEpoch, work, onTerminal);
        slot.successor = successor;
        postVirtualizedMaintenanceRequested(successor);
        return true;
      }
      coalesceVirtualizedMaintenanceRequest(slot.active, work, onTerminal);
      return true;
    }
    const request = createVirtualizedMaintenanceRequest(owner, documentEpoch, work, onTerminal);
    if (slot === void 0) {
      slot = { active: request, successor: null };
      virtualizedMaintenanceByOwner.set(owner, slot);
    } else {
      slot.active = request;
    }
    postVirtualizedMaintenanceRequested(request);
    attemptVirtualizedMaintenance(request);
    return true;
  }
  function captureCurrentVirtualizedReadingAnchor() {
    const main = document.querySelector("main.mm-document");
    return main === null ? null : captureReadingAnchor(collectLiveDocumentSectionElements(main));
  }
  function readVirtualizedFindContext() {
    return {
      beginNavigationOperation: () => acquireVirtualizedScrollOperation(
        "find-navigation",
        "supersede-programmatic"
      ),
      completeNavigationOperation: (operation) => {
        releaseVirtualizedScrollOperationAfterWrite(operation);
      },
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
    return virtualizationEnabled && virtualizedProgrammaticNavigationInProgress && virtualizedProgrammaticNavigationOperation?.isCurrent() === true;
  }
  function writeVirtualizedProgrammaticNavigationScrollTop(operation, scrollTop, writer) {
    operation.requestScrollTop(Math.max(0, scrollTop), writer);
  }
  var VIRTUALIZED_NAVIGATION_SMOOTH_DURATION_MS = 200;
  var VIRTUALIZED_NAVIGATION_FRAME_INTERVAL_MS = 1e3 / 60;
  function easeVirtualizedNavigationProgress(progress) {
    const clamped = Math.min(1, Math.max(0, progress));
    return clamped < 0.5 ? 4 * clamped * clamped * clamped : 1 - Math.pow(-2 * clamped + 2, 3) / 2;
  }
  function scheduleVirtualizedProgrammaticNavigationSmoothTransition(input) {
    if (!Number.isFinite(input.startScrollTop) || !Number.isFinite(input.destinationScrollTop) || !input.operation.isCurrent()) {
      return false;
    }
    let frame = 0;
    const advance = () => input.operation.scheduleFrameTransaction(() => {
      if (!input.operation.isCurrent() || input.generation !== virtualizedProgrammaticNavigationGeneration) {
        return;
      }
      frame++;
      const elapsedMs = Math.min(
        VIRTUALIZED_NAVIGATION_SMOOTH_DURATION_MS,
        frame * VIRTUALIZED_NAVIGATION_FRAME_INTERVAL_MS
      );
      const progress = elapsedMs / VIRTUALIZED_NAVIGATION_SMOOTH_DURATION_MS;
      const easedProgress = easeVirtualizedNavigationProgress(progress);
      const scrollTop = input.startScrollTop + (input.destinationScrollTop - input.startScrollTop) * easedProgress;
      writeVirtualizedProgrammaticNavigationScrollTop(
        input.operation,
        scrollTop,
        "navigation-smooth"
      );
      if (elapsedMs < VIRTUALIZED_NAVIGATION_SMOOTH_DURATION_MS) {
        advance();
        return;
      }
      void settleVirtualizedProgrammaticNavigationTarget(
        { descriptor: input.descriptor, viewportOffsetY: input.viewportOffsetY },
        input.generation,
        input.operation
      );
    });
    return advance();
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
  function applyVirtualizedProgrammaticNavigationContext(context, descriptor, viewportOffsetY, operation) {
    if (descriptor.kind === "source-line") {
      pendingSourceLineTarget = descriptor.sourceLine;
      suppressPreviewSourceLinePost();
    }
    const scrollTop = computeVirtualizedProgrammaticNavigationScrollTop(
      context,
      descriptor,
      viewportOffsetY
    );
    writeVirtualizedProgrammaticNavigationScrollTop(operation, scrollTop, "navigation-initial");
    return true;
  }
  function readVirtualizedProgrammaticNavigationResidual(context, descriptor, viewportOffsetY) {
    const sectionElement = context.element;
    if (sectionElement === null) {
      return null;
    }
    const sectionRectTop = sectionElement.getBoundingClientRect().top;
    if (!Number.isFinite(sectionRectTop)) {
      return null;
    }
    const targetLocalOffset = readVirtualizedTargetLocalOffset(context, descriptor);
    const normalizedViewportOffset = Number.isFinite(viewportOffsetY) ? Math.max(0, viewportOffsetY) : 0;
    return sectionRectTop + targetLocalOffset - normalizedViewportOffset;
  }
  function correctVirtualizedProgrammaticNavigationResidual(input) {
    const context = readVirtualizedProgrammaticNavigationTargetContext(input.descriptor);
    if (context === null) {
      return false;
    }
    const residual = readVirtualizedProgrammaticNavigationResidual(
      context,
      input.descriptor,
      input.viewportOffsetY
    );
    if (residual === null) {
      return false;
    }
    if (Math.abs(residual) > VIRTUALIZED_NAVIGATION_CORRECTION_TOLERANCE_PX) {
      const root = getDocumentScrollRoot();
      const nextScrollTop = Math.max(0, root.scrollTop + residual);
      writeVirtualizedProgrammaticNavigationScrollTop(input.operation, nextScrollTop, "navigation-residual");
    }
    return true;
  }
  function applyVirtualizedRenderedHeightAdoptionEffects(result, options = {}) {
    if (!hasMeasuredHeightGeometryDelta(result)) {
      return;
    }
    invalidateTopVisibleBlockIndexCache();
    invalidateSourceLineAnchors({
      reassertPendingTarget: options.alignPostSettleTarget !== false
    });
    refreshVirtualizedFindHighlights();
    if (getModelMinimapSource() !== null && minimapSourceReady) {
      syncModelMinimapCloneMetadata();
      updateMinimapViewport({ skipVisibilityUpdate: true });
    }
    if (options.alignPostSettleTarget !== false) {
      alignVirtualizedProgrammaticNavigationPostSettleTarget();
    }
    if (options.scheduleCalibration !== false) {
      scheduleVirtualizedCalibration();
    }
    postPerfMark("mm-virt-window-height-adopted", {
      maxAbsDelta: result.maxAbsDelta,
      totalHeight: virtualizedDocumentWindowModel?.getTotalHeight() ?? null,
      totalDelta: result.totalDelta,
      updatedCount: result.updatedCount
    });
  }
  function hasMeasuredHeightGeometryDelta(result) {
    return result.maxAbsDelta > Number.EPSILON || Math.abs(result.totalDelta) > Number.EPSILON;
  }
  function adoptVirtualizedProgrammaticNavigationRenderedHeights(context, operation) {
    const controller = virtualizedDocumentWindowController;
    if (!virtualizationEnabled || controller === null) {
      return false;
    }
    const ticket = beginVirtualizedGeometryWork("measured-height-adoption");
    try {
      const result = controller.adoptRenderedHeights({
        operation,
        preserveSectionIndex: context.sectionIndex,
        reanchor: false
      });
      if (hasMeasuredHeightGeometryDelta(result)) {
        mutateVirtualizedGeometry(ticket);
      }
      applyVirtualizedRenderedHeightAdoptionEffects(result, {
        alignPostSettleTarget: false,
        scheduleCalibration: false
      });
      return hasMeasuredHeightGeometryDelta(result);
    } finally {
      endVirtualizedGeometryWork(ticket);
    }
  }
  function releaseVirtualizedProgrammaticNavigationOperation(operation, clearPostSettleTarget = false) {
    virtualizedProgrammaticNavigationInProgress = false;
    virtualizedProgrammaticNavigationOperation = null;
    if (clearPostSettleTarget) {
      virtualizedProgrammaticNavigationPostSettleTarget = null;
    }
    releaseVirtualizedScrollOperation(operation);
  }
  function finishVirtualizedProgrammaticNavigationCorrection(generation, operation, input) {
    if (generation !== virtualizedProgrammaticNavigationGeneration || !operation.isCurrent()) {
      return;
    }
    postPerfMark("mm-virt-navigation-settled", {
      descriptorKind: input.descriptor.kind,
      externalShiftCount: virtualizedProgrammaticNavigationExternalShiftCount,
      passCount: input.passCount,
      residual: input.residual
    });
    updateMinimapViewport({ skipVisibilityUpdate: true });
    postScroll();
    releaseVirtualizedProgrammaticNavigationOperation(operation);
  }
  function scheduleVirtualizedProgrammaticNavigationFrame(input, generation, operation, settlement, pass, previousResidualAbs = Number.POSITIVE_INFINITY) {
    return new Promise((resolve) => {
      const attempt = () => {
        if (!operation.isCurrent() || generation !== virtualizedProgrammaticNavigationGeneration) {
          resolve({ kind: "canceled" });
          return;
        }
        const scheduled = operation.scheduleFrameTransaction(() => {
          if (!operation.isCurrent() || generation !== virtualizedProgrammaticNavigationGeneration) {
            resolve({ kind: "canceled" });
            return;
          }
          const root = getDocumentScrollRoot();
          let context = readVirtualizedProgrammaticNavigationTargetContext(input.descriptor);
          if (context === null) {
            resolve({ kind: "missing-target" });
            return;
          }
          adoptVirtualizedProgrammaticNavigationRenderedHeights(context, operation);
          context = readVirtualizedProgrammaticNavigationTargetContext(input.descriptor);
          if (context === null) {
            resolve({ kind: "missing-target" });
            return;
          }
          const plane = scrollOwnershipControlPlane;
          if (plane === null || !plane.holds(operation.lease, settlement.payload.geometryEpoch)) {
            resolve({ kind: "geometry-changed" });
            return;
          }
          const residual = readVirtualizedProgrammaticNavigationResidual(
            context,
            input.descriptor,
            input.viewportOffsetY
          );
          if (residual === null) {
            resolve({ kind: "missing-target" });
            return;
          }
          postPerfMark("mm-virt-residual-read", {
            currentGeometryEpoch: plane.captureGeometryEpoch(),
            descriptorKind: input.descriptor.kind,
            eventGeometryEpoch: settlement.payload.geometryEpoch,
            operationEpoch: operation.operationEpoch,
            residual
          });
          const residualAbs = Math.abs(residual);
          if (residualAbs <= VIRTUALIZED_NAVIGATION_CORRECTION_TOLERANCE_PX) {
            resolve({ kind: "nominal", residual });
            return;
          }
          if (pass >= VIRTUALIZED_NAVIGATION_CORRECTION_MAX_PASSES || pass > 0 && residualAbs >= previousResidualAbs - VIRTUALIZED_NAVIGATION_CORRECTION_MIN_SHRINK_PX) {
            resolve({ kind: "non-converged", residual });
            return;
          }
          writeVirtualizedProgrammaticNavigationScrollTop(
            operation,
            root.scrollTop + residual,
            "navigation-residual"
          );
          const receipt = virtualizedWriteReceipts.get(operation.operationEpoch);
          if (receipt === void 0) {
            resolve({ kind: "canceled" });
            return;
          }
          resolve({ kind: "written", receipt, residual });
        });
        if (!scheduled) {
          window.requestAnimationFrame(attempt);
        }
      };
      attempt();
    });
  }
  async function settleVirtualizedProgrammaticNavigationTarget(input, generation, operation) {
    let afterEmission = 0;
    let pass = 0;
    let previousResidualAbs = Number.POSITIVE_INFINITY;
    let settlement = null;
    while (generation === virtualizedProgrammaticNavigationGeneration && operation.isCurrent()) {
      if (settlement === null) {
        const outcome = await waitForCurrentVirtualizedGeometry(operation, afterEmission);
        if (outcome.status === "canceled") {
          return;
        }
        settlement = outcome;
      }
      const frame = await scheduleVirtualizedProgrammaticNavigationFrame(
        input,
        generation,
        operation,
        settlement,
        pass,
        previousResidualAbs
      );
      if (frame.kind === "canceled") {
        return;
      }
      if (frame.kind === "missing-target") {
        releaseVirtualizedProgrammaticNavigationOperation(operation, true);
        return;
      }
      if (frame.kind === "geometry-changed") {
        afterEmission = settlement.emission;
        settlement = null;
        continue;
      }
      if (frame.kind === "non-converged") {
        postPerfMark("mm-virt-navigation-failed", {
          descriptorKind: input.descriptor.kind,
          geometryEpoch: settlement.payload.geometryEpoch,
          passCount: pass,
          reason: "residual-non-converged",
          residual: frame.residual
        });
        releaseVirtualizedProgrammaticNavigationOperation(operation, true);
        return;
      }
      if (frame.kind === "written") {
        const write = await frame.receipt.result;
        if (write.status !== "committed") {
          return;
        }
        previousResidualAbs = Math.abs(frame.residual);
        pass++;
        afterEmission = settlement.emission;
        settlement = null;
        continue;
      }
      const confirmation = await awaitConfirmedVirtualizedGeometry(operation, settlement);
      if (confirmation.status === "canceled") {
        return;
      }
      if (confirmation.status === "changed") {
        settlement = confirmation.settlement;
        continue;
      }
      const plane = scrollOwnershipControlPlane;
      if (!operation.isCurrent() || generation !== virtualizedProgrammaticNavigationGeneration) {
        return;
      }
      if (plane?.holds(operation.lease, confirmation.confirmation.payload.geometryEpoch) !== true) {
        afterEmission = confirmation.confirmation.emission;
        settlement = null;
        continue;
      }
      finishVirtualizedProgrammaticNavigationCorrection(generation, operation, {
        descriptor: input.descriptor,
        passCount: pass,
        residual: frame.residual
      });
      return;
    }
  }
  function landVirtualizedProgrammaticNavigation(input) {
    startVirtualizedProgrammaticNavigationSettle({
      descriptor: input.descriptor,
      behavior: input.behavior,
      initialContext: input.context,
      operation: input.operation,
      viewportOffsetY: input.viewportOffsetY
    });
  }
  function tryRestoreVirtualizedReadingAnchor(readingAnchor, fallbackBlockIndex) {
    const anchor = readingAnchor ?? (fallbackBlockIndex !== null && Number.isFinite(fallbackBlockIndex) ? { blockIndex: fallbackBlockIndex, intraOffsetPx: 0 } : null);
    if (!virtualizationEnabled || anchor === null || virtualizedDocumentWindowModel === null || virtualizedDocumentWindowController === null) {
      return false;
    }
    const main = document.querySelector("main.mm-document");
    if (main === null) {
      return false;
    }
    const operation = acquireVirtualizedScrollOperation("cache-restore", "supersede-programmatic");
    if (operation === null) {
      return false;
    }
    const descriptor = { kind: "block", blockIndex: anchor.blockIndex };
    void renderWindowTargetThenAct({
      action: () => {
        operation.requestScrollTop(
          scrollTopForReadingAnchor(virtualizedDocumentWindowModel, anchor) ?? 0,
          "cache-restore-retry"
        );
        return true;
      },
      actionKind: "navigate",
      controller: virtualizedDocumentWindowController,
      descriptor,
      legacyAction: () => {
        operation.requestScrollTop(0, "cache-restore-retry-anchor-missing");
        return true;
      },
      main,
      model: virtualizedDocumentWindowModel,
      operation,
      ownerWindow: window,
      root: getDocumentScrollRoot(),
      virtualizationEnabled
    }).then(() => {
      releaseVirtualizedScrollOperationAfterWrite(operation);
    }).catch((error) => {
      postPerfMark("mm-virt-cache-restore-retry-error", {
        message: error instanceof Error ? error.message : String(error)
      });
      releaseVirtualizedScrollOperationAfterWrite(operation);
    });
    return true;
  }
  function rememberVirtualizedProgrammaticNavigationPostSettleTarget(input) {
    virtualizedProgrammaticNavigationPostSettleTarget = input;
  }
  function clearVirtualizedProgrammaticNavigationPostSettleTarget() {
    virtualizedProgrammaticNavigationPostSettleTarget = null;
  }
  function cancelVirtualizedProgrammaticNavigationState() {
    virtualizedProgrammaticNavigationInProgress = false;
    virtualizedProgrammaticNavigationGeneration++;
    virtualizedProgrammaticNavigationOperation = null;
    virtualizedProgrammaticNavigationPostSettleTarget = null;
  }
  function alignVirtualizedProgrammaticNavigationPostSettleTarget() {
    const target = virtualizedProgrammaticNavigationPostSettleTarget;
    const operation = virtualizedProgrammaticNavigationOperation;
    if (target !== null && operation !== null && operation.isCurrent()) {
      correctVirtualizedProgrammaticNavigationResidual({ ...target, operation });
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
    virtualizedProgrammaticNavigationOperation = input.operation;
    virtualizedProgrammaticNavigationExternalShiftCount = 0;
    cancelVirtualizedCalibration();
    rememberVirtualizedProgrammaticNavigationPostSettleTarget(input);
    if (input.behavior === "smooth" && input.initialContext !== void 0) {
      const destinationScrollTop = computeVirtualizedProgrammaticNavigationScrollTop(
        input.initialContext,
        input.descriptor,
        input.viewportOffsetY
      );
      const transitionScheduled = scheduleVirtualizedProgrammaticNavigationSmoothTransition({
        descriptor: input.descriptor,
        destinationScrollTop,
        generation,
        operation: input.operation,
        startScrollTop: getDocumentScrollRoot().scrollTop,
        viewportOffsetY: input.viewportOffsetY
      });
      if (transitionScheduled) {
        return;
      }
      postPerfMark("mm-virt-navigation-smooth-fallback", {
        descriptorKind: input.descriptor.kind,
        reason: "frame-transaction-unavailable"
      });
    }
    if (input.initialContext !== void 0) {
      applyVirtualizedProgrammaticNavigationContext(
        input.initialContext,
        input.descriptor,
        input.viewportOffsetY,
        input.operation
      );
    }
    void settleVirtualizedProgrammaticNavigationTarget(input, generation, input.operation);
  }
  function cancelVirtualizedCalibration() {
    if (virtualizedCalibrationHandle !== null) {
      if (virtualizedCalibrationHandle.kind === "idle") {
        window.cancelIdleCallback?.(virtualizedCalibrationHandle.id);
      } else {
        window.clearTimeout(virtualizedCalibrationHandle.id);
      }
      virtualizedCalibrationHandle = null;
    }
    endVirtualizedGeometryWork(virtualizedCalibrationGeometryTicket);
    virtualizedCalibrationGeometryTicket = null;
  }
  function resetVirtualizedDocumentWindow(resetCalibrator = true) {
    cancelVirtualizedCalibration();
    if (virtualizedDocumentWindowModel !== null) {
      cancelModelRenderedContentCoordinator("stale-model");
    }
    virtualizedWindowMathController?.cancel();
    virtualizedWindowMathController = null;
    virtualizedDocumentWindowController?.dispose();
    virtualizedDocumentWindowController = null;
    virtualizedDocumentWindowModel = null;
    endVirtualizedGeometryWork(virtualizedMeasuredHeightGeometryTicket);
    virtualizedMeasuredHeightGeometryTicket = null;
    finishVirtualizedMeasuredHeightTerminalSubscribers();
    endVirtualizedGeometryWork(virtualizedWindowFontGeometryTicket);
    virtualizedWindowFontGeometryTicket = null;
    virtualizedWindowMountGeneration++;
    virtualizedMeasureFrameRequested = false;
    virtualizedProgrammaticNavigationInProgress = false;
    virtualizedProgrammaticNavigationGeneration++;
    virtualizedProgrammaticNavigationOperation = null;
    virtualizedProgrammaticNavigationExternalShiftCount = 0;
    virtualizedProgrammaticNavigationPostSettleTarget = null;
    if (resetCalibrator) {
      virtualizedIntrinsicCalibrator.reset();
    }
  }
  function refreshVirtualizedFindHighlights() {
    virtualizedFindProvider?.refreshVisibleHighlights();
  }
  function prepareVirtualizedInsertedContent(root, mountGeneration) {
    const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    renderCodeBlocks(root);
    disconnectMermaidLazyObserver();
    mermaidRenderGeneration++;
    virtualizedWindowMathController?.cancel();
    const mathGeometryTicket = root.querySelector(".math-inline, .math-display") === null ? null : beginVirtualizedGeometryWork("window-math", mountGeneration);
    const mathController = renderMath({
      katex: hostWindow.katex ?? void 0,
      documentRoot: root
    });
    virtualizedWindowMathController = mathController;
    const scheduleAfterRichContent = () => {
      if (virtualizedWindowMathController !== mathController || mathController.isCancelled() || mountGeneration !== virtualizedWindowMountGeneration || documentEpoch === void 0 || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
        return;
      }
      invalidateSourceLineAnchors({
        reassertPendingTarget: virtualizedProgrammaticNavigationPostSettleTarget === null
      });
      refreshVirtualizedFindHighlights();
      scheduleVirtualizedMeasuredHeightAdoption();
    };
    mathController.initialVisibleReady.then(scheduleAfterRichContent, scheduleAfterRichContent);
    const finishMathGeometry = () => {
      scheduleAfterRichContent();
      if (mathGeometryTicket !== null && virtualizedWindowMathController === mathController && !mathController.isCancelled() && mountGeneration === virtualizedWindowMountGeneration && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(mathGeometryTicket.documentEpoch) === true) {
        mutateVirtualizedGeometry(mathGeometryTicket);
        scheduleVirtualizedMeasuredHeightAdoption();
      }
      endVirtualizedGeometryWork(mathGeometryTicket);
    };
    mathController.allMathRendered.then(finishMathGeometry, finishMathGeometry);
    const mermaid = hostWindow.mermaid;
    if (!mermaid) {
      return;
    }
    const mermaidNodes = Array.from(root.querySelectorAll("pre.mm-mermaid"));
    if (mermaidNodes.length === 0) {
      return;
    }
    void renderMermaidNodes(
      mermaidNodes,
      mermaid,
      "mm-mermaid-virt-window",
      "window-mermaid",
      mountGeneration
    ).finally(scheduleAfterRichContent);
  }
  function scheduleVirtualizedWindowFontReadiness(mountGeneration) {
    if (!virtualizationEnabled || mountGeneration !== virtualizedWindowMountGeneration) {
      return;
    }
    endVirtualizedGeometryWork(virtualizedWindowFontGeometryTicket);
    const ticket = beginVirtualizedGeometryWork("window-fonts", mountGeneration);
    virtualizedWindowFontGeometryTicket = ticket;
    const ready = document.fonts?.ready;
    if (ready === void 0) {
      endVirtualizedGeometryWork(ticket);
      if (virtualizedWindowFontGeometryTicket === ticket) {
        virtualizedWindowFontGeometryTicket = null;
      }
      return;
    }
    const finish2 = () => {
      if (virtualizedWindowFontGeometryTicket !== ticket) {
        endVirtualizedGeometryWork(ticket);
        return;
      }
      if (ticket !== null && mountGeneration === virtualizedWindowMountGeneration && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(ticket.documentEpoch) === true) {
        mutateVirtualizedGeometry(ticket);
        scheduleVirtualizedMeasuredHeightAdoption(() => {
          endVirtualizedGeometryWork(ticket);
          if (virtualizedWindowFontGeometryTicket === ticket) {
            virtualizedWindowFontGeometryTicket = null;
          }
        });
        return;
      }
      endVirtualizedGeometryWork(ticket);
      if (virtualizedWindowFontGeometryTicket === ticket) {
        virtualizedWindowFontGeometryTicket = null;
      }
    };
    void ready.then(finish2, finish2);
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
    const documentEpoch = scrollOwnershipControlPlane.captureDocumentEpoch();
    startRenderedFindProjectionForCurrentModel();
    virtualizedDocumentWindowController = createVirtualizedDocumentWindowController({
      beginWindowGeometryWork: (mountGeneration) => {
        const ticket = beginVirtualizedGeometryWork("window-render", mountGeneration);
        return ticket === null ? null : {
          end: () => {
            endVirtualizedGeometryWork(ticket);
          },
          mutated: () => {
            mutateVirtualizedGeometry(ticket);
          }
        };
      },
      documentEpoch,
      isCurrentDocumentEpoch: (epoch) => scrollOwnershipControlPlane?.isCurrentDocumentEpoch(epoch) === true,
      main,
      model: virtualizedDocumentWindowModel,
      ownerWindow: window,
      onRealizationReady: (mountGeneration) => {
        if (mountGeneration === virtualizedWindowMountGeneration) {
          scheduleVirtualizedMeasuredHeightAdoption();
        }
      },
      onWindowMounted: (mountGeneration) => {
        virtualizedWindowMountGeneration = mountGeneration;
        rebuildActiveHeadingObserverFromLiveDocument();
        scheduleVirtualizedWindowFontReadiness(mountGeneration);
      },
      prepareInsertedContent: prepareVirtualizedInsertedContent,
      readMeasuredHeights: readLiveBlockOffsetMeasuredHeights,
      // The delegated contentvisibilityautostatechange listener is installed only
      // by this flag-on controller path.
      realization: { enabled: true },
      root,
      trace: (event) => postPerfMark(event.id, { ...event.details })
    });
    const initialOperation = acquireVirtualizedScrollOperation("initial-window", "supersede-programmatic");
    if (initialOperation !== null) {
      const controller = virtualizedDocumentWindowController;
      pendingInitialVirtualizedWindowWork = (operation) => {
        if (!operation.isCurrent() || controller !== virtualizedDocumentWindowController) {
          return;
        }
        controller.updateWindowForScroll();
        refreshVirtualizedFindHighlights();
        invalidateTopVisibleBlockIndexCache();
        scheduleVirtualizedMeasuredHeightAdoption();
      };
      if (!initialOperation.scheduleFrameTransaction(() => {
        consumePendingInitialVirtualizedWindow(initialOperation);
        releaseVirtualizedScrollOperationAfterWrite(initialOperation);
      })) {
        releaseVirtualizedScrollOperation(initialOperation);
      }
    }
    postPerfMark("mm-virt-window-built", {
      estimateMeanAbsError: models.estimateHeightError.meanAbsError,
      sectionCount: virtualizedDocumentWindowModel.getSectionCount(),
      totalHeight: virtualizedDocumentWindowModel.getTotalHeight()
    });
  }
  function updateVirtualizedWindowForScroll(options = {}) {
    if (!virtualizationEnabled || virtualizedDocumentWindowController === null) {
      return;
    }
    if (options.force !== true && isVirtualizedProgrammaticNavigationInProgress()) {
      return;
    }
    const controller = virtualizedDocumentWindowController;
    scheduleVirtualizedMaintenance("scroll-window", (operation) => {
      if (controller !== virtualizedDocumentWindowController) {
        return;
      }
      if (controller.updateWindowForScroll({ ...options, operation })) {
        invalidateTopVisibleBlockIndexCache();
        invalidateSourceLineAnchors({
          reassertPendingTarget: virtualizedProgrammaticNavigationPostSettleTarget === null
        });
        refreshVirtualizedFindHighlights();
        scheduleVirtualizedMeasuredHeightAdoption();
      }
    });
  }
  function finishVirtualizedMeasuredHeightTerminalSubscribers() {
    const subscribers = [...virtualizedMeasuredHeightTerminalSubscribers];
    virtualizedMeasuredHeightTerminalSubscribers.clear();
    for (const subscriber of subscribers) {
      subscriber();
    }
  }
  function scheduleVirtualizedMeasuredHeightAdoption(onTerminal) {
    if (!virtualizationEnabled || virtualizedDocumentWindowController === null) {
      onTerminal?.();
      return;
    }
    if (onTerminal !== void 0) {
      virtualizedMeasuredHeightTerminalSubscribers.add(onTerminal);
    }
    if (virtualizedMeasureFrameRequested) {
      return;
    }
    virtualizedMeasureFrameRequested = true;
    const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    endVirtualizedGeometryWork(virtualizedMeasuredHeightGeometryTicket);
    const ticket = beginVirtualizedGeometryWork("measured-height-adoption");
    virtualizedMeasuredHeightGeometryTicket = ticket;
    window.requestAnimationFrame(() => {
      virtualizedMeasureFrameRequested = false;
      if (documentEpoch === void 0 || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
        endVirtualizedGeometryWork(ticket);
        if (virtualizedMeasuredHeightGeometryTicket === ticket) {
          virtualizedMeasuredHeightGeometryTicket = null;
        }
        finishVirtualizedMeasuredHeightTerminalSubscribers();
        return;
      }
      adoptVirtualizedRenderedHeights(ticket);
    });
  }
  function adoptVirtualizedRenderedHeights(ticket) {
    if (!virtualizationEnabled || virtualizedDocumentWindowController === null) {
      endVirtualizedGeometryWork(ticket);
      if (virtualizedMeasuredHeightGeometryTicket === ticket) {
        virtualizedMeasuredHeightGeometryTicket = null;
      }
      finishVirtualizedMeasuredHeightTerminalSubscribers();
      return;
    }
    const controller = virtualizedDocumentWindowController;
    const closeTicket = (terminal) => {
      endVirtualizedGeometryWork(ticket);
      if (virtualizedMeasuredHeightGeometryTicket === ticket) {
        virtualizedMeasuredHeightGeometryTicket = null;
      }
      if (terminal?.reason !== "coalesced") {
        finishVirtualizedMeasuredHeightTerminalSubscribers();
      }
    };
    const scheduled = scheduleVirtualizedMaintenance("measured-height-adoption", (operation) => {
      if (controller !== virtualizedDocumentWindowController) {
        return;
      }
      const postSettleTarget = virtualizedProgrammaticNavigationPostSettleTarget;
      const preserveSectionIndex = getVirtualizedProgrammaticNavigationPostSettleSectionIndex();
      const result = controller.adoptRenderedHeights(
        preserveSectionIndex === null ? { operation } : {
          operation,
          preserveSectionIndex,
          ...postSettleTarget === null ? {} : { reanchor: false }
        }
      );
      if (hasMeasuredHeightGeometryDelta(result)) {
        mutateVirtualizedGeometry(ticket);
      }
      applyVirtualizedRenderedHeightAdoptionEffects(result, {
        alignPostSettleTarget: postSettleTarget === null
      });
      if (postSettleTarget !== null && operation.isCurrent()) {
        correctVirtualizedProgrammaticNavigationResidual({
          ...postSettleTarget,
          operation
        });
      }
    }, closeTicket);
    if (!scheduled) {
      closeTicket();
    }
  }
  function scheduleVirtualizedCalibration() {
    if (!virtualizationEnabled || virtualizedDocumentWindowModel === null) {
      return;
    }
    if (virtualizedCalibrationGeometryTicket !== null) {
      return;
    }
    const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    const ticket = beginVirtualizedGeometryWork("calibration");
    virtualizedCalibrationGeometryTicket = ticket;
    if (documentEpoch === void 0 || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
      endVirtualizedGeometryWork(ticket);
      virtualizedCalibrationGeometryTicket = null;
      return;
    }
    runVirtualizedCalibration(documentEpoch, ticket);
  }
  function runVirtualizedCalibration(documentEpoch, ticket) {
    const model = virtualizedDocumentWindowModel;
    const controller = virtualizedDocumentWindowController;
    if (!virtualizationEnabled || model === null || controller === null) {
      endVirtualizedGeometryWork(ticket);
      if (virtualizedCalibrationGeometryTicket === ticket) {
        virtualizedCalibrationGeometryTicket = null;
      }
      return;
    }
    const closeTicket = () => {
      endVirtualizedGeometryWork(ticket);
      if (virtualizedCalibrationGeometryTicket === ticket) {
        virtualizedCalibrationGeometryTicket = null;
      }
    };
    const scheduled = scheduleVirtualizedMaintenance("calibration", (operation) => {
      if (!operation.isCurrent() || operation.documentEpoch !== documentEpoch || model !== virtualizedDocumentWindowModel || controller !== virtualizedDocumentWindowController) {
        return;
      }
      const preserveSectionIndex = getVirtualizedProgrammaticNavigationPostSettleSectionIndex();
      const postSettleTarget = virtualizedProgrammaticNavigationPostSettleTarget;
      const anchor = preserveSectionIndex === null ? captureCurrentVirtualizedReadingAnchor() : null;
      const recordedCount = model.recordIntrinsicSizeCalibrationSamples(virtualizedIntrinsicCalibrator);
      if (recordedCount === 0) {
        return;
      }
      const result = model.updateEstimatedHeightsFromCalibration(virtualizedIntrinsicCalibrator);
      if (!hasMeasuredHeightGeometryDelta(result)) {
        return;
      }
      mutateVirtualizedGeometry(ticket);
      const target = preserveSectionIndex !== null ? model.sectionTop(preserveSectionIndex) : scrollTopForReadingAnchor(model, anchor) ?? 0;
      controller.updateWindowForScroll({ desiredScrollTop: target, force: true });
      if (postSettleTarget === null) {
        operation.requestScrollTop(target, "calibration");
      } else {
        correctVirtualizedProgrammaticNavigationResidual({
          ...postSettleTarget,
          operation
        });
      }
      invalidateTopVisibleBlockIndexCache();
      invalidateSourceLineAnchors({ reassertPendingTarget: postSettleTarget === null });
      postPerfMark("mm-virt-window-calibrated", {
        maxAbsDelta: result.maxAbsDelta,
        recordedCount,
        totalDelta: result.totalDelta,
        updatedCount: result.updatedCount
      });
    }, closeTicket);
    if (!scheduled) {
      closeTicket();
    }
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
    const main = getLiveDocumentRoot();
    sourceLineAnchors = main === null ? [] : readSourceLineAnchors(main);
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
  function scheduleVirtualizedSourceLineLanding(operation, sourceLine) {
    if (sourceLineAnchors.length === 0) {
      refreshSourceLineAnchors();
    }
    const scrollTop = findScrollTopForSourceLine(sourceLineAnchors, sourceLine);
    if (scrollTop === null) {
      releaseVirtualizedScrollOperation(operation);
      return false;
    }
    pendingSourceLineTarget = sourceLine;
    suppressPreviewSourceLinePost();
    return scheduleExistingVirtualizedOperation(operation, () => {
      operation.requestScrollTop(
        Math.max(0, scrollTop - getViewportAnchorY()),
        "source-line-live-fallback"
      );
    }, true);
  }
  function scrollToSourceLine(sourceLine) {
    if (!Number.isFinite(sourceLine) || sourceLine < 0) {
      return;
    }
    if (!virtualizationEnabled) {
      scrollToSourceLineInCurrentWindow(sourceLine);
      return;
    }
    const operation = acquireVirtualizedScrollOperation("source-line-navigation", "supersede-programmatic");
    if (operation === null) {
      return;
    }
    const main = document.querySelector("main.mm-document");
    if (main === null || virtualizedDocumentWindowModel === null || virtualizedDocumentWindowController === null) {
      scheduleVirtualizedSourceLineLanding(operation, sourceLine);
      return;
    }
    void renderWindowTargetThenAct({
      action: (context) => {
        pendingSourceLineTarget = sourceLine;
        suppressPreviewSourceLinePost();
        landVirtualizedProgrammaticNavigation({
          context,
          descriptor: { kind: "source-line", sourceLine },
          operation,
          viewportOffsetY: getViewportAnchorY()
        });
        return true;
      },
      actionKind: "navigate",
      controller: virtualizedDocumentWindowController,
      descriptor: { kind: "source-line", sourceLine },
      legacyAction: () => scheduleVirtualizedSourceLineLanding(operation, sourceLine),
      main,
      model: virtualizedDocumentWindowModel,
      operation,
      ownerWindow: window,
      root: getDocumentScrollRoot(),
      virtualizationEnabled: true
    });
  }
  function invalidateSourceLineAnchors(options = {}) {
    sourceLineAnchors = [];
    if (pendingSourceLineTarget !== null && options.reassertPendingTarget !== false) {
      const target = pendingSourceLineTarget;
      if (virtualizationEnabled) {
        if (isVirtualizedProgrammaticNavigationInProgress()) {
          return;
        }
        const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
        window.requestAnimationFrame(() => {
          if (pendingSourceLineTarget !== target || documentEpoch === void 0 || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
            return;
          }
          scrollToSourceLine(target);
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
    const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    suppressPreviewSourceLineEmit = true;
    window.requestAnimationFrame(() => {
      window.requestAnimationFrame(() => {
        if (sequence === suppressPreviewSourceLineSequence && (documentEpoch === void 0 || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) === true)) {
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
    const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    window.requestAnimationFrame(() => {
      previewSourceLineFrameRequested = false;
      if (documentEpoch !== void 0 && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
        return;
      }
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
    const layoutState = cachedLayoutState !== null ? virtualizationEnabled ? { ...getScrollState(), topBlockIndex: findTopVisibleBlockIndex() } : { ...cachedLayoutState } : { ...getScrollState(), topBlockIndex: findTopVisibleBlockIndex() };
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
    if (!virtualizationEnabled && cachedLayoutState !== null) {
      queueCachedGeometryRefresh(
        cachedLayoutState.readingAnchor ?? null,
        cachedLayoutState.topBlockIndex
      );
    }
  }
  function queueCachedGeometryRefresh(readingAnchor, topBlockIndex) {
    const cacheKey = currentDocumentCacheKey;
    const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    window.clearTimeout(cachedGeometryRefreshTimer);
    cachedGeometryRefreshTimer = window.setTimeout(() => {
      cachedGeometryRefreshTimer = void 0;
      if (cacheKey !== currentDocumentCacheKey || documentEpoch !== void 0 && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
        return;
      }
      if (tryRestoreVirtualizedReadingAnchor(readingAnchor, topBlockIndex)) {
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
    const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    const workItems = postLayoutReadyWorkQueue.filter((item) => item.generation === flushGeneration);
    postLayoutReadyWorkQueue = postLayoutReadyWorkQueue.filter((item) => item.generation !== flushGeneration);
    const delayMs = viewerChromeEnabled ? 0 : POST_LAYOUT_READY_EDIT_PREVIEW_DELAY_MS;
    if (delayMs > 0) {
      postPerfMark("post-ready-enhancements-deferred", { delayMs, viewerChromeEnabled });
    }
    window.setTimeout(() => {
      if (flushGeneration !== layoutReadyGeneration || documentEpoch !== void 0 && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
        return;
      }
      for (const item of workItems) {
        item.work();
      }
    }, delayMs);
  }
  function restoreCachedScrollPosition() {
    const layoutState = restoredCachedLayoutState ?? lastKnownLayoutState;
    if (!virtualizationEnabled) {
      window.scrollTo({
        left: 0,
        top: layoutState.scrollTop,
        behavior: "instant"
      });
      updateVirtualizedWindowForScroll({ force: true });
      return;
    }
    const operation = acquireVirtualizedScrollOperation("cache-restore", "supersede-programmatic");
    if (operation === null) {
      postPerfMark("mm-virt-cache-restore-terminal", {
        reason: "lease-unavailable",
        status: "canceled"
      });
      cachedScrollRestoreCompletion = Promise.resolve();
      return;
    }
    const documentEpoch = operation.documentEpoch;
    cachedScrollRestoreCompletion = new Promise((resolve) => {
      let completed = false;
      const semanticAnchorReadyReason = "semantic-anchor-agreed";
      const publishCachedRestoreReady = (reason, geometryStatus) => {
        const liveGeometry = getScrollState();
        postPerfMark("mm-virt-cache-restore-ready-terminal", {
          documentEpoch,
          geometryStatus,
          reason,
          scrollHeight: liveGeometry.scrollHeight,
          scrollTop: liveGeometry.scrollTop,
          topBlockIndex: findTopVisibleBlockIndex()
        });
      };
      const finish2 = (status, reason, geometryStatus = status === "committed" ? "settled" : status) => {
        if (completed) {
          return;
        }
        completed = true;
        if (finishCachedScrollRestore === finish2) {
          finishCachedScrollRestore = null;
        }
        if (operation.isCurrent()) {
          releaseVirtualizedScrollOperation(operation);
        }
        const plane = scrollOwnershipControlPlane;
        if (plane?.isCurrentDocumentEpoch(documentEpoch) === true) {
          publishCachedRestoreReady(reason, geometryStatus);
        }
        postPerfMark("mm-virt-cache-restore-terminal", {
          documentEpoch,
          geometryStatus,
          reason,
          status
        });
        resolve();
      };
      finishCachedScrollRestore = finish2;
      const scheduleFrameWork = (work) => new Promise((completedWork) => {
        const attempt = () => {
          const plane = scrollOwnershipControlPlane;
          if (plane === null || !plane.isCurrentDocumentEpoch(documentEpoch)) {
            finish2("canceled", "stale-document");
            completedWork(false);
            return;
          }
          if (!operation.isCurrent()) {
            finish2("canceled", "user-supersession", "canceled");
            completedWork(false);
            return;
          }
          const scheduled = operation.scheduleFrameTransaction(() => {
            if (!operation.isCurrent() || !plane.isCurrentDocumentEpoch(documentEpoch)) {
              finish2("canceled", "stale-document");
              completedWork(false);
              return;
            }
            try {
              work();
              completedWork(true);
            } catch {
              finish2("failed", "frame-work-failed", "failed");
              completedWork(false);
            }
          });
          if (!scheduled) {
            window.requestAnimationFrame(attempt);
          }
        };
        attempt();
      });
      const scheduleWrite = (target, writer) => new Promise((completedWrite) => {
        const attempt = () => {
          const plane = scrollOwnershipControlPlane;
          if (plane === null || !plane.isCurrentDocumentEpoch(documentEpoch)) {
            finish2("canceled", "stale-document");
            completedWrite(null);
            return;
          }
          if (!operation.isCurrent()) {
            finish2("canceled", "user-supersession", "canceled");
            completedWrite(null);
            return;
          }
          const scheduled = operation.scheduleFrameTransaction(() => {
            if (!operation.isCurrent() || !plane.isCurrentDocumentEpoch(documentEpoch)) {
              finish2("canceled", "stale-document");
              completedWrite(null);
              return;
            }
            try {
              operation.requestScrollTop(target, writer);
            } catch {
              finish2("failed", "frame-work-failed", "failed");
              completedWrite(null);
              return;
            }
            completedWrite(virtualizedWriteReceipts.get(operation.operationEpoch) ?? null);
          });
          if (!scheduled) {
            window.requestAnimationFrame(attempt);
          }
        };
        attempt();
      });
      void (async () => {
        const anchor = layoutState.readingAnchor ?? null;
        let model = virtualizedDocumentWindowModel;
        let controller = virtualizedDocumentWindowController;
        let entry = anchor === null ? void 0 : model?.getEntryByBlockIndex(anchor.blockIndex);
        let target = model !== null && controller !== null && entry !== void 0 ? scrollTopForReadingAnchor(model, anchor) : null;
        const prepared = await scheduleFrameWork(() => {
          consumePendingInitialVirtualizedWindow(operation);
          if (target !== null && entry !== void 0) {
            controller?.ensureSectionRendered(entry.sectionIndex, {
              force: true,
              operation,
              preserveAnchor: false
            });
          }
        });
        if (!prepared || completed) {
          return;
        }
        const firstSettlement = await waitForCurrentVirtualizedGeometry(operation, 0);
        if (firstSettlement.status === "canceled") {
          const plane = scrollOwnershipControlPlane;
          if (firstSettlement.reason === "stale-document" || plane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
            finish2("canceled", "stale-document");
          } else if (firstSettlement.reason === "non-converged") {
            finish2("failed", "non-converged", "non-converged");
          } else {
            finish2("canceled", "user-supersession", "canceled");
          }
          return;
        }
        model = virtualizedDocumentWindowModel;
        controller = virtualizedDocumentWindowController;
        entry = anchor === null ? void 0 : model?.getEntryByBlockIndex(anchor.blockIndex);
        target = model !== null && controller !== null && entry !== void 0 ? scrollTopForReadingAnchor(model, anchor) : null;
        const coldTop = target === null;
        const initialReceipt = await scheduleWrite(
          target ?? 0,
          coldTop ? "cache-cold-top" : "cache-restore"
        );
        if (initialReceipt === null || completed) {
          return;
        }
        const initialWrite = await initialReceipt.result;
        if (initialWrite.status !== "committed") {
          finish2("failed", initialWrite.reason, "failed");
          return;
        }
        let afterEmission = firstSettlement.emission;
        let settlement = null;
        while (!completed) {
          const plane = scrollOwnershipControlPlane;
          if (plane === null || !plane.isCurrentDocumentEpoch(documentEpoch)) {
            finish2("canceled", "stale-document");
            return;
          }
          if (!operation.isCurrent()) {
            finish2("canceled", "user-supersession", "canceled");
            return;
          }
          if (settlement === null) {
            const outcome = await waitForCurrentVirtualizedGeometry(operation, afterEmission);
            if (outcome.status === "canceled") {
              if (outcome.reason === "stale-document" || !plane.isCurrentDocumentEpoch(documentEpoch)) {
                finish2("canceled", "stale-document");
              } else if (outcome.reason === "non-converged") {
                finish2("failed", "non-converged", "non-converged");
              } else {
                finish2("canceled", "user-supersession", "canceled");
              }
              return;
            }
            settlement = outcome;
          }
          model = virtualizedDocumentWindowModel;
          controller = virtualizedDocumentWindowController;
          entry = anchor === null ? void 0 : model?.getEntryByBlockIndex(anchor.blockIndex);
          target = model !== null && controller !== null && entry !== void 0 ? scrollTopForReadingAnchor(model, anchor) : null;
          const expectedTarget = target ?? 0;
          if (Math.abs(getDocumentScrollRoot().scrollTop - expectedTarget) > VIRTUALIZED_NAVIGATION_CORRECTION_TOLERANCE_PX) {
            const correctionReceipt = await scheduleWrite(
              expectedTarget,
              target === null ? "cache-cold-top" : "cache-restore-correction"
            );
            if (correctionReceipt === null || completed) {
              return;
            }
            const correction = await correctionReceipt.result;
            if (correction.status !== "committed") {
              finish2("failed", correction.reason, "failed");
              return;
            }
            afterEmission = settlement.emission;
            settlement = null;
            continue;
          }
          const confirmation = await awaitConfirmedVirtualizedGeometry(operation, settlement);
          if (confirmation.status === "canceled") {
            if (confirmation.reason === "stale-document" || !plane.isCurrentDocumentEpoch(documentEpoch)) {
              finish2("canceled", "stale-document");
            } else if (confirmation.reason === "non-converged") {
              finish2("failed", "non-converged", "non-converged");
            } else {
              finish2("canceled", "user-supersession", "canceled");
            }
            return;
          }
          if (confirmation.status === "changed") {
            settlement = confirmation.settlement;
            continue;
          }
          if (!plane.holds(operation.lease, confirmation.confirmation.payload.geometryEpoch)) {
            afterEmission = confirmation.confirmation.emission;
            settlement = null;
            continue;
          }
          finish2("committed", target === null ? "cold-top-agreed" : semanticAnchorReadyReason, "settled");
          return;
        }
      })().catch(() => {
        finish2("failed", "restore-pipeline-failed", "failed");
      });
    });
  }
  function scheduleLayoutReady(skipFrameWait = false) {
    const generation = ++layoutReadyGeneration;
    const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    const isCurrentLayoutDocument = () => documentEpoch === void 0 || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) === true;
    const scheduledRenderId = currentDocumentRenderId;
    let completed = false;
    let posted = false;
    let frameFallbackTimer;
    if (layoutReadyTimer !== void 0) {
      window.clearTimeout(layoutReadyTimer);
    }
    const post = (path) => {
      if (posted || generation !== layoutReadyGeneration || !isCurrentLayoutDocument()) {
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
      if (completed || generation !== layoutReadyGeneration || !isCurrentLayoutDocument()) {
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
          if (generation === layoutReadyGeneration && isCurrentLayoutDocument()) {
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
  var minimapCloneMetadata = /* @__PURE__ */ new WeakMap();
  var minimapCloneBlockIndexes = /* @__PURE__ */ new WeakMap();
  function parseMinimapBlockIndex(value) {
    if (value === void 0 || value.trim() === "") {
      return null;
    }
    const parsed = Number.parseInt(value, 10);
    return Number.isFinite(parsed) && parsed >= 0 ? parsed : null;
  }
  function readPositivePxValue(value) {
    let parsed = null;
    for (const match of value.matchAll(/([0-9]+(?:\.[0-9]+)?)px/g)) {
      const next = Number.parseFloat(match[1] ?? "");
      if (Number.isFinite(next) && next > 0) {
        parsed = next;
      }
    }
    return parsed;
  }
  function readMinimapSourceBlockHeight(element) {
    if (element.isConnected && element.offsetHeight > 0) {
      return element.offsetHeight;
    }
    return readPositivePxValue(element.style.minHeight) ?? readPositivePxValue(element.style.height) ?? readPositivePxValue(element.style.containIntrinsicSize);
  }
  function buildTopLevelMinimapBlockMetrics(source, clone) {
    const metrics = /* @__PURE__ */ new Map();
    const sourceChildren = Array.from(source.children);
    const cloneChildren = Array.from(clone.children);
    let top = 0;
    for (let index = 0; index < sourceChildren.length; index++) {
      const sourceChild = sourceChildren[index];
      const cloneChild = cloneChildren[index];
      if (!(sourceChild instanceof HTMLElement) || !(cloneChild instanceof HTMLElement)) {
        continue;
      }
      const height = readMinimapSourceBlockHeight(sourceChild);
      if (height === null) {
        continue;
      }
      metrics.set(cloneChild, { height, top });
      top += height;
    }
    return metrics;
  }
  function registerMinimapCloneMetadata(source, clone) {
    const sourceBlocks = Array.from(source.querySelectorAll("[data-mm-block-index]"));
    const cloneBlocks = Array.from(clone.querySelectorAll("[data-mm-block-index]"));
    const topLevelMetrics = buildTopLevelMinimapBlockMetrics(source, clone);
    const blocks = [];
    const blocksByIndex = /* @__PURE__ */ new Map();
    for (let index = 0; index < cloneBlocks.length; index++) {
      const cloneBlock = cloneBlocks[index];
      const sourceBlock = sourceBlocks[index];
      const blockIndex = parseMinimapBlockIndex(cloneBlock.dataset["mmBlockIndex"]);
      if (blockIndex === null) {
        continue;
      }
      const topLevelMetric = topLevelMetrics.get(cloneBlock);
      const record = {
        blockIndex,
        element: cloneBlock,
        height: topLevelMetric?.height ?? (sourceBlock ? readMinimapSourceBlockHeight(sourceBlock) : null),
        top: topLevelMetric?.top ?? null
      };
      minimapCloneBlockIndexes.set(cloneBlock, blockIndex);
      blocks.push(record);
      if (!blocksByIndex.has(blockIndex)) {
        blocksByIndex.set(blockIndex, record);
      }
    }
    minimapCloneMetadata.set(clone, { blocks, blocksByIndex });
  }
  function getMinimapCloneBlockIndex(block) {
    return minimapCloneBlockIndexes.get(block) ?? parseMinimapBlockIndex(block.dataset["mmBlockIndex"]);
  }
  function getMinimapCloneBlockRecord(clone, block) {
    const metadata = minimapCloneMetadata.get(clone);
    if (!metadata) {
      return null;
    }
    const blockIndex = getMinimapCloneBlockIndex(block);
    return blockIndex === null ? null : metadata.blocksByIndex.get(blockIndex) ?? null;
  }
  function findMinimapCloneBlock(clone, blockIndex) {
    const parsed = parseMinimapBlockIndex(blockIndex);
    if (parsed === null) {
      return null;
    }
    return minimapCloneMetadata.get(clone)?.blocksByIndex.get(parsed)?.element ?? clone.querySelector(`[data-mm-block-index="${blockIndex}"]`);
  }
  function getMinimapCloneBlocks(clone) {
    return minimapCloneMetadata.get(clone)?.blocks ?? Array.from(clone.querySelectorAll("[data-mm-block-index]")).flatMap((element) => {
      const blockIndex = getMinimapCloneBlockIndex(element);
      return blockIndex === null ? [] : [{ blockIndex, element, height: null, top: null }];
    });
  }
  function sanitizeMinimapCloneTree(root) {
    const nodes = [
      ...root instanceof Element ? [root] : [],
      ...Array.from(root.querySelectorAll("*"))
    ];
    nodes.forEach((node) => {
      const isHtml = node.namespaceURI === "http://www.w3.org/1999/xhtml" || node.namespaceURI === null;
      if (isHtml && node.hasAttribute("id")) node.removeAttribute("id");
      if (node.hasAttribute("data-tex")) node.removeAttribute("data-tex");
      for (const attribute of Array.from(node.attributes)) {
        if (attribute.name.startsWith("data-mm-")) {
          node.removeAttribute(attribute.name);
        }
      }
      const tag = node.tagName;
      if (tag === "A" || tag === "BUTTON" || tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") {
        node.setAttribute("tabindex", "-1");
        node.removeAttribute("href");
      }
    });
  }
  function applyMinimapClonePaintHeightLimit(clone, documentHeight) {
    const maximumPaintHeight = minimapPolicy?.maxDetailedDocumentHeight;
    if (maximumPaintHeight === void 0 || documentHeight <= maximumPaintHeight) {
      return false;
    }
    const nextMaximumHeight = `${maximumPaintHeight}px`;
    if (clone.style.maxHeight === nextMaximumHeight && clone.style.overflowY === "hidden" && clone.style.contain.includes("paint")) {
      return false;
    }
    clone.style.maxHeight = nextMaximumHeight;
    clone.style.overflowY = "hidden";
    clone.style.contain = "paint";
    return true;
  }
  function cloneDocumentElementForMinimap(source, sourceStyle, documentHeight) {
    const clone = source.cloneNode(true);
    minimapSourceReady = true;
    clone.removeAttribute("id");
    clone.setAttribute("aria-hidden", "true");
    clone.inert = true;
    clone.style.paddingTop = sourceStyle.paddingTop;
    clone.style.paddingRight = "0";
    clone.style.paddingBottom = sourceStyle.paddingBottom;
    clone.style.paddingLeft = "0";
    applyMinimapClonePaintHeightLimit(clone, documentHeight);
    registerMinimapCloneMetadata(source, clone);
    sanitizeMinimapCloneTree(clone);
    return clone;
  }
  function cloneDocumentForMinimap(documentHeight) {
    const source = document.querySelector(".mm-document");
    if (!source) {
      minimapSourceReady = false;
      return null;
    }
    return cloneDocumentElementForMinimap(source, getComputedStyle(source), documentHeight);
  }
  function cloneModelDocumentForMinimap(model, documentHeight) {
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
    const clone = cloneDocumentElementForMinimap(source, getComputedStyle(liveSource), documentHeight);
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
      releaseMinimapRenderedContentLease();
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
    if (model !== null && model.getRenderedContentState() === "unprepared") {
      const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
      if (minimapRenderedContentLease !== null && minimapRenderedContentLease.model === model && minimapRenderedContentLease.documentEpoch === documentEpoch) {
        return;
      }
      releaseMinimapRenderedContentLease();
      const lease = acquireCurrentModelRenderedContentLease("minimap-detail");
      if (lease === null) {
        emitMark("mm-minimap-refresh-end", { phase, skipped: "model-rendered-content-unavailable" });
        postPerfMark("mm-minimap-refresh-end", { phase, skipped: "model-rendered-content-unavailable" });
        return;
      }
      minimapRenderedContentLease = lease;
      void lease.readiness.then((status) => {
        if (minimapRenderedContentLease === lease) {
          minimapRenderedContentLease = null;
          lease.release();
        }
        if (!isTerminalModelRenderedContentStatus(status) || getModelMinimapSource() !== model || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(lease.documentEpoch) !== true || !shouldBuildDetailedMinimapContent().allowed) {
          return;
        }
        refreshMinimapContent(phase);
      });
      return;
    }
    const clone = model === null ? cloneDocumentForMinimap(buildDecision.documentHeight) : cloneModelDocumentForMinimap(model, buildDecision.documentHeight);
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
    if (minimapSourceReady) {
      return;
    }
    if (!shouldBuildDetailedMinimapContent().allowed) {
      releaseMinimapRenderedContentLease();
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
    const restoredClone = minimapContent?.firstElementChild;
    if (restoredClone instanceof HTMLElement) {
      applyMinimapClonePaintHeightLimit(restoredClone, restored.documentHeight);
    }
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
  function rebuildActiveHeadingObserverFromLiveDocument() {
    const main = document.querySelector("main.mm-document");
    const nodes = main === null ? [] : readLiveHeadingNodes(main).filter((node) => !!node.id);
    rebuildActiveHeadingObserver(nodes);
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
    const observer = new IntersectionObserver(callback, {
      rootMargin: "0px 0px -85% 0px",
      threshold: [0, 1]
    });
    activeHeadingObserver = observer;
    for (const node of headingNodes) {
      observer.observe(node);
    }
    const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    window.requestAnimationFrame(() => {
      if (activeHeadingObserver !== observer) {
        return;
      }
      if (documentEpoch !== void 0 && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
        return;
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
    const recordTop = getMinimapCloneBlockRecord(container, el)?.top;
    if (recordTop !== void 0 && recordTop !== null) {
      return recordTop;
    }
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
    const cln = findMinimapCloneBlock(clone, idx);
    if (!cln) return null;
    const top = cloneSpaceTop(cln, clone);
    if (top === null) return null;
    const cloneHeight = getMinimapCloneBlockRecord(clone, cln)?.height ?? cln.offsetHeight;
    const offset = clientY - rect.top;
    const contribution = offset <= 0 ? offset : rect.height > 0 ? offset / rect.height * cloneHeight : 0;
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
    for (const record of getMinimapCloneBlocks(clone)) {
      const b = record.element;
      const top = record.top ?? cloneSpaceTop(b, clone);
      if (top === null) continue;
      const h = record.height ?? b.offsetHeight;
      if (y < top) return { block: b, blockIndex: record.blockIndex, mode: "gap", value: y - top };
      if (y < top + h) return { block: b, blockIndex: record.blockIndex, mode: "frac", value: h > 0 ? (y - top) / h : 0 };
      prev = record;
      prevTop = top;
    }
    if (prev) return {
      block: prev.element,
      blockIndex: prev.blockIndex,
      mode: "tail",
      value: y - (prevTop + (prev.height ?? prev.element.offsetHeight))
    };
    return null;
  }
  function docScrollTopForCloneY(root, y) {
    if (!minimapContent) return null;
    const hit = cloneBlockAtCloneY(minimapContent, y);
    if (!hit) return null;
    const idx = String(hit.blockIndex);
    const blockIndex = hit.blockIndex;
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
    let measuredContentHeight = minimapContent.scrollHeight;
    const renderedClone = minimapContent.firstElementChild;
    if (renderedClone instanceof HTMLElement && applyMinimapClonePaintHeightLimit(renderedClone, measuredContentHeight)) {
      measuredContentHeight = minimapContent.scrollHeight;
    }
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
        if (!virtualizationEnabled) {
          window.scrollTo({ top: firstTarget, behavior: "instant" });
          let attempts = 0;
          const refine = () => {
            if (++attempts > 3) {
              return;
            }
            const next = docScrollTopForCloneY(root, cloneYTarget);
            if (next !== null && Math.abs(next - root.scrollTop) > 2) {
              window.scrollTo({ top: next, behavior: "instant" });
              window.requestAnimationFrame(refine);
            }
          };
          window.requestAnimationFrame(refine);
        } else if (requestMinimapScrollTarget(firstTarget, "minimap-click")) {
          const operation = minimapScrollOperation;
          if (operation !== null) {
            void settleMinimapScrollOperation(operation, () => docScrollTopForCloneY(root, cloneYTarget));
          }
        }
        return;
      }
    }
    const thumbTravel = getCurrentMinimapThumbTravel();
    const maxScrollTop = Math.max(0, root.scrollHeight - root.clientHeight);
    const targetScrollTop = Math.min(minimapY, thumbTravel) / thumbTravel * maxScrollTop;
    const clamped = Math.max(0, Math.min(maxScrollTop, targetScrollTop));
    if (virtualizationEnabled) {
      if (requestMinimapScrollTarget(clamped, "minimap-click-fallback")) {
        const operation = minimapScrollOperation;
        if (operation !== null) {
          void settleMinimapScrollOperation(operation);
        }
      }
    } else {
      window.scrollTo({ top: clamped, behavior: "instant" });
    }
  }
  function scrollToProgress(progressPercent) {
    const root = document.scrollingElement ?? document.documentElement;
    const maximum = Math.max(0, root.scrollHeight - root.clientHeight);
    const progress = Number.isFinite(progressPercent) ? Math.max(0, Math.min(100, progressPercent)) : 0;
    if (virtualizationEnabled) {
      scheduleVirtualizedStandaloneOperation("host-progress", "supersede-as-user", (operation) => {
        operation.requestScrollTop(maximum * (progress / 100), "host-progress");
      });
    } else {
      window.scrollTo({ top: maximum * (progress / 100), behavior: "instant" });
    }
  }
  function requestMinimapScrollTarget(target, writer) {
    const operation = minimapScrollOperation;
    if (operation === null || !operation.isCurrent()) {
      return false;
    }
    operation.requestScrollTop(target, writer);
    operation.scheduleFrameTransaction(() => void 0);
    return true;
  }
  async function settleMinimapScrollOperation(operation, readRefinedTarget) {
    let afterEmission = 0;
    let settlement = null;
    while (operation.isCurrent() && minimapScrollOperation === operation) {
      if (settlement === null) {
        const outcome = await waitForCurrentVirtualizedGeometry(operation, afterEmission);
        if (outcome.status === "canceled") {
          return;
        }
        settlement = outcome;
      }
      const refinedTarget = readRefinedTarget?.() ?? null;
      if (refinedTarget !== null && Number.isFinite(refinedTarget) && Math.abs(refinedTarget - getDocumentScrollRoot().scrollTop) > VIRTUALIZED_NAVIGATION_CORRECTION_TOLERANCE_PX) {
        if (!requestMinimapScrollTarget(refinedTarget, "minimap-refine")) {
          return;
        }
        const receipt = virtualizedWriteReceipts.get(operation.operationEpoch);
        if (receipt === void 0 || (await receipt.result).status !== "committed") {
          return;
        }
        afterEmission = settlement.emission;
        settlement = null;
        continue;
      }
      const confirmation = await awaitConfirmedVirtualizedGeometry(operation, settlement);
      if (confirmation.status === "canceled") {
        return;
      }
      if (confirmation.status === "changed") {
        settlement = confirmation.settlement;
        continue;
      }
      const plane = scrollOwnershipControlPlane;
      if (plane?.holds(operation.lease, confirmation.confirmation.payload.geometryEpoch) !== true) {
        afterEmission = confirmation.confirmation.emission;
        settlement = null;
        continue;
      }
      finishMinimapScrollOperation(operation);
      return;
    }
  }
  function finishMinimapScrollOperation(operation = minimapScrollOperation) {
    if (minimapScrollOperation === operation) {
      minimapScrollOperation = null;
    }
    if (operation !== null) {
      releaseVirtualizedScrollOperationAfterWrite(operation);
    }
  }
  function handleMinimapPointerDown(event) {
    if (virtualizationEnabled) {
      minimapScrollOperation = acquireVirtualizedScrollOperation("minimap-gesture", "supersede-as-user");
    }
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
        if (virtualizationEnabled) {
          requestMinimapScrollTarget(Math.max(0, Math.min(maxScrollTop, target)), "minimap-drag");
        } else {
          window.scrollTo({ top: Math.max(0, Math.min(maxScrollTop, target)), behavior: "instant" });
        }
        const pinnedTop = Math.max(0, Math.min(currentMinimapLayout.thumbTravel, desiredThumbTop));
        minimapViewport.style.transform = `translateY(${pinnedTop}px)`;
        event.preventDefault();
        return;
      }
    }
    const thumbTravel = getCurrentMinimapThumbTravel();
    const scrollDelta = delta * (maxScrollTop / thumbTravel);
    const clampedScrollTop = Math.max(0, Math.min(maxScrollTop, minimapDragStartScrollTop + scrollDelta));
    if (virtualizationEnabled) {
      requestMinimapScrollTarget(clampedScrollTop, "minimap-drag-fallback");
    } else {
      window.scrollTo({ top: clampedScrollTop, behavior: "instant" });
    }
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
    } else {
      const operation = minimapScrollOperation;
      if (operation !== null) {
        void settleMinimapScrollOperation(operation);
      }
    }
  }
  function queueMinimapViewportUpdate(perfMarkName) {
    if (minimapViewportFrameRequested) {
      return;
    }
    minimapViewportFrameRequested = true;
    const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    window.requestAnimationFrame(() => {
      minimapViewportFrameRequested = false;
      if (documentEpoch !== void 0 && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
        return;
      }
      updateMinimapVisibility();
      updateMinimapViewport();
      if (perfMarkName) {
        postPerfMark(perfMarkName);
      }
    });
  }
  function cancelMinimapRefreshAfterLayoutSettles() {
    if (minimapRefreshTimer !== void 0) {
      window.clearTimeout(minimapRefreshTimer);
      minimapRefreshTimer = void 0;
    }
  }
  function queueMinimapRefreshAfterLayoutSettles() {
    cancelMinimapRefreshAfterLayoutSettles();
    const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    minimapRefreshTimer = window.setTimeout(() => {
      minimapRefreshTimer = void 0;
      if (documentEpoch !== void 0 && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
        return;
      }
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
  function scheduleResizeReactions(documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch()) {
    if (resizeReactFrameRequested) {
      return;
    }
    if (modeRevealPrepared) {
      return;
    }
    resizeReactFrameRequested = true;
    window.requestAnimationFrame(() => {
      resizeReactFrameRequested = false;
      if (documentEpoch !== void 0 && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
        return;
      }
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
    if (!next.viewerChromeEnabled || next.minimapMode === "off") {
      releaseMinimapRenderedContentLease();
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
    if (!shouldBuildDetailedMinimapContent().allowed) {
      releaseMinimapRenderedContentLease();
    }
    const layoutAffectingChange = fontFamilyChanged || fontSizeChanged || lineHeightChanged || maxWidthChanged || minimapModeChanged || viewerChromeChanged;
    if (layoutAffectingChange) {
      invalidateVirtualizationShadowModel();
      if (!minimapSourceReady && shouldBuildDetailedMinimapContent().allowed) {
        queueMinimapContentRefreshAfterLayoutSettles();
      } else {
        if (!shouldBuildDetailedMinimapContent().allowed) {
          releaseMinimapRenderedContentLease();
        }
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
  function cancelHeavyLiveUpdate() {
    if (heavyLiveUpdateTimer !== void 0) {
      window.clearTimeout(heavyLiveUpdateTimer);
      heavyLiveUpdateTimer = void 0;
    }
  }
  function scheduleHeavyLiveUpdate() {
    cancelHeavyLiveUpdate();
    const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    heavyLiveUpdateTimer = window.setTimeout(() => {
      heavyLiveUpdateTimer = void 0;
      if (documentEpoch !== void 0 && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
        return;
      }
      queueMinimapViewportUpdate();
    }, HEAVY_LIVE_UPDATE_DEBOUNCE_MS);
  }
  function scrollLegacyHeadingAnchor(anchor, options) {
    document.getElementById(anchor)?.scrollIntoView(options);
  }
  function scrollToHeadingAnchor(anchor, options) {
    if (anchor.length === 0) {
      return;
    }
    if (!virtualizationEnabled) {
      scrollLegacyHeadingAnchor(anchor, options);
      return;
    }
    const operation = acquireVirtualizedScrollOperation("heading-navigation", "supersede-programmatic");
    if (operation === null) {
      return;
    }
    const main = getLiveDocumentRoot();
    if (main === null || virtualizedDocumentWindowModel === null || virtualizedDocumentWindowController === null) {
      scheduleVirtualizedElementLanding(
        operation,
        findLiveDocumentElementById(anchor),
        "heading-live-fallback"
      );
      return;
    }
    const descriptor = { anchor, kind: "heading-anchor" };
    void renderWindowTargetThenAct({
      action: (context) => {
        landVirtualizedProgrammaticNavigation({
          behavior: options.behavior,
          context,
          descriptor,
          operation,
          viewportOffsetY: 0
        });
        return true;
      },
      actionKind: "navigate",
      controller: virtualizedDocumentWindowController,
      descriptor,
      legacyAction: () => scheduleVirtualizedElementLanding(
        operation,
        findLiveDocumentElementById(anchor),
        "heading-resolver-fallback"
      ),
      main,
      model: virtualizedDocumentWindowModel,
      operation,
      ownerWindow: window,
      root: getDocumentScrollRoot(),
      virtualizationEnabled: true
    });
  }
  function readCurrentHashAnchor() {
    const hash = window.location.hash;
    if (hash.length <= 1) {
      return null;
    }
    const raw = hash.slice(1);
    try {
      return decodeURIComponent(raw);
    } catch {
      return raw;
    }
  }
  function handleCurrentHashNavigation() {
    const anchor = readCurrentHashAnchor();
    if (anchor !== null) {
      scrollToHeadingAnchor(anchor, { block: "start" });
    }
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
        if (!shouldBuildDetailedMinimapContent().allowed) {
          releaseMinimapRenderedContentLease();
        }
        queueMinimapViewportUpdate();
      }
      return;
    }
    if (message.type === "reading-preferences") {
      applyReadingPreferences(message);
      return;
    }
    if (message.type === "scroll-to") {
      scrollToHeadingAnchor(message.anchor, { block: "start" });
      return;
    }
    if (message.type === "scroll-to-heading") {
      scrollToHeadingAnchor(message.id, { behavior: "smooth", block: "start" });
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
      if (virtualizationEnabled) {
        const root = getDocumentScrollRoot();
        scheduleVirtualizedStandaloneOperation("host-scroll-by", "supersede-as-user", (operation) => {
          operation.requestScrollTop(root.scrollTop + message.deltaY, "host-scroll-by");
        });
      } else {
        window.scrollBy({ top: message.deltaY, behavior: "instant" });
      }
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
      const operation = acquireVirtualizedScrollOperation("block-navigation", "supersede-programmatic");
      if (operation === null) {
        return;
      }
      const main = document.querySelector("main.mm-document");
      if (main === null || virtualizedDocumentWindowModel === null || virtualizedDocumentWindowController === null) {
        scheduleVirtualizedElementLanding(
          operation,
          findLiveDocumentBlockElement(message.blockIndex),
          "block-live-fallback"
        );
        return;
      }
      const descriptor = { blockIndex: message.blockIndex, kind: "block" };
      void renderWindowTargetThenAct({
        action: (context) => {
          landVirtualizedProgrammaticNavigation({
            context,
            descriptor,
            operation,
            viewportOffsetY: 0
          });
          return true;
        },
        actionKind: "navigate",
        controller: virtualizedDocumentWindowController,
        descriptor,
        legacyAction: () => scheduleVirtualizedElementLanding(
          operation,
          findLiveDocumentBlockElement(message.blockIndex),
          "block-resolver-fallback"
        ),
        main,
        model: virtualizedDocumentWindowModel,
        operation,
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
      handleCurrentHashNavigation();
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
      handleCurrentHashNavigation();
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
      const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
      const isCurrentProbe = () => settleSequence === modeToggleSettleSequence && (documentEpoch === void 0 || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) === true);
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
    finishCachedScrollRestore?.("canceled", "stale-document");
    cancelPendingVirtualizedMaintenance("stale-document");
    cancelModelRenderedContentCoordinator("stale-document");
    scrollOwnershipControlPlane?.invalidateDocument();
    virtualizedWriteReceipts.clear();
    pendingInitialVirtualizedWindowWork = null;
    cachedScrollRestoreCompletion = null;
    minimapScrollOperation = null;
    ++initialRenderPipelineGeneration;
    ++processedDocumentCacheCloneGeneration;
    ++progressiveMinimapRefreshGeneration;
    cancelProcessedDocumentCacheClone();
    if (virtualizationEnabled) {
      cancelProgressiveDeferredEnhancements();
    }
    cancelDeferredMinimapContentRefresh(false);
    cancelMinimapRefreshAfterLayoutSettles();
    cancelHeavyLiveUpdate();
    resizeReactFrameRequested = false;
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
    if (useCachedDocumentState && virtualizationEnabled) {
      const main = document.querySelector("main.mm-document");
      if (main !== null) {
        reclaimClonedMermaidProxyLifecycles(main);
      }
    }
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
          mathCount: getLiveDocumentMathCount()
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
        if (virtualizationEnabled) {
          scheduleVirtualizedStandaloneOperation("cold-load-reset", "supersede-programmatic", (operation) => {
            consumePendingInitialVirtualizedWindow(operation);
            operation.requestScrollTop(0, "cold-load-reset");
          });
        } else {
          window.scrollTo({ left: 0, top: 0, behavior: "instant" });
        }
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
        const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
        const complete = () => {
          if (documentEpoch !== void 0 && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
            return;
          }
          initialRenderPipelineCompleted = true;
          hasInitialLayoutSettled = true;
          postReadyEnhancementsCompleted = true;
          postHostMessage({
            type: "document-ready",
            mathCount: getLiveDocumentMathCount()
          });
          postCachedLayoutReady();
          postPostReadyEnhancementsComplete(renderId, hasMermaid, hasHljs);
          scheduleCachedMermaidResume(hasMermaid);
        };
        const restoreCompletion = cachedScrollRestoreCompletion;
        cachedScrollRestoreCompletion = null;
        if (restoreCompletion === null) {
          complete();
        } else {
          void restoreCompletion.then(complete);
        }
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
  function runLegacyResizeObserverWork(documentEpoch) {
    if (widthHandleDragging) {
      return;
    }
    queueMinimapRefreshAfterLayoutSettles();
    scheduleResizeReactions(documentEpoch);
    invalidateSourceLineAnchors({
      reassertPendingTarget: virtualizedProgrammaticNavigationPostSettleTarget === null
    });
    scheduleVirtualizedMeasuredHeightAdoption();
    window.requestAnimationFrame(() => {
      if (documentEpoch === void 0 || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) === true) {
        postScroll();
      }
    });
  }
  function runLegacyDocumentFontsReadyWork(documentEpoch) {
    if (documentEpoch !== void 0 && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
      return;
    }
    queueMinimapRefreshAfterLayoutSettles();
    invalidateSourceLineAnchors({
      reassertPendingTarget: virtualizedProgrammaticNavigationPostSettleTarget === null
    });
    scheduleVirtualizedMeasuredHeightAdoption();
  }
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
      mathCount: getLiveDocumentMathCount()
    });
    postScroll();
    const documentElement = document.querySelector(".mm-document");
    if (documentElement) {
      const resizeObserver = new ResizeObserver(() => {
        const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
        if (!virtualizationEnabled) {
          runLegacyResizeObserverWork(documentEpoch);
          return;
        }
        const ticket = beginVirtualizedGeometryWork("resize-observer");
        mutateVirtualizedGeometry(ticket);
        try {
          runLegacyResizeObserverWork(documentEpoch);
        } finally {
          endVirtualizedGeometryWork(ticket);
        }
      });
      resizeObserver.observe(documentElement);
      resizeObserver.observe(document.body);
    }
    const fontsDocumentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    document.fonts?.ready.then(() => {
      if (!virtualizationEnabled) {
        runLegacyDocumentFontsReadyWork(fontsDocumentEpoch);
        return;
      }
      const ticket = beginVirtualizedGeometryWork("document-fonts-ready");
      mutateVirtualizedGeometry(ticket);
      try {
        runLegacyDocumentFontsReadyWork(fontsDocumentEpoch);
      } finally {
        endVirtualizedGeometryWork(ticket);
      }
    }).catch(() => void 0);
  });
  var queuePostScroll = createScrollCoalescer({
    postScroll: () => {
      updateVirtualizedWindowForScroll();
      postScroll();
      queueMinimapViewportUpdate();
    },
    schedule: (cb) => {
      const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
      window.requestAnimationFrame(() => {
        if (documentEpoch !== void 0 && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
          return;
        }
        cb();
      });
    }
  });
  document.addEventListener("scroll", () => {
    if (scrollOwnershipControlPlane !== null) {
      const classification = scrollOwnershipControlPlane.classifyNativeScroll(
        getDocumentScrollRoot().scrollTop,
        "native-scroll"
      );
      if (classification.kind === "user-supersession") {
        cancelPendingVirtualizedMaintenance("user-supersession");
        cancelVirtualizedProgrammaticNavigationState();
        queuePreviewSourceLinePost();
      } else if (classification.kind === "unattributed-failure") {
        cancelPendingVirtualizedMaintenance("unattributed-failure");
        virtualizedProgrammaticNavigationExternalShiftCount++;
        cancelVirtualizedProgrammaticNavigationState();
      }
    } else {
      const programmaticNavigationScroll = isVirtualizedProgrammaticNavigationInProgress();
      if (!programmaticNavigationScroll) {
        clearVirtualizedProgrammaticNavigationPostSettleTarget();
      }
      queuePostScroll();
      queuePreviewSourceLinePost();
      return;
    }
    queuePostScroll();
  }, { passive: true });
  window.addEventListener("hashchange", handleCurrentHashNavigation);
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
