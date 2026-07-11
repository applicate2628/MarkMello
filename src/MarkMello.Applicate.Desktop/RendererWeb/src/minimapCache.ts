import type { PostedMinimapState } from "./minimapState";

type MinimapElementStyle = {
  width: string;
  transform: string;
};

type MinimapViewportStyle = {
  height: string;
  transform: string;
};

export type MinimapSnapshotProvenance =
  | { source: "live-dom"; modelGeneration: null }
  | { source: "model-fragment"; modelGeneration: number };

export type CachedMinimapSnapshot = {
  content: DocumentFragment;
  documentHeight: number;
  lastPostedState: PostedMinimapState;
  contentStyle: MinimapElementStyle;
  viewportStyle: MinimapViewportStyle;
  provenance: MinimapSnapshotProvenance;
};

export type RestoredMinimapSnapshot = {
  contentNodeCount: number;
  documentHeight: number;
  lastPostedState: PostedMinimapState;
};

function isMinimapSnapshotProvenance(value: unknown): value is MinimapSnapshotProvenance {
  if (value === null || typeof value !== "object") {
    return false;
  }

  const candidate = value as { modelGeneration?: unknown; source?: unknown };
  if (candidate.source === "live-dom") {
    return candidate.modelGeneration === null;
  }
  const modelGeneration = candidate.modelGeneration;
  return candidate.source === "model-fragment"
    && typeof modelGeneration === "number"
    && Number.isSafeInteger(modelGeneration)
    && modelGeneration >= 0;
}

export function captureMinimapSnapshot(input: {
  ownerDocument: Document;
  minimapContent: HTMLElement | null;
  minimapViewport: HTMLElement | null;
  documentHeight: number;
  lastPostedState: PostedMinimapState;
  provenance: MinimapSnapshotProvenance | null;
}): CachedMinimapSnapshot | null {
  if (!input.minimapContent
    || input.minimapContent.childNodes.length === 0
    || !isMinimapSnapshotProvenance(input.provenance)) {
    return null;
  }

  const nodes = Array.from(input.minimapContent.childNodes);
  const content = input.ownerDocument.createDocumentFragment();
  content.append(...nodes.map(node => node.cloneNode(true)));

  return {
    content,
    documentHeight: input.documentHeight,
    lastPostedState: { ...input.lastPostedState },
    contentStyle: {
      width: input.minimapContent.style.width,
      transform: input.minimapContent.style.transform,
    },
    provenance: { ...input.provenance },
    viewportStyle: {
      height: input.minimapViewport?.style.height ?? "",
      transform: input.minimapViewport?.style.transform ?? "",
    },
  };
}

export function restoreMinimapSnapshot(
  snapshot: CachedMinimapSnapshot,
  input: {
    minimapContent: HTMLElement | null;
    minimapViewport: HTMLElement | null;
  }
): RestoredMinimapSnapshot | null {
  if (!input.minimapContent) {
    return null;
  }

  const contentNodeCount = snapshot.content.childNodes.length;
  input.minimapContent.replaceChildren(snapshot.content.cloneNode(true));
  input.minimapContent.style.width = snapshot.contentStyle.width;
  input.minimapContent.style.transform = snapshot.contentStyle.transform;

  if (input.minimapViewport) {
    input.minimapViewport.style.height = snapshot.viewportStyle.height;
    input.minimapViewport.style.transform = snapshot.viewportStyle.transform;
  }

  return {
    contentNodeCount,
    documentHeight: snapshot.documentHeight,
    lastPostedState: { ...snapshot.lastPostedState },
  };
}
