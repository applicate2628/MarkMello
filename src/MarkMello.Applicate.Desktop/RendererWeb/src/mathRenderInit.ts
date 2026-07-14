import type {
  MathObserverWindowReason,
  MathObserverWindowStats,
  MathReadinessController
} from "./initialRenderPipeline";
import { MathRenderQueue, isTerminalMathState, type MathRenderTask } from "./mathRenderQueue";

type KatexLike = {
  render(
    tex: string,
    node: HTMLElement,
    opts: { throwOnError?: boolean; displayMode?: boolean; strict?: string; trust?: boolean },
  ): void;
};

export type RenderMathDeps = {
  katex: KatexLike | undefined;
  documentRoot: Document;
  initialObservationTopBlockIndex?: number | null | undefined;
  initialObservationBottomBlockIndex?: number | null | undefined;
  isMathObserverWindowTelemetryEnabled?: (() => boolean) | undefined;
  emitMathObserverWindowMark?: ((detail: MathObserverWindowStats) => void) | undefined;
};

const INITIAL_LOOKAHEAD_PX = 500;
const MATH_RENDER_FRAME_FALLBACK_MS = 32;
const INITIAL_PAST_VIEWPORT_SCAN_LIMIT = 8;
const MATH_OBSERVER_WINDOW_CAP = 320;
const MATH_OBSERVER_MIN_PADDING_BLOCKS = 8;

type MathObservationBucket = {
  visEl: HTMLElement;
  nodes: HTMLElement[];
  blockIndex: number | null;
  order: number;
  observed: boolean;
  terminal: boolean;
};

type MathObservationSelection = {
  candidates: MathObservationBucket[];
  windowStartBlockIndex: number | null;
  windowEndBlockIndex: number | null;
};

function complexityScore(tex: string): number {
  let score = 1;
  score += (tex.match(/\\frac/g)?.length ?? 0) * 2;
  score += (tex.match(/\\sum/g)?.length ?? 0) * 2;
  score += (tex.match(/\\int/g)?.length ?? 0) * 2;
  score += (tex.match(/\\\\/g)?.length ?? 0) * 3;
  return score;
}

function reserveMathPlaceholder(node: HTMLElement): void {
  if (!node.classList.contains("math-display")) return;
  const tex = node.dataset["tex"] ?? "";
  const score = complexityScore(tex);
  const minHeight = Math.max(28, 28 * Math.ceil(score / 5));
  node.style.minHeight = `${minHeight}px`;
}

function getVisibilityElement(node: HTMLElement): HTMLElement {
  if (node.classList.contains("math-inline")) {
    return node.parentElement ?? node;
  }
  return node;
}

function parseNonNegativeInt(value: string | undefined): number | null {
  if (value === undefined || value.trim() === "") {
    return null;
  }

  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : null;
}

function getMathBlockIndex(visEl: HTMLElement): number | null {
  const direct = parseNonNegativeInt(visEl.dataset["mmBlockIndex"]);
  if (direct !== null) return direct;
  const ancestor = visEl.closest<HTMLElement>("[data-mm-block-index]");
  return parseNonNegativeInt(ancestor?.dataset["mmBlockIndex"]);
}

function isBucketTerminal(bucket: MathObservationBucket): boolean {
  return bucket.nodes.every((node) => isTerminalMathState(node.dataset["mmMathRendered"]));
}

function lowerBound(values: readonly number[], target: number): number {
  let low = 0;
  let high = values.length;
  while (low < high) {
    const mid = low + Math.floor((high - low) / 2);
    if (values[mid]! < target) {
      low = mid + 1;
    } else {
      high = mid;
    }
  }
  return low;
}

function nowMs(): number {
  return typeof performance !== "undefined" && typeof performance.now === "function"
    ? performance.now()
    : 0;
}

function rafYield(): Promise<void> {
  return new Promise(resolve => {
    let resolved = false;
    let timeout: number | undefined;
    const finish = () => {
      if (resolved) return;
      resolved = true;
      if (timeout !== undefined) {
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

export function renderMath(deps: RenderMathDeps): MathReadinessController {
  const mathNodes = Array.from(deps.documentRoot.querySelectorAll<HTMLElement>("[data-tex]"));
  const katex = deps.katex;
  if (!katex || mathNodes.length === 0) {
    return {
      initialVisibleReady: Promise.resolve(),
      allMathRendered: Promise.resolve(),
      cancel: () => {},
      initialVisibleNodes: new Set<HTMLElement>(),
      totalMathCount: mathNodes.length,
      isCancelled: () => false,
    };
  }

  // 1. Reserve display-math placeholders synchronously (no layout shift later).
  //    Inline math intentionally has no placeholder reservation. Cached DOM
  //    can already contain terminal KaTeX nodes; those keep their natural
  //    rendered height and must not re-enter the initial-ready wait set.
  mathNodes
    .filter(node => !isTerminalMathState(node.dataset["mmMathRendered"]))
    .forEach(reserveMathPlaceholder);

  // 2. Build queue with rAF yield and performance.now timing.
  const queue = new MathRenderQueue({
    katex,
    timeBudgetMs: 7,
    now: () => performance.now(),
    yield: rafYield,
  });

  // 3. Freeze initial-visible set via getBoundingClientRect of the
  //    visibility element (parent for inline math, self for display math).
  //    The frozen set is what initialVisibleReady awaits; IO promotions
  //    later in the lifecycle must NOT extend this set.
  const viewportHeight = window.innerHeight;
  const initialVisibleNodes = new Set<HTMLElement>();
  const rectCache = new Map<HTMLElement, DOMRect>();
  let stopMeasuringInitialVisibility = false;
  let consecutivePastViewportElements = 0;
  let lastMeasuredVisibilityElement: HTMLElement | null = null;
  const readRect = (element: HTMLElement): DOMRect => {
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
    const task: MathRenderTask = {
      node,
      tex,
      displayMode: node.classList.contains("math-display"),
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

  // 4. Build initialVisibleReady — resolves when ALL frozen nodes reach a
  //    terminal state (success OR failure). Subscribed via onTaskComplete;
  //    completion is observed regardless of which priority bucket the task
  //    landed in or any later IO promotion.
  let initialPending = initialVisibleNodes.size;
  const initialVisibleReady = new Promise<void>((resolve) => {
    if (initialPending === 0) { resolve(); return; }
    const unsubscribe = queue.onTaskComplete((node) => {
      if (initialVisibleNodes.has(node)) {
        initialPending--;
        if (initialPending === 0) { unsubscribe(); resolve(); }
      }
    });
  });

  // 5. Start queue — returns full-drain promise.
  const allMathRendered = queue.start();

  // 6. IntersectionObserver: build one stable bucket per visibility element,
  //    but observe only a hard-capped block-index window. Observing every math
  //    bucket in a heavy content-visibility document forces Chromium to keep
  //    computing intersections for far-off blocks on every scroll.
  const bucketByElement = new Map<HTMLElement, MathObservationBucket>();
  const buckets: MathObservationBucket[] = [];
  for (const node of mathNodes) {
    const visEl = getVisibilityElement(node);
    let bucket = bucketByElement.get(visEl);
    if (!bucket) {
      bucket = {
        visEl,
        nodes: [],
        blockIndex: getMathBlockIndex(visEl),
        order: buckets.length,
        observed: false,
        terminal: false,
      };
      buckets.push(bucket);
      bucketByElement.set(visEl, bucket);
    }
    bucket.nodes.push(node);
  }
  for (const bucket of buckets) {
    bucket.terminal = isBucketTerminal(bucket);
  }

  const bucketIndexesByBlock = new Map<number, number[]>();
  const unindexedBucketIndexes: number[] = [];
  for (const bucket of buckets) {
    if (bucket.blockIndex === null) {
      unindexedBucketIndexes.push(bucket.order);
      continue;
    }

    const indexes = bucketIndexesByBlock.get(bucket.blockIndex) ?? [];
    indexes.push(bucket.order);
    bucketIndexesByBlock.set(bucket.blockIndex, indexes);
  }
  const sortedBlockIndexes = Array.from(bucketIndexesByBlock.keys()).sort((left, right) => left - right);
  const activeObservedBuckets = new Set<MathObservationBucket>();
  let lastTopBlockIndex: number | null = null;
  let lastBottomBlockIndex: number | null = null;
  let lastWindowStartBlockIndex: number | null = null;
  let lastWindowEndBlockIndex: number | null = null;
  let observerDisconnected = false;

  const observer = new IntersectionObserver((entries) => {
    const telemetryEnabled = isObservationTelemetryEnabled();
    const callbackStart = telemetryEnabled ? nowMs() : 0;
    for (const entry of entries) {
      const bucket = bucketByElement.get(entry.target as HTMLElement);
      if (!bucket || bucket.terminal) continue;
      if (markBucketTerminal(bucket)) continue;
      for (const targetNode of bucket.nodes) {
        if (isTerminalMathState(targetNode.dataset["mmMathRendered"])) continue;
        const tex = targetNode.dataset["tex"] ?? "";
        const task: MathRenderTask = {
          node: targetNode,
          tex,
          displayMode: targetNode.classList.contains("math-display"),
        };
        queue.enqueue(task, entry.isIntersecting ? "high" : "low");
      }
    }
    if (!telemetryEnabled) return;
    emitObservationMark({
      observedCount: activeObservedBuckets.size,
      candidateCount: entries.length,
      maxObservedCount: MATH_OBSERVER_WINDOW_CAP,
      topBlockIndex: lastTopBlockIndex,
      bottomBlockIndex: lastBottomBlockIndex,
      windowStartBlockIndex: lastWindowStartBlockIndex,
      windowEndBlockIndex: lastWindowEndBlockIndex,
      addedCount: 0,
      removedCount: 0,
      terminalRemovedCount: 0,
      updateDurationMs: 0,
      callbackDurationMs: nowMs() - callbackStart,
      reason: "callback",
    });
  }, { rootMargin: `${INITIAL_LOOKAHEAD_PX}px` });

  function markBucketTerminal(bucket: MathObservationBucket): boolean {
    if (bucket.terminal) return true;
    if (!isBucketTerminal(bucket)) return false;
    bucket.terminal = true;
    return true;
  }

  function pushCandidate(
    bucketIndex: number,
    candidates: MathObservationBucket[],
  ): void {
    if (candidates.length >= MATH_OBSERVER_WINDOW_CAP) return;
    const bucket = buckets[bucketIndex];
    if (!bucket || markBucketTerminal(bucket)) return;
    candidates.push(bucket);
  }

  function collectDocumentOrderCandidates(): MathObservationSelection {
    const candidates: MathObservationBucket[] = [];
    let windowStartBlockIndex: number | null = null;
    let windowEndBlockIndex: number | null = null;
    for (let index = 0; index < buckets.length && candidates.length < MATH_OBSERVER_WINDOW_CAP; index++) {
      pushCandidate(index, candidates);
    }
    for (const bucket of candidates) {
      if (bucket.blockIndex === null) continue;
      windowStartBlockIndex = windowStartBlockIndex === null
        ? bucket.blockIndex
        : Math.min(windowStartBlockIndex, bucket.blockIndex);
      windowEndBlockIndex = windowEndBlockIndex === null
        ? bucket.blockIndex
        : Math.max(windowEndBlockIndex, bucket.blockIndex);
    }
    return { candidates, windowStartBlockIndex, windowEndBlockIndex };
  }

  function collectWindowCandidates(
    topBlockIndex: number | null,
    bottomBlockIndex: number | null,
  ): MathObservationSelection {
    if (sortedBlockIndexes.length === 0) {
      return collectDocumentOrderCandidates();
    }
    if (topBlockIndex === null) {
      return { candidates: [], windowStartBlockIndex: null, windowEndBlockIndex: null };
    }

    const candidates: MathObservationBucket[] = [];
    const visibleStart = bottomBlockIndex === null
      ? topBlockIndex
      : Math.min(topBlockIndex, bottomBlockIndex);
    const visibleEnd = bottomBlockIndex === null
      ? topBlockIndex
      : Math.max(topBlockIndex, bottomBlockIndex);
    const visibleSpan = Math.max(0, visibleEnd - visibleStart);
    const padding = Math.max(MATH_OBSERVER_MIN_PADDING_BLOCKS, visibleSpan);
    const windowStartBlockIndex = Math.max(0, visibleStart - padding);
    const windowEndBlockIndex = visibleEnd + padding;
    const startIndex = lowerBound(sortedBlockIndexes, windowStartBlockIndex);

    for (
      let index = startIndex;
      index < sortedBlockIndexes.length && sortedBlockIndexes[index]! <= windowEndBlockIndex;
      index++
    ) {
      const blockIndex = sortedBlockIndexes[index]!;
      for (const bucketIndex of bucketIndexesByBlock.get(blockIndex) ?? []) {
        pushCandidate(bucketIndex, candidates);
        if (candidates.length >= MATH_OBSERVER_WINDOW_CAP) break;
      }
      if (candidates.length >= MATH_OBSERVER_WINDOW_CAP) break;
    }

    return { candidates, windowStartBlockIndex, windowEndBlockIndex };
  }

  function unobserveBucket(bucket: MathObservationBucket): void {
    if (!bucket.observed) return;
    observer.unobserve(bucket.visEl);
    bucket.observed = false;
    activeObservedBuckets.delete(bucket);
  }

  function emitObservationMark(detail: MathObserverWindowStats): void {
    deps.emitMathObserverWindowMark?.(detail);
  }

  function isObservationTelemetryEnabled(): boolean {
    if (!deps.emitMathObserverWindowMark) return false;
    return deps.isMathObserverWindowTelemetryEnabled?.() ?? true;
  }

  function updateMathObservationWindow(
    topBlockIndex: number | null,
    reason: MathObserverWindowReason = "scroll",
    bottomBlockIndex: number | null = topBlockIndex,
    priorTerminalRemovedCount = 0,
  ): MathObserverWindowStats | null {
    if (observerDisconnected) {
      return null;
    }

    const telemetryEnabled = isObservationTelemetryEnabled();
    const updateStart = telemetryEnabled ? nowMs() : 0;
    lastTopBlockIndex = topBlockIndex;
    lastBottomBlockIndex = bottomBlockIndex;
    let terminalRemovedCount = priorTerminalRemovedCount;
    for (const bucket of Array.from(activeObservedBuckets)) {
      if (markBucketTerminal(bucket)) {
        unobserveBucket(bucket);
        terminalRemovedCount++;
      }
    }

    const selection = collectWindowCandidates(topBlockIndex, bottomBlockIndex);
    lastWindowStartBlockIndex = selection.windowStartBlockIndex;
    lastWindowEndBlockIndex = selection.windowEndBlockIndex;
    const desired = new Set(selection.candidates);
    let addedCount = 0;
    let removedCount = 0;

    for (const bucket of Array.from(activeObservedBuckets)) {
      if (!desired.has(bucket)) {
        unobserveBucket(bucket);
        removedCount++;
      }
    }

    for (const bucket of selection.candidates) {
      if (bucket.observed || markBucketTerminal(bucket)) continue;
      observer.observe(bucket.visEl);
      bucket.observed = true;
      activeObservedBuckets.add(bucket);
      addedCount++;
    }

    if (!telemetryEnabled) return null;
    const detail: MathObserverWindowStats = {
      observedCount: activeObservedBuckets.size,
      candidateCount: selection.candidates.length,
      maxObservedCount: MATH_OBSERVER_WINDOW_CAP,
      topBlockIndex,
      bottomBlockIndex,
      windowStartBlockIndex: lastWindowStartBlockIndex,
      windowEndBlockIndex: lastWindowEndBlockIndex,
      addedCount,
      removedCount,
      terminalRemovedCount,
      updateDurationMs: nowMs() - updateStart,
      callbackDurationMs: 0,
      reason,
    };
    emitObservationMark(detail);
    return detail;
  }

  function disconnectObserver(reason: "drain" | "cancel"): void {
    if (observerDisconnected) return;
    observerDisconnected = true;
    observer.disconnect();
    for (const bucket of activeObservedBuckets) {
      bucket.observed = false;
    }
    activeObservedBuckets.clear();
    if (!isObservationTelemetryEnabled()) return;
    emitObservationMark({
      observedCount: 0,
      candidateCount: 0,
      maxObservedCount: MATH_OBSERVER_WINDOW_CAP,
      topBlockIndex: lastTopBlockIndex,
      bottomBlockIndex: lastBottomBlockIndex,
      windowStartBlockIndex: lastWindowStartBlockIndex,
      windowEndBlockIndex: lastWindowEndBlockIndex,
      addedCount: 0,
      removedCount: 0,
      terminalRemovedCount: 0,
      updateDurationMs: 0,
      callbackDurationMs: 0,
      reason,
    });
  }

  updateMathObservationWindow(
    deps.initialObservationTopBlockIndex ?? null,
    "initial",
    deps.initialObservationBottomBlockIndex ?? deps.initialObservationTopBlockIndex ?? null
  );

  // Stop observing a visibility element once ALL of its math has reached a
  // terminal render state. Rendered elements never need re-enqueue, so dropping
  // them from the active window keeps per-scroll intersection work bounded.
  const unobserveCompletedMath = queue.onTaskComplete((node) => {
    const bucket = bucketByElement.get(getVisibilityElement(node));
    if (!bucket || !markBucketTerminal(bucket)) return;
    const wasObserved = bucket.observed;
    unobserveBucket(bucket);
    if (wasObserved) {
      updateMathObservationWindow(lastTopBlockIndex, "complete-backfill", lastBottomBlockIndex, 1);
    }
  });

  let cancelled = false;
  void allMathRendered.then(() => {
    if (!cancelled) {
      disconnectObserver("drain");
    }
  });

  return {
    initialVisibleReady,
    allMathRendered,
    cancel: () => {
      cancelled = true;
      unobserveCompletedMath();
      disconnectObserver("cancel");
      queue.cancel();
    },
    initialVisibleNodes,
    totalMathCount: mathNodes.length,
    isCancelled: () => cancelled,
    updateMathObservationWindow,
  };
}
