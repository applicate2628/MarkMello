import type { PostedMinimapState } from "./minimapState";

type MinimapElementStyle = {
  width: string;
  transform: string;
};

type MinimapViewportStyle = {
  height: string;
  transform: string;
};

export type CachedMinimapSnapshot = {
  content: DocumentFragment;
  documentHeight: number;
  lastPostedState: PostedMinimapState;
  contentStyle: MinimapElementStyle;
  viewportStyle: MinimapViewportStyle;
};

export type RestoredMinimapSnapshot = {
  contentNodeCount: number;
  documentHeight: number;
  lastPostedState: PostedMinimapState;
};

export function captureMinimapSnapshot(input: {
  ownerDocument: Document;
  minimapContent: HTMLElement | null;
  minimapViewport: HTMLElement | null;
  documentHeight: number;
  lastPostedState: PostedMinimapState;
}): CachedMinimapSnapshot | null {
  if (!input.minimapContent || input.minimapContent.childNodes.length === 0) {
    return null;
  }

  const nodes = Array.from(input.minimapContent.childNodes);
  const content = input.ownerDocument.createDocumentFragment();
  content.append(...nodes);

  return {
    content,
    documentHeight: input.documentHeight,
    lastPostedState: { ...input.lastPostedState },
    contentStyle: {
      width: input.minimapContent.style.width,
      transform: input.minimapContent.style.transform,
    },
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
  input.minimapContent.replaceChildren(snapshot.content);
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
