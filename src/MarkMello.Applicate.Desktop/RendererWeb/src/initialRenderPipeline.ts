export type MathReadinessController = {
  initialVisibleReady: Promise<void>;
  allMathRendered: Promise<void>;
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
};

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
  if (shouldRunMermaid) {
    try {
      await deps.renderMermaid();
    } catch {
      // mermaid failure is non-fatal — fallbacks already applied in renderMermaid
    }
  }
  deps.renderCodeBlocks();

  await mathController.initialVisibleReady;
  deps.scheduleLayoutReady();
}
