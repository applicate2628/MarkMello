import { shouldPostMinimapState, type PostedMinimapState } from "./minimapState";
import {
  calculateMinimapDocumentWidth,
  calculateMinimapViewportLayout,
  type MinimapViewportLayout
} from "./minimapLayout";
import {
  getWidthResizerVisibilityClasses,
  normalizeWidthResizerVisibility,
  type WidthResizerVisibility
} from "./widthResizerVisibility";
import { isMermaidNodeNearViewport, renderMermaidNode, type MermaidApiLike } from "./mermaidRender";
import { normalizeHljsLanguage } from "./hljsLanguage";
import { runInitialRenderPipeline, type MathReadinessController, type RendererTheme } from "./initialRenderPipeline";
import { applyLoadDocument, clearDocumentState } from "./loadDocument";
import { renderMath as renderMathInit } from "./mathRenderInit";
import { schedulePhaseBRebuild } from "./schematicMinimap";
import { emitMark, installLongTaskObserver, recordScrollIpc, getReport, getFpsSampler } from "./performanceMarks";
import { createScrollCoalescer } from "./scrollCoalescer";
import { calculateWidthHandleLeft, clampWidthHandleLeft } from "./widthHandleLayout";
import { createFindBar, type FindBarController } from "./findBar";
import {
  findScrollTopForSourceLine,
  findSourceLineAtDocumentY,
  readSourceLineAnchors,
  type SourceLineAnchor
} from "./sourceLineSync";
import {
  captureMinimapSnapshot,
  restoreMinimapSnapshot,
  type CachedMinimapSnapshot
} from "./minimapCache";
import { renderSchematicSvg, type DocumentBlock, type DocumentBlockKind } from "./schematicMinimap";

type KatexApi = {
  render: (
    tex: string,
    element: Element,
    options: {
      throwOnError: boolean;
      displayMode: boolean;
      strict: "warn";
      trust: false;
    }
  ) => void;
};

type MermaidApi = MermaidApiLike & {
  initialize: (config: { startOnLoad: boolean; theme: string; securityLevel: string; maxTextSize: number }) => void;
};

type HljsApi = {
  highlightElement: (element: Element) => void;
  getLanguage: (name: string) => unknown;
};

type RendererWindow = Window & {
  katex?: KatexApi;
  mermaid?: MermaidApi;
  hljs?: HljsApi;
  chrome?: {
    webview?: {
      postMessage: (message: unknown) => void;
      addEventListener?: (type: "message", listener: (event: MessageEvent<unknown>) => void) => void;
    };
  };
  invokeCSharpAction?: (message: string) => void;
};

type RendererMessage =
  | { type: "document-ready"; mathCount: number }
  | { type: "layout-ready"; scrollTop: number; scrollHeight: number; clientHeight: number; cached?: boolean }
  | { type: "post-ready-enhancements-complete"; renderId?: number; hasMermaid: boolean; hasHljs: boolean }
  | { type: "link-clicked"; href: string; button: number; ctrlKey: boolean; shiftKey: boolean; altKey: boolean; metaKey: boolean }
  | { type: "minimap-state"; visible: boolean; reservedWidth: number }
  | { type: "minimap-settled"; transactionGeneration: number; visible: boolean; reservedWidth: number }
  | { type: "scroll"; scrollTop: number; scrollHeight: number; clientHeight: number; topBlockIndex: number | null }
  | { type: "viewer-interaction" }
  | { type: "wheel"; deltaY: number; deltaMode: number }
  | { type: "width-drag"; phase: "start" | "move" | "end"; deltaX: number }
  | { type: "drag-hover"; hovering: boolean }
  | { type: "drop-file"; name: string; text: string }
  | { type: "host-shortcut"; combo: string }
  | { type: "debug-log"; text: string }
  | { type: "debug-log"; message: string }
  // Round-2 perf-engineer plan item C, [renderer-perf] group. The renderer
  // posts a perf-mark whenever a startup-relevant pipeline milestone fires;
  // the host stamps elapsed-ms against its own process-anchored Stopwatch
  // (avoids clock-skew between renderer performance.now() and host wall clock)
  // and re-emits as `[renderer-perf] <name> ms=<elapsed>` via ApplicateTrace.
  | { type: "perf-mark"; name: string; detail?: string }
  | { type: "headings-updated"; headings: ReadonlyArray<{ id: string; level: number; text: string }> }
  | { type: "active-heading-changed"; id: string }
  | { type: "preview-source-line"; sourceLine: number }
  | { type: "csp-violation"; blockedURI: string; violatedDirective: string; sourceFile: string; lineNumber: number; columnNumber: number }
  // Mode-toggle reveal gate (2026-05-20). Posted in response to a host-sent
  // `mode-settle-probe` message after the renderer has applied pending reading
  // preferences and let layout chrome such as the minimap paint at the new slot
  // bounds. The host uses this
  // to defer `SetNativeWebViewVisibility(true)` on the Commit fast-path
  // (Ctrl+E mode toggle within the same document), so the user never sees the
  // HWND repainted at the old document width before the renderer catches up.
  | { type: "mode-toggle-settled"; transactionGeneration?: number };

type MinimapMode = "auto" | "on" | "off";

type MinimapPolicy = {
  minHostWidth: number;
  minScrollableViewportRatio: number;
  maxDetailedDocumentHeight: number;
};

type FontFamilyMode = "serif" | "sans" | "mono";

type HostMessage =
  | { type: "theme"; theme: RendererTheme }
  | { type: "minimap-policy"; minimapPolicy: MinimapPolicy }
  | {
      type: "reading-preferences";
      fontSize: number;
      lineHeight: number;
      maxWidth: number;
      minMaxWidth?: number;
      minimapMode: MinimapMode;
      fontFamily?: FontFamilyMode;
      viewerChromeEnabled?: boolean;
      documentScrollEnabled?: boolean;
      wheelProxyEnabled?: boolean;
      widthResizerVisibility?: WidthResizerVisibility;
      skipFrameWait?: boolean;
    }
  | { type: "scroll-by"; deltaY: number }
  | { type: "scroll-to-block"; blockIndex: number }
  | { type: "scroll-to"; anchor: string }
  | { type: "scroll-to-progress"; progressPercent: number }
  | { type: "load-document"; html: string; documentName?: string; theme?: RendererTheme; hasMermaid?: boolean; hasHljs?: boolean; renderId?: number; skipFrameWait?: boolean }
  | { type: "clear-document" }
  | { type: "scroll-to-heading"; id: string }
  | { type: "scroll-to-source-line"; sourceLine: number }
  | { type: "open-find-bar" }
  | { type: "host-scrollbar"; active: boolean }
  // Host-sent probe (2026-05-20). The host sends this after Avalonia
  // UpdateLayout has settled the slot bounds but BEFORE making the WebView2
  // HWND visible on the Commit fast-path (Ctrl+E same-document reparent).
  // The renderer applies any pending reading preferences, schedules at least
  // two requestAnimationFrame ticks so CSS reflow has propagated and one paint
  // has happened, then posts `mode-toggle-settled` back after layout-dependent
  // chrome has been refreshed. If chrome visibility changes during that
  // refresh, the ack waits one more paint. This keeps the host reveal behind
  // the final minimap/width-handle geometry instead of exposing one frame at
  // the previous text width.
  | {
      type: "mode-settle-probe";
      fontSize?: number;
      lineHeight?: number;
      maxWidth?: number;
      minMaxWidth?: number;
      minimapMode?: MinimapMode;
      fontFamily?: FontFamilyMode;
      viewerChromeEnabled?: boolean;
      documentScrollEnabled?: boolean;
      wheelProxyEnabled?: boolean;
      widthResizerVisibility?: WidthResizerVisibility;
      viewportWidth?: number;
      viewportHeight?: number;
      transactionGeneration?: number;
      skipFrameWait?: boolean;
    }
  | { type: "minimap-settle-probe"; transactionGeneration: number }
  | { type: "host-shortcuts-reset" }
  | { type: "mode-reveal-prepare"; durationMs?: number }
  | { type: "mode-reveal-start"; durationMs?: number }
  | { type: "document-reveal-prepare"; durationMs?: number; theme?: RendererTheme }
  | { type: "document-reveal-start"; durationMs?: number };

const hostWindow = window as RendererWindow;
const MINIMAP_CLASS = "mm-minimap";
const MINIMAP_VIEWPORT_CLASS = "mm-minimap-viewport";
const MINIMAP_VISIBLE_CLASS = "mm-has-minimap";
const MINIMAP_REFRESH_DEBOUNCE_MS = 100;
const WIDTH_HANDLE_CLASS = "mm-width-handle";
const WIDTH_HANDLE_DRAGGING_CLASS = "mm-dragging";
const WIDTH_RESIZER_ALWAYS_CLASS = "mm-width-resizer-always";
const MODE_REVEAL_EASING = "cubic-bezier(0.215, 0.61, 0.355, 1)";
const MODE_SETTLE_VIEWPORT_TOLERANCE = 2;
const MODE_SETTLE_VIEWPORT_MAX_FRAMES = 18;

let minimapMode: MinimapMode = "off";
let hasReceivedHostPreferences = false;
// Polish #5 — width-handle stays hidden until first layout settles (initial
// visible math reaches terminal state). Without this gate the handle reveals
// on innerHTML swap inside `ensureChromeNodes` (called from `loadDocument.ts`)
// BEFORE KaTeX initial-visible math finishes laying out, so its computed
// position is based on a pre-settle `.mm-document` bounding rect; once layout
// settles, a follow-up `updateWidthHandlePosition` call (from mode-toggle,
// scroll, or layout-ready) moves the handle to the correct x — user-visible
// as "ресайзер сначала появляется по центру, потом в нужное положение
// переходит". Gate flipped once in `controller.initialVisibleReady.then(...)`.
let hasInitialLayoutSettled = false;
let minimapViewportFrameRequested = false;
let minimapRefreshTimer: number | undefined;
let minimapContentRefreshTimer: number | undefined;
let cachedGeometryRefreshTimer: number | undefined;
let mermaidCacheResumeTimer: number | undefined;
// Resize coalescing (2026-05-20). The window.addEventListener("resize", ...)
// handler at module-init bottom and the ResizeObserver watching `.mm-document`
// + `document.body` both want to refresh chrome positions (width handle) and
// queue a viewport-update. Without coalescing, a fast window-edge drag fires
// both paths several times per frame; updateWidthHandlePosition() reads
// getBoundingClientRect() (forces synchronous layout) on every call and the
// chrome (width handle + minimap thumb) snaps visibly on each pass. One rAF
// token serializes all reactive resize-time work to at most one pass per frame.
// Note: this only coalesces JS-side reactive work; CSS reflow itself remains
// browser-native (best-of-class). KaTeX is NOT re-triggered here — its layout
// is already settled and the responsive sizing flows through CSS max-width.
let resizeReactFrameRequested = false;
// Mode-toggle reveal gate (2026-05-20). Set to a one-shot resolver when the
// host's `mode-settle-probe` message lands; cleared after the renderer posts
// `mode-toggle-settled` back. At least two rAFs separate the probe from the
// response so CSS reflow on any new slot bounds has propagated and one paint
// has happened after minimap visibility settles.
let modeToggleProbeFrameRequested = false;
let modeToggleProbeToken = 0;
let modeToggleProbeTransactionGeneration: number | undefined;
let modeRevealPrepared = false;
let modeRevealShield: HTMLElement | null = null;
let documentRevealShield: HTMLElement | null = null;
let minimapRoot: HTMLElement | null = null;
let minimapContent: HTMLElement | null = null;
let minimapViewport: HTMLElement | null = null;
let currentMinimapLayout: MinimapViewportLayout | null = null;
let minimapDragging = false;
let minimapDragStartClientY: number | null = null;
let minimapDragStartScrollTop = 0;
let minimapDragMode: "tentative" | "panning" = "tentative";
const MINIMAP_DRAG_THRESHOLD_PX = 4;
let minimapSourceReady = false;
let mermaidRenderGeneration = 0;
let mermaidLazyObserver: IntersectionObserver | null = null;
let mermaidLazyRenderQueue: Promise<void> = Promise.resolve();
let themeMermaidRefreshGeneration = 0;
let themeMermaidRefreshTimer: number | undefined;
let initialRenderPipelineGeneration = 0;
let initialRenderPipelineCompleted = false;
let postReadyEnhancementsCompleted = false;
let currentController: MathReadinessController | null = null;
const MERMAID_PER_DIAGRAM_TIMEOUT_MS = 3000;
const MERMAID_WATCHDOG_MS = 15_000;
const MERMAID_EAGER_VIEWPORT_MARGIN_PX = 700;
const MERMAID_LAZY_ROOT_MARGIN_PX = 1400;
const THEME_MERMAID_REFRESH_DELAY_MS = 160;
const POST_LAYOUT_READY_EDIT_PREVIEW_DELAY_MS = 120;
let widthResizerVisibility: WidthResizerVisibility = "on-hover";
let viewerChromeEnabled = false;
let documentScrollEnabled = true;
let wheelProxyEnabled = false;
// Find-in-document (Ctrl+F) — lazily created on first user trigger
// (open keystroke). Module-scoped so `resetModuleGlobalsForLoadDocument`
// can call close() on doc-swap and `wireFindBar` can install the
// keystroke listener that toggles it.
let findBarController: FindBarController | null = null;
let widthHandleRoot: HTMLElement | null = null;
let widthHandleDragging = false;
let widthHandleStartClientX = 0;
let widthHandleStartMaxWidth = 0;
let pendingWidthDragDeltaX = 0;
let widthHandleDragStartLeft = 0;
let widthHandleDragHitArea = 24;
let widthHandleDragMinimapReservedWidth = 0;
let widthDragFrameRequested = false;
let widthDragApplyFrameRequested = false;
let widthDragPerfStartTime: number | undefined;
let widthDragPerfMoveEvents = 0;
let widthDragPerfMovePosts = 0;
let widthDragPerfApplyFrames = 0;
let widthDragPerfMaxApplyMs = 0;
let widthDragPerfStartMaxWidth = 0;
let widthDragPerfLastMaxWidth = 0;
let layoutReadyGeneration = 0;
let layoutReadyTimer: number | undefined;
let postLayoutReadyWorkQueue: Array<{ generation: number; work: () => void }> = [];
let lastPostedMinimapState: PostedMinimapState = { hasPosted: false, visible: false, reservedWidth: 0 };
// F-07 fix: the host (ApplicateWebMarkdownDocumentView.SendMinimapPolicy)
// always pushes the canonical ApplicateDocumentMinimapBuildPolicy values
// before any user document loads (SendMinimapPolicy is invoked alongside
// SendReadingPreferences in the document-ready / shell-ready paths).
// minimapPolicy stays null until that message lands; shouldShowMinimap
// gates on this so the renderer cannot make a minimap decision against
// stale literals that drifted from C#.
let minimapPolicy: MinimapPolicy | null = null;
let sourceLineAnchors: SourceLineAnchor[] = [];
let previewSourceLineFrameRequested = false;
let suppressPreviewSourceLineEmit = false;
let suppressPreviewSourceLineSequence = 0;
let lastPostedPreviewSourceLine: number | null = null;
const PROCESSED_DOCUMENT_CACHE_LIMIT = 4;
type ProcessedDocumentCacheEntry = {
  fragment: DocumentFragment;
  nodeCount: number;
  layoutState: CachedLayoutState;
  headings: HeadingPayload[];
  minimapSnapshot: CachedMinimapSnapshot | null;
};

type HeadingPayload = {
  id: string;
  level: number;
  text: string;
};

type CachedLayoutState = {
  scrollTop: number;
  scrollHeight: number;
  clientHeight: number;
  topBlockIndex: number | null;
};

const processedDocumentCache = new Map<string, ProcessedDocumentCacheEntry>();
let currentDocumentCacheKey: string | null = null;
let restoredCachedLayoutState: CachedLayoutState | null = null;
let restoredCachedHeadings: HeadingPayload[] | null = null;
let restoredCachedMinimapSnapshot: CachedMinimapSnapshot | null = null;
let lastExtractedHeadings: HeadingPayload[] = [];
let lastKnownLayoutState: CachedLayoutState = {
  scrollTop: 0,
  scrollHeight: 0,
  clientHeight: 0,
  topBlockIndex: null,
};

function hashDocumentHtml(html: string): string {
  let hash = 2166136261;
  for (let index = 0; index < html.length; index++) {
    hash ^= html.charCodeAt(index);
    hash = Math.imul(hash, 16777619);
  }
  return (hash >>> 0).toString(16).padStart(8, "0");
}

function createProcessedDocumentCacheKey(html: string, theme: RendererTheme): string {
  return `${theme}|${html.length}|${hashDocumentHtml(html)}`;
}

function getCachedProcessedDocumentFragment(cacheKey: string): DocumentFragment | undefined {
  const cached = processedDocumentCache.get(cacheKey);
  if (cached === undefined) {
    return undefined;
  }

  processedDocumentCache.delete(cacheKey);
  restoredCachedLayoutState = { ...cached.layoutState };
  restoredCachedHeadings = cached.headings.map(heading => ({ ...heading }));
  restoredCachedMinimapSnapshot = cached.minimapSnapshot;
  return cached.fragment;
}

function setCurrentProcessedDocumentCacheKey(cacheKey: string | null): void {
  currentDocumentCacheKey = cacheKey;
}

function preserveCurrentProcessedDocument(): void {
  if (!currentDocumentCacheKey || !initialRenderPipelineCompleted || !postReadyEnhancementsCompleted) {
    return;
  }

  const main = document.querySelector<HTMLElement>("main.mm-document");
  if (!main || main.childNodes.length === 0) {
    return;
  }

  const fragment = document.createDocumentFragment();
  const nodes = Array.from(main.childNodes);
  fragment.append(...nodes);
  const minimapSnapshot = captureMinimapSnapshot({
    ownerDocument: document,
    minimapContent,
    minimapViewport,
    documentHeight: minimapDocumentHeight,
    lastPostedState: lastPostedMinimapState,
  });
  processedDocumentCache.delete(currentDocumentCacheKey);
  processedDocumentCache.set(currentDocumentCacheKey, {
    fragment,
    nodeCount: nodes.length,
    layoutState: { ...lastKnownLayoutState },
    headings: lastExtractedHeadings.map(heading => ({ ...heading })),
    minimapSnapshot,
  });
  while (processedDocumentCache.size > PROCESSED_DOCUMENT_CACHE_LIMIT) {
    const oldest = processedDocumentCache.keys().next().value;
    if (oldest === undefined) {
      break;
    }
    processedDocumentCache.delete(oldest);
  }
  currentDocumentCacheKey = null;
  postPerfMark("mm-document-cache-store", {
    entries: processedDocumentCache.size,
    nodeCount: nodes.length,
  });
}

function applyViewerChromeState(): void {
  document.documentElement.dataset.mmChrome = viewerChromeEnabled ? "on" : "off";
}

function applyDocumentScrollState(): void {
  document.documentElement.dataset.mmDocumentScroll = documentScrollEnabled ? "on" : "off";
  if (!documentScrollEnabled) {
    // When the host owns scroll (viewer mode forwarding wheel to the outer
    // Avalonia ScrollViewer), force the document to position 0 so the host
    // and renderer agree on the starting offset.
    window.scrollTo({ left: 0, top: 0, behavior: "instant" as ScrollBehavior });
  }
}

function clampModeRevealDuration(durationMs: unknown): number {
  return typeof durationMs === "number" && Number.isFinite(durationMs)
    ? Math.max(0, Math.min(600, Math.round(durationMs)))
    : 0;
}

function getModeRevealTarget(): HTMLElement | null {
  return document.querySelector<HTMLElement>("main.mm-document");
}

function getRevealShieldBackground(theme: RendererTheme = getCurrentTheme()): string {
  const bodyBackground = window.getComputedStyle(document.body).backgroundColor;
  if (bodyBackground && bodyBackground !== "rgba(0, 0, 0, 0)" && bodyBackground !== "transparent") {
    return bodyBackground;
  }

  return theme === "dark" ? "#11100d" : "#ffffff";
}

function getModeRevealShieldBackground(): string {
  return getRevealShieldBackground();
}

function getThemeRevealShieldBackground(theme: RendererTheme): string {
  return theme === "dark" ? "#11100d" : "#ffffff";
}

function ensureModeRevealShield(): HTMLElement {
  if (modeRevealShield && modeRevealShield.isConnected) {
    return modeRevealShield;
  }

  modeRevealShield = document.createElement("div");
  modeRevealShield.className = "mm-mode-reveal-shield";
  modeRevealShield.setAttribute("aria-hidden", "true");
  modeRevealShield.style.position = "fixed";
  modeRevealShield.style.inset = "0";
  modeRevealShield.style.zIndex = "2147483647";
  modeRevealShield.style.pointerEvents = "none";
  document.body.append(modeRevealShield);
  postPerfMark("mm-mode-reveal-shield-created", {
    viewportWidth: window.innerWidth,
    viewportHeight: window.innerHeight,
  });
  return modeRevealShield;
}

function clearModeRevealShield(): void {
  if (modeRevealShield) {
    postPerfMark("mm-mode-reveal-shield-cleared", {
      connected: modeRevealShield.isConnected,
      opacity: modeRevealShield.style.opacity,
    });
  }

  modeRevealShield?.remove();
  modeRevealShield = null;
}

function ensureDocumentRevealShield(): HTMLElement {
  if (documentRevealShield && documentRevealShield.isConnected) {
    return documentRevealShield;
  }

  documentRevealShield = document.createElement("div");
  documentRevealShield.className = "mm-document-reveal-shield";
  documentRevealShield.setAttribute("aria-hidden", "true");
  documentRevealShield.style.position = "fixed";
  documentRevealShield.style.inset = "0";
  documentRevealShield.style.zIndex = "2147483646";
  documentRevealShield.style.pointerEvents = "none";
  document.body.append(documentRevealShield);
  postPerfMark("mm-document-reveal-shield-created", {
    viewportWidth: window.innerWidth,
    viewportHeight: window.innerHeight,
  });
  return documentRevealShield;
}

function clearDocumentRevealShield(): void {
  if (documentRevealShield) {
    postPerfMark("mm-document-reveal-shield-cleared", {
      connected: documentRevealShield.isConnected,
      opacity: documentRevealShield.style.opacity,
    });
  }

  documentRevealShield?.remove();
  documentRevealShield = null;
}

function prepareDocumentReveal(durationMs: unknown, theme?: RendererTheme): void {
  const shield = ensureDocumentRevealShield();
  shield.style.background = theme
    ? getThemeRevealShieldBackground(theme)
    : getRevealShieldBackground();
  shield.style.opacity = "1";
  shield.style.transition = "none";
  postPerfMark("mm-document-reveal-shield-prepared", {
    durationMs: clampModeRevealDuration(durationMs),
    viewportWidth: window.innerWidth,
    viewportHeight: window.innerHeight,
    connected: shield.isConnected,
  });
}

function startDocumentReveal(durationMs: unknown): void {
  postPerfMark("mm-document-reveal-start", {
    durationMs: clampModeRevealDuration(durationMs),
    hasShield: documentRevealShield !== null,
    shieldConnected: documentRevealShield?.isConnected ?? false,
  });
  const duration = clampModeRevealDuration(durationMs);
  if (duration <= 0) {
    clearDocumentRevealShield();
    return;
  }

  if (documentRevealShield) {
    void documentRevealShield.offsetWidth;
    documentRevealShield.style.transition = `opacity ${duration}ms ${MODE_REVEAL_EASING}`;
    documentRevealShield.style.opacity = "0";
  }
  window.setTimeout(clearDocumentRevealShield, duration);
}

function prepareModeReveal(durationMs: unknown): void {
  modeRevealPrepared = true;
  const shield = ensureModeRevealShield();
  shield.style.background = getModeRevealShieldBackground();
  shield.style.opacity = "1";
  shield.style.transition = "none";
  postPerfMark("mm-mode-reveal-shield-prepared", {
    durationMs: clampModeRevealDuration(durationMs),
    viewportWidth: window.innerWidth,
    viewportHeight: window.innerHeight,
    connected: shield.isConnected,
  });

  const target = getModeRevealTarget();
  if (!target) {
    postPerfMark("mm-mode-reveal-prepare-missing-target");
    return;
  }

  const duration = clampModeRevealDuration(durationMs);
  target.style.transition = "none";
  target.style.opacity = "1";
  target.style.transform = duration > 0 ? "translateY(4px)" : "";
  target.style.willChange = duration > 0 ? "transform" : "";
}

function startModeReveal(durationMs: unknown): void {
  modeRevealPrepared = false;
  const target = getModeRevealTarget();
  postPerfMark("mm-mode-reveal-start", {
    durationMs: clampModeRevealDuration(durationMs),
    hasShield: modeRevealShield !== null,
    shieldConnected: modeRevealShield?.isConnected ?? false,
    hasTarget: target !== null,
  });
  if (!target) {
    clearModeRevealShield();
    return;
  }

  const duration = clampModeRevealDuration(durationMs);
  if (duration <= 0) {
    clearModeRevealShield();
    target.style.transition = "none";
    target.style.opacity = "1";
    target.style.transform = "";
    target.style.willChange = "";
    return;
  }

  void target.offsetWidth;
  target.style.transition = `transform ${duration}ms ${MODE_REVEAL_EASING}`;
  target.style.opacity = "1";
  target.style.transform = "translateY(0)";
  if (modeRevealShield) {
    void modeRevealShield.offsetWidth;
    modeRevealShield.style.transition = `opacity ${duration}ms ${MODE_REVEAL_EASING}`;
    modeRevealShield.style.opacity = "0";
  }
  window.setTimeout(() => {
    if (target.style.transition.includes("transform")) {
      target.style.transition = "";
      target.style.transform = "";
      target.style.willChange = "";
    }
    clearModeRevealShield();
  }, duration);
}

function postHostMessage(message: RendererMessage): void {
  const serialized = JSON.stringify(message);
  if (hostWindow.chrome?.webview) {
    hostWindow.chrome.webview.postMessage(message);
    return;
  }

  hostWindow.invokeCSharpAction?.(serialized);
}

function postDebugLog(text: string): void {
  postHostMessage({ type: "debug-log", text });
}

// Round-2 perf-engineer plan item C, [renderer-perf] group. Bridges a
// renderer-side pipeline milestone to the host so the host can stamp
// elapsed-ms against its own process-anchored Stopwatch and forward to
// the shared `[renderer-perf] <name> ms=<elapsed>` log stream. No-op when
// the host IPC channel is missing (vitest harness, smoke fixtures).
function postPerfMark(name: string, detail?: Record<string, unknown>): void {
  const message: { type: "perf-mark"; name: string; detail?: string } = { type: "perf-mark", name };
  if (detail !== undefined) {
    try {
      message.detail = JSON.stringify(detail);
    } catch {
      // Detail serialization is best-effort; the mark must still post.
    }
  }
  postHostMessage(message);
}

function countFailedInSet(nodes: Iterable<HTMLElement>): number {
  let count = 0;
  for (const node of nodes) {
    if (node.dataset["mmMathRendered"] === "failed") count++;
  }
  return count;
}

function renderMath(): MathReadinessController {
  // Thin wrapper preserves renderer-local side effects (perf marks,
  // __mmRendererState exposure, Phase B scheduling) while delegating the
  // rendering loop to the seam in mathRenderInit.ts.
  emitMark("mm-render-math-start", { mathCount: document.querySelectorAll("[data-tex]").length });
  const katex = hostWindow.katex ?? undefined;
  const controller = renderMathInit({ katex, documentRoot: document });
  const phaseBGeneration = layoutReadyGeneration;
  const initialVisualSettleReady = schedulePhaseBRebuild({
    allMathRendered: controller.allMathRendered,
    getCurrentDocumentHeight: () => (document.scrollingElement ?? document.documentElement).scrollHeight,
    getCachedDocumentHeight: () => minimapDocumentHeight,
    refresh: (phase) => {
      if (phaseBGeneration !== layoutReadyGeneration || controller.isCancelled()) {
        return;
      }

      refreshMinimapContent(phase);
    },
  });
  const readinessController: MathReadinessController = {
    ...controller,
    initialVisualSettleReady,
  };
  currentController = readinessController;
  // Lifecycle marks read failed-counts from the controller's frozen set (single
  // source of truth — no duplicate classification). For all-math, walk all
  // [data-tex] nodes since IO may have rendered nodes outside the frozen set.
  controller.initialVisibleReady.then(() => {
    emitMark("mm-initial-visible-ready", {
      visibleCount: controller.initialVisibleNodes.size,
      failedCount: countFailedInSet(controller.initialVisibleNodes),
    });
    postPerfMark("mm-initial-visible-ready", {
      visibleCount: controller.initialVisibleNodes.size,
      failedCount: countFailedInSet(controller.initialVisibleNodes),
    });
    // Phase A minimap rebuild now happens here — once initial-visible math has
    // reached terminal state (heights stable), clone the document for the minimap.
    // This replaces the old katexHasRun gate without racing the rAF in queueMinimapRefresh.
    refreshMinimapContent("A");
    // Polish #5 — first-layout-settled gate for width-handle reveal. Now that
    // .mm-document has settled at its final width, updateWidthHandlePosition
    // computes the correct x. Flipping this once is enough: subsequent calls
    // (resize, scroll, mode toggle) keep position fresh; document swaps in
    // loadDocument.ts reset hasInitialLayoutSettled back to false so the
    // next initialVisibleReady gates again.
    hasInitialLayoutSettled = true;
    updateWidthHandlePositionForCurrentLayout();
  });
  controller.allMathRendered.then(() => {
    const allMathNodes = Array.from(document.querySelectorAll<HTMLElement>("[data-tex]"));
    emitMark("mm-all-math-rendered", {
      totalCount: controller.totalMathCount,
      failedCount: countFailedInSet(allMathNodes),
      cancelled: controller.isCancelled(),
    });
  });
  return readinessController;
}

function getCurrentTheme(): RendererTheme {
  const theme = document.documentElement.dataset.theme;
  return theme === "dark" || theme === "classic-white" ? theme : "light";
}

function applyTheme(theme: RendererTheme): void {
  document.documentElement.dataset.theme = theme;
}

function initMermaidWithTheme(theme: RendererTheme): void {
  hostWindow.mermaid?.initialize({
    startOnLoad: false,
    theme: theme === "dark" ? "dark" : "default",
    securityLevel: "strict",
    maxTextSize: 100_000
  });
}

async function renderMermaidNodes(
  allNodes: HTMLElement[],
  mermaid: MermaidApiLike,
  perfMarkName = "mm-mermaid-visible-first"
): Promise<void> {
  if (allNodes.length === 0) return;

  const generation = ++mermaidRenderGeneration;
  mermaidLazyRenderQueue = Promise.resolve();
  const viewportHeight = getViewportHeightForMermaid();
  const eagerNodes = allNodes.filter(node =>
    isMermaidNodeNearViewport(node, viewportHeight, MERMAID_EAGER_VIEWPORT_MARGIN_PX));
  const eagerSet = new Set(eagerNodes);
  const lazyNodes = allNodes.filter(node => !eagerSet.has(node));

  postPerfMark(perfMarkName, {
    total: allNodes.length,
    eager: eagerNodes.length,
    lazy: lazyNodes.length
  });
  installLazyMermaidObserver(lazyNodes, generation, mermaid);
  if (eagerNodes.length === 0) return;

  const watchdog = window.setTimeout(() => {
    if (generation === mermaidRenderGeneration) {
      ++mermaidRenderGeneration;
    }
  }, MERMAID_WATCHDOG_MS);

  try {
    for (const node of eagerNodes) {
      await renderMermaidNode(node, generation, () => mermaidRenderGeneration, mermaid, MERMAID_PER_DIAGRAM_TIMEOUT_MS);
      if (generation !== mermaidRenderGeneration) return;
    }
  } finally {
    window.clearTimeout(watchdog);
  }
}

async function renderMermaid(): Promise<void> {
  disconnectMermaidLazyObserver();
  const mermaid = hostWindow.mermaid;
  if (!mermaid) return;

  const allNodes = Array.from(document.querySelectorAll<HTMLElement>("pre.mm-mermaid"));
  await renderMermaidNodes(allNodes, mermaid);
}

function scheduleCachedMermaidResume(hasMermaid?: boolean): void {
  if (hasMermaid === false) {
    return;
  }

  const cacheKey = currentDocumentCacheKey;
  window.clearTimeout(mermaidCacheResumeTimer);
  mermaidCacheResumeTimer = window.setTimeout(() => {
    mermaidCacheResumeTimer = undefined;
    if (cacheKey !== currentDocumentCacheKey) {
      return;
    }

    disconnectMermaidLazyObserver();
    const mermaid = hostWindow.mermaid;
    if (!mermaid) {
      postPerfMark("mm-mermaid-cache-resume-skipped", { reason: "no-mermaid-api" });
      return;
    }

    const missingNodes = Array.from(document.querySelectorAll<HTMLElement>("pre.mm-mermaid:not(.is-rendered)"));
    if (missingNodes.length === 0) {
      postPerfMark("mm-mermaid-cache-resume-skipped", { reason: "all-rendered" });
      return;
    }

    void renderMermaidNodes(missingNodes, mermaid, "mm-mermaid-cache-resume");
  }, 0);
}

function getViewportHeightForMermaid(): number {
  const root = document.scrollingElement ?? document.documentElement;
  return root.clientHeight || window.innerHeight || 0;
}

function disconnectMermaidLazyObserver(): void {
  mermaidLazyObserver?.disconnect();
  mermaidLazyObserver = null;
}

function installLazyMermaidObserver(
  nodes: HTMLElement[],
  generation: number,
  mermaid: MermaidApiLike
): void {
  if (nodes.length === 0) return;

  postPerfMark("mm-mermaid-lazy-observe", {
    total: nodes.length,
    rootMarginPx: MERMAID_LAZY_ROOT_MARGIN_PX
  });
  if (typeof window.IntersectionObserver !== "function") {
    for (const node of nodes) {
      enqueueLazyMermaidRender(node, generation, mermaid);
    }
    return;
  }

  mermaidLazyObserver = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      const node = entry.target as HTMLElement;
      mermaidLazyObserver?.unobserve(node);
      enqueueLazyMermaidRender(node, generation, mermaid);
    }
  }, {
    root: null,
    rootMargin: `${MERMAID_LAZY_ROOT_MARGIN_PX}px 0px ${MERMAID_LAZY_ROOT_MARGIN_PX}px 0px`,
    threshold: 0
  });

  for (const node of nodes) {
    mermaidLazyObserver.observe(node);
  }
}

function enqueueLazyMermaidRender(
  node: HTMLElement,
  generation: number,
  mermaid: MermaidApiLike
): void {
  if (generation !== mermaidRenderGeneration) return;
  const marker = String(generation);
  if (node.dataset.mmMermaidRenderQueued === marker) return;
  node.dataset.mmMermaidRenderQueued = marker;

  mermaidLazyRenderQueue = mermaidLazyRenderQueue
    .catch(() => undefined)
    .then(async () => {
      if (generation !== mermaidRenderGeneration) return;
      postPerfMark("mm-mermaid-lazy-render-start");
      await renderMermaidNode(node, generation, () => mermaidRenderGeneration, mermaid, MERMAID_PER_DIAGRAM_TIMEOUT_MS);
      if (generation === mermaidRenderGeneration) {
        postPerfMark("mm-mermaid-lazy-render-end");
      }
    });
}

function renderCodeBlocks(): void {
  const hljs = hostWindow.hljs;
  if (!hljs) return;

  const nodes = Array.from(document.querySelectorAll<HTMLElement>("code[data-mm-code], code[data-mm-mermaid]"));
  for (const node of nodes) {
    const langClass = Array.from(node.classList).find(c => c.startsWith("language-"));
    const rawLang = langClass?.slice("language-".length);
    const normalized = normalizeHljsLanguage(rawLang);
    if (!hljs.getLanguage(normalized)) continue;
    if (langClass && langClass !== `language-${normalized}`) {
      node.classList.remove(langClass);
      node.classList.add(`language-${normalized}`);
    }
    try { hljs.highlightElement(node); } catch { /* leave plain */ }
  }
}

function deferPostReadyEnhancements(work: () => void): void {
  postLayoutReadyWorkQueue.push({ generation: layoutReadyGeneration, work });
}

function postPostReadyEnhancementsComplete(
  renderId: number | undefined,
  hasMermaid: boolean | undefined,
  hasHljs: boolean | undefined
): void {
  postReadyEnhancementsCompleted = true;
  const message: RendererMessage = {
    type: "post-ready-enhancements-complete",
    hasMermaid: hasMermaid === true,
    hasHljs: hasHljs === true
  };
  if (renderId !== undefined) {
    message.renderId = renderId;
  }
  postHostMessage(message);
}

function hasMermaidNodes(): boolean {
  return document.querySelector("pre.mm-mermaid") !== null;
}

function scheduleThemeMermaidRefresh(theme: RendererTheme): void {
  const generation = ++themeMermaidRefreshGeneration;
  ++mermaidRenderGeneration;
  if (themeMermaidRefreshTimer !== undefined) {
    window.clearTimeout(themeMermaidRefreshTimer);
    themeMermaidRefreshTimer = undefined;
  }

  if (!hostWindow.mermaid || !hasMermaidNodes()) {
    postPerfMark("mm-theme-mermaid-refresh-skipped", {
      theme,
      reason: hostWindow.mermaid ? "no-mermaid-nodes" : "no-mermaid-api"
    });
    return;
  }

  postPerfMark("mm-theme-mermaid-refresh-scheduled", {
    theme,
    delayMs: THEME_MERMAID_REFRESH_DELAY_MS
  });
  themeMermaidRefreshTimer = window.setTimeout(() => {
    themeMermaidRefreshTimer = undefined;
    if (generation !== themeMermaidRefreshGeneration) {
      return;
    }

    postPerfMark("mm-theme-mermaid-refresh-start", { theme });
    void renderMermaid().finally(() => {
      if (generation === themeMermaidRefreshGeneration) {
        postPerfMark("mm-theme-mermaid-refresh-end", { theme });
      }
    });
  }, THEME_MERMAID_REFRESH_DELAY_MS);
}

function handleThemeChange(theme: RendererTheme): void {
  postPerfMark("mm-theme-change-start", { theme });
  applyTheme(theme);
  initMermaidWithTheme(theme);
  postPerfMark("mm-theme-change-applied", { theme });
  scheduleThemeMermaidRefresh(theme);
}

function getScrollState(): { scrollTop: number; scrollHeight: number; clientHeight: number } {
  const root = document.scrollingElement ?? document.documentElement;
  return {
    scrollTop: root.scrollTop,
    scrollHeight: root.scrollHeight,
    clientHeight: root.clientHeight
  };
}

// The top visible block: the first element with data-mm-block-index whose
// bottom edge is below the viewport's top. Returns null if no annotated
// block exists yet (before first render, or document without blocks).
function findTopVisibleBlockIndex(): number | null {
  const elements = document.querySelectorAll<HTMLElement>("[data-mm-block-index]");
  if (elements.length === 0) return null;
  const viewportTop = 0;
  for (const el of Array.from(elements)) {
    const rect = el.getBoundingClientRect();
    if (rect.bottom >= viewportTop) {
      const raw = el.dataset["mmBlockIndex"];
      const parsed = raw === undefined ? Number.NaN : Number.parseInt(raw, 10);
      return Number.isFinite(parsed) ? parsed : null;
    }
  }
  // All blocks above viewport — return the last one.
  const lastRaw = elements[elements.length - 1]!.dataset["mmBlockIndex"];
  const lastParsed = lastRaw === undefined ? Number.NaN : Number.parseInt(lastRaw, 10);
  return Number.isFinite(lastParsed) ? lastParsed : null;
}

function postScroll(): void {
  const scrollState = getScrollState();
  const topBlockIndex = findTopVisibleBlockIndex();
  lastKnownLayoutState = { ...scrollState, topBlockIndex };
  recordScrollIpc();
  postHostMessage({
    type: "scroll",
    ...scrollState,
    topBlockIndex
  });
}

function refreshSourceLineAnchors(): void {
  sourceLineAnchors = readSourceLineAnchors(document);
}

function scrollToSourceLine(sourceLine: number): void {
  if (!Number.isFinite(sourceLine) || sourceLine < 0) {
    return;
  }

  if (sourceLineAnchors.length === 0) {
    refreshSourceLineAnchors();
  }

  const scrollTop = findScrollTopForSourceLine(sourceLineAnchors, sourceLine);
  if (scrollTop === null) {
    return;
  }

  suppressPreviewSourceLinePost();
  window.scrollTo({ left: 0, top: scrollTop, behavior: "instant" as ScrollBehavior });
}

function suppressPreviewSourceLinePost(): void {
  const sequence = ++suppressPreviewSourceLineSequence;
  suppressPreviewSourceLineEmit = true;
  window.requestAnimationFrame(() => {
    window.requestAnimationFrame(() => {
      if (sequence === suppressPreviewSourceLineSequence) {
        suppressPreviewSourceLineEmit = false;
      }
    });
  });
}

function queuePreviewSourceLinePost(): void {
  if (suppressPreviewSourceLineEmit || !documentScrollEnabled || previewSourceLineFrameRequested) {
    return;
  }

  previewSourceLineFrameRequested = true;
  window.requestAnimationFrame(() => {
    previewSourceLineFrameRequested = false;
    if (suppressPreviewSourceLineEmit || !documentScrollEnabled) {
      return;
    }

    if (sourceLineAnchors.length === 0) {
      refreshSourceLineAnchors();
    }

    const sourceLine = findSourceLineAtDocumentY(
      sourceLineAnchors,
      window.scrollY + getViewportAnchorY());
    if (sourceLine === null || sourceLine === lastPostedPreviewSourceLine) {
      return;
    }

    lastPostedPreviewSourceLine = sourceLine;
    postHostMessage({ type: "preview-source-line", sourceLine });
  });
}

function getViewportAnchorY(): number {
  const viewportHeight = Math.max(0, window.innerHeight);
  if (viewportHeight <= 0) {
    return 24;
  }

  if (viewportHeight <= 48) {
    return viewportHeight * 0.5;
  }

  return Math.max(24, Math.min(viewportHeight * 0.38, viewportHeight - 24));
}

function postLayoutReady(): void {
  try {
    const scrollState = getScrollState();
    const topBlockIndex = findTopVisibleBlockIndex();
    lastKnownLayoutState = { ...scrollState, topBlockIndex };
    recordScrollIpc();
    postHostMessage({
      type: "scroll",
      ...scrollState,
      topBlockIndex
    });
    postHostMessage({
      type: "layout-ready",
      ...scrollState
    });
    postPerfMark("mm-layout-ready");
    flushPostLayoutReadyWork();
  } catch (error) {
    postPerfMark("mm-layout-ready-post-error", {
      message: error instanceof Error ? error.message : String(error),
    });
    throw error;
  }
}

function postCachedLayoutReady(): void {
  const cachedLayoutState = restoredCachedLayoutState;
  restoredCachedLayoutState = null;
  const layoutState = cachedLayoutState !== null
    ? { ...cachedLayoutState }
    : { ...getScrollState(), topBlockIndex: findTopVisibleBlockIndex() };
  lastKnownLayoutState = { ...layoutState };
  recordScrollIpc();
  postHostMessage({
    type: "scroll",
    scrollTop: layoutState.scrollTop,
    scrollHeight: layoutState.scrollHeight,
    clientHeight: layoutState.clientHeight,
    topBlockIndex: layoutState.topBlockIndex
  });
  postHostMessage({
    type: "layout-ready",
    scrollTop: layoutState.scrollTop,
    scrollHeight: layoutState.scrollHeight,
    clientHeight: layoutState.clientHeight,
    cached: true
  });
  postPerfMark("mm-layout-ready", { cached: true });
  flushPostLayoutReadyWork();
  if (cachedLayoutState !== null) {
    queueCachedGeometryRefresh(cachedLayoutState.topBlockIndex);
  }
}

function queueCachedGeometryRefresh(topBlockIndex: number | null): void {
  const cacheKey = currentDocumentCacheKey;
  window.clearTimeout(cachedGeometryRefreshTimer);
  cachedGeometryRefreshTimer = window.setTimeout(() => {
    cachedGeometryRefreshTimer = undefined;
    if (cacheKey !== currentDocumentCacheKey) {
      return;
    }

    const scrollState = getScrollState();
    const layoutState = { ...scrollState, topBlockIndex };
    lastKnownLayoutState = { ...layoutState };
    recordScrollIpc();
    postHostMessage({
      type: "scroll",
      scrollTop: layoutState.scrollTop,
      scrollHeight: layoutState.scrollHeight,
      clientHeight: layoutState.clientHeight,
      topBlockIndex: layoutState.topBlockIndex
    });
  }, 180);
}

function flushPostLayoutReadyWork(): void {
  if (postLayoutReadyWorkQueue.length === 0) {
    return;
  }

  const flushGeneration = layoutReadyGeneration;
  const workItems = postLayoutReadyWorkQueue.filter(item => item.generation === flushGeneration);
  postLayoutReadyWorkQueue = postLayoutReadyWorkQueue.filter(item => item.generation !== flushGeneration);
  const delayMs = viewerChromeEnabled ? 0 : POST_LAYOUT_READY_EDIT_PREVIEW_DELAY_MS;
  if (delayMs > 0) {
    postPerfMark("post-ready-enhancements-deferred", { delayMs, viewerChromeEnabled });
  }
  window.setTimeout(() => {
    if (flushGeneration !== layoutReadyGeneration) {
      return;
    }

    for (const item of workItems) {
      item.work();
    }
  }, delayMs);
}

function restoreCachedScrollPosition(): void {
  const layoutState = restoredCachedLayoutState ?? lastKnownLayoutState;
  window.scrollTo({
    left: 0,
    top: layoutState.scrollTop,
    behavior: "instant" as ScrollBehavior,
  });
}

function scheduleLayoutReady(skipFrameWait = false): void {
  const generation = ++layoutReadyGeneration;
  let completed = false;
  let posted = false;
  let frameFallbackTimer: number | undefined;
  if (layoutReadyTimer !== undefined) {
    window.clearTimeout(layoutReadyTimer);
  }

  const post = (path: "skip-frame-wait" | "raf" | "frame-fallback") => {
    if (posted || generation !== layoutReadyGeneration) {
      return;
    }

    posted = true;
    if (frameFallbackTimer !== undefined) {
      window.clearTimeout(frameFallbackTimer);
      frameFallbackTimer = undefined;
    }

    if (path === "frame-fallback") {
      postPerfMark("mm-layout-ready-frame-fallback", { generation });
    }
    postLayoutReady();
  };

  const complete = () => {
    if (completed || generation !== layoutReadyGeneration) {
      return;
    }

    completed = true;
    if (layoutReadyTimer !== undefined) {
      window.clearTimeout(layoutReadyTimer);
      layoutReadyTimer = undefined;
    }

    if (skipFrameWait) {
      postPerfMark("mm-layout-ready-frame-wait-skipped");
      post("skip-frame-wait");
      return;
    }

    // WebView2 may throttle requestAnimationFrame while the native child is
    // hidden behind the startup/document reveal gate. The host is waiting for
    // layout-ready before revealing that child, so a pure rAF wait can deadlock
    // until the host's 15s fallback. Keep the two-rAF paint path when frames
    // flow, but guarantee readiness from a short timer when they do not.
    frameFallbackTimer = window.setTimeout(() => {
      post("frame-fallback");
    }, 120);
    window.requestAnimationFrame(() => {
      window.requestAnimationFrame(() => {
        if (generation === layoutReadyGeneration) {
          post("raf");
        } else {
          postPerfMark("mm-layout-ready-frame-stale", { generation, current: layoutReadyGeneration });
        }
      });
    });
  };

  layoutReadyTimer = window.setTimeout(complete, 250);
  document.fonts?.ready.then(complete).catch(complete);
}

function readRootPixelVariable(name: string, fallback: number): number {
  const raw = getComputedStyle(document.documentElement).getPropertyValue(name);
  const parsed = Number.parseFloat(raw);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

function readPixelValue(value: string | null | undefined): number {
  const parsed = Number.parseFloat(value ?? "");
  return Number.isFinite(parsed) && parsed > 0 ? parsed : 0;
}

function ensureWidthHandle(): void {
  if (widthHandleRoot) {
    return;
  }

  widthHandleRoot = document.createElement("div");
  widthHandleRoot.className = WIDTH_HANDLE_CLASS;
  widthHandleRoot.hidden = true;
  widthHandleRoot.setAttribute("aria-hidden", "true");

  const track = document.createElement("div");
  track.className = "mm-width-handle-track";

  widthHandleRoot.append(track);
  document.body.append(widthHandleRoot);
  widthHandleRoot.addEventListener("pointerdown", handleWidthHandlePointerDown);
  widthHandleRoot.addEventListener("pointermove", handleWidthHandlePointerMove);
  widthHandleRoot.addEventListener("pointerup", handleWidthHandlePointerUp);
  widthHandleRoot.addEventListener("pointercancel", handleWidthHandlePointerUp);
  widthHandleRoot.addEventListener("lostpointercapture", handleWidthHandlePointerCaptureLost);
  window.addEventListener("pointerup", handleWidthHandlePointerUp, true);
  window.addEventListener("pointercancel", handleWidthHandlePointerUp, true);
  window.addEventListener("blur", cancelWidthHandleDrag);
}

function updateWidthHandlePosition(): void {
  ensureWidthHandle();
  if (!widthHandleRoot) {
    return;
  }

  widthHandleRoot.hidden = !hasReceivedHostPreferences || !viewerChromeEnabled || !hasInitialLayoutSettled;
  if (widthHandleRoot.hidden) {
    return;
  }

  const documentElement = document.querySelector<HTMLElement>(".mm-document");
  if (!documentElement) {
    widthHandleRoot.hidden = true;
    return;
  }

  const hitArea = readRootPixelVariable("--mm-width-handle-hit-area", 24);
  const minimapReservedWidth = getCurrentMinimapReservedWidth();
  const documentRect = documentElement.getBoundingClientRect();
  // Position handle at the right edge of the VISIBLE TEXT COLUMN, not at the
  // .mm-document box.right. With minimap visible, body.mm-has-minimap adds
  // ~168px to padding-right (reserves space for the fixed minimap aside) so
  // box.right is far past where the user sees the readable column end. The
  // handle should sit just past the text — otherwise it floats in dead space
  // mid-document, making the column resize feel disconnected.
  const documentStyle = getComputedStyle(documentElement);
  const documentPaddingRight = Number.parseFloat(documentStyle.paddingRight) || 0;
  const clampedLeft = calculateWidthHandleLeft({
    documentRight: documentRect.right,
    documentPaddingRight,
    hitArea,
    minimapReservedWidth,
    viewportWidth: window.innerWidth,
  });
  widthHandleRoot.style.left = `${Math.round(clampedLeft)}px`;
}

function updateWidthHandlePositionFromCssModel(minimapVisible: boolean): void {
  ensureWidthHandle();
  if (!widthHandleRoot) {
    return;
  }

  widthHandleRoot.hidden = !hasReceivedHostPreferences || !viewerChromeEnabled || !hasInitialLayoutSettled;
  if (widthHandleRoot.hidden) {
    return;
  }

  const documentElement = document.querySelector<HTMLElement>(".mm-document");
  if (!documentElement) {
    widthHandleRoot.hidden = true;
    return;
  }

  const hitArea = readRootPixelVariable("--mm-width-handle-hit-area", 24);
  const basePadding = readRootPixelVariable("--mm-document-base-padding-x", 72);
  const minimapReservedWidth = minimapVisible ? readConfiguredMinimapReservedWidth() : 0;
  const inlineMaxWidth = Number.parseFloat(
    document.documentElement.style.getPropertyValue("--mm-document-max-width")
  );
  const documentMaxWidth = Number.isFinite(inlineMaxWidth) && inlineMaxWidth > 0
    ? inlineMaxWidth
    : (lastAppliedReadingPreferences?.maxWidth ?? readRootPixelVariable("--mm-document-max-width", 820));
  const borderBoxWidth = Math.min(
    Math.max(0, window.innerWidth),
    Math.max(0, documentMaxWidth + minimapReservedWidth)
  );
  const documentRight = (Math.max(0, window.innerWidth) + borderBoxWidth) / 2;
  const clampedLeft = calculateWidthHandleLeft({
    documentRight,
    documentPaddingRight: basePadding + minimapReservedWidth,
    hitArea,
    minimapReservedWidth,
    viewportWidth: window.innerWidth,
  });
  widthHandleRoot.style.left = `${Math.round(clampedLeft)}px`;
}

function readDocumentMaxWidthFromCssModel(): number {
  const inlineMaxWidth = Number.parseFloat(
    document.documentElement.style.getPropertyValue("--mm-document-max-width")
  );
  return Number.isFinite(inlineMaxWidth) && inlineMaxWidth > 0
    ? inlineMaxWidth
    : (lastAppliedReadingPreferences?.maxWidth ?? readRootPixelVariable("--mm-document-max-width", 820));
}

function calculateDocumentContentWidthFromCssModel(minimapVisible: boolean): number {
  const basePadding = readRootPixelVariable("--mm-document-base-padding-x", 72);
  const minimapReservedWidth = minimapVisible ? readConfiguredMinimapReservedWidth() : 0;
  const borderBoxWidth = Math.min(
    Math.max(0, window.innerWidth),
    Math.max(0, readDocumentMaxWidthFromCssModel() + minimapReservedWidth)
  );
  return Math.max(1, borderBoxWidth - basePadding * 2 - minimapReservedWidth);
}

function isPolicyHeavyMinimapDocument(): boolean {
  return isPolicyHeavyMinimapHeight(minimapDocumentHeight);
}

function updateWidthHandlePositionForCurrentLayout(): void {
  if (isPolicyHeavyMinimapDocument()) {
    updateWidthHandlePositionFromCssModel(minimapRoot ? !minimapRoot.hidden : false);
    return;
  }

  updateWidthHandlePosition();
}

function captureWidthHandleDragGeometry(): void {
  if (!widthHandleRoot) {
    widthHandleDragStartLeft = 0;
    widthHandleDragHitArea = 24;
    widthHandleDragMinimapReservedWidth = 0;
    return;
  }

  const inlineLeft = Number.parseFloat(widthHandleRoot.style.left);
  widthHandleDragStartLeft = Number.isFinite(inlineLeft)
    ? inlineLeft
    : widthHandleRoot.getBoundingClientRect().left;
  widthHandleDragHitArea = readRootPixelVariable("--mm-width-handle-hit-area", 24);
  widthHandleDragMinimapReservedWidth = getCurrentMinimapReservedWidth();
}

function updateWidthHandleDragPreviewPosition(previewMaxWidth: number): void {
  if (!widthHandleRoot) {
    return;
  }

  const widthDelta = previewMaxWidth - widthHandleStartMaxWidth;
  const targetLeft = clampWidthHandleLeft({
    candidateLeft: widthHandleDragStartLeft + widthDelta / 2,
    hitArea: widthHandleDragHitArea,
    minimapReservedWidth: widthHandleDragMinimapReservedWidth,
    viewportWidth: window.innerWidth,
  });
  widthHandleRoot.style.transform = `translateX(${Math.round(targetLeft - widthHandleDragStartLeft)}px)`;
}

function postWidthDragMove(): void {
  if (widthDragFrameRequested) {
    return;
  }

  widthDragFrameRequested = true;
  window.requestAnimationFrame(() => {
    widthDragFrameRequested = false;
    widthDragPerfMovePosts++;
    postHostMessage({ type: "width-drag", phase: "move", deltaX: pendingWidthDragDeltaX });
  });
}

function resetWidthDragPerf(startMaxWidth: number): void {
  widthDragPerfStartTime = typeof performance !== "undefined" ? performance.now() : undefined;
  widthDragPerfMoveEvents = 0;
  widthDragPerfMovePosts = 0;
  widthDragPerfApplyFrames = 0;
  widthDragPerfMaxApplyMs = 0;
  widthDragPerfStartMaxWidth = startMaxWidth;
  widthDragPerfLastMaxWidth = startMaxWidth;
}

function completeWidthDragPerf(reason: "end" | "cancel", deltaX: number): void {
  const now = typeof performance !== "undefined" ? performance.now() : undefined;
  const durationMs = widthDragPerfStartTime !== undefined && now !== undefined
    ? Math.max(0, now - widthDragPerfStartTime)
    : 0;
  postPerfMark(`mm-width-drag-${reason}`, {
    durationMs: Number(durationMs.toFixed(1)),
    moveEvents: widthDragPerfMoveEvents,
    movePosts: widthDragPerfMovePosts,
    applyFrames: widthDragPerfApplyFrames,
    maxApplyMs: Number(widthDragPerfMaxApplyMs.toFixed(1)),
    deltaX: Number(deltaX.toFixed(1)),
    startMaxWidth: Number(widthDragPerfStartMaxWidth.toFixed(1)),
    finalMaxWidth: Number(widthDragPerfLastMaxWidth.toFixed(1)),
    minimapVisible: minimapRoot ? !minimapRoot.hidden : false,
    minimapMode,
  });
  widthDragPerfStartTime = undefined;
}

function handleWidthHandlePointerDown(event: PointerEvent): void {
  if (event.button !== 0 || !widthHandleRoot) {
    return;
  }

  widthHandleDragging = true;
  widthHandleStartClientX = event.clientX;
  pendingWidthDragDeltaX = 0;
  // Snapshot current maxWidth for local live-preview during drag. Read from
  // the inline style if set, else fall back to last applied prefs.
  const inlineMaxWidth = parseFloat(
    document.documentElement.style.getPropertyValue("--mm-document-max-width")
  );
  widthHandleStartMaxWidth = Number.isFinite(inlineMaxWidth) && inlineMaxWidth > 0
    ? inlineMaxWidth
    : (lastAppliedReadingPreferences?.maxWidth ?? 720);
  captureWidthHandleDragGeometry();
  resetWidthDragPerf(widthHandleStartMaxWidth);
  postPerfMark("mm-width-drag-start", {
    startMaxWidth: Number(widthHandleStartMaxWidth.toFixed(1)),
    minimapVisible: minimapRoot ? !minimapRoot.hidden : false,
    minimapMode,
  });
  widthHandleRoot.classList.add(WIDTH_HANDLE_DRAGGING_CLASS);
  widthHandleRoot.setPointerCapture(event.pointerId);
  postHostMessage({ type: "width-drag", phase: "start", deltaX: 0 });
  event.preventDefault();
}

function handleWidthHandlePointerMove(event: PointerEvent): void {
  if (!widthHandleDragging) {
    return;
  }

  // Hot path: pointermove fires at native rate (often 120-1000Hz). Calling
  // style.setProperty + getBoundingClientRect synchronously here causes
  // forced sync layout per event, which on heavy formula-dense documents
  // becomes layout-thrashing. Coalesce into a single rAF flush — at most
  // one reflow per animation frame, regardless of pointermove rate.
  widthDragPerfMoveEvents++;
  pendingWidthDragDeltaX = event.clientX - widthHandleStartClientX;
  scheduleWidthDragApply();
  postWidthDragMove();
  event.preventDefault();
}

function scheduleWidthDragApply(): void {
  if (widthDragApplyFrameRequested) {
    return;
  }
  widthDragApplyFrameRequested = true;
  window.requestAnimationFrame(() => {
    widthDragApplyFrameRequested = false;
    if (!widthHandleDragging) {
      return;
    }
    // Live local preview: compute new maxWidth from deltaX and apply directly.
    // Column is center-aligned, so a cursor delta of N px moves each edge by
    // N px (column total grows by 2N). Bypass host round-trip — renderer owns
    // the visual during drag, host gets final value on release.
    // Min mirrors host's clamp (sent via reading-preferences as minMaxWidth);
    // fallback constant matches host's MinManualContentWidth in case the
    // message hasn't arrived yet (drag started before first prefs message).
    const applyStart = typeof performance !== "undefined" ? performance.now() : undefined;
    const previewMaxWidth = Math.max(hostMinMaxWidth, widthHandleStartMaxWidth + 2 * pendingWidthDragDeltaX);
    widthDragPerfLastMaxWidth = previewMaxWidth;
    document.documentElement.style.setProperty("--mm-document-max-width", `${previewMaxWidth}px`);
    // Keep drag JS under the 100ms budget: do not read .mm-document geometry
    // after changing --mm-document-max-width. That forced Chromium to lay out
    // the heavy document inside this frame. The handle follows the preferred
    // width from the pointerdown snapshot, then pointerup/cancel performs one
    // canonical geometry read to reconcile exact clamped/content-constrained x.
    updateWidthHandleDragPreviewPosition(previewMaxWidth);
    // Do not update the detailed minimap clone on every drag frame. The clone
    // path reads its full scrollHeight, so heavy documents turn a width drag
    // into full-document reflow work. Keep the visible minimap stable during
    // drag and reconcile it once on pointerup/cancel below.
    if (applyStart !== undefined && typeof performance !== "undefined") {
      const duration = Math.max(0, performance.now() - applyStart);
      widthDragPerfMaxApplyMs = Math.max(widthDragPerfMaxApplyMs, duration);
    }
    widthDragPerfApplyFrames++;
  });
}

function handleWidthHandlePointerUp(event: PointerEvent): void {
  if (!widthHandleDragging) {
    return;
  }

  const deltaX = event.clientX - widthHandleStartClientX;
  widthHandleDragging = false;
  widthHandleRoot?.classList.remove(WIDTH_HANDLE_DRAGGING_CLASS);
  if (widthHandleRoot) {
    widthHandleRoot.style.transform = "";
  }
  try {
    widthHandleRoot?.releasePointerCapture(event.pointerId);
  } catch {
    // Pointer capture may already be gone after WebView focus changes.
  }

  // Local preview applied --mm-document-max-width directly during drag,
  // bypassing the reading-preferences path. lastAppliedReadingPreferences
  // still holds the pre-drag value (or last host echo). Sync it to what's
  // actually in the DOM so the next host echo (which compares against the
  // tracked value) sees the true delta and re-applies the host-clamped
  // width. Without this sync: renderer at 200, tracked at 320, host echoes
  // 320, flushPendingReadingPreferences thinks "no change", CSS var stays
  // at 200 while host thinks it's at 320 — handle/document desync.
  if (lastAppliedReadingPreferences !== null) {
    const inlineMaxWidth = parseFloat(
      document.documentElement.style.getPropertyValue("--mm-document-max-width")
    );
    if (Number.isFinite(inlineMaxWidth) && inlineMaxWidth > 0) {
      lastAppliedReadingPreferences = {
        ...lastAppliedReadingPreferences,
        maxWidth: inlineMaxWidth,
      };
    }
  }
  updateWidthHandlePosition();
  // Cheap viewport-only update — no full re-clone. Width drag changes only
  // the document's wrap width, not its CONTENT. The minimap clone has CSS
  // max-width:none and padding:0; its wrap follows minimapContent.style.width
  // which updateMinimapViewport sets to the now-current source.clientWidth.
  // So the clone re-wraps to match the new source layout automatically — no
  // cloneNode needed. Saves ~50-100ms per drag-end on heavy formula docs.
  queueMinimapViewportUpdate();
  completeWidthDragPerf("end", deltaX);

  postHostMessage({ type: "width-drag", phase: "end", deltaX });
  event.preventDefault();
}

function handleWidthHandlePointerCaptureLost(): void {
  cancelWidthHandleDrag();
}

function cancelWidthHandleDrag(): void {
  if (!widthHandleDragging) {
    return;
  }

  widthHandleDragging = false;
  widthHandleRoot?.classList.remove(WIDTH_HANDLE_DRAGGING_CLASS);
  if (widthHandleRoot) {
    widthHandleRoot.style.transform = "";
  }
  // Same state-sync rationale as handleWidthHandlePointerUp.
  if (lastAppliedReadingPreferences !== null) {
    const inlineMaxWidth = parseFloat(
      document.documentElement.style.getPropertyValue("--mm-document-max-width")
    );
    if (Number.isFinite(inlineMaxWidth) && inlineMaxWidth > 0) {
      lastAppliedReadingPreferences = {
        ...lastAppliedReadingPreferences,
        maxWidth: inlineMaxWidth,
      };
    }
  }
  updateWidthHandlePosition();
  queueMinimapViewportUpdate();
  completeWidthDragPerf("cancel", pendingWidthDragDeltaX);
  postHostMessage({ type: "width-drag", phase: "end", deltaX: pendingWidthDragDeltaX });
}

function ensureMinimap(): void {
  if (minimapRoot) {
    return;
  }

  minimapRoot = document.createElement("aside");
  minimapRoot.className = MINIMAP_CLASS;
  minimapRoot.setAttribute("aria-hidden", "true");

  minimapContent = document.createElement("div");
  minimapContent.className = "mm-minimap-content";

  minimapViewport = document.createElement("div");
  minimapViewport.className = MINIMAP_VIEWPORT_CLASS;

  minimapRoot.append(minimapContent, minimapViewport);
  document.body.append(minimapRoot);
  minimapRoot.addEventListener("pointerdown", handleMinimapPointerDown);
  minimapRoot.addEventListener("pointermove", handleMinimapPointerMove);
  minimapRoot.addEventListener("pointerup", handleMinimapPointerUp);
  minimapRoot.addEventListener("pointercancel", handleMinimapPointerUp);
}

// Read by Task 15 schedulePhaseBRebuild to decide if Phase B rebuild is needed.
let minimapDocumentHeight = 0;

function getDocumentScrollMetrics(): { documentHeight: number; viewportHeight: number } {
  const root = document.scrollingElement ?? document.documentElement;
  return {
    documentHeight: root.scrollHeight,
    viewportHeight: root.clientHeight,
  };
}

function shouldBuildDetailedMinimapContent(): { allowed: boolean; reason?: string; documentHeight: number } {
  const source = document.querySelector<HTMLElement>(".mm-document");
  const { documentHeight, viewportHeight } = getDocumentScrollMetrics();
  if (!source) {
    return { allowed: false, reason: "no-source", documentHeight };
  }

  if (!hasReceivedHostPreferences) {
    return { allowed: false, reason: "host-prefs-missing", documentHeight };
  }

  if (!viewerChromeEnabled) {
    return { allowed: false, reason: "chrome-off", documentHeight };
  }

  if (minimapMode === "off") {
    return { allowed: false, reason: "mode-off", documentHeight };
  }

  if (!minimapPolicy) {
    return { allowed: false, reason: "policy-missing", documentHeight };
  }

  if (viewportHeight <= 0 || documentHeight <= viewportHeight) {
    return { allowed: false, reason: "not-scrollable", documentHeight };
  }

  if (minimapMode === "auto" && documentHeight > minimapPolicy.maxDetailedDocumentHeight) {
    return { allowed: false, reason: "auto-heavy", documentHeight };
  }

  return { allowed: true, documentHeight };
}

function isPolicyHeavyMinimapHeight(documentHeight: number): boolean {
  return minimapPolicy !== null && documentHeight > minimapPolicy.maxDetailedDocumentHeight;
}

function cloneDocumentForMinimap(): HTMLElement | null {
  const source = document.querySelector<HTMLElement>(".mm-document");
  if (!source) {
    minimapSourceReady = false;
    return null;
  }
  const sourceStyle = getComputedStyle(source);
  const clone = source.cloneNode(true) as HTMLElement;
  minimapSourceReady = true;
  clone.removeAttribute("id");
  clone.setAttribute("aria-hidden", "true");
  clone.inert = true;
  clone.style.paddingTop = sourceStyle.paddingTop;
  clone.style.paddingRight = "0";
  clone.style.paddingBottom = sourceStyle.paddingBottom;
  clone.style.paddingLeft = "0";
  // Single tree walk: id-strip + interactive-disable per node. Aria/role/name/for
  // scrubbing dropped — the clone is already inert + aria-hidden, so per-node
  // aria attributes have no a11y effect. On a 138-formula doc KaTeX produces
  // many aria-hidden spans; skipping per-node attribute iteration is a
  // measurable refresh-clone cost reduction.
  //
  // IDs are stripped only on HTML elements. SVG ids are load-bearing —
  // mermaid emits `<style>#mm-mermaid-XYZ .node rect{fill:...}</style>`
  // inside the SVG and `<path marker-end="url(#arrowhead-XYZ)"/>` arrow
  // refs, both scoped by the SVG's root id. Stripping those leaves the
  // SVG's selectors orphaned (boxes fall back to default black fill,
  // arrowheads disappear) — visible as dark filled rectangles in the
  // minimap clone while the source view paints correctly. Duplicate
  // ids across source/clone are accepted: the clone is inert and
  // aria-hidden, and SVG `url(#...)` lookups in Chromium resolve to the
  // first DOM match deterministically.
  clone.querySelectorAll<Element>("*").forEach((node) => {
    const isHtml = node.namespaceURI === "http://www.w3.org/1999/xhtml" || node.namespaceURI === null;
    if (isHtml && node.hasAttribute("id")) node.removeAttribute("id");
    const tag = node.tagName;
    if (tag === "A" || tag === "BUTTON" || tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") {
      node.setAttribute("tabindex", "-1");
      node.removeAttribute("href");
    }
  });
  return clone;
}

function refreshMinimapContent(phase: "A" | "B" = "A"): void {
  emitMark("mm-minimap-refresh-start", { phase });
  postPerfMark("mm-minimap-refresh-start", { phase });
  ensureMinimap();
  if (!minimapContent || !minimapRoot) {
    emitMark("mm-minimap-refresh-end", { phase, skipped: "no-mount" });
    postPerfMark("mm-minimap-refresh-end", { phase, skipped: "no-mount" });
    return;
  }
  const buildDecision = shouldBuildDetailedMinimapContent();
  if (!buildDecision.allowed) {
    minimapSourceReady = false;
    minimapDocumentHeight = buildDecision.documentHeight;
    minimapContent.replaceChildren();
    updateMinimapVisibility(true);
    emitMark("mm-minimap-refresh-skipped", {
      phase,
      reason: buildDecision.reason ?? "not-allowed",
      documentHeight: buildDecision.documentHeight
    });
    postPerfMark("mm-minimap-refresh-skipped", {
      phase,
      reason: buildDecision.reason ?? "not-allowed",
      documentHeight: buildDecision.documentHeight
    });
    return;
  }
  const clone = cloneDocumentForMinimap();
  if (!clone) {
    emitMark("mm-minimap-refresh-end", { phase, skipped: "no-source" });
    postPerfMark("mm-minimap-refresh-end", { phase, skipped: "no-source" });
    return;
  }
  const root = document.scrollingElement ?? document.documentElement;
  minimapDocumentHeight = root.scrollHeight;
  if (isPolicyHeavyMinimapDocument()) {
    minimapContent.style.width = `${calculateDocumentContentWidthFromCssModel(true)}px`;
  }
  minimapContent.replaceChildren(clone);
  updateMinimapVisibility(true);
  updateMinimapViewport({ skipVisibilityUpdate: true });
  emitMark("mm-minimap-refresh-end", { phase, documentHeight: minimapDocumentHeight });
  postPerfMark("mm-minimap-refresh-end", { phase, documentHeight: minimapDocumentHeight });
}

function ensureDetailedMinimapContentForVisiblePath(phase: "A" | "B" = "A"): void {
  if (minimapSourceReady || !shouldBuildDetailedMinimapContent().allowed) {
    return;
  }

  if (minimapContentRefreshTimer !== undefined) {
    window.clearTimeout(minimapContentRefreshTimer);
    minimapContentRefreshTimer = undefined;
  }
  refreshMinimapContent(phase);
}

function refreshInitialVisibleMinimapContent(): void {
  if (!minimapSourceReady) {
    refreshMinimapContent("A");
    return;
  }

  const root = document.scrollingElement ?? document.documentElement;
  minimapDocumentHeight = root.scrollHeight;
  updateMinimapVisibility(true);
  updateMinimapViewport();
  emitMark("mm-minimap-refresh-skipped", {
    phase: "A",
    reason: "initial-source-ready",
    documentHeight: minimapDocumentHeight
  });
  postPerfMark("mm-minimap-refresh-skipped", {
    phase: "A",
    reason: "initial-source-ready",
    documentHeight: minimapDocumentHeight
  });
}

function postCachedMinimapState(state: PostedMinimapState): void {
  ensureMinimap();
  if (!minimapRoot) {
    return;
  }

  const visible = state.hasPosted && state.visible;
  const reservedWidth = visible ? Math.max(0, state.reservedWidth) : 0;
  minimapRoot.hidden = !visible;
  document.body.classList.toggle(MINIMAP_VISIBLE_CLASS, visible);
  lastPostedMinimapState = { hasPosted: true, visible, reservedWidth };
  postHostMessage({ type: "minimap-state", visible, reservedWidth });
}

function restoreCachedMinimapContent(): boolean {
  const snapshot = restoredCachedMinimapSnapshot;
  restoredCachedMinimapSnapshot = null;
  if (!snapshot) {
    return false;
  }

  ensureMinimap();
  const restored = restoreMinimapSnapshot(snapshot, { minimapContent, minimapViewport });
  if (!restored) {
    return false;
  }

  minimapDocumentHeight = restored.documentHeight;
  minimapSourceReady = true;
  postCachedMinimapState(restored.lastPostedState);
  emitMark("mm-minimap-cache-hit", {
    documentHeight: restored.documentHeight,
    nodeCount: restored.contentNodeCount,
  });
  postPerfMark("mm-minimap-cache-hit", {
    documentHeight: restored.documentHeight,
    nodeCount: restored.contentNodeCount,
  });

  const refreshGeneration = layoutReadyGeneration;
  window.requestAnimationFrame(() => {
    if (refreshGeneration !== layoutReadyGeneration) {
      return;
    }

    updateMinimapVisibility(true);
    updateMinimapViewport();
    updateWidthHandlePositionForCurrentLayout();
  });
  return true;
}

// Avalonia-side Table of Contents (v0.3.2) — the renderer scans the active
// document's heading nodes after each chrome rebuild and posts the list
// upstream so the host can render a TreeView/ItemsControl panel outside
// the WebView. Stable slug ids generated by MarkdownHeadingAnchorSlugger
// during HTML production drive both directions of the IPC: list payload
// out, scroll-to-heading lookup in. Renderer-side TOC was deleted in
// commit 4aee666; this is the host-side replacement that meets the
// requirement to span the full content-area height with its own scroll.
let activeHeadingObserver: IntersectionObserver | null = null;
let lastPostedActiveHeadingId: string | null = null;

function extractAndPostHeadings(): void {
  const main = document.querySelector<HTMLElement>("main.mm-document");
  if (!main) {
    postHostMessage({ type: "headings-updated", headings: [] });
    lastExtractedHeadings = [];
    lastPostedActiveHeadingId = null;
    return;
  }

  const nodes = Array.from(
    main.querySelectorAll<HTMLHeadingElement>("h1, h2, h3, h4, h5, h6")
  );
  const headings = nodes
    .map((node) => {
      const id = node.id;
      if (!id) {
        return null;
      }
      const tag = node.tagName.toUpperCase();
      const level = Number.parseInt(tag.slice(1), 10);
      if (!Number.isFinite(level) || level < 1 || level > 6) {
        return null;
      }
      const text = (node.textContent ?? "").trim();
      return { id, level, text };
    })
    .filter((h): h is HeadingPayload => h !== null);

  lastExtractedHeadings = headings.map(heading => ({ ...heading }));
  postHostMessage({ type: "headings-updated", headings });
  rebuildActiveHeadingObserver(nodes.filter((n) => !!n.id));
}

function postCachedHeadings(): void {
  const headings = restoredCachedHeadings ?? [];
  restoredCachedHeadings = null;
  lastExtractedHeadings = headings.map(heading => ({ ...heading }));
  postHostMessage({ type: "headings-updated", headings });
  if (activeHeadingObserver) {
    activeHeadingObserver.disconnect();
    activeHeadingObserver = null;
  }
  lastPostedActiveHeadingId = null;
  const rebuildGeneration = layoutReadyGeneration;
  window.setTimeout(() => {
    if (rebuildGeneration !== layoutReadyGeneration) {
      return;
    }

    const main = document.querySelector<HTMLElement>("main.mm-document");
    if (!main) {
      return;
    }

    const nodes = Array.from(
      main.querySelectorAll<HTMLHeadingElement>("h1, h2, h3, h4, h5, h6")
    );
    rebuildActiveHeadingObserver(nodes.filter((n) => !!n.id));
  }, 750);
}

function rebuildActiveHeadingObserver(headingNodes: HTMLHeadingElement[]): void {
  if (activeHeadingObserver) {
    activeHeadingObserver.disconnect();
    activeHeadingObserver = null;
  }
  lastPostedActiveHeadingId = null;

  if (headingNodes.length === 0) {
    return;
  }

  // Track which headings intersect the top-of-viewport zone. Per heading we
  // store whether it is currently inside the zone; on each callback we pick
  // the first heading whose top is at-or-above the threshold but whose
  // bottom is at-or-below, i.e. the heading "above" the current scroll
  // position. Top-margin = 0; bottom margin = -<viewport-100px> so only
  // headings near the top edge fire intersections. This matches the
  // behaviour the deleted renderer-side tocPanel.ts had — using the same
  // sliver-at-top IntersectionObserver pattern.
  const inViewport = new Set<HTMLHeadingElement>();
  const callback: IntersectionObserverCallback = (entries) => {
    for (const entry of entries) {
      const target = entry.target as HTMLHeadingElement;
      if (entry.isIntersecting) {
        inViewport.add(target);
      } else {
        inViewport.delete(target);
      }
    }

    // Active heading: the LAST heading whose top has crossed above the
    // viewport top. If none have, fall back to the first heading.
    let active: HTMLHeadingElement | null = null;
    for (const node of headingNodes) {
      const rect = node.getBoundingClientRect();
      if (rect.top <= 10) {
        active = node;
      } else {
        break;
      }
    }
    if (active === null) {
      active = headingNodes[0] ?? null;
    }
    if (active === null) {
      return;
    }

    const id = active.id;
    if (id && id !== lastPostedActiveHeadingId) {
      lastPostedActiveHeadingId = id;
      postHostMessage({ type: "active-heading-changed", id });
    }
  };

  // rootMargin: top=0 (count any heading whose top crossed the viewport
  // top); bottom = -(viewport-50) so observers only fire near the top.
  activeHeadingObserver = new IntersectionObserver(callback, {
    rootMargin: "0px 0px -85% 0px",
    threshold: [0, 1],
  });
  for (const node of headingNodes) {
    activeHeadingObserver.observe(node);
  }

  // Emit an initial active-heading guess so the TOC highlights the right
  // row before the user scrolls.
  window.requestAnimationFrame(() => {
    let active: HTMLHeadingElement | null = null;
    for (const node of headingNodes) {
      const rect = node.getBoundingClientRect();
      if (rect.top <= 10) {
        active = node;
      } else {
        break;
      }
    }
    if (active === null) {
      active = headingNodes[0] ?? null;
    }
    if (active && active.id && active.id !== lastPostedActiveHeadingId) {
      lastPostedActiveHeadingId = active.id;
      postHostMessage({ type: "active-heading-changed", id: active.id });
    }
  });
}

function shouldShowMinimap(): boolean {
  const root = document.scrollingElement ?? document.documentElement;
  const documentHeight = root.scrollHeight;
  const viewportHeight = root.clientHeight;
  // F-07 fix: minimap decisions require both host preferences AND the
  // canonical minimap policy delivered via the minimap-policy IPC
  // message. Either missing means the renderer is still in the pre-
  // policy bootstrap window; deny the decision so a stale built-in
  // literal cannot drive a minimap show/hide.
  if (!hasReceivedHostPreferences
    || !minimapPolicy
    || !viewerChromeEnabled
    || !minimapSourceReady
    || minimapMode === "off"
    || viewportHeight <= 0
    || documentHeight <= viewportHeight) {
    return false;
  }

  if (minimapMode === "on") {
    return true;
  }

  if (documentHeight > minimapPolicy.maxDetailedDocumentHeight) {
    return false;
  }

  return window.innerWidth >= minimapPolicy.minHostWidth
    && documentHeight >= viewportHeight * minimapPolicy.minScrollableViewportRatio;
}

function updateMinimapVisibility(forcePostState = false): boolean {
  ensureMinimap();
  if (!minimapRoot) {
    return false;
  }

  const wasVisible = !minimapRoot.hidden;
  const hadClass = document.body.classList.contains(MINIMAP_VISIBLE_CLASS);
  const visible = shouldShowMinimap();
  minimapRoot.hidden = !visible;
  document.body.classList.toggle(MINIMAP_VISIBLE_CLASS, visible);
  postMinimapState(visible, forcePostState);
  // Explicit handle re-alignment on minimap visibility transition. The body
  // class toggle changes .mm-document padding-right (240↔72) and max-width
  // calc (X+168↔X). When content forces a column min-width that exceeds the
  // user-set max-width (wide formulas/tables/code), the BORDER-box width
  // stays constant across the toggle while only padding-right shifts —
  // ResizeObserver default observes content-box but its callback delivery
  // relative to paint is not guaranteed same-frame in WebView2, producing
  // a visible one-frame gap where the handle sits at the OLD textRight+24
  // while text expands by 168px to the new position. Directly calling
  // updateWidthHandlePosition here closes that gap; idempotent with the
  // ResizeObserver fallback, costs one forced layout when (and only when)
  // visibility actually transitioned.
  const changed = wasVisible !== visible || hadClass !== visible;
  if (changed) {
    updateWidthHandlePositionForCurrentLayout();
  }
  return changed;
}

function readConfiguredMinimapReservedWidth(): number {
  const minimapGap = readRootPixelVariable("--mm-minimap-gap", 0);
  const configuredMinimapWidth = readRootPixelVariable("--mm-minimap-width", 0);
  if (configuredMinimapWidth > 0) {
    return Math.max(0, configuredMinimapWidth + minimapGap * 2);
  }

  return 0;
}

function getCurrentMinimapReservedWidth(): number {
  if (!minimapRoot || minimapRoot.hidden) {
    return 0;
  }

  const configuredReservedWidth = readConfiguredMinimapReservedWidth();
  if (configuredReservedWidth > 0) {
    return configuredReservedWidth;
  }

  const minimapGap = readRootPixelVariable("--mm-minimap-gap", 0);
  const minimapWidth = minimapRoot.getBoundingClientRect().width;
  return Math.max(0, minimapWidth + minimapGap * 2);
}

function postMinimapState(visible: boolean, force = false): void {
  const reservedWidth = visible ? getCurrentMinimapReservedWidth() : 0;
  const nextState = { visible, reservedWidth };
  if (!shouldPostMinimapState(lastPostedMinimapState, nextState, force)) {
    return;
  }

  lastPostedMinimapState = { ...nextState, hasPosted: true };
  postHostMessage({ type: "minimap-state", visible, reservedWidth });
}

function postTransactionMinimapSettled(transactionGeneration: number): void {
  if (!Number.isFinite(transactionGeneration) || transactionGeneration <= 0) {
    return;
  }

  ensureDetailedMinimapContentForVisiblePath();
  updateMinimapVisibility(true);
  updateMinimapViewport({ skipVisibilityUpdate: true });
  const visible = minimapRoot ? !minimapRoot.hidden : false;
  const reservedWidth = visible ? getCurrentMinimapReservedWidth() : 0;
  postHostMessage({
    type: "minimap-settled",
    transactionGeneration,
    visible,
    reservedWidth,
  });
}

function shouldMeasureMinimapCloneHeight(documentScrollHeight: number): boolean {
  return !minimapPolicy || documentScrollHeight <= minimapPolicy.maxDetailedDocumentHeight;
}

type MinimapViewportUpdateOptions = {
  skipVisibilityUpdate?: boolean;
};

function updateMinimapViewport(options: MinimapViewportUpdateOptions = {}): void {
  ensureMinimap();
  if (!minimapRoot || !minimapContent || !minimapViewport) {
    return;
  }

  if (options.skipVisibilityUpdate !== true) {
    updateMinimapVisibility();
  }
  if (minimapRoot.hidden) {
    currentMinimapLayout = null;
    return;
  }

  const root = document.scrollingElement ?? document.documentElement;
  // root.scrollHeight / root.scrollTop describe the REAL scroll range the user
  // scrolls over; keep them as the basis for scroll PROGRESS so the thumb
  // tracks actual user scroll (thumb correctness is not negotiable).
  const knownPolicyHeavyDocument = isPolicyHeavyMinimapDocument();
  const documentScrollHeight = knownPolicyHeavyDocument && minimapDocumentHeight > 0
    ? minimapDocumentHeight
    : root.scrollHeight;
  const policyHeavyDocument = knownPolicyHeavyDocument
    || (minimapPolicy !== null && documentScrollHeight > minimapPolicy.maxDetailedDocumentHeight);
  const source = policyHeavyDocument ? null : document.querySelector<HTMLElement>(".mm-document");
  if (!policyHeavyDocument && !source) {
    return;
  }
  const minimapWidth = policyHeavyDocument
    ? readRootPixelVariable("--mm-minimap-width", 136)
    : minimapRoot.clientWidth;
  const minimapHeight = policyHeavyDocument
    ? Math.max(0, window.innerHeight - 128)
    : minimapRoot.clientHeight;
  const documentWidth = policyHeavyDocument
    ? calculateDocumentContentWidthFromCssModel(!minimapRoot.hidden)
    : (() => {
        const sourceElement = source!;
        const sourceStyle = getComputedStyle(sourceElement);
        return calculateMinimapDocumentWidth({
          borderBoxWidth: sourceElement.clientWidth || sourceElement.getBoundingClientRect().width,
          paddingLeft: readPixelValue(sourceStyle.paddingLeft),
          paddingRight: readPixelValue(sourceStyle.paddingRight),
        });
      })();
  const viewportHeight = policyHeavyDocument
    ? Math.max(0, window.innerHeight)
    : root.clientHeight;
  if (minimapHeight <= 0 || minimapWidth <= 0 || documentScrollHeight <= 0 || viewportHeight <= 0) {
    return;
  }

  // Content height must be the CLONE's true rendered height, not
  // root.scrollHeight for ordinary documents: the minimap clone is intentionally
  // NOT content-visibility-covered, so exact clone height keeps bottom mapping
  // precise. On policy-heavy documents in explicit `on` mode, that first clone
  // scrollHeight read is itself a full-clone synchronous layout; use the source
  // scroll range instead and keep interaction under the 100ms heavy-doc budget.
  const nextContentWidth = `${documentWidth}px`;
  if (minimapContent.style.width !== nextContentWidth) {
    minimapContent.style.width = nextContentWidth;
  }
  const measuredContentHeight = shouldMeasureMinimapCloneHeight(documentScrollHeight)
    ? minimapContent.scrollHeight
    : 0;
  const contentHeight = measuredContentHeight > 0 ? measuredContentHeight : documentScrollHeight;

  // Bridge the two coordinate systems (real content-visibility scroll range vs.
  // the clone's full height) through dimensionless progress, then express the
  // position in clone space. The existing (unit-tested) layout math then drives
  // both content and thumb to the true bottom at progress 1.
  const realMaxScrollTop = Math.max(0, documentScrollHeight - viewportHeight);
  const scrollProgress = realMaxScrollTop > 0
    ? Math.min(1, Math.max(0, root.scrollTop / realMaxScrollTop))
    : 0;
  const contentScrollTop = scrollProgress * Math.max(0, contentHeight - viewportHeight);

  const layout = calculateMinimapViewportLayout({
    minimapWidth,
    minimapHeight,
    documentWidth,
    documentHeight: contentHeight,
    viewportHeight,
    scrollTop: contentScrollTop
  });
  if (!layout) {
    currentMinimapLayout = null;
    return;
  }

  currentMinimapLayout = layout;
  minimapContent.style.transform = layout.transform;
  // width already set above (same value as layout.contentWidth) for measurement
  minimapViewport.style.transform = `translateY(${layout.thumbTop}px)`;
  minimapViewport.style.height = `${layout.thumbHeight}px`;
}

function getCurrentMinimapThumbTravel(): number {
  if (currentMinimapLayout) {
    return Math.max(1, currentMinimapLayout.thumbTravel);
  }

  const minimapHeight = minimapRoot?.clientHeight ?? 0;
  return Math.max(1, minimapHeight - 22);
}

function scrollFromMinimapClientY(clientY: number): void {
  if (!minimapRoot) {
    return;
  }

  const root = document.scrollingElement ?? document.documentElement;
  const rect = minimapRoot.getBoundingClientRect();
  const minimapY = Math.max(0, Math.min(rect.height, clientY - rect.top));
  // Range-based click-jump: cursor at minimap_y maps to scrollTop such that
  // the viewport indicator's TOP ends up at minimap_y. Consistent with the
  // pan/grab drag math: pointer position on the thumb-travel range maps
  // linearly to scrollTop on its scrollable range. Clicking at top of
  // minimap = top of document; clicking at the max-thumb-top position =
  // bottom of document.
  const thumbTravel = getCurrentMinimapThumbTravel();
  const maxScrollTop = Math.max(0, root.scrollHeight - root.clientHeight);
  const targetScrollTop = (Math.min(minimapY, thumbTravel) / thumbTravel) * maxScrollTop;
  const clamped = Math.max(0, Math.min(maxScrollTop, targetScrollTop));
  window.scrollTo({ top: clamped, behavior: "instant" as ScrollBehavior });
}

function scrollToProgress(progressPercent: number): void {
  const root = document.scrollingElement ?? document.documentElement;
  const maximum = Math.max(0, root.scrollHeight - root.clientHeight);
  const progress = Number.isFinite(progressPercent) ? Math.max(0, Math.min(100, progressPercent)) : 0;
  window.scrollTo({ top: maximum * (progress / 100), behavior: "instant" as ScrollBehavior });
}

// Pointer-down records start state but does NOT scroll yet. Tap-vs-drag is
// distinguished on the first pointer-move that exceeds MINIMAP_DRAG_THRESHOLD_PX.
// - Below threshold (and on pointer-up while still tentative): treat as a
//   tap → centered click-jump (existing scrollbar-trough-click behavior).
// - At/above threshold: switch to "panning" mode — drag DOWN on the minimap
//   pulls the document content DOWN (i.e., scrolls scrollTop UP), so the
//   point under the cursor stays anchored. Pan/grab UX, like dragging a
//   paper map under a fixed crosshair. Opposite direction from a scrollbar.
function handleMinimapPointerDown(event: PointerEvent): void {
  minimapDragging = true;
  minimapDragStartClientY = event.clientY;
  const root = document.scrollingElement ?? document.documentElement;
  minimapDragStartScrollTop = root.scrollTop;
  minimapDragMode = "tentative";
  minimapRoot?.setPointerCapture(event.pointerId);
  event.preventDefault();
}

function handleMinimapPointerMove(event: PointerEvent): void {
  if (!minimapDragging || minimapDragStartClientY === null) {
    return;
  }

  const delta = event.clientY - minimapDragStartClientY;
  if (minimapDragMode === "tentative" && Math.abs(delta) < MINIMAP_DRAG_THRESHOLD_PX) {
    return;
  }
  minimapDragMode = "panning";

  // Range-based mapping: cursor traversing the rendered thumb-travel range
  // scrolls the document across its full
  // scrollable range (scrollHeight - clientHeight). The indicator's top
  // follows the cursor 1:1 in minimap pixels — feels like "grabbing the
  // viewport indicator and dragging it from start to end".
  const root = document.scrollingElement ?? document.documentElement;
  const thumbTravel = getCurrentMinimapThumbTravel();
  const maxScrollTop = Math.max(0, root.scrollHeight - root.clientHeight);
  const scrollDelta = delta * (maxScrollTop / thumbTravel);
  const newScrollTop = minimapDragStartScrollTop + scrollDelta;
  const clampedScrollTop = Math.max(0, Math.min(maxScrollTop, newScrollTop));
  window.scrollTo({ top: clampedScrollTop, behavior: "instant" as ScrollBehavior });
  event.preventDefault();
}

function handleMinimapPointerUp(event: PointerEvent): void {
  if (!minimapDragging) {
    return;
  }
  const wasTap = minimapDragMode === "tentative";
  minimapDragging = false;
  minimapDragStartClientY = null;
  minimapDragMode = "tentative";
  try {
    minimapRoot?.releasePointerCapture(event.pointerId);
  } catch {
    // Pointer capture may already be gone after WebView focus changes.
  }
  if (wasTap) {
    // Below drag threshold — treat as click → centered jump.
    scrollFromMinimapClientY(event.clientY);
  }
}

function queueMinimapViewportUpdate(perfMarkName?: string): void {
  if (minimapViewportFrameRequested) {
    return;
  }

  minimapViewportFrameRequested = true;
  window.requestAnimationFrame(() => {
    minimapViewportFrameRequested = false;
    updateMinimapVisibility();
    updateMinimapViewport();
    if (perfMarkName) {
      postPerfMark(perfMarkName);
    }
  });
}

function queueMinimapRefreshAfterLayoutSettles(): void {
  window.clearTimeout(minimapRefreshTimer);
  minimapRefreshTimer = window.setTimeout(() => {
    // Stage 2 decoupling: only viewport update on resize; content rebuild
    // is driven by content-change OR Phase B (Task 15) — not by layout settling.
    queueMinimapViewportUpdate();
  }, MINIMAP_REFRESH_DEBOUNCE_MS);
}

function queueMinimapContentRefreshAfterLayoutSettles(phase: "A" | "B" = "A"): void {
  window.clearTimeout(minimapContentRefreshTimer);
  minimapContentRefreshTimer = window.setTimeout(() => {
    minimapContentRefreshTimer = undefined;
    refreshMinimapContent(phase);
  }, MINIMAP_REFRESH_DEBOUNCE_MS);
}

// Coalesces resize-time reactive work to at most one synchronous-layout pass
// per frame. Both `window.addEventListener("resize", ...)` and the
// ResizeObserver watching .mm-document + body route through this. Without
// coalescing, a fast window-edge drag fires getBoundingClientRect() reads on
// every event, causing the width-handle to snap visibly each tick. The actual
// minimap viewport update is itself rAF-coalesced via queueMinimapViewportUpdate
// (single token); coalescing the synchronous-layout reads here is the missing
// piece for stable chrome positions during drag.
function scheduleResizeReactions(): void {
  if (resizeReactFrameRequested) {
    return;
  }

  if (modeRevealPrepared) {
    return;
  }

  resizeReactFrameRequested = true;
  window.requestAnimationFrame(() => {
    resizeReactFrameRequested = false;
    if (widthHandleDragging) {
      // The drag pipeline owns width preview during pointer drag and performs
      // canonical handle/minimap reconciliation on release.
      // Resize ticks during drag must not double-process.
      return;
    }
    updateWidthHandlePositionForCurrentLayout();
    queueMinimapViewportUpdate();
  });
}

type AppliedReadingPreferences = {
  fontFamily: FontFamilyMode;
  fontSize: number;
  lineHeight: number;
  maxWidth: number;
  minimapMode: MinimapMode;
  viewerChromeEnabled: boolean;
  documentScrollEnabled: boolean;
  wheelProxyEnabled: boolean;
  widthResizerVisibility: WidthResizerVisibility;
};

let lastAppliedReadingPreferences: AppliedReadingPreferences | null = null;
let pendingReadingPreferences: AppliedReadingPreferences | null = null;
let pendingReadingPreferencesSkipFrameWait = false;
let applyPrefsFrameRequested = false;
// Host-provided floor for max-width during drag (mirrors host's clamp).
// Without it, drag preview can dip below host's min and snap wider on release.
const RENDERER_FALLBACK_MIN_MAX_WIDTH = 320;
let hostMinMaxWidth = RENDERER_FALLBACK_MIN_MAX_WIDTH;
let heavyLiveUpdateTimer: number | undefined;
const HEAVY_LIVE_UPDATE_DEBOUNCE_MS = 80;

function normalizeFontFamilyMode(value: string | undefined): FontFamilyMode {
  if (value === "sans" || value === "mono") return value;
  return "serif";
}

// Single canonical live-preference application:
//   Step 1 — stash latest desired values; coalesce rapid IPC bursts via rAF
//   Step 2 (rAF) — diff vs last-applied → Phase A (sync CSS) → Phase B (debounced)
// Coalescing matters on heavy docs: each delta causes a CSS reflow over the
// whole document, and when reflow takes >16ms the browser naturally skips rAFs
// until done — so apply rate self-throttles to what the device can sustain.
// One pipeline, one fast path, data-driven by which fields actually changed.
function applyReadingPreferences(message: Extract<HostMessage, { type: "reading-preferences" }>): void {
  if (typeof message.minMaxWidth === "number" && Number.isFinite(message.minMaxWidth) && message.minMaxWidth > 0) {
    hostMinMaxWidth = message.minMaxWidth;
  }
  const next: AppliedReadingPreferences = {
    fontFamily: normalizeFontFamilyMode(message.fontFamily),
    fontSize: message.fontSize,
    lineHeight: message.lineHeight,
    maxWidth: message.maxWidth,
    minimapMode: message.minimapMode,
    viewerChromeEnabled: message.viewerChromeEnabled ?? true,
    documentScrollEnabled: message.documentScrollEnabled ?? true,
    wheelProxyEnabled: message.wheelProxyEnabled ?? false,
    widthResizerVisibility: normalizeWidthResizerVisibility(message.widthResizerVisibility),
  };
  pendingReadingPreferences = next;
  pendingReadingPreferencesSkipFrameWait =
    pendingReadingPreferencesSkipFrameWait || message.skipFrameWait === true;
  if (!next.viewerChromeEnabled) {
    viewerChromeEnabled = false;
    applyViewerChromeState();
    updateMinimapVisibility(true);
    updateWidthHandlePositionForCurrentLayout();
  }
  if (applyPrefsFrameRequested) return;
  applyPrefsFrameRequested = true;
  requestAnimationFrame(flushPendingReadingPreferences);
}

function flushPendingReadingPreferences(): void {
  applyPrefsFrameRequested = false;
  const next = pendingReadingPreferences;
  const skipFrameWait = pendingReadingPreferencesSkipFrameWait;
  pendingReadingPreferences = null;
  pendingReadingPreferencesSkipFrameWait = false;
  if (!next) return;

  const prev = lastAppliedReadingPreferences;
  const fontFamilyChanged = !prev || prev.fontFamily !== next.fontFamily;
  const fontSizeChanged = !prev || prev.fontSize !== next.fontSize;
  const lineHeightChanged = !prev || prev.lineHeight !== next.lineHeight;
  const maxWidthChanged = !prev || prev.maxWidth !== next.maxWidth;
  const minimapModeChanged = !prev || prev.minimapMode !== next.minimapMode;
  const viewerChromeChanged = !prev || prev.viewerChromeEnabled !== next.viewerChromeEnabled;
  const documentScrollChanged = !prev || prev.documentScrollEnabled !== next.documentScrollEnabled;
  const wheelProxyChanged = !prev || prev.wheelProxyEnabled !== next.wheelProxyEnabled;
  const widthResizerVisibilityChanged = !prev || prev.widthResizerVisibility !== next.widthResizerVisibility;

  // Phase A — presentation (cheap, synchronous).
  const root = document.documentElement;
  if (fontFamilyChanged) root.dataset.mmFontFamily = next.fontFamily;
  if (fontSizeChanged) root.style.setProperty("--mm-document-font-size", `${next.fontSize}px`);
  if (lineHeightChanged) root.style.setProperty("--mm-document-line-height", `${next.lineHeight}`);
  // Width drag owns its own local preview via handleWidthHandlePointerMove;
  // skip applying host's echo during drag so the column doesn't snap.
  if (maxWidthChanged && !widthHandleDragging) {
    root.style.setProperty("--mm-document-max-width", `${next.maxWidth}px`);
  }
  if (minimapModeChanged) minimapMode = next.minimapMode;
  if (viewerChromeChanged) {
    viewerChromeEnabled = next.viewerChromeEnabled;
    applyViewerChromeState();
    // Anti-blink for edit/reading toggle. When chrome flips to false
    // (entering edit-preview), minimap + width-handle must hide in this
    // synchronous Phase-A rather than waiting for the 80 ms scheduleHeavy-
    // LiveUpdate timer that eventually runs queueMinimapViewportUpdate
    // → updateMinimapVisibility. The delayed path leaves them painted
    // from the prior viewer state for ~80–180 ms after the toggle
    // ("minimap+resizer не успевают спрятаться, из-за этого
    // перерисовывается render"). updateMinimapVisibility is idempotent;
    // updateWidthHandlePosition reads viewerChromeEnabled for its
    // hidden flag. For the chrome=true (entering reader) direction
    // we keep the heavy-update path for minimap (shouldShowMinimap
    // requires policy + layout signals that may not be ready in
    // Phase A), but call updateWidthHandlePosition synchronously on
    // BOTH branches — without the chrome=true call the width-handle
    // stays hidden after edit→viewer round-trip because
    // hasInitialLayoutSettled remains true (correct, doc settled
    // previously) yet nothing fires updateWidthHandlePosition on the
    // chrome=true branch unless minimap visibility flips or window
    // is resized (Codex polish-05 r1 finding).
    if (!viewerChromeEnabled) {
      updateMinimapVisibility(true);
      updateWidthHandlePositionForCurrentLayout();
    } else {
      updateWidthHandlePositionForCurrentLayout();
    }
  }
  if (documentScrollChanged) {
    documentScrollEnabled = next.documentScrollEnabled;
    applyDocumentScrollState();
  }
  if (wheelProxyChanged) {
    wheelProxyEnabled = next.wheelProxyEnabled;
  }
  if (widthResizerVisibilityChanged) {
    widthResizerVisibility = next.widthResizerVisibility;
    const widthResizerClasses = getWidthResizerVisibilityClasses(widthResizerVisibility);
    document.body.classList.toggle(WIDTH_RESIZER_ALWAYS_CLASS, widthResizerClasses.alwaysClass);
  }

  const hadHostPreferences = hasReceivedHostPreferences;
  hasReceivedHostPreferences = true;
  lastAppliedReadingPreferences = next;

  // Width handle anchor depends purely on .mm-document size + body size →
  // observed by the canonical ResizeObserver wired in DOMContentLoaded. The
  // explicit per-pref-change call here was redundant defensive code from
  // an earlier era when there was no observer; removed for one-path clarity.

  // Phase B — heavy work (minimap viewport recompute). Only schedule when a
  // layout-affecting field actually changed; debounce so rapid slider drags
  // coalesce to one recompute per quiet period instead of one per IPC frame.
  // widthResizerVisibility changes only handle opacity → no minimap impact.
  const layoutAffectingChange = fontFamilyChanged
    || fontSizeChanged
    || lineHeightChanged
    || maxWidthChanged
    || minimapModeChanged
    || viewerChromeChanged;
  if (layoutAffectingChange) {
    if (!minimapSourceReady && shouldBuildDetailedMinimapContent().allowed) {
      queueMinimapContentRefreshAfterLayoutSettles();
    } else {
      scheduleHeavyLiveUpdate();
    }
  }

  if (!hadHostPreferences && !initialRenderPipelineCompleted) {
    // First reading-preferences — the pipeline owns layout-ready; heavy
    // Mermaid/code-block enhancement is deferred behind the first readable
    // paint so large full-DOM documents do not block on off-screen work.
    const pipelineGeneration = ++initialRenderPipelineGeneration;
    void runInitialRenderPipeline({
      getCurrentTheme,
      applyTheme,
      initMermaidWithTheme,
      renderMath,
      renderMermaid,
      renderCodeBlocks,
      deferPostReadyWork: deferPostReadyEnhancements,
      scheduleLayoutReady: () => {
        initialRenderPipelineCompleted = true;
        scheduleLayoutReady(skipFrameWait);
      },
      postPerfMark,
      notifyPostReadyEnhancementsComplete: () => {
        postPostReadyEnhancementsComplete(undefined, undefined, undefined);
      },
      isCurrent: () => pipelineGeneration === initialRenderPipelineGeneration,
    });
  }

  // Live updates intentionally do NOT call scheduleLayoutReady(). The host's
  // SendReadingPreferences no longer resets _awaitingLayoutReady on live
  // updates, so re-emitting it would only add per-frame IPC noise.
}

function scheduleHeavyLiveUpdate(): void {
  if (heavyLiveUpdateTimer !== undefined) {
    window.clearTimeout(heavyLiveUpdateTimer);
  }
  heavyLiveUpdateTimer = window.setTimeout(() => {
    heavyLiveUpdateTimer = undefined;
    queueMinimapViewportUpdate();
  }, HEAVY_LIVE_UPDATE_DEBOUNCE_MS);
}

function handleHostMessage(raw: unknown): void {
  const message = raw as HostMessage;
  if (message.type === "host-shortcuts-reset") {
    resetHostShortcutsForModeSwitch?.();
    return;
  }

  if (message.type === "theme") {
    if (initialRenderPipelineCompleted) {
      void handleThemeChange(message.theme);
    } else {
      // Pre-pipeline theme — just set the attribute; the pipeline will
      // re-initialize Mermaid with the right theme when it runs.
      document.documentElement.dataset.theme = message.theme;
    }
    return;
  }

  if (message.type === "minimap-policy") {
    minimapPolicy = message.minimapPolicy;
    if (!minimapSourceReady && shouldBuildDetailedMinimapContent().allowed) {
      queueMinimapContentRefreshAfterLayoutSettles();
    } else {
      queueMinimapViewportUpdate();
    }
    return;
  }

  if (message.type === "reading-preferences") {
    applyReadingPreferences(message);
    return;
  }

  if (message.type === "scroll-to") {
    document.getElementById(message.anchor)?.scrollIntoView({ block: "start" });
    return;
  }

  if (message.type === "scroll-to-heading") {
    // Avalonia-side TOC click handler. The heading id matches the slug
    // used by MarkdownHeadingAnchorSlugger when generating <h1..h6 id="...">
    // in ApplicateHtmlMarkdownRenderer, so getElementById resolves the
    // exact heading the user clicked in the host-side TOC panel.
    const target = document.getElementById(message.id);
    if (target) {
      target.scrollIntoView({ behavior: "smooth", block: "start" });
    }
    return;
  }

  if (message.type === "scroll-to-source-line") {
    scrollToSourceLine(message.sourceLine);
    return;
  }

  if (message.type === "open-find-bar") {
    // Magnifier toolbar button — open the same find bar the Ctrl+F
    // keystroke would open. Toggle, so a second magnifier click closes it.
    findBarController?.toggle();
    return;
  }

  if (message.type === "host-scrollbar") {
    // When the Avalonia host installs its own overlay ScrollBar (edit mode
    // preview pane), hide the Chromium ::-webkit-scrollbar so the two
    // scrollbars don't compete visually and the host control is the only
    // visible scroll affordance. Wheel/touch/keyboard scrolling continues
    // to work natively because overflow-y stays auto.
    document.documentElement.dataset.mmHostScrollbar = message.active ? "on" : "off";
    return;
  }

  if (message.type === "scroll-to-progress") {
    scrollToProgress(message.progressPercent);
    return;
  }

  if (message.type === "scroll-by") {
    window.scrollBy({ top: message.deltaY, behavior: "instant" as ScrollBehavior });
    return;
  }

  if (message.type === "scroll-to-block") {
    const target = document.querySelector<HTMLElement>(
      `[data-mm-block-index="${message.blockIndex}"]`
    );
    if (target) {
      target.scrollIntoView({ block: "start", behavior: "instant" as ScrollBehavior });
    }
    return;
  }

  if (message.type === "load-document") {
    const loadMessage: import("./loadDocument").LoadDocumentMessage = { html: message.html };
    if (message.documentName !== undefined) {
      loadMessage.documentName = message.documentName;
    }
    if (message.theme !== undefined) {
      loadMessage.theme = message.theme;
    }
    if (message.renderId !== undefined) {
      loadMessage.renderId = message.renderId;
    }
    if (message.skipFrameWait !== undefined) {
      loadMessage.skipFrameWait = message.skipFrameWait;
    }
    // PE r2 item G — propagate hasMermaid from the IPC payload (host computes
    // this from ApplicateHtmlMarkdownRenderer.RenderBodyAsync at
    // ApplicateWebMarkdownDocumentView.cs:557). Undefined → mermaid runs by
    // default; false → mermaid init+render are skipped in the pipeline.
    if (message.hasMermaid !== undefined) {
      loadMessage.hasMermaid = message.hasMermaid;
    }
    if (message.hasHljs !== undefined) {
      loadMessage.hasHljs = message.hasHljs;
    }
    loadMessage.cacheKey = createProcessedDocumentCacheKey(
      message.html,
      message.theme ?? getCurrentTheme());
    applyLoadDocument(loadMessage, buildLoadDocumentDeps());
    return;
  }

  if (message.type === "clear-document") {
    clearModeRevealShield();
    clearDocumentState(buildLoadDocumentDeps());
    return;
  }

  if (message.type === "mode-settle-probe") {
    postPerfMark("mm-mode-settle-probe-received");
    applyModeSettleProbePreferences(message);
    const transactionGeneration = readModeSettleTransactionGeneration(message);
    if (modeToggleProbeFrameRequested) {
      if (
        transactionGeneration === undefined
        || (
          modeToggleProbeTransactionGeneration !== undefined
          && transactionGeneration <= modeToggleProbeTransactionGeneration
        )
      ) {
        postPerfMark("mm-mode-settle-probe-duplicate");
        return;
      }

      postPerfMark("mm-mode-settle-probe-superseded", {
        previousGeneration: modeToggleProbeTransactionGeneration,
        transactionGeneration,
      });
    }

    modeToggleProbeFrameRequested = true;
    modeToggleProbeTransactionGeneration = transactionGeneration;
    const probeToken = ++modeToggleProbeToken;
    const isCurrentProbe = () => probeToken === modeToggleProbeToken;
    const postModeToggleSettleAck = () => {
      if (!isCurrentProbe()) {
        return;
      }

      postPerfMark("mm-mode-settle-chrome-ready");
      modeToggleProbeFrameRequested = false;
      modeToggleProbeTransactionGeneration = undefined;
      if (transactionGeneration === undefined) {
        postHostMessage({ type: "mode-toggle-settled" });
      } else {
        postHostMessage({ type: "mode-toggle-settled", transactionGeneration });
      }
    };
    flushPendingReadingPreferences();
    ensureDetailedMinimapContentForVisiblePath();
    if (message.skipFrameWait === true) {
      postPerfMark("mm-mode-settle-frame-wait-skipped", {
        transactionGeneration,
      });
      postModeToggleSettleAck();
      return;
    }

    const completeModeToggleSettleAfterPaint = () => {
      if (!isCurrentProbe()) {
        return;
      }

      updateMinimapViewport();
      updateWidthHandlePositionForCurrentLayout();
      window.requestAnimationFrame(() => {
        if (!isCurrentProbe()) {
          return;
        }

        postPerfMark("mm-mode-settle-post-chrome-paint");
        postModeToggleSettleAck();
      });
    };

    const settleAfterViewportReady = (attempt: number) => {
      if (!isCurrentProbe()) {
        return;
      }

      if (!isModeSettleViewportReady(message) && attempt < MODE_SETTLE_VIEWPORT_MAX_FRAMES) {
        postPerfMark("mm-mode-settle-viewport-wait", {
          attempt,
          width: window.innerWidth,
          height: window.innerHeight,
          expectedWidth: message.viewportWidth,
          expectedHeight: message.viewportHeight,
        });
        window.requestAnimationFrame(() => settleAfterViewportReady(attempt + 1));
        return;
      }

      postPerfMark("mm-mode-settle-first-raf");
      flushPendingReadingPreferences();
      ensureDetailedMinimapContentForVisiblePath();
      updateMinimapVisibility();
      updateMinimapViewport();
      updateWidthHandlePositionForCurrentLayout();

      if (!viewerChromeEnabled) {
        completeModeToggleSettleAfterPaint();
        return;
      }

      window.requestAnimationFrame(() => {
        if (!isCurrentProbe()) {
          return;
        }

        postPerfMark("mm-mode-settle-second-raf");
        flushPendingReadingPreferences();
        ensureDetailedMinimapContentForVisiblePath();
        updateMinimapVisibility();
        updateMinimapViewport();
        updateWidthHandlePositionForCurrentLayout();
        completeModeToggleSettleAfterPaint();
      });
    };

    window.requestAnimationFrame(() => settleAfterViewportReady(0));
    return;
  }

  if (message.type === "mode-reveal-prepare") {
    prepareModeReveal(message.durationMs);
    return;
  }

  if (message.type === "mode-reveal-start") {
    startModeReveal(message.durationMs);
    return;
  }

  if (message.type === "document-reveal-prepare") {
    prepareDocumentReveal(message.durationMs, message.theme);
    return;
  }

  if (message.type === "document-reveal-start") {
    startDocumentReveal(message.durationMs);
    return;
  }

  if (message.type === "minimap-settle-probe") {
    postTransactionMinimapSettled(message.transactionGeneration);
    return;
  }
}

function isModeSettleViewportReady(message: Extract<HostMessage, { type: "mode-settle-probe" }>): boolean {
  const widthReady = typeof message.viewportWidth !== "number"
    || !Number.isFinite(message.viewportWidth)
    || message.viewportWidth <= 0
    || Math.abs(window.innerWidth - message.viewportWidth) <= MODE_SETTLE_VIEWPORT_TOLERANCE;
  const heightReady = typeof message.viewportHeight !== "number"
    || !Number.isFinite(message.viewportHeight)
    || message.viewportHeight <= 0
    || Math.abs(window.innerHeight - message.viewportHeight) <= MODE_SETTLE_VIEWPORT_TOLERANCE;
  return widthReady && heightReady;
}

function readModeSettleTransactionGeneration(message: Extract<HostMessage, { type: "mode-settle-probe" }>): number | undefined {
  if (typeof message.transactionGeneration !== "number"
    || !Number.isFinite(message.transactionGeneration)
    || message.transactionGeneration <= 0) {
    return undefined;
  }

  return message.transactionGeneration;
}

function applyModeSettleProbePreferences(message: Extract<HostMessage, { type: "mode-settle-probe" }>): void {
  if (
    typeof message.fontSize !== "number" ||
    typeof message.lineHeight !== "number" ||
    typeof message.maxWidth !== "number" ||
    message.minimapMode === undefined
  ) {
    return;
  }

  const preferences: Extract<HostMessage, { type: "reading-preferences" }> = {
    type: "reading-preferences",
    fontSize: message.fontSize,
    lineHeight: message.lineHeight,
    maxWidth: message.maxWidth,
    minimapMode: message.minimapMode,
  };

  if (message.minMaxWidth !== undefined) {
    preferences.minMaxWidth = message.minMaxWidth;
  }
  if (message.fontFamily !== undefined) {
    preferences.fontFamily = message.fontFamily;
  }
  if (message.viewerChromeEnabled !== undefined) {
    preferences.viewerChromeEnabled = message.viewerChromeEnabled;
  }
  if (message.documentScrollEnabled !== undefined) {
    preferences.documentScrollEnabled = message.documentScrollEnabled;
  }
  if (message.wheelProxyEnabled !== undefined) {
    preferences.wheelProxyEnabled = message.wheelProxyEnabled;
  }
  if (message.widthResizerVisibility !== undefined) {
    preferences.widthResizerVisibility = message.widthResizerVisibility;
  }
  if (message.skipFrameWait !== undefined) {
    preferences.skipFrameWait = message.skipFrameWait;
  }

  applyReadingPreferences(preferences);
}

function resetModuleGlobalsForLoadDocument(): void {
  ++initialRenderPipelineGeneration;
  initialRenderPipelineCompleted = false;
  postReadyEnhancementsCompleted = false;
  currentController?.cancel();
  currentController = null;
  restoredCachedLayoutState = null;
  restoredCachedHeadings = null;
  restoredCachedMinimapSnapshot = null;
  ++layoutReadyGeneration;
  if (layoutReadyTimer !== undefined) {
    window.clearTimeout(layoutReadyTimer);
    layoutReadyTimer = undefined;
  }
  if (minimapContentRefreshTimer !== undefined) {
    window.clearTimeout(minimapContentRefreshTimer);
    minimapContentRefreshTimer = undefined;
  }
  if (cachedGeometryRefreshTimer !== undefined) {
    window.clearTimeout(cachedGeometryRefreshTimer);
    cachedGeometryRefreshTimer = undefined;
  }
  if (mermaidCacheResumeTimer !== undefined) {
    window.clearTimeout(mermaidCacheResumeTimer);
    mermaidCacheResumeTimer = undefined;
  }
  postLayoutReadyWorkQueue = [];
  if (themeMermaidRefreshTimer !== undefined) {
    window.clearTimeout(themeMermaidRefreshTimer);
    themeMermaidRefreshTimer = undefined;
  }
  ++themeMermaidRefreshGeneration;
  disconnectMermaidLazyObserver();
  mermaidLazyRenderQueue = Promise.resolve();
  // INCREMENT, not reset-to-0 — invalidates in-flight mermaid render callbacks
  // that compare against the old generation token. Resetting to 0 would let a
  // stale callback whose generation === 0 pass the check, painting an
  // old-document diagram onto the new document. (Codex review 2026-05-15.)
  ++mermaidRenderGeneration;
  minimapDocumentHeight = 0;
  lastPostedMinimapState = { hasPosted: false, visible: false, reservedWidth: 0 };
  minimapSourceReady = false;
  // Polish #5 — reset the width-handle reveal gate so the next document's
  // initialVisibleReady has to fire again before the handle becomes visible
  // at its (now-correct) post-settle x. Without this reset, every doc after
  // the first would skip the gate (it stays true from the prior doc) and the
  // pre-layout updateWidthHandlePosition call in ensureChromeNodes would
  // briefly show the handle at the wrong x — the same jitter the gate was
  // added to prevent, just on the second-and-subsequent doc loads.
  hasInitialLayoutSettled = false;
  // Find bar — close on doc swap. The bar lives as a body sibling so it
  // survives the <main> innerHTML write, but its match-state references
  // detached nodes after the swap. Close clears state and removes
  // the open class; the controller node stays for fast reopen.
  findBarController?.close();
  // TOC active-heading observer holds references to heading nodes that
  // are about to be replaced by the new document's innerHTML. Disconnect
  // here; ensureChromeNodes's extractAndPostHeadings call rebuilds the
  // observer against the new heading set right after the swap.
  if (activeHeadingObserver) {
    activeHeadingObserver.disconnect();
    activeHeadingObserver = null;
  }
  lastPostedActiveHeadingId = null;
  sourceLineAnchors = [];
  previewSourceLineFrameRequested = false;
  suppressPreviewSourceLineEmit = false;
  lastPostedPreviewSourceLine = null;
}

function ensureChromeNodes(useCachedDocumentState = false): void {
  ensureMinimap();
  ensureWidthHandle();
  ensureDropOverlay();
  // Width-handle X depends on the new .mm-document bounding rect after innerHTML
  // swap; ensureWidthHandle only ensures the node exists.
  updateWidthHandlePositionForCurrentLayout();
  // Cold path still needs a synchronous Phase A seed because cancelled async
  // pipelines may never reach initialVisibleReady. Cache hits carry the already
  // rendered minimap DOM, so restore it here and defer geometry reconciliation
  // out of the cache-hit layout-ready path.
  if (!useCachedDocumentState || !restoreCachedMinimapContent()) {
    refreshMinimapContent("A");
  }
  // v0.3.2 — TOC migrated from renderer-side panel (deleted 4aee666) to
  // Avalonia. Scan headings after each chrome rebuild and push the list to
  // the host so the host-side Table of Contents panel populates. Stable
  // anchor ids from MarkdownHeadingAnchorSlugger drive scroll-to-heading
  // round-trips back from the host.
  if (useCachedDocumentState) {
    postCachedHeadings();
  } else {
    extractAndPostHeadings();
  }
}

function buildLoadDocumentDeps(): import("./loadDocument").LoadDocumentDeps {
  return {
    // PE r2 item G — accept the per-document `hasMermaid` so the pipeline
    // skips mermaid init+render for docs without mermaid blocks. Undefined
    // passes through to the pipeline's `!== false` default, preserving the
    // pre-G behavior for any caller that doesn't carry the flag.
    runInitialRenderPipeline: async (hasMermaid, skipFrameWait, renderId, hasHljs) => {
      const pipelineGeneration = ++initialRenderPipelineGeneration;
      await runInitialRenderPipeline({
        getCurrentTheme,
        applyTheme,
        initMermaidWithTheme,
        renderMath,
        renderMermaid,
        renderCodeBlocks,
        deferPostReadyWork: deferPostReadyEnhancements,
        scheduleLayoutReady: () => {
          initialRenderPipelineCompleted = true;
          scheduleLayoutReady(skipFrameWait === true);
          // Re-emit document-ready so the host's _hasLoadedDocument state
          // machine restarts for the new document.
          postHostMessage({
            type: "document-ready",
            mathCount: document.querySelectorAll("[data-tex]").length
          });
        },
        hasMermaid,
        postPerfMark,
        notifyPostReadyEnhancementsComplete: () => {
          postPostReadyEnhancementsComplete(renderId, hasMermaid, hasHljs);
        },
        isCurrent: () => pipelineGeneration === initialRenderPipelineGeneration,
      });
    },
    cancelCurrentMathController: () => { currentController?.cancel(); },
    resetModuleGlobals: resetModuleGlobalsForLoadDocument,
    scrollWindowToTop: () => { window.scrollTo({ left: 0, top: 0, behavior: "instant" as ScrollBehavior }); },
    // Mirror selected renderer-side perf marks into the host's
    // [renderer-perf] stream. Only `mm-load-document` is bridged from this
    // path per round-2 plan item C; other marks are bridged at their own
    // emission sites in renderer.ts so the bridging is colocated with the
    // semantic anchor rather than centralized here.
    emitMark: (name, detail) => {
      emitMark(name, detail);
      if (name === "mm-load-document" || name === "mm-load-document-cache-hit") {
        postPerfMark(name, (detail as Record<string, unknown> | undefined) ?? undefined);
      }
    },
    ensureChromeNodes,
    applyTheme,
    debugLog: postDebugLog,
    preserveCurrentDocumentCache: preserveCurrentProcessedDocument,
    getCachedDocumentFragment: getCachedProcessedDocumentFragment,
    setCurrentDocumentCacheKey: setCurrentProcessedDocumentCacheKey,
    restoreCachedScrollPosition,
    completeCachedDocumentLoad: (renderId, hasMermaid, hasHljs) => {
      initialRenderPipelineCompleted = true;
      hasInitialLayoutSettled = true;
      postReadyEnhancementsCompleted = true;
      postHostMessage({
        type: "document-ready",
        mathCount: document.querySelectorAll("[data-tex]").length
      });
      postCachedLayoutReady();
      postPostReadyEnhancementsComplete(renderId, hasMermaid, hasHljs);
      scheduleCachedMermaidResume(hasMermaid);
    },
  };
}

function wireLinks(): void {
  document.addEventListener("click", (event) => {
    const target = event.target instanceof Element
      ? event.target.closest<HTMLAnchorElement>("a[href]")
      : null;
    if (!target) {
      return;
    }

    event.preventDefault();
    postHostMessage({
      // `target.href` is the absolute URI the browser resolved against the
      // generated HTML's base URL (which lives in the OS temp folder). For
      // relative markdown links like `[doc](other.md)` this hides the
      // actual relative path inside a temp-folder file URI — useless to
      // the host because the host needs to resolve against the ORIGINAL
      // markdown source directory. Send the raw attribute value too so
      // the host can pick the right one for resolution.
      type: "link-clicked",
      href: target.dataset.mmHref ?? target.getAttribute("href") ?? target.href,
      button: event.button,
      ctrlKey: event.ctrlKey,
      shiftKey: event.shiftKey,
      altKey: event.altKey,
      metaKey: event.metaKey
    });
  });
}

function wireViewerInteraction(): void {
  document.addEventListener("pointerdown", (event) => {
    if (event.button === 0) {
      postHostMessage({ type: "viewer-interaction" });
    }
  }, true);
}

function wireWheelProxy(): void {
  document.addEventListener("wheel", (event) => {
    // Orthogonal to document scroll: wheel proxying is its own routing
    // decision. Some surfaces (viewer mode) need to forward wheel to the
    // host even when the document itself owns scroll — the host translates
    // the wheel into a scroll-by IPC back into the document. Surfaces that
    // rely on Chromium's default wheel handling leave the proxy disabled.
    if (!wheelProxyEnabled) {
      return;
    }

    if (Math.abs(event.deltaY) <= Number.EPSILON || Math.abs(event.deltaX) > Math.abs(event.deltaY)) {
      return;
    }

    postHostMessage({
      type: "wheel",
      deltaY: event.deltaY,
      deltaMode: event.deltaMode
    });
    event.preventDefault();
  }, { capture: true, passive: false });
}

// File drop on viewer body — bridges Windows OLE drop (which is captured
// by the WebView2 HWND and never reaches the Avalonia parent) to the host
// via JS. Visual: in-page overlay matching the upstream IsDragHovering
// Border. Behaviour: read dropped .md/.markdown file content, post it to
// the host, which writes a temp file and routes through the existing
// MainWindowViewModel.OpenDroppedFileAsync pipeline.
const MARKDOWN_EXTENSIONS = [".md", ".markdown", ".mdown", ".markdn"] as const;
const DROP_OVERLAY_ID = "mm-drop-overlay";
const DROP_OVERLAY_TEXT = "Drop your Markdown file to open";
let dropDragCounter = 0;

function isFileDrag(event: DragEvent): boolean {
  const types = event.dataTransfer?.types;
  if (!types) return false;
  for (let i = 0; i < types.length; i++) {
    if (types[i] === "Files") return true;
  }
  return false;
}

function isMarkdownFileName(name: string): boolean {
  const lower = name.toLowerCase();
  return MARKDOWN_EXTENSIONS.some(ext => lower.endsWith(ext));
}

function ensureDropOverlay(): HTMLElement {
  const existing = document.getElementById(DROP_OVERLAY_ID);
  if (existing) return existing;
  const node = document.createElement("div");
  node.id = DROP_OVERLAY_ID;
  node.className = "mm-drop-overlay";
  node.textContent = DROP_OVERLAY_TEXT;
  // Append to documentElement so the overlay survives even when body is
  // briefly empty during a re-render.
  (document.body ?? document.documentElement).appendChild(node);
  return node;
}

function setDropOverlayVisible(visible: boolean): void {
  const node = ensureDropOverlay();
  if (visible) {
    node.setAttribute("data-visible", "true");
  } else {
    node.removeAttribute("data-visible");
  }
}

// Forward window-level KeyBindings (declared in MainWindow.axaml) from inside
// the WebView2 native HWND to the Avalonia host. When the user clicks inside
// the rendered document, focus moves to the WebView2 child window and the OS
// delivers WM_KEYDOWN directly to it — bypassing Avalonia's keyboard routing,
// so window-level KeyBindings stop firing until focus returns to the
// host-side tab strip or title bar. This handler captures the accelerator
// combos the host cares about, posts them to the host, and preventDefault's
// the in-WebView behavior so the user can use shortcuts without first
// clicking back into the title bar.
let resetHostShortcutsForModeSwitch: (() => void) | undefined;

function wireHostShortcuts(): void {
  let editModeShortcutDown = false;
  let editModeShortcutResetTimer: number | undefined;
  const resetEditModeShortcut = () => {
    editModeShortcutDown = false;
    window.clearTimeout(editModeShortcutResetTimer);
  };
  resetHostShortcutsForModeSwitch = resetEditModeShortcut;
  const keepEditModeShortcutHeld = () => {
    editModeShortcutDown = true;
    window.clearTimeout(editModeShortcutResetTimer);
    editModeShortcutResetTimer = window.setTimeout(resetEditModeShortcut, 1000);
  };
  const hostShortcuts = new Set<string>([
    "ctrl+1",
    "ctrl+2",
    "ctrl+3",
    "ctrl+4",
    "ctrl+5",
    "ctrl+6",
    "ctrl+7",
    "ctrl+8",
    "ctrl+9",
    "ctrl+e",
    "ctrl+o",
    "ctrl+s",
    "ctrl+shift+s",
    "ctrl+n",
    "ctrl+r",
    "ctrl+t",
    "f5",
    "escape"
  ]);

  window.addEventListener(
    "keydown",
    (event) => {
      const key = event.key.toLowerCase();
      const combo =
        (event.ctrlKey || event.metaKey ? "ctrl+" : "") +
        (event.shiftKey ? "shift+" : "") +
        (event.altKey ? "alt+" : "") +
        key;
      if (!hostShortcuts.has(combo)) {
        return;
      }
      event.preventDefault();
      event.stopPropagation();
      if (combo === "ctrl+e") {
        if (editModeShortcutDown || event.repeat) {
          keepEditModeShortcutHeld();
          return;
        }

        keepEditModeShortcutHeld();
      }
      postHostMessage({ type: "host-shortcut", combo });
    },
    { capture: true }
  );
  window.addEventListener(
    "keyup",
    (event) => {
      const key = event.key.toLowerCase();
      if (key === "e" || (!event.ctrlKey && !event.metaKey)) {
        resetEditModeShortcut();
      }
    },
    { capture: true }
  );
  window.addEventListener("blur", resetEditModeShortcut);
}

function wireFileDrop(): void {
  document.addEventListener("dragenter", (event) => {
    if (!isFileDrag(event)) return;
    event.preventDefault();
    dropDragCounter++;
    if (dropDragCounter === 1) {
      setDropOverlayVisible(true);
      postHostMessage({ type: "drag-hover", hovering: true });
    }
  });

  document.addEventListener("dragover", (event) => {
    if (!isFileDrag(event)) return;
    event.preventDefault();
    if (event.dataTransfer) {
      event.dataTransfer.dropEffect = "copy";
    }
  });

  document.addEventListener("dragleave", (event) => {
    if (!isFileDrag(event)) return;
    dropDragCounter--;
    if (dropDragCounter <= 0) {
      dropDragCounter = 0;
      setDropOverlayVisible(false);
      postHostMessage({ type: "drag-hover", hovering: false });
    }
  });

  document.addEventListener("drop", async (event) => {
    if (!isFileDrag(event)) return;
    event.preventDefault();
    dropDragCounter = 0;
    setDropOverlayVisible(false);
    postHostMessage({ type: "drag-hover", hovering: false });

    const files = event.dataTransfer?.files;
    if (!files || files.length === 0) return;
    for (let i = 0; i < files.length; i++) {
      const file = files.item(i);
      if (file && isMarkdownFileName(file.name)) {
        try {
          const text = await file.text();
          postHostMessage({ type: "drop-file", name: file.name, text });
        } catch {
          // Ignore unreadable file; user can retry.
        }
        return;
      }
    }
  });
}

// Ctrl+F → in-document find bar. Renderer-side MVP: pure DOM, no host
// involvement. Limitation: the keystroke only fires when WebView2 has
// keyboard focus; if focus is on the Avalonia chrome (title bar, tab
// strip, etc.) the OS routes WM_KEYDOWN to Avalonia and this handler
// never sees it. Host-level Ctrl+F forwarding is a v0.3.1 backlog
// item — once Avalonia.Controls.WebView exposes the managed
// CoreWebView2 surface we can either bridge host KeyBindings into the
// WebView (same approach as `wireHostShortcuts` does for ctrl+e etc.)
// or call CoreWebView2.FindController natively.
//
// We register on `window` with capture so that Esc-while-find-bar-open
// is consumed before reaching the host-shortcuts forwarder (which
// would otherwise post "escape" to the host and trigger the global
// escape action). Ctrl+F is not in the host-shortcuts set, so the
// forwarder ignores it regardless.
function wireFindBar(): void {
  findBarController = createFindBar();
  window.addEventListener(
    "keydown",
    (event) => {
      // Toggle on Ctrl+F / Cmd+F. Treat any modifier-combo as
      // distinct from the bare key so e.g. Ctrl+Shift+F (potential
      // future "find in folder") does not steal the same accelerator.
      const isFindCombo =
        (event.ctrlKey || event.metaKey) &&
        !event.shiftKey &&
        !event.altKey &&
        event.key.toLowerCase() === "f";
      if (isFindCombo) {
        event.preventDefault();
        event.stopPropagation();
        findBarController?.toggle();
        return;
      }
      // Escape while find bar is open → close, and prevent the host
      // from also acting on Escape. When the bar is closed, let
      // Escape bubble normally (host may map it to "exit edit mode"
      // or similar).
      if (event.key === "Escape" && findBarController?.isOpen === true) {
        event.preventDefault();
        event.stopPropagation();
        findBarController.close();
      }
    },
    { capture: true }
  );
}

// Right-click → "Save Page As" snapshots the live DOM and writes it to
// disk. `@media print` does not apply, so the minimap and width handle
// (built by JS as direct children of <body>) leak into the saved HTML.
// We do NOT want to hide chrome on every context menu open — the user
// often invokes other items (Copy, Inspect Image, etc.) and the visual
// blink is unwelcome. Pattern that almost always means a save/inspect
// dialog opened: a `contextmenu` was just raised AND the window then
// loses focus. Hide chrome at that moment so Edge's DOM snapshot at
// save-confirm time does not include it; restore on focus return.
// No timers — the contextMenuPending flag is reset on focus along
// with the class, so false positives self-correct as soon as the user
// returns to the window.
let contextMenuPending = false;
function wireSaveAsPageChromeSuppress(): void {
  document.addEventListener("contextmenu", () => {
    contextMenuPending = true;
  });
  window.addEventListener("blur", () => {
    if (contextMenuPending) {
      document.body.classList.add("mm-saving");
    }
  });
  window.addEventListener("focus", () => {
    contextMenuPending = false;
    document.body.classList.remove("mm-saving");
  });
}

document.addEventListener("securitypolicyviolation", (e) => {
  postHostMessage({
    type: "csp-violation",
    blockedURI: (e.blockedURI ?? "").substring(0, 200),
    violatedDirective: (e.violatedDirective ?? "").substring(0, 200),
    sourceFile: (e.sourceFile ?? "").substring(0, 200),
    lineNumber: e.lineNumber ?? 0,
    columnNumber: e.columnNumber ?? 0
  });
});

document.addEventListener("DOMContentLoaded", () => {
  emitMark("mm-doc-loaded");
  postPerfMark("mm-doc-loaded");
  requestAnimationFrame(() => {
    emitMark("mm-doc-painted");
    postPerfMark("mm-doc-painted");
  });
  installLongTaskObserver();
  applyViewerChromeState();
  applyDocumentScrollState();
  // Defer renderMath / renderMermaid / renderCodeBlocks to runInitialRenderPipeline,
  // which is triggered by the first reading-preferences message from the host.
  wireLinks();
  wireViewerInteraction();
  wireWheelProxy();
  wireFileDrop();
  // Find bar BEFORE host shortcuts so capture-phase Esc-while-bar-open
  // is consumed by the find bar's handler before reaching the
  // host-shortcuts forwarder.
  wireFindBar();
  wireHostShortcuts();
  wireSaveAsPageChromeSuppress();
  postHostMessage({
    type: "document-ready",
    mathCount: document.querySelectorAll("[data-tex]").length
  });
  postScroll();

  // Background observer for handle + minimap repositioning. Observes:
  //   .mm-document — catches max-width / padding changes (host reading-prefs,
  //     theme/font changes that affect column metrics)
  //   document.body — catches body content-area changes (html scrollbar
  //     appearing/disappearing as content-visibility blocks settle their real
  //     heights → shifts .mm-document's centered position without changing
  //     its own size, invisible to the .mm-document observer)
  // Gated during drag because scheduleWidthDragApply owns the live preview
  // without synchronous .mm-document geometry reads; canonical handle position
  // and minimap viewport are reconciled once the drag settles. Minimap
  // visibility toggles call updateWidthHandlePosition directly (see
  // updateMinimapVisibility) so they don't depend on this observer landing in
  // the same frame as the body class change.
  const documentElement = document.querySelector<HTMLElement>(".mm-document");
  if (documentElement) {
    const resizeObserver = new ResizeObserver(() => {
      if (widthHandleDragging) {
        return;
      }
      // 100ms-debounced rebuild path stays as-is — it's already coarse.
      queueMinimapRefreshAfterLayoutSettles();
      // Synchronous-layout reads (updateWidthHandlePosition reads
      // getBoundingClientRect) plus the viewport refresh are coalesced into
      // a single rAF via scheduleResizeReactions, so a fast window-edge drag
      // does not snap the chrome on every observer tick.
      scheduleResizeReactions();
      window.requestAnimationFrame(postScroll);
    });
    resizeObserver.observe(documentElement);
    resizeObserver.observe(document.body);
  }

  document.fonts?.ready.then(() => {
    queueMinimapRefreshAfterLayoutSettles();
  }).catch(() => undefined);
});

const queuePostScroll = createScrollCoalescer({
  postScroll: () => {
    postScroll();
    queueMinimapViewportUpdate();
  },
  schedule: (cb) => { window.requestAnimationFrame(cb); },
});

document.addEventListener("scroll", () => {
  queuePostScroll();
  queuePreviewSourceLinePost();
}, { passive: true });

hostWindow.chrome?.webview?.addEventListener?.("message", (event) => handleHostMessage(event.data));
window.addEventListener("message", (event) => handleHostMessage(event.data));
// Resize-time reactive work (chrome reposition + minimap viewport refresh)
// is coalesced to at most one rAF per frame via scheduleResizeReactions; see
// its declaration for the rationale. CSS reflow on the document itself stays
// browser-native and is not affected by this debounce.
window.addEventListener("resize", () => {
  scheduleResizeReactions();
});

(window as unknown as { __mmPerfReport: typeof getReport; __mmFpsSampler: ReturnType<typeof getFpsSampler> }).__mmPerfReport = getReport;
(window as unknown as { __mmPerfReport: typeof getReport; __mmFpsSampler: ReturnType<typeof getFpsSampler> }).__mmFpsSampler = getFpsSampler();
(window as unknown as { __mmRendererState: { initialVisibleReady: Promise<void>; allMathRendered: Promise<void> } }).__mmRendererState = {
  get initialVisibleReady() { return currentController?.initialVisibleReady ?? Promise.resolve(); },
  get allMathRendered() { return currentController?.allMathRendered ?? Promise.resolve(); },
};

// Test-only seam — lets vitest exercise load-document/clear-document against
// the real renderer module-globals via the same dispatcher the WebView uses.
(window as unknown as { __mmRendererLoad: (msg: unknown) => void }).__mmRendererLoad =
  (msg) => handleHostMessage(msg);
