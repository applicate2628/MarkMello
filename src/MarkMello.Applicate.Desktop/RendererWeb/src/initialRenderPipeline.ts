export type InitialRenderPipelineDeps = {
  getCurrentTheme: () => "light" | "dark";
  applyTheme: (theme: "light" | "dark") => void;
  initMermaidWithTheme: (theme: "light" | "dark") => void;
  renderMath: () => void;
  renderMermaid: () => Promise<void>;
  renderCodeBlocks: () => void;
  scheduleLayoutReady: () => void;
};

export async function runInitialRenderPipeline(deps: InitialRenderPipelineDeps): Promise<void> {
  const theme = deps.getCurrentTheme();
  deps.applyTheme(theme);
  deps.initMermaidWithTheme(theme);
  deps.renderMath();
  try {
    await deps.renderMermaid();
  } catch {
    // mermaid failure is non-fatal — fallbacks already applied in renderMermaid
  }
  deps.renderCodeBlocks();
  deps.scheduleLayoutReady();
}
