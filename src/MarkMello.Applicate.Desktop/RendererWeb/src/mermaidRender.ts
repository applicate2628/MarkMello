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
  perDiagramTimeoutMs: number
): Promise<void> {
  const codeEl = node.querySelector<HTMLElement>("code[data-mm-mermaid]");
  if (!codeEl) return;
  const source = codeEl.textContent ?? "";

  let timeoutHandle: ReturnType<typeof setTimeout> | undefined;
  try {
    const id = `mm-mermaid-${generation}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    const timeoutPromise = new Promise<never>((_, reject) => {
      timeoutHandle = setTimeout(() => reject(new Error("mermaid render timeout")), perDiagramTimeoutMs);
    });
    const { svg } = await Promise.race([mermaid.render(id, source), timeoutPromise]);

    if (getCurrentGeneration() !== generation) return;  // stale, abort

    let svgHost = node.nextElementSibling as HTMLElement | null;
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
    if (timeoutHandle !== undefined) clearTimeout(timeoutHandle);
  }
}
