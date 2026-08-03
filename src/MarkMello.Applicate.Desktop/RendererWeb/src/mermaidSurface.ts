import { LIVE_DOCUMENT_ROOT_SELECTOR } from "./topVisibleBlockIndex";

// ONE RENDER DECISION PER DIAGRAM.
//
// A mermaid diagram exists on more than one surface: once in the live document the
// reader scrolls, and once inside the minimap's clone of that document. The eager/lazy
// predicate asks "is this ELEMENT near the viewport", so one diagram gets two answers —
// the clone is a shrunken second rendering of the whole document sitting INSIDE the
// viewport, so its eager window is ~8x wider than the live one and does not move with
// the reader. That is a single-owner violation, and no correction of the rectangle fixes
// it: the fix is to make exactly one element decide.
//
// This leaf owns the classification that makes that possible, and the pairing key that
// lets a decision's RESULT reach the other surface. It knows nothing about mermaid
// rendering and nothing about the minimap's geometry.

export type MermaidSurfaceClass = "live" | "follower" | "unscoped";

export type MermaidSurfaceRoots = {
  // `body > main.mm-document` — the document the reader scrolls.
  readonly liveRoot: Element | null;
  // `div.mm-minimap-content` — the mount point of the clone.
  readonly followerRoot: Element | null;
};

export type MermaidSurfacePartition = {
  // live + unscoped, in the caller's original (document) order. Only these decide.
  readonly deciding: HTMLElement[];
  readonly live: number;
  readonly follower: number;
  readonly unscoped: number;
};

export function resolveLiveDocumentRoot(ownerDocument: Document): HTMLElement | null {
  return ownerDocument.querySelector<HTMLElement>(LIVE_DOCUMENT_ROOT_SELECTOR);
}

// Three-way, not binary. `unscoped` — a node on neither known surface — keeps TODAY'S
// behaviour (it decides for itself), so only-forward does not depend on this enumeration
// being complete; and it is counted, so an unenumerated surface shows up as a number
// rather than as silence.
export function classifyMermaidSurface(node: Node, roots: MermaidSurfaceRoots): MermaidSurfaceClass {
  if (roots.liveRoot !== null && roots.liveRoot.contains(node)) {
    return "live";
  }
  if (roots.followerRoot !== null && roots.followerRoot.contains(node)) {
    return "follower";
  }
  return "unscoped";
}

export function partitionMermaidNodesBySurface(
  nodes: readonly HTMLElement[],
  roots: MermaidSurfaceRoots
): MermaidSurfacePartition {
  const deciding: HTMLElement[] = [];
  let live = 0;
  let follower = 0;
  let unscoped = 0;
  for (const node of nodes) {
    const surface = classifyMermaidSurface(node, roots);
    if (surface === "follower") {
      follower++;
      continue;
    }
    if (surface === "live") {
      live++;
    } else {
      unscoped++;
    }
    deciding.push(node);
  }
  return { deciding, live, follower, unscoped };
}

// The pairing key is `data-mm-block-index`. It is emitted on every `pre.mm-mermaid` by
// ApplicateHtmlMarkdownRenderer.BlockDataAttributes, is document-unique (one counter,
// incremented once per block, and progressive append slices an already-rendered body
// rather than renumbering), and survives cloning — sanitizeMinimapCloneTree removes
// `id`, the TH/TD cell attributes and `contenteditable`, never this one. It is already
// load-bearing on both sides: minimap drag maps clone-Y to document-Y through it.
export function resolveMermaidMirrors(
  node: HTMLElement,
  followerRoot: ParentNode | null
): HTMLElement[] {
  if (followerRoot === null) {
    return [];
  }
  const key = readMermaidBlockIndexKey(node);
  if (key === null) {
    return [];
  }
  const mirrors: HTMLElement[] = [];
  const candidates = followerRoot.querySelectorAll<HTMLElement>(
    `pre.mm-mermaid[data-mm-block-index="${key}"]`
  );
  for (const candidate of candidates) {
    if (candidate !== node) {
      mirrors.push(candidate);
    }
  }
  return mirrors;
}

// The producer emits a decimal integer. Refusing anything else keeps the composed
// selector well-formed BY CONSTRUCTION, so twin resolution is total: it can never throw
// a selector error into a render path and turn a rendered diagram into a failed one.
function readMermaidBlockIndexKey(node: HTMLElement): string | null {
  const raw = node.dataset["mmBlockIndex"];
  if (raw === undefined) {
    return null;
  }
  return /^\d+$/.test(raw) ? raw : null;
}
