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

export type InitialRenderPipelineDeps = {
  getCurrentTheme: () => "light" | "dark";
  applyTheme: (theme: "light" | "dark") => void;
  initMermaidWithTheme: (theme: "light" | "dark") => void;
  renderMath: () => MathReadinessController;
  renderMermaid: () => Promise<void>;
  renderCodeBlocks: () => void;
  scheduleLayoutReady: () => void;
};

export async function runInitialRenderPipeline(deps: InitialRenderPipelineDeps): Promise<void> {
  const theme = deps.getCurrentTheme();
  deps.applyTheme(theme);
  deps.initMermaidWithTheme(theme);

  const mathController = deps.renderMath();
  try {
    await deps.renderMermaid();
  } catch {
    // mermaid failure is non-fatal — fallbacks already applied in renderMermaid
  }
  deps.renderCodeBlocks();

  await mathController.initialVisibleReady;
  deps.scheduleLayoutReady();
}
