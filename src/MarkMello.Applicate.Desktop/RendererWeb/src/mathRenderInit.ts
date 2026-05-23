import type { MathReadinessController } from "./initialRenderPipeline";
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
};

const INITIAL_LOOKAHEAD_PX = 500;

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

function rafYield(): Promise<void> {
  return new Promise(r => window.requestAnimationFrame(() => r()));
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
  for (const node of mathNodes) {
    if (isTerminalMathState(node.dataset["mmMathRendered"])) {
      continue;
    }

    const visEl = getVisibilityElement(node);
    const rect = visEl.getBoundingClientRect();
    const tex = node.dataset["tex"] ?? "";
    const task: MathRenderTask = {
      node,
      tex,
      displayMode: node.classList.contains("math-display"),
    };
    if (rect.bottom >= -INITIAL_LOOKAHEAD_PX && rect.top <= viewportHeight + INITIAL_LOOKAHEAD_PX) {
      initialVisibleNodes.add(node);
      queue.enqueue(task, "high");
    } else {
      queue.enqueue(task, "low");
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

  // 6. IntersectionObserver: observe one entry per unique visibility element
  //    (parent for inline math); on intersection event, re-enqueue the
  //    associated math nodes with the appropriate priority. Terminal-state
  //    nodes are skipped. The frozen initial-visible set is NOT extended.
  const observedToMathNodes = new Map<HTMLElement, HTMLElement[]>();
  for (const node of mathNodes) {
    const visEl = getVisibilityElement(node);
    const bucket = observedToMathNodes.get(visEl) ?? [];
    bucket.push(node);
    observedToMathNodes.set(visEl, bucket);
  }
  const observer = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      const visEl = entry.target as HTMLElement;
      const targets = observedToMathNodes.get(visEl);
      if (!targets) continue;
      for (const targetNode of targets) {
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
    isCancelled: () => cancelled,
  };
}
