import { assessMermaidRenderCost, type MermaidCostVerdict } from "./mermaidCostGuard";

export type MermaidApiLike = {
  render: (id: string, source: string) => Promise<{ svg: string }>;
};

// A refused (over-cap) diagram never reaches mermaid.render; instead its slot gets a
// transparent, export-safe placeholder that names the size and the limit and keeps the
// raw source copyable. Plain DOM + text only: no <img>, no url(), nothing the HTML/PDF
// export snapshot's resource validator would reject, so it survives export unchanged
// via the same is-rendered/sibling mechanism a real diagram uses.
function renderOversizePlaceholder(
  node: HTMLElement,
  source: string,
  verdict: MermaidCostVerdict
): void {
  let host = node.nextElementSibling as HTMLElement | null;
  if (!host || !host.classList.contains("mm-mermaid-svg")) {
    host = document.createElement("div");
    host.className = "mm-mermaid-svg";
    node.after(host);
  }
  host.classList.add("mm-mermaid-oversize");
  host.replaceChildren();

  const noun = verdict.kind === "nodes" ? "nodes" : "elements";
  const note = document.createElement("div");
  note.className = "mm-mermaid-oversize-note";
  note.setAttribute("role", "note");
  note.textContent =
    `Diagram too large to render safely (${verdict.count} ${noun}, limit ${verdict.limit}). ` +
    `Rendering it would freeze the app, so the source is shown instead.`;

  const src = document.createElement("pre");
  src.className = "mm-mermaid-oversize-source";
  const code = document.createElement("code");
  code.textContent = source;
  src.appendChild(code);

  host.append(note, src);
}

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

  // Refuse a diagram whose layout would synchronously freeze the renderer thread
  // BEFORE calling mermaid.render (see mermaidCostGuard.ts). The freeze is CPU-bound
  // and un-cancellable once started, so the only defence is to not start it. A refused
  // diagram is marked is-rendered like a real one, so the export barrier and every lazy
  // sweep (their selector is `:not(.is-rendered)`) skip it and never re-attempt it.
  const cost = assessMermaidRenderCost(source);
  if (cost.refuse) {
    const wasRendered = node.classList.contains("is-rendered");
    renderOversizePlaceholder(node, source, cost);
    node.classList.add("is-rendered");
    if (!wasRendered) onLayoutBoxChange?.();
    return;
  }

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
