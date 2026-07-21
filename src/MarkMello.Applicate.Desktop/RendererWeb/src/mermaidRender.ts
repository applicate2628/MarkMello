export type MermaidApiLike = {
  render: (id: string, source: string) => Promise<{ svg: string }>;
};

export function isMermaidNodeNearViewport(
  node: HTMLElement,
  viewportHeight: number,
  marginPx: number
): boolean {
  const rect = node.getBoundingClientRect();
  return rect.bottom >= -marginPx && rect.top <= viewportHeight + marginPx;
}

export async function renderMermaidNode(
  node: HTMLElement,
  generation: number,
  getCurrentGeneration: () => number,
  mermaid: MermaidApiLike,
  onLayoutBoxChange?: () => void
): Promise<void> {
  const codeEl = node.querySelector<HTMLElement>("code[data-mm-mermaid]");
  if (!codeEl) return;
  const source = codeEl.textContent ?? "";

  try {
    const id = `mm-mermaid-${generation}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    // Completion is the settle of mermaid.render itself, never a deadline. mermaid
    // 11.15.0 resolves only once renderer.draw() has produced the SVG, and rethrows
    // every parse failure and every draw failure, so a single settle per diagram is
    // an exhaustive success/failure signal bounded by the render's own work.
    // mermaid exposes no cancellation handle, so a deadline could never stop a slow
    // render - only discard a result that was still coming, which is exactly how
    // documents that render fine ended up rejected at the export barrier.
    const { svg } = await mermaid.render(id, source);

    if (getCurrentGeneration() !== generation) return;  // stale, abort

    let svgHost = node.nextElementSibling as HTMLElement | null;
    if (!svgHost || !svgHost.classList.contains("mm-mermaid-svg")) {
      svgHost = document.createElement("div");
      svgHost.className = "mm-mermaid-svg";
      node.after(svgHost);
    }
    svgHost.innerHTML = svg;
    const wasRendered = node.classList.contains("is-rendered");
    node.classList.add("is-rendered");
    if (!wasRendered) onLayoutBoxChange?.();
  } catch {
    if (getCurrentGeneration() !== generation) return;
    const wasRendered = node.classList.contains("is-rendered");
    node.classList.remove("is-rendered");
    const sibling = node.nextElementSibling;
    if (sibling?.classList.contains("mm-mermaid-svg")) sibling.remove();
    if (wasRendered) onLayoutBoxChange?.();
  }
}
