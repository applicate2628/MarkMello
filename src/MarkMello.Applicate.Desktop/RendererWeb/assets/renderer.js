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
    return {
      contentWidth: input.documentWidth,
      scale,
      contentTranslateY,
      transform: `translateY(${contentTranslateY}px) scale(${scale})`,
      thumbTop,
      thumbHeight,
      thumbTravel
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
    const shouldRunMermaid = deps.hasMermaid !== false;
    if (shouldRunMermaid) {
      deps.initMermaidWithTheme(theme);
    } else {
      deps.postPerfMark?.("mermaid-skipped", { hasMermaid: false });
    }
    const mathController = deps.renderMath();
    if (shouldRunMermaid) {
      try {
        await deps.renderMermaid();
      } catch {
      }
    }
    deps.renderCodeBlocks();
    await mathController.initialVisibleReady;
    deps.scheduleLayoutReady();
  }

  // RendererWeb/src/loadDocument.ts
  function applyLoadDocument(message, deps) {
    const main = document.querySelector("main.mm-document");
    if (!main) {
      return;
    }
    deps.emitMark("mm-load-document", {
      documentName: message.documentName ?? "",
      htmlLength: message.html.length,
      renderId: message.renderId ?? null
    });
    deps.debugLog(`load-document:start id=${message.renderId ?? "(none)"} name=${message.documentName ?? ""} theme=${message.theme ?? "(none)"} currentTheme=${document.documentElement.dataset.theme ?? "(none)"} htmlLength=${message.html.length}`);
    deps.cancelCurrentMathController();
    deps.resetModuleGlobals();
    if (message.theme) {
      deps.applyTheme(message.theme);
    }
    main.innerHTML = message.html;
    const firstHeading = main.querySelector("h1,h2,h3")?.textContent?.trim().replace(/\s+/g, " ").slice(0, 120) ?? "";
    deps.debugLog(`load-document:swapped id=${message.renderId ?? "(none)"} name=${message.documentName ?? ""} theme=${document.documentElement.dataset.theme ?? "(none)"} firstHeading=${firstHeading}`);
    deps.ensureChromeNodes();
    deps.scrollWindowToTop();
    void deps.runInitialRenderPipeline(message.hasMermaid);
  }
  function clearDocumentState(deps) {
    const main = document.querySelector("main.mm-document");
    deps.emitMark("mm-clear-document");
    deps.debugLog("clear-document");
    deps.cancelCurrentMathController();
    deps.resetModuleGlobals();
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
  var PHASE_B_HEIGHT_DELTA_THRESHOLD_PX = 100;
  function shouldTriggerPhaseB(currentHeight, cachedHeight) {
    if (cachedHeight <= 0) return false;
    return Math.abs(currentHeight - cachedHeight) >= PHASE_B_HEIGHT_DELTA_THRESHOLD_PX;
  }
  function schedulePhaseBRebuild(deps) {
    deps.allMathRendered.then(() => {
      if (!shouldTriggerPhaseB(deps.getCurrentDocumentHeight(), deps.getCachedDocumentHeight())) {
        return;
      }
      const win = window;
      if (typeof win.requestIdleCallback === "function") {
        win.requestIdleCallback(() => deps.refresh("B"), { timeout: 500 });
      } else {
        window.setTimeout(() => deps.refresh("B"), 50);
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
  var SKIP_TAGS = /* @__PURE__ */ new Set([
    "SCRIPT",
    "STYLE",
    "NOSCRIPT",
    "ASIDE"
    // minimap aside
  ]);
  var SKIP_CLASSES = /* @__PURE__ */ new Set([
    "mm-minimap",
    "mm-minimap-viewport",
    "mm-width-handle",
    "mm-drop-overlay",
    FIND_BAR_CLASS
  ]);
  function countMatchesInRoot(root, needle) {
    if (needle.length === 0) {
      return 0;
    }
    const lowered = needle.toLowerCase();
    let total = 0;
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
            return NodeFilter.FILTER_SKIP;
          }
          return NodeFilter.FILTER_ACCEPT;
        }
      }
    );
    let current = walker.nextNode();
    while (current !== null) {
      const text = (current.nodeValue ?? "").toLowerCase();
      if (text.length >= lowered.length) {
        let idx = text.indexOf(lowered);
        while (idx !== -1) {
          total++;
          idx = text.indexOf(lowered, idx + lowered.length);
        }
      }
      current = walker.nextNode();
    }
    return total;
  }
  function currentMatchIndex(root, needle) {
    if (needle.length === 0) {
      return 0;
    }
    const sel = window.getSelection();
    if (sel === null || sel.rangeCount === 0) {
      return 0;
    }
    const range = sel.getRangeAt(0);
    if (range.collapsed) {
      return 0;
    }
    const lowered = needle.toLowerCase();
    let count = 0;
    let found = false;
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
            return NodeFilter.FILTER_SKIP;
          }
          return NodeFilter.FILTER_ACCEPT;
        }
      }
    );
    let current = walker.nextNode();
    while (current !== null && !found) {
      const text = (current.nodeValue ?? "").toLowerCase();
      if (text.length >= lowered.length) {
        let idx = text.indexOf(lowered);
        while (idx !== -1) {
          count++;
          if (current === range.startContainer && idx === range.startOffset) {
            found = true;
            break;
          }
          idx = text.indexOf(lowered, idx + lowered.length);
        }
      }
      current = walker.nextNode();
    }
    return found ? count : 0;
  }
  function createFindBar() {
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
        totalMatches: 0,
        hasNavigated: false
      };
    }
    function updateCountDisplay(s) {
      const query = s.input.value;
      if (query.length === 0) {
        s.count.textContent = "";
        s.bar.classList.remove("mm-find-no-match");
        return;
      }
      if (s.totalMatches === 0) {
        s.count.textContent = "0 of 0";
        s.bar.classList.add("mm-find-no-match");
        return;
      }
      s.bar.classList.remove("mm-find-no-match");
      if (s.hasNavigated) {
        const idx = currentMatchIndex(document.body, s.lastSearched);
        if (idx > 0) {
          s.count.textContent = `${idx} of ${s.totalMatches}`;
          return;
        }
      }
      s.count.textContent = `${s.totalMatches} match${s.totalMatches === 1 ? "" : "es"}`;
    }
    function runSearch(s, query) {
      s.lastSearched = query;
      if (query.length === 0) {
        s.totalMatches = 0;
        s.hasNavigated = false;
        window.getSelection()?.removeAllRanges();
        updateCountDisplay(s);
        return;
      }
      s.totalMatches = countMatchesInRoot(document.body, query);
      s.hasNavigated = false;
      if (s.totalMatches > 0) {
        const main = document.querySelector("main.mm-document") ?? document.body;
        const range = document.createRange();
        range.setStart(main, 0);
        range.collapse(true);
        const sel = window.getSelection();
        if (sel !== null) {
          sel.removeAllRanges();
          sel.addRange(range);
        }
        navigate(
          s,
          "next",
          /* initialPlacement */
          true
        );
      } else {
        window.getSelection()?.removeAllRanges();
      }
      updateCountDisplay(s);
    }
    function navigate(s, direction, initialPlacement = false) {
      if (s.lastSearched.length === 0 || s.totalMatches === 0) {
        return;
      }
      const find = window.find;
      if (typeof find !== "function") {
        return;
      }
      const backwards = direction === "prev";
      find(s.lastSearched, false, backwards, true, false, false, false);
      s.hasNavigated = true;
      if (!initialPlacement) {
        updateCountDisplay(s);
      }
    }
    function attachListeners(s) {
      s.input.addEventListener("input", () => {
        const query = s.input.value;
        if (s.debounceTimer !== null) {
          window.clearTimeout(s.debounceTimer);
        }
        s.debounceTimer = window.setTimeout(() => {
          s.debounceTimer = null;
          runSearch(s, query);
        }, FIND_DEBOUNCE_MS);
      });
      s.input.addEventListener("keydown", (event) => {
        if (event.key === "Enter") {
          event.preventDefault();
          if (s.debounceTimer !== null) {
            window.clearTimeout(s.debounceTimer);
            s.debounceTimer = null;
            runSearch(s, s.input.value);
            return;
          }
          navigate(s, event.shiftKey ? "prev" : "next");
        } else if (event.key === "Escape") {
          event.preventDefault();
          event.stopPropagation();
          close();
        }
      });
      s.prevBtn.addEventListener("click", () => {
        navigate(s, "prev");
        s.input.focus();
      });
      s.nextBtn.addEventListener("click", () => {
        navigate(s, "next");
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
      state2.bar.classList.remove("mm-find-bar-open");
      state2.input.value = "";
      state2.lastSearched = "";
      state2.totalMatches = 0;
      state2.hasNavigated = false;
      state2.count.textContent = "";
      state2.bar.classList.remove("mm-find-no-match");
      window.getSelection()?.removeAllRanges();
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

  // RendererWeb/src/sourceLineSync.ts
  var SOURCE_LINE_ANCHOR_SELECTOR = "[data-mm-source-line]";
  function readSourceLineAnchors(root = document, scrollY = window.scrollY) {
    const anchors = [];
    for (const element of Array.from(root.querySelectorAll(SOURCE_LINE_ANCHOR_SELECTOR))) {
      const sourceLine = parseNonNegativeInt(element.dataset["mmSourceLine"]);
      if (sourceLine === null) {
        continue;
      }
      const endLine = parseNonNegativeInt(element.dataset["mmSourceEndLine"]) ?? sourceLine;
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
  function parseNonNegativeInt(value) {
    if (value === void 0 || value.trim() === "") {
      return null;
    }
    const parsed = Number.parseInt(value, 10);
    return Number.isFinite(parsed) && parsed >= 0 ? parsed : null;
  }
  function clamp01(value) {
    return Math.max(0, Math.min(1, value));
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
  var hasInitialLayoutSettled = false;
  var minimapViewportFrameRequested = false;
  var minimapRefreshTimer;
  var resizeReactFrameRequested = false;
  var modeToggleProbeFrameRequested = false;
  var minimapRoot = null;
  var minimapContent = null;
  var minimapViewport = null;
  var currentMinimapLayout = null;
  var minimapDragging = false;
  var minimapDragStartClientY = null;
  var minimapDragStartScrollTop = 0;
  var minimapDragMode = "tentative";
  var MINIMAP_DRAG_THRESHOLD_PX = 4;
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
  var findBarController = null;
  var widthHandleRoot = null;
  var widthHandleDragging = false;
  var widthHandleStartClientX = 0;
  var widthHandleStartMaxWidth = 0;
  var pendingWidthDragDeltaX = 0;
  var widthDragFrameRequested = false;
  var widthDragApplyFrameRequested = false;
  var layoutReadyGeneration = 0;
  var layoutReadyTimer;
  var lastPostedMinimapState = { hasPosted: false, visible: false, reservedWidth: 0 };
  var minimapPolicy = null;
  var sourceLineAnchors = [];
  var sourceLineAnchorRefreshFrameRequested = false;
  var previewSourceLineFrameRequested = false;
  var suppressPreviewSourceLineEmit = false;
  var suppressPreviewSourceLineToken = 0;
  var lastPostedPreviewSourceLine = null;
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
      postPerfMark("mm-initial-visible-ready", {
        visibleCount: controller.initialVisibleNodes.size,
        failedCount: countFailedInSet(controller.initialVisibleNodes)
      });
      refreshMinimapContent("A");
      hasInitialLayoutSettled = true;
      updateWidthHandlePosition();
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
  function refreshSourceLineAnchors() {
    sourceLineAnchors = readSourceLineAnchors(document);
  }
  function queueSourceLineAnchorRefresh() {
    if (sourceLineAnchorRefreshFrameRequested) {
      return;
    }
    sourceLineAnchorRefreshFrameRequested = true;
    window.requestAnimationFrame(() => {
      sourceLineAnchorRefreshFrameRequested = false;
      refreshSourceLineAnchors();
    });
  }
  function scrollToSourceLine(sourceLine) {
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
    suppressPreviewSourceLinePost();
    window.scrollTo({ left: 0, top: scrollTop, behavior: "instant" });
  }
  function suppressPreviewSourceLinePost() {
    const token = ++suppressPreviewSourceLineToken;
    suppressPreviewSourceLineEmit = true;
    window.requestAnimationFrame(() => {
      window.requestAnimationFrame(() => {
        if (token === suppressPreviewSourceLineToken) {
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
      if (sourceLineAnchors.length === 0) {
        refreshSourceLineAnchors();
      }
      const sourceLine = findSourceLineAtDocumentY(
        sourceLineAnchors,
        window.scrollY + getViewportAnchorY()
      );
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
  function postLayoutReady() {
    refreshSourceLineAnchors();
    postScroll();
    postHostMessage({
      type: "layout-ready",
      ...getScrollState()
    });
    postPerfMark("mm-layout-ready");
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
      const previewMaxWidth = Math.max(hostMinMaxWidth, widthHandleStartMaxWidth + 2 * pendingWidthDragDeltaX);
      document.documentElement.style.setProperty("--mm-document-max-width", `${previewMaxWidth}px`);
      updateWidthHandlePosition();
      queueMinimapViewportUpdate();
    });
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
    const sourceStyle = getComputedStyle(source);
    const clone = source.cloneNode(true);
    minimapSourceReady = true;
    clone.removeAttribute("id");
    clone.setAttribute("aria-hidden", "true");
    clone.inert = true;
    clone.style.paddingTop = sourceStyle.paddingTop;
    clone.style.paddingRight = "0";
    clone.style.paddingBottom = sourceStyle.paddingBottom;
    clone.style.paddingLeft = "0";
    clone.querySelectorAll("*").forEach((node) => {
      const isHtml = node.namespaceURI === "http://www.w3.org/1999/xhtml" || node.namespaceURI === null;
      if (isHtml && node.hasAttribute("id")) node.removeAttribute("id");
      const tag = node.tagName;
      if (tag === "A" || tag === "BUTTON" || tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") {
        node.setAttribute("tabindex", "-1");
        node.removeAttribute("href");
      }
    });
    return clone;
  }
  function refreshMinimapContent(phase = "A") {
    emitMark("mm-minimap-refresh-start", { phase });
    postPerfMark("mm-minimap-refresh-start", { phase });
    ensureMinimap();
    if (!minimapContent || !minimapRoot) {
      emitMark("mm-minimap-refresh-end", { phase, skipped: "no-mount" });
      postPerfMark("mm-minimap-refresh-end", { phase, skipped: "no-mount" });
      return;
    }
    const clone = cloneDocumentForMinimap();
    if (!clone) {
      emitMark("mm-minimap-refresh-end", { phase, skipped: "no-source" });
      postPerfMark("mm-minimap-refresh-end", { phase, skipped: "no-source" });
      return;
    }
    const root = document.scrollingElement ?? document.documentElement;
    minimapDocumentHeight = root.scrollHeight;
    minimapContent.replaceChildren(clone);
    updateMinimapVisibility(true);
    updateMinimapViewport();
    emitMark("mm-minimap-refresh-end", { phase, documentHeight: minimapDocumentHeight });
    postPerfMark("mm-minimap-refresh-end", { phase, documentHeight: minimapDocumentHeight });
  }
  var activeHeadingObserver = null;
  var lastPostedActiveHeadingId = null;
  function extractAndPostHeadings() {
    const main = document.querySelector("main.mm-document");
    if (!main) {
      postHostMessage({ type: "headings-updated", headings: [] });
      lastPostedActiveHeadingId = null;
      return;
    }
    const nodes = Array.from(
      main.querySelectorAll("h1, h2, h3, h4, h5, h6")
    );
    const headings = nodes.map((node) => {
      const id = node.id;
      if (!id) {
        return null;
      }
      const tag = node.tagName.toUpperCase();
      const level = Number.parseInt(tag.slice(1), 10);
      if (!Number.isFinite(level) || level < 1 || level > 6) {
        return null;
      }
      const text = (node.textContent ?? "").trim();
      return { id, level, text };
    }).filter((h) => h !== null);
    postHostMessage({ type: "headings-updated", headings });
    rebuildActiveHeadingObserver(nodes.filter((n) => !!n.id));
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
    const root = document.scrollingElement ?? document.documentElement;
    const documentHeight = root.scrollHeight;
    const viewportHeight = root.clientHeight;
    if (!hasReceivedHostPreferences || !minimapPolicy || !viewerChromeEnabled || !minimapSourceReady || minimapMode === "off" || viewportHeight <= 0 || documentHeight <= viewportHeight) {
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
    const wasVisible = !minimapRoot.hidden;
    const hadClass = document.body.classList.contains(MINIMAP_VISIBLE_CLASS);
    const visible = shouldShowMinimap();
    minimapRoot.hidden = !visible;
    document.body.classList.toggle(MINIMAP_VISIBLE_CLASS, visible);
    postMinimapState(visible, forcePostState);
    if (wasVisible !== visible || hadClass !== visible) {
      updateWidthHandlePosition();
    }
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
    const minimapHeight = minimapRoot.clientHeight;
    const minimapWidth = minimapRoot.clientWidth;
    const documentHeight = root.scrollHeight;
    const sourceStyle = getComputedStyle(source);
    const documentWidth = calculateMinimapDocumentWidth({
      borderBoxWidth: source.clientWidth || source.getBoundingClientRect().width,
      paddingLeft: readPixelValue(sourceStyle.paddingLeft),
      paddingRight: readPixelValue(sourceStyle.paddingRight)
    });
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
    const thumbTravel = getCurrentMinimapThumbTravel();
    const maxScrollTop = Math.max(0, root.scrollHeight - root.clientHeight);
    const scrollDelta = delta * (maxScrollTop / thumbTravel);
    const newScrollTop = minimapDragStartScrollTop + scrollDelta;
    const clampedScrollTop = Math.max(0, Math.min(maxScrollTop, newScrollTop));
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
  function scheduleResizeReactions() {
    if (resizeReactFrameRequested) {
      return;
    }
    resizeReactFrameRequested = true;
    window.requestAnimationFrame(() => {
      resizeReactFrameRequested = false;
      if (widthHandleDragging) {
        return;
      }
      updateWidthHandlePosition();
      queueMinimapViewportUpdate();
    });
  }
  var lastAppliedReadingPreferences = null;
  var pendingReadingPreferences = null;
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
      if (!viewerChromeEnabled) {
        updateMinimapVisibility(true);
        updateWidthHandlePosition();
      } else {
        updateWidthHandlePosition();
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
    if (message.type === "scroll-to-heading") {
      const target = document.getElementById(message.id);
      if (target) {
        target.scrollIntoView({ behavior: "smooth", block: "start" });
      }
      return;
    }
    if (message.type === "scroll-to-source-line") {
      scrollToSourceLine(message.sourceLine);
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
      const target = document.querySelector(
        `[data-mm-block-index="${message.blockIndex}"]`
      );
      if (target) {
        target.scrollIntoView({ block: "start", behavior: "instant" });
      }
      return;
    }
    if (message.type === "load-document") {
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
      if (message.hasMermaid !== void 0) {
        loadMessage.hasMermaid = message.hasMermaid;
      }
      applyLoadDocument(loadMessage, buildLoadDocumentDeps());
      return;
    }
    if (message.type === "clear-document") {
      clearDocumentState(buildLoadDocumentDeps());
      return;
    }
    if (message.type === "mode-settle-probe") {
      postPerfMark("mm-mode-settle-probe-received");
      if (modeToggleProbeFrameRequested) {
        postPerfMark("mm-mode-settle-probe-duplicate");
        return;
      }
      modeToggleProbeFrameRequested = true;
      window.requestAnimationFrame(() => {
        postPerfMark("mm-mode-settle-first-raf");
        window.requestAnimationFrame(() => {
          postPerfMark("mm-mode-settle-second-raf");
          modeToggleProbeFrameRequested = false;
          updateWidthHandlePosition();
          updateMinimapVisibility();
          updateMinimapViewport();
          postPerfMark("mm-mode-settle-pre-ack");
          postHostMessage({ type: "mode-toggle-settled" });
        });
      });
      return;
    }
  }
  function resetModuleGlobalsForLoadDocument() {
    initialRenderPipelineCompleted = false;
    currentController?.cancel();
    currentController = null;
    ++mermaidRenderGeneration;
    minimapDocumentHeight = 0;
    lastPostedMinimapState = { hasPosted: false, visible: false, reservedWidth: 0 };
    minimapSourceReady = false;
    hasInitialLayoutSettled = false;
    findBarController?.close();
    if (activeHeadingObserver) {
      activeHeadingObserver.disconnect();
      activeHeadingObserver = null;
    }
    lastPostedActiveHeadingId = null;
    sourceLineAnchors = [];
    sourceLineAnchorRefreshFrameRequested = false;
    previewSourceLineFrameRequested = false;
    suppressPreviewSourceLineEmit = false;
    lastPostedPreviewSourceLine = null;
  }
  function ensureChromeNodes() {
    ensureMinimap();
    ensureWidthHandle();
    ensureDropOverlay();
    updateWidthHandlePosition();
    refreshMinimapContent("A");
    extractAndPostHeadings();
    refreshSourceLineAnchors();
  }
  function buildLoadDocumentDeps() {
    return {
      // PE r2 item G — accept the per-document `hasMermaid` so the pipeline
      // skips mermaid init+render for docs without mermaid blocks. Undefined
      // passes through to the pipeline's `!== false` default, preserving the
      // pre-G behavior for any caller that doesn't carry the flag.
      runInitialRenderPipeline: (hasMermaid) => runInitialRenderPipeline({
        getCurrentTheme,
        applyTheme,
        initMermaidWithTheme,
        renderMath: renderMath2,
        renderMermaid,
        renderCodeBlocks,
        scheduleLayoutReady: () => {
          initialRenderPipelineCompleted = true;
          scheduleLayoutReady();
          postHostMessage({
            type: "document-ready",
            mathCount: document.querySelectorAll("[data-tex]").length
          });
        },
        hasMermaid,
        postPerfMark
      }),
      cancelCurrentMathController: () => {
        currentController?.cancel();
      },
      resetModuleGlobals: resetModuleGlobalsForLoadDocument,
      scrollWindowToTop: () => {
        window.scrollTo({ left: 0, top: 0, behavior: "instant" });
      },
      // Mirror selected renderer-side perf marks into the host's
      // [renderer-perf] stream. Only `mm-load-document` is bridged from this
      // path per round-2 plan item C; other marks are bridged at their own
      // emission sites in renderer.ts so the bridging is colocated with the
      // semantic anchor rather than centralized here.
      emitMark: (name, detail) => {
        emitMark(name, detail);
        if (name === "mm-load-document") {
          postPerfMark(name, detail ?? void 0);
        }
      },
      ensureChromeNodes,
      applyTheme,
      debugLog: postDebugLog
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
  function wireHostShortcuts() {
    const hostShortcuts = /* @__PURE__ */ new Set([
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
        postHostMessage({ type: "host-shortcut", combo });
      },
      { capture: true }
    );
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
    findBarController = createFindBar();
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
        queueSourceLineAnchorRefresh();
        scheduleResizeReactions();
        window.requestAnimationFrame(postScroll);
      });
      resizeObserver.observe(documentElement);
      resizeObserver.observe(document.body);
    }
    document.fonts?.ready.then(() => {
      queueMinimapRefreshAfterLayoutSettles();
      queueSourceLineAnchorRefresh();
    }).catch(() => void 0);
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
    queuePreviewSourceLinePost();
  }, { passive: true });
  window.addEventListener("message", (event) => handleHostMessage(event.data));
  window.addEventListener("resize", () => {
    scheduleResizeReactions();
    queueSourceLineAnchorRefresh();
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
