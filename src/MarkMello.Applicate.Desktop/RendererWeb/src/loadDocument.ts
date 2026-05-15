export type LoadDocumentMessage = {
  html: string;
  documentName?: string;
};

export type LoadDocumentDeps = {
  runInitialRenderPipeline: () => Promise<void>;
  cancelCurrentMathController: () => void;
  resetModuleGlobals: () => void;
  scrollWindowToTop: () => void;
  emitMark: (name: string, detail?: Record<string, unknown>) => void;
  ensureChromeNodes: () => void;
};

export function applyLoadDocument(message: LoadDocumentMessage, deps: LoadDocumentDeps): void {
  const main = document.querySelector<HTMLElement>("main.mm-document");
  if (!main) {
    return;
  }

  deps.emitMark("mm-load-document", {
    documentName: message.documentName ?? "",
    htmlLength: message.html.length,
  });

  // Cancel before swap — the in-flight MathReadinessController owns Promises
  // that observers will resolve from the about-to-be-discarded DOM nodes.
  // Failing to cancel keeps frozen initialVisibleNodes pointing into the
  // detached subtree, producing phantom math marks against the previous doc.
  deps.cancelCurrentMathController();
  deps.resetModuleGlobals();

  // Body swap (single innerHTML write). Minimap aside / width-handle / drop-overlay
  // are siblings of <main> under <body>, so they survive this swap. Their event
  // wiring (document-bound + window-bound listeners from wireLinks / wireFileDrop
  // etc.) survives too because the swap does not touch document or window.
  main.innerHTML = message.html;

  // Re-anchor any chrome nodes that depend on the new body geometry (width-handle
  // position references the .mm-document bounding rect; minimap re-clones the new
  // contents on its Phase A/B rebuild). The ensureChromeNodes() callback wraps
  // ensureMinimap() / ensureWidthHandle() / ensureDropOverlay() so they recreate
  // detached nodes if a previous call accidentally removed them.
  deps.ensureChromeNodes();

  // Reset scroll to top — host owns scroll restore via subsequent
  // scroll-to-progress message after document-ready.
  deps.scrollWindowToTop();

  // Re-run the initial render pipeline against the new body. The pipeline owns
  // math, mermaid, code-block, layout-ready, and document-ready emission.
  void deps.runInitialRenderPipeline();
}

export function clearDocumentState(deps: LoadDocumentDeps): void {
  const main = document.querySelector<HTMLElement>("main.mm-document");
  deps.emitMark("mm-clear-document");
  deps.cancelCurrentMathController();
  deps.resetModuleGlobals();
  if (main) {
    main.innerHTML = "";
  }
}
