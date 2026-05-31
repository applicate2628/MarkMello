export type MathReadinessController = {
  initialVisibleReady: Promise<void>;
  allMathRendered: Promise<void>;
  initialVisualSettleReady?: Promise<void>;
  cancel: () => void;
  // Stats for lifecycle marks (mm-initial-visible-ready, mm-all-math-rendered).
  // initialVisibleNodes is the FROZEN set captured at construction time.
  // Renderer derives failedCount by scanning dataset.mmMathRendered === "failed"
  // against these surfaces (single source of truth — no duplicate classification).
  initialVisibleNodes: ReadonlySet<HTMLElement>;
  totalMathCount: number;
  isCancelled(): boolean;
};

export type RendererTheme = "light" | "dark" | "classic-white";

export type InitialRenderPipelineDeps = {
  getCurrentTheme: () => RendererTheme;
  applyTheme: (theme: RendererTheme) => void;
  initMermaidWithTheme: (theme: RendererTheme) => void;
  renderMath: () => MathReadinessController;
  renderMermaid: () => Promise<void>;
  renderCodeBlocks: () => void;
  scheduleLayoutReady: () => void;
  // Heavy document enhancements (Mermaid + hljs) are useful, but they must not
  // hold the first readable paint for large full-DOM documents.
  deferPostReadyWork?: ((work: () => void) => void) | undefined;
  notifyPostReadyEnhancementsComplete?: (() => void) | undefined;
  // Hidden/offscreen WebView paint can stall requestAnimationFrame-backed math
  // readiness. Math must improve first paint, not deadlock document reveal.
  initialVisibleReadyTimeoutMs?: number | undefined;
  // PE r2 item G — host-provided per-document flag. `undefined` defaults to
  // RUN mermaid (backward-compat: older host paths or first-preferences
  // bootstrap before any load-document carry no flag). `false` skips both
  // mermaid init and mermaid render for the entire pipeline. `true` runs
  // mermaid. The guard is `!== false`, never `=== true`. The `| undefined`
  // suffix is required under `exactOptionalPropertyTypes` so callers can
  // pass `hasMermaid: someBoolOrUndefined` via shorthand without TS2379.
  hasMermaid?: boolean | undefined;
  // PE r2 item C bridge — when the guard skips mermaid, the pipeline emits
  // a renderer-side perf mark so the host's [renderer-perf] log stream
  // shows `mermaid-skipped hasMermaid=false`. Optional so callers that
  // don't need the trace (test harness) can omit it; `| undefined` for the
  // same exactOptionalPropertyTypes reason as `hasMermaid` above.
  postPerfMark?: ((name: string, detail?: Record<string, unknown>) => void) | undefined;
  // The host may reset the document while an older first-preferences pipeline
  // is still awaiting math readiness. This guard lets the owner suppress stale
  // layout/post-ready work instead of letting it race the current renderId.
  isCurrent?: (() => boolean) | undefined;
};

const DEFAULT_INITIAL_VISIBLE_READY_TIMEOUT_MS = 1200;
const DEFAULT_INITIAL_VISUAL_SETTLE_TIMEOUT_MS = 1800;

function deferPostReadyWork(deps: InitialRenderPipelineDeps, work: () => void): void {
  if (deps.deferPostReadyWork) {
    deps.deferPostReadyWork(work);
    return;
  }

  globalThis.setTimeout(work, 0);
}

function isCurrentPipeline(deps: InitialRenderPipelineDeps): boolean {
  return deps.isCurrent?.() !== false;
}

async function runPostReadyEnhancements(
  deps: InitialRenderPipelineDeps,
  shouldRunMermaid: boolean
): Promise<void> {
  if (!isCurrentPipeline(deps)) return;
  deps.postPerfMark?.("post-ready-enhancements-start", { hasMermaid: shouldRunMermaid });
  try {
    if (shouldRunMermaid) {
      try {
        await deps.renderMermaid();
      } catch {
        // Mermaid failure is non-fatal; fall through so hljs can still prepare
        // regular code blocks and mermaid source fallbacks.
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

async function waitForInitialVisibleReady(
  deps: InitialRenderPipelineDeps,
  mathController: MathReadinessController
): Promise<void> {
  const timeoutMs = deps.initialVisibleReadyTimeoutMs ?? DEFAULT_INITIAL_VISIBLE_READY_TIMEOUT_MS;
  if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) {
    await mathController.initialVisibleReady;
    return;
  }

  let timeout: ReturnType<typeof globalThis.setTimeout> | undefined;
  const result = await Promise.race<"ready" | "timeout">([
    mathController.initialVisibleReady.then(() => "ready"),
    new Promise<"timeout">(resolve => {
      timeout = globalThis.setTimeout(() => resolve("timeout"), timeoutMs);
    })
  ]);
  if (timeout !== undefined) {
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

async function waitForInitialVisualSettle(
  deps: InitialRenderPipelineDeps,
  mathController: MathReadinessController
): Promise<void> {
  const visualSettleReady = mathController.initialVisualSettleReady;
  if (!visualSettleReady) {
    return;
  }

  const timeoutMs = DEFAULT_INITIAL_VISUAL_SETTLE_TIMEOUT_MS;
  let timeout: ReturnType<typeof globalThis.setTimeout> | undefined;
  const result = await Promise.race<"ready" | "timeout" | "error">([
    visualSettleReady.then(() => "ready", () => "error"),
    new Promise<"timeout">(resolve => {
      timeout = globalThis.setTimeout(() => resolve("timeout"), timeoutMs);
    })
  ]);
  if (timeout !== undefined) {
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

export async function runInitialRenderPipeline(deps: InitialRenderPipelineDeps): Promise<void> {
  const theme = deps.getCurrentTheme();
  deps.applyTheme(theme);
  // PE r2 item G — guard BOTH unconditional mermaid calls (init + render).
  // Without the init guard, mermaid.initialize still runs per-document even
  // when no mermaid blocks exist (Sonnet r2 finding 2). `!== false` defaults
  // undefined to "run", preserving behavior for older docs / call sites that
  // don't carry the flag.
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
    void runPostReadyEnhancements(deps, shouldRunMermaid)
      .then(() => {
        if (!isCurrentPipeline(deps)) return;
        deps.notifyPostReadyEnhancementsComplete?.();
        return waitForInitialVisualSettle(deps, mathController);
      });
  });
}
