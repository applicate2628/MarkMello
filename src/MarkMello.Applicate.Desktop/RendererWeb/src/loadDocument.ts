export type LoadDocumentMessage = {
  html?: string;
  documentName?: string;
  theme?: "light" | "dark" | "classic-white";
  renderId?: number;
  cacheKey?: string | null;
  skipFrameWait?: boolean;
  // PE r2 item G — host-provided per-document mermaid presence flag,
  // populated from C#'s `body.HasMermaidBlock` at the IPC boundary
  // (ApplicateWebMarkdownDocumentView.cs:557, IPC type at renderer.ts:108).
  // Threaded down into runInitialRenderPipeline so its mermaid init/render
  // calls skip when false. `undefined` defaults to running (backward-compat
  // for older docs that don't carry the flag).
  hasMermaid?: boolean;
  hasHljs?: boolean;
};

export type LoadDocumentDeps = {
  // PE r2 item G — accepts the per-document `hasMermaid` so the deps
  // closure in renderer.ts can build InitialRenderPipelineDeps with the
  // mermaid guard set correctly for this specific load. Omitting the arg
  // (e.g. test harness, first-reading-preferences bootstrap) leaves the
  // pipeline at the "run mermaid" default.
  runInitialRenderPipeline: (hasMermaid?: boolean, skipFrameWait?: boolean, renderId?: number, hasHljs?: boolean, ownsCompleteFreshBody?: boolean) => Promise<void>;
  cancelCurrentMathController: () => void;
  resetModuleGlobals: () => void;
  scrollWindowToTop: () => void;
  emitMark: (name: string, detail?: Record<string, unknown>) => void;
  ensureChromeNodes: (useCachedDocumentState?: boolean, options?: { allowVirtualization?: boolean }) => void;
  applyTheme: (theme: "light" | "dark" | "classic-white") => void;
  debugLog: (text: string) => void;
  preserveCurrentDocumentCache?: () => void;
  getCachedDocumentFragment?: (cacheKey: string) => DocumentFragment | undefined;
  setCurrentDocumentCacheKey?: (cacheKey: string | null) => void;
  restoreCachedScrollPosition?: () => void;
  completeCachedDocumentLoad?: (renderId?: number, hasMermaid?: boolean, hasHljs?: boolean, skipFrameWait?: boolean) => void;
  notifyDocumentCacheMiss?: (renderId?: number, cacheKey?: string) => void;
};

export function applyLoadDocument(message: LoadDocumentMessage, deps: LoadDocumentDeps): void {
  const main = document.querySelector<HTMLElement>("main.mm-document");
  if (!main) {
    return;
  }

  deps.emitMark("mm-load-document", {
    documentName: message.documentName ?? "",
    htmlLength: message.html?.length ?? 0,
    renderId: message.renderId ?? null,
  });
  deps.debugLog(`load-document:start id=${message.renderId ?? "(none)"} name=${message.documentName ?? ""} theme=${message.theme ?? "(none)"} currentTheme=${document.documentElement.dataset.theme ?? "(none)"} htmlLength=${message.html?.length ?? 0}`);

  // Cancel before swap — the in-flight MathReadinessController owns Promises
  // that observers will resolve from the about-to-be-discarded DOM nodes.
  // Failing to cancel keeps frozen initialVisibleNodes pointing into the
  // detached subtree, producing phantom math marks against the previous doc.
  const restoreOnly = message.html === undefined;
  const isProgressiveInitial = message.cacheKey === null;
  let cachedFragment = restoreOnly && message.cacheKey
    ? deps.getCachedDocumentFragment?.(message.cacheKey)
    : undefined;
  if (cachedFragment === undefined && restoreOnly) {
    deps.emitMark("mm-load-document-cache-miss", {
      documentName: message.documentName ?? "",
      renderId: message.renderId ?? null,
    });
    deps.notifyDocumentCacheMiss?.(message.renderId, message.cacheKey ?? undefined);
    return;
  }

  deps.preserveCurrentDocumentCache?.();
  deps.cancelCurrentMathController();
  deps.resetModuleGlobals();
  if (message.theme) {
    deps.applyTheme(message.theme);
  }

  if (!restoreOnly && message.cacheKey) {
    cachedFragment = deps.getCachedDocumentFragment?.(message.cacheKey);
  }

  if (cachedFragment !== undefined) {
    deps.emitMark("mm-load-document-cache-hit", {
      documentName: message.documentName ?? "",
      nodeCount: cachedFragment.childNodes.length,
      renderId: message.renderId ?? null,
    });
  }

  // Body swap (single innerHTML write). Minimap aside / width-handle / drop-overlay
  // are siblings of <main> under <body>, so they survive this swap. Their event
  // wiring (document-bound + window-bound listeners from wireLinks / wireFileDrop
  // etc.) survives too because the swap does not touch document or window.
  if (cachedFragment !== undefined) {
    main.replaceChildren(cachedFragment);
  } else {
    main.innerHTML = message.html ?? "";
  }
  deps.setCurrentDocumentCacheKey?.(message.cacheKey ?? null);
  const firstHeading = main.querySelector("h1,h2,h3")?.textContent?.trim().replace(/\s+/g, " ").slice(0, 120) ?? "";
  deps.debugLog(`load-document:swapped id=${message.renderId ?? "(none)"} name=${message.documentName ?? ""} theme=${document.documentElement.dataset.theme ?? "(none)"} firstHeading=${firstHeading}`);

  // Re-anchor any chrome nodes that depend on the new body geometry (width-handle
  // position references the .mm-document bounding rect; minimap re-clones the new
  // contents on its Phase A/B rebuild). The ensureChromeNodes() callback wraps
  // ensureMinimap() / ensureWidthHandle() / ensureDropOverlay() so they recreate
  // detached nodes if a previous call accidentally removed them.
  deps.ensureChromeNodes(cachedFragment !== undefined, { allowVirtualization: !isProgressiveInitial });

  if (cachedFragment !== undefined) {
    deps.restoreCachedScrollPosition?.();
  } else {
    // Cold load starts at top. Cached loads restore their previous scroll
    // before layout-ready so the host never reveals the document at 0 first.
    deps.scrollWindowToTop();
  }

  // Re-run the initial render pipeline against the new body. The pipeline owns
  // math, layout-ready, document-ready emission, and post-ready rich enhancement.
  // PE r2 item G — thread the per-document `hasMermaid` flag down so the
  // pipeline can skip mermaid init+render entirely for docs without mermaid
  // blocks. Undefined defaults to running (backward-compat).
  if (cachedFragment !== undefined && deps.completeCachedDocumentLoad) {
    deps.completeCachedDocumentLoad(message.renderId, message.hasMermaid, message.hasHljs, message.skipFrameWait);
    return;
  }

  void deps.runInitialRenderPipeline(
    message.hasMermaid,
    message.skipFrameWait,
    message.renderId,
    message.hasHljs,
    !isProgressiveInitial);
}

export function clearDocumentState(deps: LoadDocumentDeps): void {
  const main = document.querySelector<HTMLElement>("main.mm-document");
  deps.emitMark("mm-clear-document");
  deps.debugLog("clear-document");
  deps.cancelCurrentMathController();
  deps.resetModuleGlobals();
  deps.setCurrentDocumentCacheKey?.(null);
  if (main) {
    main.innerHTML = "";
  }
}
