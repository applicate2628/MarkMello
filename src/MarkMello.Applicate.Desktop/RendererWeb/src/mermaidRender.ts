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

// A diagram lives on more than one surface, but it is rendered ONCE. The port below
// hands renderMermaidNode the mirrors of the node it just decided; the caller owns
// which surfaces exist, this leaf owns what a rendered slot IS. Injected, never
// imported: this module keeps zero knowledge of the minimap.
export type MermaidMirrorResolver = (node: HTMLElement) => readonly HTMLElement[];

const MIRROR_ID_SUFFIX = "-mm-mirror-";

// A pure IDREF attribute carries bare ids, not `#id` references, so it needs its own
// rewrite. Measured absent from the corpus's SVGs (0 occurrences) — handled anyway,
// because the mechanical rewrite's whole point is that it assumes nothing about what
// produced the markup.
const IDREF_ATTRIBUTES = new Set([
  "aria-labelledby",
  "aria-describedby",
  "aria-owns",
  "aria-controls",
  "aria-flowto"
]);

// `#` followed by the MAXIMAL run of identifier characters. Maximal matters: mermaid
// derives ids by suffixing the root render id, so one id is a strict prefix of another
// ("X" vs "X_flowchart-v2-pointEnd"). Capturing the whole run and looking THAT up means
// the short id can never be rewritten inside the long one. Colour literals (#fff,
// #1f2937) simply miss the map and are left alone.
const ID_REFERENCE_PATTERN = /#([A-Za-z0-9_\-\u0080-\uFFFF]+)/g;

function mermaidSlotHost(node: HTMLElement): HTMLElement | null {
  const sibling = node.nextElementSibling;
  if (sibling === null || !(sibling instanceof HTMLElement)) return null;
  return sibling.classList.contains("mm-mermaid-svg") ? sibling : null;
}

// A rendered mermaid SVG carries ~95 ids and addresses them from inside itself: 47
// `marker-end="url(#…)"` refs and 70 `#id` selectors in its own <style> block, measured
// on the corpus's largest diagram. A DOM copy therefore duplicates every one of them,
// and the duplication is NOT benign:
//
//  - `url(#X)` resolves FIRST-IN-TREE-ORDER, so whichever subtree is second silently
//    borrows the first one's markers. Measured both directions: 117 of 117 references
//    cross the boundary, and recolouring the ORIGINAL's marker repainted the untouched
//    COPY (531 px changed).
//  - a `<style>` `#X` selector is document-scoped CSS and matches BOTH copies.
//  - getComputedStyle cannot see any of this: it returns the specified `url(#…)` token
//    regardless of what the reference resolves to, so correctly-bound, wrongly-bound and
//    dangling copies all report the same string.
//  - and the wrong copy renders IDENTICALLY, and even self-heals when its source is
//    removed, because its own duplicate ids then become the only match.
//
// So the copy is made self-contained by construction: every id in it is renamed and
// every reference to that id rewritten, leaving nothing inside the copy that can resolve
// to an element outside it.
export function makeCopiedSlotSelfContained(copy: Element, suffix: string): void {
  const elements: Element[] = [copy, ...Array.from(copy.querySelectorAll("*"))];
  const renamed = new Map<string, string>();
  for (const element of elements) {
    const id = element.getAttribute("id");
    if (id === null || id === "" || renamed.has(id)) continue;
    renamed.set(id, id + suffix);
  }
  if (renamed.size === 0) return;

  for (const element of elements) {
    const id = element.getAttribute("id");
    const renamedId = id === null ? undefined : renamed.get(id);
    if (renamedId !== undefined) element.setAttribute("id", renamedId);

    for (const attribute of Array.from(element.attributes)) {
      if (attribute.name === "id") continue;
      const value = attribute.value;
      const rewritten = rewriteIdReferences(value, renamed, IDREF_ATTRIBUTES.has(attribute.name));
      if (rewritten === value) continue;
      if (attribute.namespaceURI) element.setAttributeNS(attribute.namespaceURI, attribute.name, rewritten);
      else element.setAttribute(attribute.name, rewritten);
    }

    // Only a <style> element's text can carry an id REFERENCE. Every other text node is
    // content the reader sees, and rewriting it would make the minimap show something
    // the document does not.
    if (element.tagName.toLowerCase() !== "style") continue;
    const css = element.textContent ?? "";
    const rewrittenCss = rewriteIdReferences(css, renamed, false);
    if (rewrittenCss !== css) element.textContent = rewrittenCss;
  }
}

function rewriteIdReferences(
  value: string,
  renamed: ReadonlyMap<string, string>,
  isIdRefList: boolean
): string {
  if (isIdRefList) {
    const trimmed = value.trim();
    if (trimmed === "") return value;
    return trimmed.split(/\s+/).map((token) => renamed.get(token) ?? token).join(" ");
  }
  if (!value.includes("#")) return value;
  return value.replace(ID_REFERENCE_PATTERN, (match, name: string) => {
    const renamedId = renamed.get(name);
    return renamedId === undefined ? match : `#${renamedId}`;
  });
}

// Install the source node's SETTLED slot into each mirror. The slot is the whole
// contract — the `is-rendered` class that hides the <pre>, and the `.mm-mermaid-svg`
// sibling that shows the result — so all three render outcomes propagate through here
// and the mirror can never disagree with its source:
//
//   success  -> a self-contained copy of the rendered host, mirror is-rendered
//   refusal  -> a copy of the oversize placeholder, mirror is-rendered (same as source)
//   failure  -> host removed, is-rendered removed (source shows raw text; so must the mirror)
//
// A success-only mirror would leave the minimap showing a stale diagram where the
// document shows raw source after a failed theme re-render.
export function mirrorMermaidSlot(node: HTMLElement, mirrors: readonly HTMLElement[]): void {
  const sourceHost = mermaidSlotHost(node);
  const sourceRendered = node.classList.contains("is-rendered");
  for (let index = 0; index < mirrors.length; index++) {
    const mirror = mirrors[index]!;
    if (mirror === node) continue;
    const existingHost = mermaidSlotHost(mirror);
    if (!sourceRendered || sourceHost === null) {
      existingHost?.remove();
      mirror.classList.remove("is-rendered");
      continue;
    }
    const copy = sourceHost.cloneNode(true) as HTMLElement;
    makeCopiedSlotSelfContained(copy, `${MIRROR_ID_SUFFIX}${index}`);
    if (existingHost === null) mirror.after(copy);
    else existingHost.replaceWith(copy);
    mirror.classList.add("is-rendered");
  }
}

function propagateMermaidSlot(node: HTMLElement, resolveMirrors?: MermaidMirrorResolver): void {
  if (resolveMirrors === undefined) return;
  const mirrors = resolveMirrors(node);
  if (mirrors.length === 0) return;
  mirrorMermaidSlot(node, mirrors);
}

export async function renderMermaidNode(
  node: HTMLElement,
  generation: number,
  getCurrentGeneration: () => number,
  mermaid: MermaidApiLike,
  onLayoutBoxChange?: () => void,
  resolveMirrors?: MermaidMirrorResolver
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
    propagateMermaidSlot(node, resolveMirrors);
    if (!wasRendered) onLayoutBoxChange?.();
    return;
  }

  // The mirror install sits AFTER the try/catch, not inside it, for two reasons that
  // both matter: one settled slot is propagated regardless of which outcome produced it,
  // and a defect in the copy can never be caught as a render failure and silently turn a
  // correctly rendered LIVE diagram back into raw source. It runs BEFORE
  // onLayoutBoxChange so a single geometry-change signal covers both surfaces' boxes.
  let notifyLayoutBoxChange = false;
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
    notifyLayoutBoxChange = !wasRendered;
  } catch {
    if (getCurrentGeneration() !== generation) return;
    const wasRendered = node.classList.contains("is-rendered");
    node.classList.remove("is-rendered");
    const sibling = node.nextElementSibling;
    if (sibling?.classList.contains("mm-mermaid-svg")) sibling.remove();
    notifyLayoutBoxChange = wasRendered;
  }

  propagateMermaidSlot(node, resolveMirrors);
  if (notifyLayoutBoxChange) onLayoutBoxChange?.();
}
