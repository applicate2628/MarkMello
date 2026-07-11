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
import {
  isMermaidNodeNearViewport,
  reclaimClonedMermaidProxyLifecycles,
  renderMermaidNode,
  type MermaidApiLike,
} from "./mermaidRender";
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
  createVirtualizedFindProvider,
  type FindQueryMessage,
  type FindResultsMessage,
  type VirtualizedFindProvider,
} from "./virtualizedFindProvider";
import {
  findScrollTopForSourceLine,
  findSourceLineAtDocumentY,
  findSourceLineAtDocumentYWithFallback,
  readSourceLineAnchors,
  type SourceLineAnchor
} from "./sourceLineSync";
import {
  captureMinimapSnapshot,
  restoreMinimapSnapshot,
  type CachedMinimapSnapshot
} from "./minimapCache";
import {
  collectLiveDocumentBlockElements,
  findTopVisibleBlockIndexFromBlocks
} from "./topVisibleBlockIndex";
import {
  buildDocumentWindowModelsFromLiveBlocks,
  collectLiveDocumentSectionElements,
  readLiveBlockOffsetMeasuredHeights,
  type DocumentWindowModel,
  type MeasuredHeightUpdateResult,
} from "./documentWindow";
import {
  prepareDocumentWindowModelRenderedContent,
  type ModelRenderedContentEvent,
  type ModelRenderedContentPreparationStatus,
  type PrepareDocumentWindowModelRenderedContentDeps,
} from "./modelRenderedContent";
import {
  createSectionIntrinsicCalibrator,
  readIntrinsicSizeMetrics,
} from "./sectionIntrinsicSize";
import { readVirtualizationFlag } from "./virtualizationFlags";
import {
  captureReadingAnchor,
  createFullDocumentFragmentFromWindowModel,
  createVirtualizedDocumentWindowController,
  scrollTopForReadingAnchor,
  type ReadingAnchor,
  type VirtualizedDocumentWindowController,
  type VirtualizedWindowOperation,
} from "./virtualizedDocumentWindow";
import {
  createVirtualizationShadowValidator,
  readVirtualizationShadowFlag,
  type VirtualizationShadowValidator
} from "./virtualizationShadow";
import {
  renderWindowTargetThenAct,
  resolveWindowTarget,
  readWindowTargetContext,
  type WindowTargetContext,
  type WindowTargetDescriptor,
  type WindowTargetOperation,
} from "./windowTargetResolver";
import {
  createScrollOwnershipControlPlane,
  GEOMETRY_SETTLED_EVENT,
  type GeometrySettledWaitOutcome,
  type GeometryWorkTicket,
  type ScrollAcquirePolicy,
  type ScrollLease,
  type ScrollOwnershipControlPlane,
  type ScrollWriteReceipt,
} from "./scrollOwnershipControlPlane";

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
  | { type: "layout-ready"; scrollTop: number; scrollHeight: number; clientHeight: number; cached?: boolean; renderId?: number | null }
  | { type: "post-ready-enhancements-complete"; renderId?: number; hasMermaid: boolean; hasHljs: boolean }
  | { type: "theme-applied"; theme: RendererTheme; requestId: number }
  | { type: "link-clicked"; href: string; button: number; ctrlKey: boolean; shiftKey: boolean; altKey: boolean; metaKey: boolean }
  | { type: "task-toggle"; line: number; checked: boolean; key: string | null }
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
  | { type: "headings-updated"; headings: ReadonlyArray<HeadingPayload> }
  | { type: "active-heading-changed"; id: string }
  | { type: "preview-source-line"; sourceLine: number }
  | { type: "csp-violation"; blockedURI: string; violatedDirective: string; sourceFile: string; lineNumber: number; columnNumber: number }
  | { type: "document-cache-miss"; renderId?: number; cacheKey?: string }
  | FindQueryMessage
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
  | { type: "theme"; theme: RendererTheme; requestId?: number }
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
  | { type: "load-document"; html: string; documentName?: string; theme?: RendererTheme; hasMermaid?: boolean; hasHljs?: boolean; renderId?: number; skipFrameWait?: boolean; cacheKey?: string | null }
  | { type: "append-document"; html: string; hasMermaid?: boolean; hasHljs?: boolean; renderId?: number; isFinal?: boolean; cacheKey?: string | null }
  | { type: "load-cached-document"; cacheKey: string; documentName?: string; theme?: RendererTheme; hasMermaid?: boolean; hasHljs?: boolean; renderId?: number; skipFrameWait?: boolean }
  | { type: "clear-document" }
  | { type: "invalidate-document-cache-key" }
  | { type: "set-task-checkbox"; line: number; checked: boolean }
  | { type: "scroll-to-heading"; id: string }
  | { type: "scroll-to-source-line"; sourceLine: number }
  | FindResultsMessage
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
const VIRTUALIZED_NAVIGATION_SETTLE_TOLERANCE_PX = 0.5;
const VIRTUALIZED_NAVIGATION_SETTLE_STABLE_FRAMES = 2;
const VIRTUALIZED_NAVIGATION_SETTLE_MAX_FRAMES = 120;
const VIRTUALIZED_NAVIGATION_CORRECTION_TOLERANCE_PX = 2;
const VIRTUALIZED_NAVIGATION_CORRECTION_MAX_PASSES = 3;
const VIRTUALIZED_NAVIGATION_CORRECTION_MIN_SHRINK_PX = 0.5;

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
let minimapDeferredContentRefreshHandle: { kind: "idle" | "timeout"; id: number } | null = null;
let progressiveDeferredEnhancementHandle: { kind: "idle" | "timeout"; id: number } | null = null;
let progressiveMinimapRefreshGeneration = 0;
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
let modeToggleSettleSequence = 0;
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
// Minimap-local offset between the grab point and the viewport-indicator top at
// pointer-down, so the block-anchor drag keeps the grabbed point under the cursor.
let minimapDragGrabOffset = 0;
const MINIMAP_DRAG_THRESHOLD_PX = 4;
let minimapSourceReady = false;
let mermaidRenderGeneration = 0;
let mermaidLazyObserver: IntersectionObserver | null = null;
let mermaidLazyRenderQueue: Promise<void> = Promise.resolve();
let themeMermaidRefreshGeneration = 0;
let themeMermaidRefreshTimer: number | undefined;
let themeAppliedAckGeneration = 0;
let initialRenderPipelineGeneration = 0;
let initialRenderPipelineCompleted = false;
let firstPrefsBootstrapSuppressedByLoadGeneration: number | null = null;
let postReadyEnhancementsCompleted = false;
let currentController: MathReadinessController | null = null;
const MERMAID_PER_DIAGRAM_TIMEOUT_MS = 3000;
const MERMAID_WATCHDOG_MS = 15_000;
const MERMAID_EAGER_VIEWPORT_MARGIN_PX = 700;
const MERMAID_LAZY_ROOT_MARGIN_PX = 1400;
const THEME_MERMAID_REFRESH_DELAY_MS = 160;
const THEME_APPLIED_ACK_FALLBACK_MS = 120;
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
// Last PROGRAMMATIC line target (host scroll-to-source-line). Re-asserted after
// every layout-invalidating pass (math/fonts/resize/final append) so the target
// line stays at the anchor as geometry inflates; cleared on real user scroll.
let pendingSourceLineTarget: number | null = null;
let previewSourceLineFrameRequested = false;
let suppressPreviewSourceLineEmit = false;
let suppressPreviewSourceLineSequence = 0;
let lastPostedPreviewSourceLine: number | null = null;
let liveDocumentBlockElements: HTMLElement[] = [];
let liveDocumentBlockElementsStale = true;
const virtualizationEnabled = readVirtualizationFlag(window, document);
const scrollOwnershipControlPlane: ScrollOwnershipControlPlane | null = virtualizationEnabled
  ? createScrollOwnershipControlPlane({
    cancelFrame: handle => window.cancelAnimationFrame(handle),
    emitGeometrySettled: payload => {
      document.dispatchEvent(new CustomEvent(GEOMETRY_SETTLED_EVENT, { detail: payload }));
    },
    prepareGeometrySettleCandidate: () =>
      virtualizedDocumentWindowController?.recensusRealizationWatches() ?? true,
    requestFrame: callback => window.requestAnimationFrame(callback),
    root: getDocumentScrollRoot(),
    trace: event => postPerfMark(event.id, {
      ...event.details,
      documentEpoch: event.documentEpoch,
      frame: event.frame,
      geometryEpoch: event.geometryEpoch,
      operationEpoch: event.operationEpoch,
    }),
  })
  : null;
if (scrollOwnershipControlPlane !== null) {
  const virtualizationRoot = getDocumentScrollRoot();
  if (virtualizationRoot instanceof HTMLElement) {
    virtualizationRoot.dataset.mmVirtualizationActive = "true";
  } else {
    virtualizationRoot.setAttribute("data-mm-virtualization-active", "true");
  }
  window.addEventListener("pagehide", () => {
    finishMinimapScrollOperation();
    cancelPendingVirtualizedMaintenance("teardown");
    finishCachedScrollRestore?.("canceled", "teardown");
    cancelProcessedDocumentCacheClone();
    cancelProgressiveDeferredEnhancements();
    cancelDeferredMinimapContentRefresh(false);
    cancelMinimapRefreshAfterLayoutSettles();
    cancelHeavyLiveUpdate();
    if (minimapContentRefreshTimer !== undefined) {
      window.clearTimeout(minimapContentRefreshTimer);
      minimapContentRefreshTimer = undefined;
    }
    cancelModelRenderedContentCoordinator("teardown");
    resetVirtualizedDocumentWindow(false);
    scrollOwnershipControlPlane.dispose();
    getDocumentScrollRoot().removeAttribute("data-mm-virtualization-active");
  }, { once: true });
}
const virtualizationShadowEnabled = readVirtualizationShadowFlag(window, document);
let virtualizationShadowValidator: VirtualizationShadowValidator | null = null;
let virtualizationShadowDocumentFinal = true;
let virtualizedDocumentWindowController: VirtualizedDocumentWindowController | null = null;
let virtualizedDocumentWindowModel: DocumentWindowModel | null = null;
let virtualizedFindProvider: VirtualizedFindProvider | null = null;
const virtualizedIntrinsicCalibrator = createSectionIntrinsicCalibrator();
let virtualizedMeasureFrameRequested = false;
let virtualizedMeasuredHeightGeometryTicket: GeometryWorkTicket | null = null;
const virtualizedMeasuredHeightTerminalSubscribers = new Set<() => void>();
let virtualizedCalibrationHandle: { kind: "idle" | "timeout"; id: number } | null = null;
let virtualizedCalibrationGeometryTicket: GeometryWorkTicket | null = null;
let virtualizedWindowMountGeneration = 0;
let virtualizedWindowFontGeometryTicket: GeometryWorkTicket | null = null;
let virtualizedProgrammaticNavigationInProgress = false;
let virtualizedProgrammaticNavigationGeneration = 0;
let virtualizedProgrammaticNavigationExternalShiftCount = 0;
let virtualizedProgrammaticNavigationPostSettleTarget: {
  descriptor: WindowTargetDescriptor;
  viewportOffsetY: number;
} | null = null;
let virtualizedProgrammaticNavigationOperation: VirtualizedScrollOperation | null = null;
let minimapScrollOperation: VirtualizedScrollOperation | null = null;
const virtualizedWriteReceipts = new Map<number, ScrollWriteReceipt>();
let cachedScrollRestoreCompletion: Promise<void> | null = null;
let finishCachedScrollRestore: ((status: "canceled" | "committed" | "failed", reason: string) => void) | null = null;
let virtualizedWindowMathController: MathReadinessController | null = null;
type ModelRenderedContentConsumerId = "minimap-detail" | "rendered-find-projection";
type ModelRenderedContentLease = {
  consumer: ModelRenderedContentConsumerId;
  documentEpoch: number;
  model: DocumentWindowModel;
  readiness: Promise<ModelRenderedContentPreparationStatus>;
  release: () => void;
};
type ModelRenderedContentCoordinatorState = {
  cancelMarkPosted: boolean;
  cancelReason: string | null;
  cancelled: boolean;
  documentEpoch: number;
  leases: Map<number, ModelRenderedContentConsumerId>;
  model: DocumentWindowModel;
  promise: Promise<ModelRenderedContentPreparationStatus> | null;
  runSerial: number;
};
let modelRenderedContentCoordinatorState: ModelRenderedContentCoordinatorState | null = null;
let modelRenderedContentLeaseSerial = 0;
let minimapRenderedContentLease: ModelRenderedContentLease | null = null;
let renderedFindContentLease: ModelRenderedContentLease | null = null;
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
  blockIndex?: number;
  sectionIndex?: number;
  text: string;
  segments: HeadingSegmentPayload[];
};

type HeadingSegmentPayload = {
  kind: "text" | "math";
  text: string;
};

function cloneHeadingPayload(heading: HeadingPayload): HeadingPayload {
  return {
    ...heading,
    segments: heading.segments.map(segment => ({ ...segment })),
  };
}

type CachedLayoutState = {
  scrollTop: number;
  scrollHeight: number;
  clientHeight: number;
  topBlockIndex: number | null;
  readingAnchor?: ReadingAnchor | null;
  settledGeometryEpoch?: number;
};

const processedDocumentCache = new Map<string, ProcessedDocumentCacheEntry>();
let currentDocumentCacheKey: string | null = null;
let currentDocumentRenderId: number | null = null;
let restoredCachedLayoutState: CachedLayoutState | null = null;
let restoredCachedHeadings: HeadingPayload[] | null = null;
let restoredCachedMinimapSnapshot: CachedMinimapSnapshot | null = null;
let processedDocumentCacheCloneGeneration = 0;
let processedDocumentCacheCloneHandle: { kind: "idle" | "timeout"; id: number } | null = null;
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
  processedDocumentCache.set(cacheKey, cached);
  restoredCachedLayoutState = { ...cached.layoutState };
  restoredCachedHeadings = cached.headings.map(cloneHeadingPayload);
  restoredCachedMinimapSnapshot = cached.minimapSnapshot;
  return cached.fragment.cloneNode(true) as DocumentFragment;
}

function setCurrentProcessedDocumentCacheKey(cacheKey: string | null): void {
  currentDocumentCacheKey = cacheKey;
}

function cancelProcessedDocumentCacheClone(): void {
  if (!processedDocumentCacheCloneHandle) {
    return;
  }

  const handle = processedDocumentCacheCloneHandle;
  processedDocumentCacheCloneHandle = null;
  if (handle.kind === "idle") {
    (window as Window & { cancelIdleCallback?: (id: number) => void }).cancelIdleCallback?.(handle.id);
  } else {
    window.clearTimeout(handle.id);
  }
}

function captureCurrentProcessedDocumentCacheEntry(mode: "clone" | "move"): ProcessedDocumentCacheEntry | null {
  const main = document.querySelector<HTMLElement>("main.mm-document");
  if (!main || main.childNodes.length === 0) {
    return null;
  }

  const virtualizedLayoutState = virtualizationEnabled
    ? {
      readingAnchor: captureCurrentVirtualizedReadingAnchor(),
      settledGeometryEpoch: scrollOwnershipControlPlane!.captureGeometryEpoch(),
    }
    : null;

  const virtualizedFullFragment = virtualizationEnabled && virtualizedDocumentWindowModel !== null
    ? createFullDocumentFragmentFromWindowModel(document, virtualizedDocumentWindowModel)
    : null;
  const sourceNodes = Array.from(virtualizedFullFragment?.childNodes ?? main.childNodes);
  const fragment = document.createDocumentFragment();
  if (mode === "clone" || virtualizedFullFragment !== null) {
    const clones = sourceNodes.map(node => node.cloneNode(true));
    // Stamp each realized block's settled height onto its clone as
    // contain-intrinsic-size. Top-level blocks are `content-visibility: auto;
    // contain-intrinsic-size: auto 120px` (renderer.css): Chromium's
    // last-remembered size is per-ELEMENT internal state that does NOT survive
    // cloneNode, so a re-mounted clone reverts every off-screen block to the
    // 120px estimate and changes the semantic anchor's model position.
    // Persisting the realized offsetHeight of the blocks that WERE laid out at
    // capture (near the viewport — the ones the restore position depends on)
    // makes the clone re-mount at truthful geometry, so the existing pixel
    // restore is simply correct. Off-screen blocks were themselves estimated at
    // capture; stamping their estimate is a no-op, harmless.
    for (let index = 0; index < sourceNodes.length; index++) {
      const live = sourceNodes[index];
      const clone = clones[index];
      if (live instanceof HTMLElement && clone instanceof HTMLElement) {
        const settledHeight = live.offsetHeight;
        if (settledHeight > 0) {
          clone.style.containIntrinsicSize = `auto ${settledHeight}px`;
        }
      }
    }
    fragment.append(...clones);
  } else {
    // "move" mode reuses the live nodes; read their settled height BEFORE the
    // append detaches them (offsetHeight is 0 once out of the document).
    // Two-pass (read ALL heights, then write) so a containIntrinsicSize write
    // on one live in-document node cannot dirty layout and force a synchronous
    // reflow on the next node's offsetHeight read (layout thrash on the
    // synchronous swap-away path). Clone mode writes to detached clones and has
    // no such hazard.
    const settledHeights = sourceNodes.map(node =>
      node instanceof HTMLElement ? node.offsetHeight : 0);
    for (let index = 0; index < sourceNodes.length; index++) {
      const node = sourceNodes[index];
      const settledHeight = settledHeights[index] ?? 0;
      if (node instanceof HTMLElement && settledHeight > 0) {
        node.style.containIntrinsicSize = `auto ${settledHeight}px`;
      }
    }
    fragment.append(...sourceNodes);
  }

  const minimapSnapshot = captureMinimapSnapshot({
    ownerDocument: document,
    minimapContent,
    minimapViewport,
    documentHeight: minimapDocumentHeight,
    lastPostedState: lastPostedMinimapState,
  });

  return {
    fragment,
    nodeCount: sourceNodes.length,
    layoutState: virtualizedLayoutState !== null
      ? {
        ...lastKnownLayoutState,
        ...virtualizedLayoutState,
      }
      : { ...lastKnownLayoutState },
    headings: lastExtractedHeadings.map(cloneHeadingPayload),
    minimapSnapshot,
  };
}

function storeProcessedDocumentCacheEntry(cacheKey: string, entry: ProcessedDocumentCacheEntry): void {
  processedDocumentCache.delete(cacheKey);
  processedDocumentCache.set(cacheKey, entry);
  while (processedDocumentCache.size > PROCESSED_DOCUMENT_CACHE_LIMIT) {
    const oldest = processedDocumentCache.keys().next().value;
    if (oldest === undefined) {
      break;
    }
    processedDocumentCache.delete(oldest);
  }
}

function cachedFragmentIsBehindLiveDocument(cached: ProcessedDocumentCacheEntry): boolean {
  const main = document.querySelector<HTMLElement>("main.mm-document");
  if (!main) {
    return false;
  }

  if (main.childNodes.length > cached.nodeCount) {
    return true;
  }

  const liveHeadingCount = main.querySelectorAll("h1,h2,h3,h4,h5,h6").length;
  if (liveHeadingCount > cached.headings.length) {
    return true;
  }

  return cached.minimapSnapshot === null
    && minimapContent !== null
    && minimapContent.childNodes.length > 0;
}

function refreshProcessedDocumentCacheState(cacheKey: string, markName: string): boolean {
  const cached = processedDocumentCache.get(cacheKey);
  if (cached === undefined) {
    return false;
  }
  if (cachedFragmentIsBehindLiveDocument(cached)) {
    return false;
  }

  const minimapSnapshot = captureMinimapSnapshot({
    ownerDocument: document,
    minimapContent,
    minimapViewport,
    documentHeight: minimapDocumentHeight,
    lastPostedState: lastPostedMinimapState,
  });

  processedDocumentCache.delete(cacheKey);
  processedDocumentCache.set(cacheKey, {
    ...cached,
    layoutState: virtualizationEnabled
      ? {
        ...lastKnownLayoutState,
        readingAnchor: captureCurrentVirtualizedReadingAnchor(),
        settledGeometryEpoch: scrollOwnershipControlPlane!.captureGeometryEpoch(),
      }
      : { ...lastKnownLayoutState },
    headings: lastExtractedHeadings.map(cloneHeadingPayload),
    minimapSnapshot,
  });
  postPerfMark(markName, {
    entries: processedDocumentCache.size,
    nodeCount: cached.nodeCount,
  });
  return true;
}

function scheduleCurrentProcessedDocumentCacheClone(delayMs = 240): void {
  const cacheKey = currentDocumentCacheKey;
  if (!cacheKey || !initialRenderPipelineCompleted || !postReadyEnhancementsCompleted) {
    return;
  }

  const generation = ++processedDocumentCacheCloneGeneration;
  const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
  cancelProcessedDocumentCacheClone();

  const run = () => {
    if (
      generation !== processedDocumentCacheCloneGeneration
      || currentDocumentCacheKey !== cacheKey
      || (
        documentEpoch !== undefined
        && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true
      )
    ) {
      return;
    }

    processedDocumentCacheCloneHandle = null;
    const entry = captureCurrentProcessedDocumentCacheEntry("clone");
    if (!entry) {
      return;
    }

    storeProcessedDocumentCacheEntry(cacheKey, entry);
    postPerfMark("mm-document-cache-prestore", {
      entries: processedDocumentCache.size,
      nodeCount: entry.nodeCount,
    });
  };

  const requestIdle = (window as Window & {
    requestIdleCallback?: (callback: () => void, options?: { timeout?: number }) => number;
  }).requestIdleCallback;

  if (requestIdle) {
    processedDocumentCacheCloneHandle = {
      kind: "idle",
      id: requestIdle(run, { timeout: Math.max(delayMs, 1200) }),
    };
  } else {
    processedDocumentCacheCloneHandle = {
      kind: "timeout",
      id: window.setTimeout(run, delayMs),
    };
  }
}

function preserveCurrentProcessedDocument(): void {
  if (!currentDocumentCacheKey || !initialRenderPipelineCompleted || !postReadyEnhancementsCompleted) {
    return;
  }

  const cacheKey = currentDocumentCacheKey;
  cancelProcessedDocumentCacheClone();
  if (refreshProcessedDocumentCacheState(cacheKey, "mm-document-cache-refresh")) {
    currentDocumentCacheKey = null;
    return;
  }

  const entry = captureCurrentProcessedDocumentCacheEntry("move");
  if (!entry) {
    return;
  }

  storeProcessedDocumentCacheEntry(cacheKey, entry);
  currentDocumentCacheKey = null;
  postPerfMark("mm-document-cache-store", {
    entries: processedDocumentCache.size,
    nodeCount: entry.nodeCount,
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
    if (virtualizationEnabled) {
      scheduleVirtualizedStandaloneOperation("scroll-disabled-reset", "supersede-as-user", operation => {
        operation.requestScrollTop(0, "scroll-disabled-reset");
      });
    } else {
      window.scrollTo({ left: 0, top: 0, behavior: "instant" as ScrollBehavior });
    }
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
  if (theme === "dark") {
    return "#11100d";
  }
  if (theme === "classic-white") {
    return "#ffffff";
  }
  return "#fcfaf6";
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

function isTerminalModelRenderedContentStatus(status: ModelRenderedContentPreparationStatus): boolean {
  return status === "not-needed" || status === "ready" || status === "ready-with-failures";
}

function readModelRenderedContentConsumers(state: ModelRenderedContentCoordinatorState): string[] {
  return Array.from(new Set(state.leases.values())).sort();
}

function postModelRenderedContentMark(
  state: ModelRenderedContentCoordinatorState,
  name: string,
  detail: Record<string, unknown> = {}
): void {
  postPerfMark(name, {
    ...detail,
    activeLeaseCount: state.leases.size,
    consumers: readModelRenderedContentConsumers(state),
    documentEpoch: state.documentEpoch,
  });
}

function isCurrentModelRenderedContentState(state: ModelRenderedContentCoordinatorState): boolean {
  return state.model === virtualizedDocumentWindowModel
    && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(state.documentEpoch) === true;
}

function postModelRenderedContentCancellation(
  state: ModelRenderedContentCoordinatorState,
  reason: string
): void {
  if (state.cancelMarkPosted) {
    return;
  }
  state.cancelMarkPosted = true;
  postModelRenderedContentMark(state, "mm-model-rendered-content-cancel", {
    reason,
    status: state.model.getRenderedContentState(),
  });
}

function cancelModelRenderedContentState(
  state: ModelRenderedContentCoordinatorState,
  reason: string
): void {
  state.cancelled = true;
  state.cancelReason = reason;
  postModelRenderedContentCancellation(state, reason);
}

function cancelModelRenderedContentCoordinator(reason: string): void {
  minimapRenderedContentLease = null;
  renderedFindContentLease = null;
  const state = modelRenderedContentCoordinatorState;
  if (state !== null) {
    state.leases.clear();
    if (state.promise !== null && state.model.getRenderedContentState() === "unprepared") {
      cancelModelRenderedContentState(state, reason);
    }
  }
  modelRenderedContentCoordinatorState = null;
}

function yieldModelRenderedContentWork(): Promise<void> {
  return new Promise(resolve => {
    window.requestAnimationFrame(() => resolve());
  });
}

function handleModelRenderedContentEvent(
  state: ModelRenderedContentCoordinatorState,
  event: ModelRenderedContentEvent
): void {
  if (event.type === "progress") {
    postModelRenderedContentMark(state, "mm-model-rendered-content-progress", {
      committed: event.committed,
      failedMathCount: event.failedMathCount,
      pendingMathCount: event.pendingMathCount,
      renderedMathCount: event.renderedMathCount,
      sectionIndex: event.sectionIndex,
      status: event.status,
    });
    return;
  }

  const detail = {
    failedMathCount: event.failedMathCount,
    pendingMathCount: event.pendingMathCount,
    renderedMathCount: event.renderedMathCount,
    status: event.status,
  };
  if (event.type === "complete") {
    postModelRenderedContentMark(state, "mm-model-rendered-content-end", detail);
  } else if (event.type === "skipped-no-katex") {
    postModelRenderedContentMark(state, "mm-model-rendered-content-skipped-no-katex", detail);
  } else {
    postModelRenderedContentCancellation(state, state.cancelReason ?? "cancelled");
  }
}

function ensureModelRenderedContentJob(
  state: ModelRenderedContentCoordinatorState
): Promise<ModelRenderedContentPreparationStatus> {
  if (state.promise !== null) {
    return state.promise;
  }

  const runSerial = ++state.runSerial;
  state.cancelled = false;
  state.cancelReason = null;
  state.cancelMarkPosted = false;
  postModelRenderedContentMark(state, "mm-model-rendered-content-start", {
    status: state.model.getRenderedContentState(),
  });
  const katex = hostWindow.katex as PrepareDocumentWindowModelRenderedContentDeps["katex"] | undefined;
  const promise: Promise<ModelRenderedContentPreparationStatus> = prepareDocumentWindowModelRenderedContent(state.model, {
    katex,
    now: () => performance.now(),
    onProgress: event => handleModelRenderedContentEvent(state, event),
    ownerDocument: document,
    shouldContinue: () =>
      !state.cancelled
      && state.leases.size > 0
      && isCurrentModelRenderedContentState(state),
    yield: yieldModelRenderedContentWork,
  })
    .then(result => {
      if (
        modelRenderedContentCoordinatorState === state
        && state.runSerial === runSerial
      ) {
        state.promise = null;
        if (result.completed) {
          scheduleCurrentProcessedDocumentCacheClone();
        }
      }
      return result.status;
    }, error => {
      cancelModelRenderedContentState(state, `error:${String(error)}`);
      if (
        modelRenderedContentCoordinatorState === state
        && state.runSerial === runSerial
      ) {
        state.promise = null;
      }
      return "cancelled" satisfies ModelRenderedContentPreparationStatus;
    });
  state.promise = promise;
  return promise;
}

function getCurrentModelRenderedContentState(
  model: DocumentWindowModel,
  documentEpoch: number
): ModelRenderedContentCoordinatorState {
  const existing = modelRenderedContentCoordinatorState;
  if (existing !== null) {
    if (existing.model === model && existing.documentEpoch === documentEpoch) {
      return existing;
    }
    cancelModelRenderedContentState(existing, "stale-model");
  }

  const state: ModelRenderedContentCoordinatorState = {
    cancelMarkPosted: false,
    cancelReason: null,
    cancelled: false,
    documentEpoch,
    leases: new Map<number, ModelRenderedContentConsumerId>(),
    model,
    promise: null,
    runSerial: 0,
  };
  modelRenderedContentCoordinatorState = state;
  return state;
}

function acquireCurrentModelRenderedContentLease(
  consumer: ModelRenderedContentConsumerId
): ModelRenderedContentLease | null {
  const model = virtualizedDocumentWindowModel;
  const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
  if (
    !virtualizationEnabled
    || model === null
    || documentEpoch === undefined
    || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true
  ) {
    return null;
  }

  const modelStatus = model.getRenderedContentState();
  if (modelStatus !== "unprepared") {
    return {
      consumer,
      documentEpoch,
      model,
      readiness: Promise.resolve(modelStatus),
      release: () => {},
    };
  }

  const state = getCurrentModelRenderedContentState(model, documentEpoch);
  const leaseId = ++modelRenderedContentLeaseSerial;
  state.leases.set(leaseId, consumer);
  const readiness = ensureModelRenderedContentJob(state);
  let released = false;
  return {
    consumer,
    documentEpoch,
    model,
    readiness,
    release: () => {
      if (released) {
        return;
      }
      released = true;
      state.leases.delete(leaseId);
      if (
        state.promise !== null
        && state.leases.size === 0
        && state.model.getRenderedContentState() === "unprepared"
      ) {
        cancelModelRenderedContentState(state, "last-lease-released");
      }
    },
  };
}

function releaseMinimapRenderedContentLease(): void {
  const lease = minimapRenderedContentLease;
  minimapRenderedContentLease = null;
  lease?.release();
}

function releaseRenderedFindContentLease(): void {
  const lease = renderedFindContentLease;
  renderedFindContentLease = null;
  lease?.release();
}

function requestRenderedFindModelReadiness(): void {
  const current = renderedFindContentLease;
  const model = virtualizedDocumentWindowModel;
  const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
  if (
    current !== null
    && current.model === model
    && current.documentEpoch === documentEpoch
  ) {
    return;
  }
  releaseRenderedFindContentLease();
  const lease = acquireCurrentModelRenderedContentLease("rendered-find-projection");
  if (lease === null || isTerminalModelRenderedContentStatus(lease.model.getRenderedContentState())) {
    lease?.release();
    return;
  }
  renderedFindContentLease = lease;
  void lease.readiness.finally(() => {
    if (renderedFindContentLease === lease) {
      renderedFindContentLease = null;
      lease.release();
    }
  });
}

function getLiveDocumentRoot(): HTMLElement | null {
  return document.querySelector<HTMLElement>("body > main.mm-document");
}

function readLiveDocumentMathNodes(): HTMLElement[] {
  return Array.from(getLiveDocumentRoot()?.querySelectorAll<HTMLElement>("[data-tex]") ?? []);
}

function getLiveDocumentMathCount(): number {
  return readLiveDocumentMathNodes().length;
}

function findLiveDocumentElementById(id: string): HTMLElement | null {
  const main = getLiveDocumentRoot();
  if (main === null) {
    return null;
  }

  if (main.id === id) {
    return main;
  }

  for (const element of Array.from(main.querySelectorAll<HTMLElement>("[id]"))) {
    if (element.id === id) {
      return element;
    }
  }
  return null;
}

function findLiveDocumentBlockElement(blockIndex: number): HTMLElement | null {
  const main = getLiveDocumentRoot();
  if (main === null || !Number.isFinite(blockIndex)) {
    return null;
  }

  for (const element of Array.from(main.querySelectorAll<HTMLElement>("[data-mm-block-index]"))) {
    if (Number.parseInt(element.dataset["mmBlockIndex"] ?? "", 10) === blockIndex) {
      return element;
    }
  }
  return null;
}

function countFailedInSet(nodes: Iterable<HTMLElement>): number {
  let count = 0;
  for (const node of nodes) {
    if (node.dataset["mmMathRendered"] === "failed") count++;
  }
  return count;
}

function hasUnrenderedDocumentMath(): boolean {
  return (getLiveDocumentRoot()?.querySelector("[data-tex]:not([data-mm-math-rendered])") ?? null) !== null;
}

function renderMath(): MathReadinessController {
  // Thin wrapper preserves renderer-local side effects (perf marks,
  // __mmRendererState exposure, Phase B scheduling) while delegating the
  // rendering loop to the seam in mathRenderInit.ts.
  emitMark("mm-render-math-start", { mathCount: getLiveDocumentMathCount() });
  const katex = hostWindow.katex ?? undefined;
  const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
  const geometryTicket = virtualizationEnabled
    ? beginVirtualizedGeometryWork("document-math")
    : null;
  const controller = renderMathInit({ katex, documentRoot: getLiveDocumentRoot() ?? document });
  // Phase B fires after allMathRendered to re-clone the minimap when the
  // document height genuinely drifted (>=100px). The staleness guard must key
  // off document IDENTITY (currentDocumentCacheKey — same token used by
  // scheduleCachedMermaidResume), NOT layoutReadyGeneration: the latter is
  // bumped by this same render's scheduleLayoutReady BETWEEN this capture and
  // Phase B firing, so the old generation-token guard always cancelled and the
  // rebuild was dead on every initial render. isCancelled() still guards a real
  // new-document load.
  const phaseBDocumentCacheKey = currentDocumentCacheKey;
  const initialVisualSettleReady = virtualizationEnabled
    ? controller.allMathRendered.then(() => {
        if (
          (documentEpoch !== undefined
            && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true)
          || phaseBDocumentCacheKey !== currentDocumentCacheKey
          || controller.isCancelled()
        ) {
          return;
        }

        if (getModelMinimapSource() !== null && minimapSourceReady) {
          syncModelMinimapCloneMetadata();
          updateMinimapViewport({ skipVisibilityUpdate: true });
        }
      })
    : schedulePhaseBRebuild({
        allMathRendered: controller.allMathRendered,
        getCurrentDocumentHeight: () => (document.scrollingElement ?? document.documentElement).scrollHeight,
        getCachedDocumentHeight: () => minimapDocumentHeight,
        refresh: (phase) => {
          if (phaseBDocumentCacheKey !== currentDocumentCacheKey || controller.isCancelled()) {
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
    if (
      documentEpoch !== undefined
      && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true
    ) {
      return;
    }
    emitMark("mm-initial-visible-ready", {
      visibleCount: controller.initialVisibleNodes.size,
      failedCount: countFailedInSet(controller.initialVisibleNodes),
    });
    postPerfMark("mm-initial-visible-ready", {
      visibleCount: controller.initialVisibleNodes.size,
      failedCount: countFailedInSet(controller.initialVisibleNodes),
    });
    // Phase A minimap settle now happens here — once initial-visible math has
    // reached terminal state. Full-DOM documents already seeded minimap content
    // immediately after load, so this path only refreshes geometry unless the
    // source was not ready.
    refreshInitialVisibleMinimapContent();
    // Polish #5 — first-layout-settled gate for width-handle reveal. Now that
    // .mm-document has settled at its final width, updateWidthHandlePosition
    // computes the correct x. Flipping this once is enough: subsequent calls
    // (resize, scroll, mode toggle) keep position fresh; document swaps in
    // loadDocument.ts reset hasInitialLayoutSettled back to false so the
    // next initialVisibleReady gates again.
    hasInitialLayoutSettled = true;
    updateWidthHandlePositionForCurrentLayout();
    // Initial-visible math has inflated heights above/below anchors.
    invalidateSourceLineAnchors();
  });
  controller.allMathRendered.then(() => {
    if (
      documentEpoch !== undefined
      && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true
    ) {
      return;
    }
    // Full math pass settled — anchor tops may all have shifted.
    invalidateSourceLineAnchors();
    const allMathNodes = readLiveDocumentMathNodes();
    emitMark("mm-all-math-rendered", {
      totalCount: controller.totalMathCount,
      failedCount: countFailedInSet(allMathNodes),
      cancelled: controller.isCancelled(),
    });
  });
  const finishGeometryWork = () => {
    if (
      geometryTicket !== null
      && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(geometryTicket.documentEpoch) === true
    ) {
      mutateVirtualizedGeometry(geometryTicket);
      scheduleVirtualizedMeasuredHeightAdoption();
    }
    endVirtualizedGeometryWork(geometryTicket);
  };
  void controller.allMathRendered.then(finishGeometryWork, finishGeometryWork);
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
  perfMarkName = "mm-mermaid-visible-first",
  geometrySource = "mermaid-eager",
  geometryMountGeneration?: number
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
  installLazyMermaidObserver(lazyNodes, generation, mermaid, geometryMountGeneration);
  if (eagerNodes.length === 0) return;
  const geometryTicket = virtualizationEnabled
    ? beginVirtualizedGeometryWork(geometrySource, geometryMountGeneration)
    : null;

  // Budget the EAGER batch's wall-clock. When it expires we stop starting new
  // eager renders — but we must NOT bump mermaidRenderGeneration. The lazy
  // IntersectionObserver installed just above holds THIS generation; bumping it
  // makes every not-yet-scrolled lazy diagram abort as stale in
  // enqueueLazyMermaidRender, so a document whose eager batch is slow (watchdog
  // fires) would silently never render its lazy diagrams. A local flag stops the
  // eager loop while leaving the lazy generation — and any in-flight render,
  // already bounded by its own per-diagram timeout — intact.
  let eagerBudgetExpired = false;
  const watchdog = window.setTimeout(() => {
    eagerBudgetExpired = true;
  }, MERMAID_WATCHDOG_MS);

  try {
    for (const node of eagerNodes) {
      await renderMermaidNode(
        node,
        generation,
        () => mermaidRenderGeneration,
        mermaid,
        MERMAID_PER_DIAGRAM_TIMEOUT_MS,
        virtualizationEnabled ? { manageVirtualizedProxyLifecycle: true } : undefined
      );
      if (eagerBudgetExpired || generation !== mermaidRenderGeneration) return;
    }
  } finally {
    window.clearTimeout(watchdog);
    if (
      geometryTicket !== null
      && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(geometryTicket.documentEpoch) === true
      && (
        geometryMountGeneration === undefined
        || geometryMountGeneration === virtualizedWindowMountGeneration
      )
    ) {
      mutateVirtualizedGeometry(geometryTicket);
      scheduleVirtualizedMeasuredHeightAdoption();
    }
    endVirtualizedGeometryWork(geometryTicket);
  }
}

async function renderMermaid(): Promise<void> {
  disconnectMermaidLazyObserver();
  const mermaid = hostWindow.mermaid;
  if (!mermaid) return;

  const allNodes = Array.from(getLiveDocumentRoot()?.querySelectorAll<HTMLElement>("pre.mm-mermaid") ?? []);
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

    const missingNodes = Array.from(
      getLiveDocumentRoot()?.querySelectorAll<HTMLElement>("pre.mm-mermaid:not(.is-rendered)") ?? []);
    if (missingNodes.length === 0) {
      postPerfMark("mm-mermaid-cache-resume-skipped", { reason: "all-rendered" });
      return;
    }

    void renderMermaidNodes(missingNodes, mermaid, "mm-mermaid-cache-resume");
  }, 0);
}

function cancelProgressiveDeferredEnhancements(): void {
  const handle = progressiveDeferredEnhancementHandle;
  progressiveDeferredEnhancementHandle = null;
  if (handle === null) {
    return;
  }
  if (handle.kind === "idle") {
    (window as Window & { cancelIdleCallback?: (id: number) => void }).cancelIdleCallback?.(handle.id);
  } else {
    window.clearTimeout(handle.id);
  }
}

function scheduleProgressiveDeferredEnhancements(message: Extract<HostMessage, { type: "append-document" }>): void {
  if (virtualizationEnabled) {
    cancelProgressiveDeferredEnhancements();
  }
  const renderId = message.renderId;
  const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
  const run = () => {
    if (virtualizationEnabled) {
      progressiveDeferredEnhancementHandle = null;
    }
    if (
      documentEpoch !== undefined
      && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true
    ) {
      return;
    }
    if (
      renderId !== undefined
      && currentDocumentRenderId !== null
      && renderId !== currentDocumentRenderId
    ) {
      postPerfMark("mm-progressive-enhancements-stale", {
        renderId,
        currentRenderId: currentDocumentRenderId
      });
      return;
    }

    postPerfMark("mm-progressive-enhancements-start", {
      renderId: renderId ?? null
    });
    renderMath();
    scheduleCachedMermaidResume(message.hasMermaid);
    scheduleCurrentProcessedDocumentCacheClone(1200);
    postPerfMark("mm-progressive-enhancements-end", {
      renderId: renderId ?? null
    });
  };

  const requestIdle = (window as Window & {
    requestIdleCallback?: (callback: () => void, options?: { timeout?: number }) => number;
  }).requestIdleCallback;
  if (requestIdle) {
    const id = requestIdle(run, { timeout: 4000 });
    if (virtualizationEnabled) {
      progressiveDeferredEnhancementHandle = { kind: "idle", id };
    }
    return;
  }

  const id = window.setTimeout(run, 800);
  if (virtualizationEnabled) {
    progressiveDeferredEnhancementHandle = { kind: "timeout", id };
  }
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
  mermaid: MermaidApiLike,
  mountGeneration?: number
): void {
  if (nodes.length === 0) return;

  postPerfMark("mm-mermaid-lazy-observe", {
    total: nodes.length,
    rootMarginPx: MERMAID_LAZY_ROOT_MARGIN_PX
  });
  if (typeof window.IntersectionObserver !== "function") {
    for (const node of nodes) {
      enqueueLazyMermaidRender(node, generation, mermaid, mountGeneration);
    }
    return;
  }

  mermaidLazyObserver = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      const node = entry.target as HTMLElement;
      mermaidLazyObserver?.unobserve(node);
      enqueueLazyMermaidRender(node, generation, mermaid, mountGeneration);
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
  mermaid: MermaidApiLike,
  mountGeneration?: number
): void {
  if (
    generation !== mermaidRenderGeneration
    || (mountGeneration !== undefined && mountGeneration !== virtualizedWindowMountGeneration)
  ) return;
  const marker = String(generation);
  if (node.dataset.mmMermaidRenderQueued === marker) return;
  node.dataset.mmMermaidRenderQueued = marker;
  const geometryTicket = virtualizationEnabled
    ? beginVirtualizedGeometryWork("lazy-mermaid", mountGeneration)
    : null;

  mermaidLazyRenderQueue = mermaidLazyRenderQueue
    .catch(() => undefined)
    .then(async () => {
      try {
        if (
          generation !== mermaidRenderGeneration
          || (mountGeneration !== undefined && mountGeneration !== virtualizedWindowMountGeneration)
        ) return;
        postPerfMark("mm-mermaid-lazy-render-start");
        await renderMermaidNode(
          node,
          generation,
          () => mermaidRenderGeneration,
          mermaid,
          MERMAID_PER_DIAGRAM_TIMEOUT_MS,
          virtualizationEnabled ? { manageVirtualizedProxyLifecycle: true } : undefined
        );
        if (
          generation === mermaidRenderGeneration
          && (mountGeneration === undefined || mountGeneration === virtualizedWindowMountGeneration)
        ) {
          postPerfMark("mm-mermaid-lazy-render-end");
          scheduleCurrentProcessedDocumentCacheClone();
          mutateVirtualizedGeometry(geometryTicket);
          scheduleVirtualizedMeasuredHeightAdoption();
        }
      } finally {
        endVirtualizedGeometryWork(geometryTicket);
      }
    });
}

function renderCodeBlocks(root: ParentNode = document): void {
  const hljs = hostWindow.hljs;
  if (!hljs) return;

  const nodes = Array.from(root.querySelectorAll<HTMLElement>("code[data-mm-code], code[data-mm-mermaid]"));
  for (const node of nodes) {
    if (node.dataset["mmHighlighted"] === "true") {
      continue;
    }

    const langClass = Array.from(node.classList).find(c => c.startsWith("language-"));
    const rawLang = langClass?.slice("language-".length);
    const normalized = normalizeHljsLanguage(rawLang);
    if (!hljs.getLanguage(normalized)) {
      node.dataset["mmHighlighted"] = "true";
      continue;
    }

    if (langClass && langClass !== `language-${normalized}`) {
      node.classList.remove(langClass);
      node.classList.add(`language-${normalized}`);
    }
    try { hljs.highlightElement(node); } catch { /* leave plain */ }
    node.dataset["mmHighlighted"] = "true";
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
  scheduleCurrentProcessedDocumentCacheClone();
}

function hasMermaidNodes(): boolean {
  return (getLiveDocumentRoot()?.querySelector("pre.mm-mermaid") ?? null) !== null;
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

function appendProgressiveDocumentHtml(message: Extract<HostMessage, { type: "append-document" }>): void {
  if (
    message.renderId !== undefined
    && currentDocumentRenderId !== null
    && message.renderId !== currentDocumentRenderId
  ) {
    postPerfMark("mm-progressive-append-stale", {
      renderId: message.renderId,
      currentRenderId: currentDocumentRenderId
    });
    return;
  }

  const main = document.querySelector<HTMLElement>("main.mm-document");
  if (!main || message.html.length === 0) {
    return;
  }

  const isFinal = message.isFinal !== false;
  postPerfMark("mm-progressive-append-start", {
    htmlLength: message.html.length,
    renderId: message.renderId ?? null,
    isFinal
  });
  const template = document.createElement("template");
  template.innerHTML = message.html;
  if (message.hasHljs !== false) {
    renderCodeBlocks(template.content);
  }

  main.append(template.content);
  virtualizationShadowDocumentFinal = isFinal;
  invalidateTopVisibleBlockIndexCache();

  if (!isFinal) {
    postPerfMark("mm-progressive-append-end", {
      htmlLength: message.html.length,
      renderId: message.renderId ?? null,
      isFinal: false
    });
    return;
  }

  if (typeof message.cacheKey === "string" && message.cacheKey.length > 0) {
    setCurrentProcessedDocumentCacheKey(message.cacheKey);
  }

  ensureChromeNodes(false, { refreshMinimap: false });
  // Appended blocks are absent from the anchor cache — invalidate (which also
  // re-asserts a live programmatic line target against the FINAL geometry).
  invalidateSourceLineAnchors();
  postPerfMark("mm-progressive-append-end", {
    htmlLength: message.html.length,
    renderId: message.renderId ?? null,
    isFinal: true
  });
  queueProgressiveMinimapAppendRefresh(message);
  scheduleProgressiveDeferredEnhancements(message);
}

function postThemeAppliedAfterPaint(theme: RendererTheme, requestId?: number): void {
  if (requestId === undefined || !Number.isFinite(requestId) || requestId <= 0) {
    return;
  }

  const generation = ++themeAppliedAckGeneration;
  let posted = false;
  const postAck = () => {
    if (posted || generation !== themeAppliedAckGeneration) {
      return;
    }

    posted = true;
    postHostMessage({ type: "theme-applied", theme, requestId });
  };

  window.requestAnimationFrame(() => window.requestAnimationFrame(postAck));
  window.setTimeout(postAck, THEME_APPLIED_ACK_FALLBACK_MS);
}

function handleThemeChange(theme: RendererTheme, requestId?: number): void {
  postPerfMark("mm-theme-change-start", { theme });
  applyTheme(theme);
  initMermaidWithTheme(theme);
  postPerfMark("mm-theme-change-applied", { theme });
  postThemeAppliedAfterPaint(theme, requestId);
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

function invalidateTopVisibleBlockIndexCache(): void {
  liveDocumentBlockElements = [];
  liveDocumentBlockElementsStale = true;
  invalidateVirtualizationShadowModel();
}

function refreshTopVisibleBlockIndexCache(): void {
  liveDocumentBlockElements = collectLiveDocumentBlockElements(document);
  liveDocumentBlockElementsStale = false;
}

function getLiveDocumentBlockElements(): readonly HTMLElement[] {
  if (liveDocumentBlockElementsStale) {
    refreshTopVisibleBlockIndexCache();
  }
  return liveDocumentBlockElements;
}

// The top visible block: the first element with data-mm-block-index whose
// bottom edge is below the viewport's top. Returns null if no annotated
// block exists yet (before first render, or document without blocks).
function findTopVisibleBlockIndex(): number | null {
  const root = document.scrollingElement ?? document.documentElement;
  return findTopVisibleBlockIndexFromBlocks(getLiveDocumentBlockElements(), root.scrollTop);
}

function getVirtualizationShadowValidator(): VirtualizationShadowValidator {
  if (virtualizationShadowValidator === null) {
    virtualizationShadowValidator = createVirtualizationShadowValidator({
      ownerDocument: document,
      ownerWindow: window,
      isDocumentFinal: () => virtualizationShadowDocumentFinal,
      postDebugLog,
      postPerfMark,
    });
  }
  return virtualizationShadowValidator;
}

function invalidateVirtualizationShadowModel(): void {
  virtualizationShadowValidator?.invalidate();
}

function scheduleVirtualizationShadowValidation(): void {
  if (!virtualizationShadowEnabled) {
    return;
  }

  getVirtualizationShadowValidator().schedule();
}

function getDocumentScrollRoot(): Element & { scrollTop: number; scrollHeight: number; clientHeight: number } {
  return (document.scrollingElement ?? document.documentElement) as Element & {
    scrollTop: number;
    scrollHeight: number;
    clientHeight: number;
  };
}

type VirtualizedScrollOperation = WindowTargetOperation & {
  lease: ScrollLease;
};

type VirtualizedMaintenancePhase = "executing" | "frame-scheduled" | "pending" | "retry-pending" | "terminal";

type VirtualizedMaintenanceRequestId = Readonly<{
  documentEpoch: number;
  requestSerial: number;
}>;

type VirtualizedMaintenanceBinding = Readonly<{
  operation: VirtualizedScrollOperation;
  operationEpoch: number;
  ownsLease: boolean;
}>;

type VirtualizedMaintenanceTerminal = Readonly<{
  reason: string;
  status: "canceled" | "completed" | "failed";
}>;

type VirtualizedMaintenanceRequest = {
  binding: VirtualizedMaintenanceBinding | null;
  documentEpoch: number;
  executionCount: 0 | 1;
  owner: string;
  onTerminal: ((terminal: VirtualizedMaintenanceTerminal) => void) | null;
  phase: VirtualizedMaintenancePhase;
  requestId: VirtualizedMaintenanceRequestId;
  retryFrame: number | null;
  terminal: VirtualizedMaintenanceTerminal | null;
  work: (operation: VirtualizedScrollOperation) => void;
  workRevision: number;
};

type VirtualizedMaintenanceOwnerSlot = {
  active: VirtualizedMaintenanceRequest | null;
  successor: VirtualizedMaintenanceRequest | null;
};

type VirtualizedMaintenanceReleaseHold = {
  operation: VirtualizedScrollOperation;
  releaseRequested: boolean;
  requestSerials: Set<number>;
};

const virtualizedMaintenanceByOwner = new Map<string, VirtualizedMaintenanceOwnerSlot>();
const virtualizedMaintenanceReleaseHolds = new Map<number, VirtualizedMaintenanceReleaseHold>();
const virtualizedMaintenanceDeferredPromotionOwners = new Set<string>();
let virtualizedMaintenanceCancellationBatchDepth = 0;
let virtualizedMaintenanceRequestSerial = 0;
let pendingInitialVirtualizedWindowWork: ((operation: VirtualizedScrollOperation) => void) | null = null;

function beginVirtualizedGeometryWork(
  source: string,
  mountGeneration = virtualizedWindowMountGeneration
): GeometryWorkTicket | null {
  const plane = scrollOwnershipControlPlane;
  if (plane === null) {
    return null;
  }
  return plane.beginGeometryWork(source, plane.captureDocumentEpoch(), mountGeneration);
}

function mutateVirtualizedGeometry(ticket: GeometryWorkTicket | null): boolean {
  return ticket !== null && scrollOwnershipControlPlane?.geometryMutated(ticket) === true;
}

function endVirtualizedGeometryWork(ticket: GeometryWorkTicket | null): boolean {
  return ticket !== null && scrollOwnershipControlPlane?.endGeometryWork(ticket) === true;
}

type CurrentGeometrySettlement = Extract<GeometrySettledWaitOutcome, { status: "settled" }>;

async function waitForCurrentVirtualizedGeometry(
  operation: VirtualizedScrollOperation,
  afterEmission: number
): Promise<GeometrySettledWaitOutcome> {
  const plane = scrollOwnershipControlPlane;
  if (plane === null || !operation.isCurrent()) {
    return { reason: "programmatic-supersession", status: "canceled" };
  }
  return plane.waitForGeometrySettled(operation.documentEpoch, afterEmission);
}

async function awaitConfirmedVirtualizedGeometry(
  operation: VirtualizedScrollOperation,
  nominal: CurrentGeometrySettlement
): Promise<
  | { confirmation: CurrentGeometrySettlement; status: "confirmed" }
  | { settlement: CurrentGeometrySettlement; status: "changed" }
  | Extract<GeometrySettledWaitOutcome, { status: "canceled" }>
> {
  const plane = scrollOwnershipControlPlane;
  if (plane === null || !operation.isCurrent()) {
    return { reason: "programmatic-supersession", status: "canceled" };
  }
  const documentEpoch = operation.documentEpoch;
  const afterEmission = nominal.emission;
  const confirmation = await plane.waitForGeometrySettled(documentEpoch, afterEmission);
  if (confirmation.status === "canceled") {
    return confirmation;
  }
  if (
    confirmation.payload.geometryEpoch === nominal.payload.geometryEpoch
    && plane.holds(operation.lease, confirmation.payload.geometryEpoch)
  ) {
    return { confirmation, status: "confirmed" };
  }
  return { settlement: confirmation, status: "changed" };
}

function consumePendingInitialVirtualizedWindow(operation: VirtualizedScrollOperation): boolean {
  const work = pendingInitialVirtualizedWindowWork;
  pendingInitialVirtualizedWindowWork = null;
  if (work === null || !operation.isCurrent()) {
    return false;
  }
  work(operation);
  return true;
}

function createVirtualizedScrollOperation(lease: ScrollLease): VirtualizedScrollOperation | null {
  const plane = scrollOwnershipControlPlane;
  if (plane === null) {
    return null;
  }
  return {
    documentEpoch: lease.documentEpoch,
    operationEpoch: lease.operationEpoch,
    lease,
    isCurrent: () => plane.isCurrentDocumentEpoch(lease.documentEpoch) && plane.holds(lease),
    requestScrollTop: (target, writer) => {
      if (!plane.isCurrentDocumentEpoch(lease.documentEpoch) || !plane.holds(lease)) {
        return;
      }
      const receipt = plane.write(lease, { target, writer });
      virtualizedWriteReceipts.set(lease.operationEpoch, receipt);
      void receipt.result.then(() => {
        if (virtualizedWriteReceipts.get(lease.operationEpoch) === receipt) {
          virtualizedWriteReceipts.delete(lease.operationEpoch);
        }
      });
    },
    scheduleFrameTransaction: work => plane.scheduleFrameTransaction(lease, work),
  };
}

function acquireVirtualizedScrollOperation(
  owner: string,
  policy: ScrollAcquirePolicy
): VirtualizedScrollOperation | null {
  const maintenanceCutoff = virtualizedMaintenanceRequestSerial;
  const acquired = scrollOwnershipControlPlane?.acquire(owner, policy);
  if (acquired?.status !== "acquired") {
    return null;
  }
  const operation = createVirtualizedScrollOperation(acquired.lease);
  if (operation !== null && policy !== "defer") {
    cancelVirtualizedMaintenanceThrough(
      maintenanceCutoff,
      policy === "supersede-as-user" ? "user-supersession" : "programmatic-supersession"
    );
  }
  return operation;
}

function releaseVirtualizedScrollOperationAfterWrite(operation: VirtualizedScrollOperation): void {
  const plane = scrollOwnershipControlPlane;
  if (plane === null) {
    return;
  }
  const receipt = virtualizedWriteReceipts.get(operation.operationEpoch);
  if (receipt === undefined) {
    if (operation.isCurrent()) {
      releaseVirtualizedScrollOperation(operation);
    }
    return;
  }
  void receipt.result.then(() => {
    if (operation.isCurrent()) {
      releaseVirtualizedScrollOperation(operation);
    }
  });
}

function releaseVirtualizedScrollOperation(operation: VirtualizedScrollOperation): boolean {
  const plane = scrollOwnershipControlPlane;
  const hold = virtualizedMaintenanceReleaseHolds.get(operation.operationEpoch);
  if (hold !== undefined && hold.requestSerials.size > 0) {
    hold.releaseRequested = true;
    return true;
  }
  if (plane === null || !plane.release(operation.lease)) {
    return false;
  }
  return true;
}

function scheduleVirtualizedStandaloneOperation(
  owner: string,
  policy: ScrollAcquirePolicy,
  work: (operation: VirtualizedScrollOperation) => void
): VirtualizedScrollOperation | null {
  const operation = acquireVirtualizedScrollOperation(owner, policy);
  if (operation === null) {
    return null;
  }
  const scheduled = operation.scheduleFrameTransaction(() => {
    if (!operation.isCurrent()) {
      return;
    }
    work(operation);
    releaseVirtualizedScrollOperationAfterWrite(operation);
  });
  if (!scheduled) {
    releaseVirtualizedScrollOperation(operation);
    return null;
  }
  return operation;
}

function scheduleExistingVirtualizedOperation(
  operation: VirtualizedScrollOperation,
  work: () => void,
  releaseAfterWrite = false
): boolean {
  const scheduled = operation.scheduleFrameTransaction(() => {
    if (!operation.isCurrent()) {
      return;
    }
    work();
    if (releaseAfterWrite) {
      releaseVirtualizedScrollOperationAfterWrite(operation);
    }
  });
  if (!scheduled && releaseAfterWrite && operation.isCurrent()) {
    releaseVirtualizedScrollOperation(operation);
  }
  return scheduled;
}

function scheduleVirtualizedElementLanding(
  operation: VirtualizedScrollOperation,
  element: HTMLElement | null,
  writer: string,
  viewportOffsetY = 0
): boolean {
  if (element === null) {
    if (operation.isCurrent()) {
      releaseVirtualizedScrollOperation(operation);
    }
    return false;
  }
  return scheduleExistingVirtualizedOperation(operation, () => {
    const target = readElementDocumentTop(element) - Math.max(0, viewportOffsetY);
    operation.requestScrollTop(target, writer);
  }, true);
}

function virtualizedMaintenanceDetail(request: VirtualizedMaintenanceRequest): Record<string, unknown> {
  return {
    documentEpoch: request.documentEpoch,
    owner: request.owner,
    requestId: request.requestId,
    requestSerial: request.requestId.requestSerial,
    workRevision: request.workRevision,
  };
}

function postVirtualizedMaintenanceEvent(
  name: "mm-virt-maintenance-bound"
    | "mm-virt-maintenance-coalesced"
    | "mm-virt-maintenance-requested"
    | "mm-virt-maintenance-retry",
  request: VirtualizedMaintenanceRequest,
  detail: Record<string, unknown> = {}
): void {
  postPerfMark(name, {
    ...virtualizedMaintenanceDetail(request),
    ...detail,
  });
}

function isLiveVirtualizedMaintenanceRequest(request: VirtualizedMaintenanceRequest): boolean {
  if (request.terminal !== null || request.phase === "terminal") {
    return false;
  }
  const slot = virtualizedMaintenanceByOwner.get(request.owner);
  return slot?.active === request || slot?.successor === request;
}

function isActiveVirtualizedMaintenanceRequest(request: VirtualizedMaintenanceRequest): boolean {
  return isLiveVirtualizedMaintenanceRequest(request)
    && virtualizedMaintenanceByOwner.get(request.owner)?.active === request;
}

function createVirtualizedMaintenanceRequest(
  owner: string,
  documentEpoch: number,
  work: (operation: VirtualizedScrollOperation) => void,
  onTerminal: ((terminal: VirtualizedMaintenanceTerminal) => void) | null
): VirtualizedMaintenanceRequest {
  const requestSerial = ++virtualizedMaintenanceRequestSerial;
  return {
    binding: null,
    documentEpoch,
    executionCount: 0,
    owner,
    onTerminal,
    phase: "pending",
    requestId: Object.freeze({ documentEpoch, requestSerial }),
    retryFrame: null,
    terminal: null,
    work,
    workRevision: 1,
  };
}

function postVirtualizedMaintenanceRequested(request: VirtualizedMaintenanceRequest): void {
  postVirtualizedMaintenanceEvent("mm-virt-maintenance-requested", request);
}

function coalesceVirtualizedMaintenanceRequest(
  request: VirtualizedMaintenanceRequest,
  work: (operation: VirtualizedScrollOperation) => void,
  onTerminal: ((terminal: VirtualizedMaintenanceTerminal) => void) | null
): void {
  if (!isLiveVirtualizedMaintenanceRequest(request)) {
    return;
  }
  const replacedTerminal = request.onTerminal;
  request.onTerminal = onTerminal;
  request.work = work;
  request.workRevision++;
  replacedTerminal?.({ reason: "coalesced", status: "canceled" });
  postVirtualizedMaintenanceEvent("mm-virt-maintenance-coalesced", request);
}

function registerVirtualizedMaintenanceReleaseHold(
  request: VirtualizedMaintenanceRequest,
  binding: VirtualizedMaintenanceBinding
): void {
  let hold = virtualizedMaintenanceReleaseHolds.get(binding.operationEpoch);
  if (hold === undefined) {
    hold = {
      operation: binding.operation,
      releaseRequested: false,
      requestSerials: new Set<number>(),
    };
    virtualizedMaintenanceReleaseHolds.set(binding.operationEpoch, hold);
  }
  hold.requestSerials.add(request.requestId.requestSerial);
}

type VirtualizedMaintenanceReleaseAction = {
  afterWrite: boolean;
  operation: VirtualizedScrollOperation;
};

function detachVirtualizedMaintenanceBinding(
  request: VirtualizedMaintenanceRequest,
  terminal: VirtualizedMaintenanceTerminal
): VirtualizedMaintenanceReleaseAction | null {
  const binding = request.binding;
  if (binding === null) {
    return null;
  }
  if (binding.ownsLease) {
    if (terminal.status === "completed") {
      return { afterWrite: true, operation: binding.operation };
    }
    if (terminal.status === "canceled" && binding.operation.isCurrent()) {
      return { afterWrite: false, operation: binding.operation };
    }
    return null;
  }

  const hold = virtualizedMaintenanceReleaseHolds.get(binding.operationEpoch);
  if (hold === undefined) {
    return null;
  }
  hold.requestSerials.delete(request.requestId.requestSerial);
  if (hold.requestSerials.size > 0) {
    return null;
  }
  virtualizedMaintenanceReleaseHolds.delete(binding.operationEpoch);
  return hold.releaseRequested && hold.operation.isCurrent()
    ? { afterWrite: true, operation: hold.operation }
    : null;
}

function promoteVirtualizedMaintenanceSuccessor(owner: string): void {
  const slot = virtualizedMaintenanceByOwner.get(owner);
  if (slot === undefined || slot.active !== null) {
    return;
  }
  const successor = slot.successor;
  slot.successor = null;
  if (successor === null) {
    virtualizedMaintenanceByOwner.delete(owner);
    return;
  }
  slot.active = successor;
  attemptVirtualizedMaintenance(successor);
}

function flushVirtualizedMaintenancePromotions(): void {
  if (virtualizedMaintenanceCancellationBatchDepth !== 0) {
    return;
  }
  const owners = [...virtualizedMaintenanceDeferredPromotionOwners];
  virtualizedMaintenanceDeferredPromotionOwners.clear();
  for (const owner of owners) {
    promoteVirtualizedMaintenanceSuccessor(owner);
  }
}

function finishVirtualizedMaintenance(
  request: VirtualizedMaintenanceRequest,
  status: "canceled" | "completed" | "failed",
  reason: string
): boolean {
  if (request.terminal !== null || request.phase === "terminal") {
    return false;
  }
  const terminal = Object.freeze({ reason, status });
  request.terminal = terminal;
  request.phase = "terminal";
  const onTerminal = request.onTerminal;
  request.onTerminal = null;
  if (request.retryFrame !== null) {
    window.cancelAnimationFrame(request.retryFrame);
    request.retryFrame = null;
  }
  const releaseAction = detachVirtualizedMaintenanceBinding(request, terminal);
  const slot = virtualizedMaintenanceByOwner.get(request.owner);
  if (slot?.active === request) {
    slot.active = null;
  } else if (slot?.successor === request) {
    slot.successor = null;
  }
  if (slot !== undefined && slot.active === null && slot.successor === null) {
    virtualizedMaintenanceByOwner.delete(request.owner);
  }

  postPerfMark("mm-virt-maintenance-terminal", {
    ...virtualizedMaintenanceDetail(request),
    executionCount: request.executionCount,
    reason,
    status,
  });
  onTerminal?.(terminal);

  if (releaseAction !== null) {
    if (releaseAction.afterWrite) {
      releaseVirtualizedScrollOperationAfterWrite(releaseAction.operation);
    } else {
      releaseVirtualizedScrollOperation(releaseAction.operation);
    }
  }

  if (slot?.active === null && slot.successor !== null) {
    if (status === "failed") {
      finishVirtualizedMaintenance(slot.successor, "canceled", reason);
    } else if (virtualizedMaintenanceCancellationBatchDepth === 0) {
      promoteVirtualizedMaintenanceSuccessor(request.owner);
    } else {
      virtualizedMaintenanceDeferredPromotionOwners.add(request.owner);
    }
  }
  return true;
}

function cancelVirtualizedMaintenanceRequests(
  predicate: (request: VirtualizedMaintenanceRequest) => boolean,
  reason: string
): void {
  const selected: VirtualizedMaintenanceRequest[] = [];
  for (const slot of virtualizedMaintenanceByOwner.values()) {
    for (const request of [slot.active, slot.successor]) {
      if (request !== null && isLiveVirtualizedMaintenanceRequest(request) && predicate(request)) {
        selected.push(request);
      }
    }
  }
  virtualizedMaintenanceCancellationBatchDepth++;
  try {
    for (const request of selected) {
      finishVirtualizedMaintenance(request, "canceled", reason);
    }
  } finally {
    virtualizedMaintenanceCancellationBatchDepth--;
    flushVirtualizedMaintenancePromotions();
  }
}

function cancelPendingVirtualizedMaintenance(reason: string): void {
  cancelVirtualizedMaintenanceRequests(() => true, reason);
}

function cancelVirtualizedMaintenanceThrough(cutoff: number, reason: string): void {
  cancelVirtualizedMaintenanceRequests(
    request => request.requestId.requestSerial <= cutoff,
    reason
  );
}

function scheduleVirtualizedMaintenanceRetry(request: VirtualizedMaintenanceRequest): void {
  if (
    request.retryFrame !== null
    || !isActiveVirtualizedMaintenanceRequest(request)
    || request.phase === "terminal"
  ) {
    return;
  }
  request.phase = "retry-pending";
  request.retryFrame = window.requestAnimationFrame(() => {
    request.retryFrame = null;
    if (!isActiveVirtualizedMaintenanceRequest(request)) {
      return;
    }
    if (scrollOwnershipControlPlane?.isCurrentDocumentEpoch(request.documentEpoch) !== true) {
      finishVirtualizedMaintenance(request, "canceled", "stale-document");
      return;
    }
    request.phase = "pending";
    attemptVirtualizedMaintenance(request);
  });
  postVirtualizedMaintenanceEvent("mm-virt-maintenance-retry", request, {
    reason: "frame-transaction-occupied",
  });
}

function deliverVirtualizedMaintenance(
  request: VirtualizedMaintenanceRequest,
  operation: VirtualizedScrollOperation
): void {
  const binding = request.binding;
  if (
    !isActiveVirtualizedMaintenanceRequest(request)
    || request.phase !== "frame-scheduled"
    || binding === null
    || binding.operation !== operation
  ) {
    return;
  }
  if (!operation.isCurrent() || operation.documentEpoch !== request.documentEpoch) {
    finishVirtualizedMaintenance(request, "canceled", "stale-operation");
    return;
  }
  if (request.executionCount !== 0) {
    finishVirtualizedMaintenance(request, "failed", "execution-count-invariant");
    return;
  }
  request.phase = "executing";
  request.executionCount = 1;
  const work = request.work;
  try {
    work(operation);
  } catch (error) {
    finishVirtualizedMaintenance(request, "failed", "frame-work-failed");
    throw error;
  }
  finishVirtualizedMaintenance(request, "completed", "delivered");
}

function attemptVirtualizedMaintenance(request: VirtualizedMaintenanceRequest): void {
  const plane = scrollOwnershipControlPlane;
  if (
    plane === null
    || !isActiveVirtualizedMaintenanceRequest(request)
    || !plane.isCurrentDocumentEpoch(request.documentEpoch)
  ) {
    if (isLiveVirtualizedMaintenanceRequest(request)) {
      finishVirtualizedMaintenance(request, "canceled", "stale-document");
    }
    return;
  }
  const joined = plane.joinMaintenance(request.owner);
  if (joined === null) {
    finishVirtualizedMaintenance(request, "canceled", "lease-unavailable");
    return;
  }
  const operation = createVirtualizedScrollOperation(joined.lease);
  if (operation === null) {
    finishVirtualizedMaintenance(request, "canceled", "operation-unavailable");
    return;
  }
  const scheduled = operation.scheduleFrameTransaction(() => {
    deliverVirtualizedMaintenance(request, operation);
  });
  if (!scheduled) {
    if (joined.ownsLease && operation.isCurrent()) {
      releaseVirtualizedScrollOperation(operation);
    }
    scheduleVirtualizedMaintenanceRetry(request);
    return;
  }

  const binding = Object.freeze({
    operation,
    operationEpoch: operation.operationEpoch,
    ownsLease: joined.ownsLease,
  });
  request.binding = binding;
  request.phase = "frame-scheduled";
  if (!binding.ownsLease) {
    registerVirtualizedMaintenanceReleaseHold(request, binding);
  }
  postVirtualizedMaintenanceEvent("mm-virt-maintenance-bound", request, {
    operationEpoch: binding.operationEpoch,
    ownsLease: binding.ownsLease,
  });
}

function scheduleVirtualizedMaintenance(
  owner: string,
  work: (operation: VirtualizedScrollOperation) => void,
  onTerminal: ((terminal: VirtualizedMaintenanceTerminal) => void) | null = null
): boolean {
  const plane = scrollOwnershipControlPlane;
  if (plane === null) {
    return false;
  }
  const documentEpoch = plane.captureDocumentEpoch();
  const staleSlot = virtualizedMaintenanceByOwner.get(owner);
  if (
    staleSlot !== undefined
    && [staleSlot.active, staleSlot.successor].some(request =>
      request !== null && request.documentEpoch !== documentEpoch)
  ) {
    cancelVirtualizedMaintenanceRequests(
      request => request.owner === owner && request.documentEpoch !== documentEpoch,
      "stale-document"
    );
  }

  let slot = virtualizedMaintenanceByOwner.get(owner);
  if (slot?.active !== null && slot?.active !== undefined) {
    if (slot.active.phase === "executing") {
      if (slot.successor !== null) {
        coalesceVirtualizedMaintenanceRequest(slot.successor, work, onTerminal);
        return true;
      }
      const successor = createVirtualizedMaintenanceRequest(owner, documentEpoch, work, onTerminal);
      slot.successor = successor;
      postVirtualizedMaintenanceRequested(successor);
      return true;
    }
    coalesceVirtualizedMaintenanceRequest(slot.active, work, onTerminal);
    return true;
  }

  const request = createVirtualizedMaintenanceRequest(owner, documentEpoch, work, onTerminal);
  if (slot === undefined) {
    slot = { active: request, successor: null };
    virtualizedMaintenanceByOwner.set(owner, slot);
  } else {
    slot.active = request;
  }
  postVirtualizedMaintenanceRequested(request);
  attemptVirtualizedMaintenance(request);
  return true;
}

function captureCurrentVirtualizedReadingAnchor(): ReadingAnchor | null {
  const main = document.querySelector<HTMLElement>("main.mm-document");
  return main === null ? null : captureReadingAnchor(collectLiveDocumentSectionElements(main));
}

function readVirtualizedFindContext(): import("./virtualizedFindProvider").VirtualizedFindContext {
  return {
    beginNavigationOperation: () => acquireVirtualizedScrollOperation(
      "find-navigation",
      "supersede-programmatic"
    ),
    completeNavigationOperation: operation => {
      releaseVirtualizedScrollOperationAfterWrite(operation as VirtualizedScrollOperation);
    },
    controller: virtualizedDocumentWindowController,
    main: document.querySelector<HTMLElement>("main.mm-document"),
    model: virtualizedDocumentWindowModel,
    ownerWindow: window,
    renderId: currentDocumentRenderId,
    root: getDocumentScrollRoot(),
    virtualizationEnabled,
  };
}

function isVirtualizedProgrammaticNavigationInProgress(): boolean {
  return virtualizationEnabled
    && virtualizedProgrammaticNavigationInProgress
    && virtualizedProgrammaticNavigationOperation?.isCurrent() === true;
}

function writeVirtualizedProgrammaticNavigationScrollTop(
  operation: VirtualizedScrollOperation,
  scrollTop: number,
  writer: string
): void {
  operation.requestScrollTop(Math.max(0, scrollTop), writer);
}

function resolveVirtualizedNavigationTargetSectionIndex(descriptor: WindowTargetDescriptor): number | null {
  const model = virtualizedDocumentWindowModel;
  if (model === null) {
    return null;
  }

  const entry = (() => {
    switch (descriptor.kind) {
      case "block":
        return model.getEntryContainingBlockIndex(descriptor.blockIndex);
      case "heading-anchor":
        return model.getEntryByHeadingAnchor(descriptor.anchor);
      case "source-line":
        return model.getEntryBySourceLine(descriptor.sourceLine);
      case "document-y":
        return model.sections[model.sectionIndexAtDocumentY(descriptor.documentY)];
      case "section":
        return model.sections[descriptor.sectionIndex];
      case "find-match":
        return descriptor.blockIndex === undefined
          ? undefined
          : model.getEntryContainingBlockIndex(descriptor.blockIndex);
    }
  })();
  if (entry === undefined) {
    return null;
  }

  const sectionIndex = model.sections.findIndex(candidate => candidate.blockIndex === entry.blockIndex);
  return sectionIndex < 0 ? null : sectionIndex;
}

function forceRenderVirtualizedNavigationTarget(
  descriptor: WindowTargetDescriptor,
  operation?: VirtualizedWindowOperation
): boolean {
  const controller = virtualizedDocumentWindowController;
  if (controller === null) {
    return false;
  }

  const sectionIndex = resolveVirtualizedNavigationTargetSectionIndex(descriptor);
  if (sectionIndex === null) {
    return false;
  }

  return controller.ensureSectionRendered(sectionIndex, {
    force: true,
    ...(operation === undefined ? {} : { operation }),
    preserveAnchor: false,
  });
}

function readVirtualizedProgrammaticNavigationTargetContext(
  descriptor: WindowTargetDescriptor
): WindowTargetContext | null {
  const model = virtualizedDocumentWindowModel;
  const controller = virtualizedDocumentWindowController;
  const main = document.querySelector<HTMLElement>("main.mm-document");
  if (model === null || controller === null || main === null) {
    return null;
  }

  const resolution = resolveWindowTarget(model, descriptor);
  if (resolution === null) {
    return null;
  }

  return readWindowTargetContext({
    controller,
    main,
    model,
    ownerWindow: window,
  }, resolution);
}

function readElementDocumentTop(element: HTMLElement): number {
  return element.getBoundingClientRect().top + getDocumentScrollRoot().scrollTop;
}

function readVirtualizedTargetLocalOffset(
  context: WindowTargetContext,
  descriptor: WindowTargetDescriptor
): number {
  const sectionElement = context.element;
  if (sectionElement === null) {
    return 0;
  }

  if (descriptor.kind === "source-line") {
    refreshSourceLineAnchors();
    const sourceLineTop = findScrollTopForSourceLine(sourceLineAnchors, descriptor.sourceLine);
    if (sourceLineTop !== null) {
      return Math.max(0, sourceLineTop - readElementDocumentTop(sectionElement));
    }
  }

  const targetElement = context.targetElement ?? sectionElement;
  return Math.max(
    0,
    targetElement.getBoundingClientRect().top - sectionElement.getBoundingClientRect().top
  );
}

function computeVirtualizedProgrammaticNavigationScrollTop(
  context: WindowTargetContext,
  descriptor: WindowTargetDescriptor,
  viewportOffsetY: number
): number {
  const targetLocalOffset = readVirtualizedTargetLocalOffset(context, descriptor);
  const normalizedViewportOffset = Number.isFinite(viewportOffsetY) ? Math.max(0, viewportOffsetY) : 0;
  return Math.max(0, context.sectionTop + targetLocalOffset - normalizedViewportOffset);
}

function applyVirtualizedProgrammaticNavigationContext(
  context: WindowTargetContext,
  descriptor: WindowTargetDescriptor,
  viewportOffsetY: number,
  operation: VirtualizedScrollOperation
): boolean {
  if (descriptor.kind === "source-line") {
    pendingSourceLineTarget = descriptor.sourceLine;
    suppressPreviewSourceLinePost();
  }
  const scrollTop = computeVirtualizedProgrammaticNavigationScrollTop(
    context,
    descriptor,
    viewportOffsetY
  );
  writeVirtualizedProgrammaticNavigationScrollTop(operation, scrollTop, "navigation-initial");
  return true;
}

function readVirtualizedProgrammaticNavigationResidual(
  context: WindowTargetContext,
  descriptor: WindowTargetDescriptor,
  viewportOffsetY: number
): number | null {
  const sectionElement = context.element;
  if (sectionElement === null) {
    return null;
  }

  const sectionRectTop = sectionElement.getBoundingClientRect().top;
  if (!Number.isFinite(sectionRectTop)) {
    return null;
  }

  const targetLocalOffset = readVirtualizedTargetLocalOffset(context, descriptor);
  const normalizedViewportOffset = Number.isFinite(viewportOffsetY) ? Math.max(0, viewportOffsetY) : 0;
  return sectionRectTop + targetLocalOffset - normalizedViewportOffset;
}

function correctVirtualizedProgrammaticNavigationResidual(input: {
  descriptor: WindowTargetDescriptor;
  operation: VirtualizedScrollOperation;
  viewportOffsetY: number;
}): boolean {
  const context = readVirtualizedProgrammaticNavigationTargetContext(input.descriptor);
  if (context === null) {
    return false;
  }

  const residual = readVirtualizedProgrammaticNavigationResidual(
    context,
    input.descriptor,
    input.viewportOffsetY
  );
  if (residual === null) {
    return false;
  }

  if (Math.abs(residual) > VIRTUALIZED_NAVIGATION_CORRECTION_TOLERANCE_PX) {
    const root = getDocumentScrollRoot();
    const nextScrollTop = Math.max(0, root.scrollTop + residual);
    writeVirtualizedProgrammaticNavigationScrollTop(input.operation, nextScrollTop, "navigation-residual");
  }

  return true;
}

function applyVirtualizedRenderedHeightAdoptionEffects(
  result: MeasuredHeightUpdateResult,
  options: { alignPostSettleTarget?: boolean; scheduleCalibration?: boolean } = {}
): void {
  if (!hasMeasuredHeightGeometryDelta(result)) {
    return;
  }

  invalidateTopVisibleBlockIndexCache();
  invalidateSourceLineAnchors({
    reassertPendingTarget: options.alignPostSettleTarget !== false,
  });
  refreshVirtualizedFindHighlights();
  if (getModelMinimapSource() !== null && minimapSourceReady) {
    syncModelMinimapCloneMetadata();
    updateMinimapViewport({ skipVisibilityUpdate: true });
  }
  if (options.alignPostSettleTarget !== false) {
    alignVirtualizedProgrammaticNavigationPostSettleTarget();
  }
  if (options.scheduleCalibration !== false) {
    scheduleVirtualizedCalibration();
  }
  postPerfMark("mm-virt-window-height-adopted", {
    maxAbsDelta: result.maxAbsDelta,
    totalHeight: virtualizedDocumentWindowModel?.getTotalHeight() ?? null,
    totalDelta: result.totalDelta,
    updatedCount: result.updatedCount,
  });
}

function hasMeasuredHeightGeometryDelta(result: MeasuredHeightUpdateResult): boolean {
  return result.maxAbsDelta > Number.EPSILON || Math.abs(result.totalDelta) > Number.EPSILON;
}

function adoptVirtualizedProgrammaticNavigationRenderedHeights(
  context: WindowTargetContext,
  operation: VirtualizedScrollOperation
): boolean {
  const controller = virtualizedDocumentWindowController;
  if (!virtualizationEnabled || controller === null) {
    return false;
  }

  const ticket = beginVirtualizedGeometryWork("measured-height-adoption");
  try {
    const result = controller.adoptRenderedHeights({
      operation,
      preserveSectionIndex: context.sectionIndex,
      reanchor: false,
    });
    if (hasMeasuredHeightGeometryDelta(result)) {
      mutateVirtualizedGeometry(ticket);
    }
    applyVirtualizedRenderedHeightAdoptionEffects(result, {
      alignPostSettleTarget: false,
      scheduleCalibration: false,
    });
    return hasMeasuredHeightGeometryDelta(result);
  } finally {
    endVirtualizedGeometryWork(ticket);
  }
}

function releaseVirtualizedProgrammaticNavigationOperation(
  operation: VirtualizedScrollOperation,
  clearPostSettleTarget = false
): void {
  virtualizedProgrammaticNavigationInProgress = false;
  virtualizedProgrammaticNavigationOperation = null;
  if (clearPostSettleTarget) {
    virtualizedProgrammaticNavigationPostSettleTarget = null;
  }
  releaseVirtualizedScrollOperation(operation);
}

function finishVirtualizedProgrammaticNavigationCorrection(
  generation: number,
  operation: VirtualizedScrollOperation,
  input: {
    descriptor: WindowTargetDescriptor;
    passCount: number;
    residual: number | null;
  }
): void {
  if (generation !== virtualizedProgrammaticNavigationGeneration || !operation.isCurrent()) {
    return;
  }

  postPerfMark("mm-virt-navigation-settled", {
    descriptorKind: input.descriptor.kind,
    externalShiftCount: virtualizedProgrammaticNavigationExternalShiftCount,
    passCount: input.passCount,
    residual: input.residual,
  });
  updateMinimapViewport({ skipVisibilityUpdate: true });
  postScroll();
  releaseVirtualizedProgrammaticNavigationOperation(operation);
}

type VirtualizedNavigationFrameOutcome =
  | { kind: "canceled" }
  | { kind: "geometry-changed" }
  | { kind: "missing-target" }
  | { kind: "nominal"; residual: number }
  | { kind: "non-converged"; residual: number }
  | { kind: "written"; receipt: ScrollWriteReceipt; residual: number };

function scheduleVirtualizedProgrammaticNavigationFrame(
  input: { descriptor: WindowTargetDescriptor; viewportOffsetY: number },
  generation: number,
  operation: VirtualizedScrollOperation,
  settlement: CurrentGeometrySettlement,
  pass: number,
  previousResidualAbs = Number.POSITIVE_INFINITY
): Promise<VirtualizedNavigationFrameOutcome> {
  return new Promise(resolve => {
    const attempt = (): void => {
      if (!operation.isCurrent() || generation !== virtualizedProgrammaticNavigationGeneration) {
        resolve({ kind: "canceled" });
        return;
      }
      const scheduled = operation.scheduleFrameTransaction(() => {
        if (!operation.isCurrent() || generation !== virtualizedProgrammaticNavigationGeneration) {
          resolve({ kind: "canceled" });
          return;
        }
        const root = getDocumentScrollRoot();
        let context = readVirtualizedProgrammaticNavigationTargetContext(input.descriptor);
        if (context === null) {
          resolve({ kind: "missing-target" });
          return;
        }
        adoptVirtualizedProgrammaticNavigationRenderedHeights(context, operation);
        context = readVirtualizedProgrammaticNavigationTargetContext(input.descriptor);
        if (context === null) {
          resolve({ kind: "missing-target" });
          return;
        }
        const plane = scrollOwnershipControlPlane;
        if (plane === null || !plane.holds(operation.lease, settlement.payload.geometryEpoch)) {
          resolve({ kind: "geometry-changed" });
          return;
        }
        const residual = readVirtualizedProgrammaticNavigationResidual(
          context,
          input.descriptor,
          input.viewportOffsetY
        );
        if (residual === null) {
          resolve({ kind: "missing-target" });
          return;
        }
        postPerfMark("mm-virt-residual-read", {
          currentGeometryEpoch: plane.captureGeometryEpoch(),
          descriptorKind: input.descriptor.kind,
          eventGeometryEpoch: settlement.payload.geometryEpoch,
          operationEpoch: operation.operationEpoch,
          residual,
        });
        const residualAbs = Math.abs(residual);
        if (residualAbs <= VIRTUALIZED_NAVIGATION_CORRECTION_TOLERANCE_PX) {
          resolve({ kind: "nominal", residual });
          return;
        }
        if (
          pass >= VIRTUALIZED_NAVIGATION_CORRECTION_MAX_PASSES
          || (pass > 0 && residualAbs >= previousResidualAbs - VIRTUALIZED_NAVIGATION_CORRECTION_MIN_SHRINK_PX)
        ) {
          resolve({ kind: "non-converged", residual });
          return;
        }
        writeVirtualizedProgrammaticNavigationScrollTop(
          operation,
          root.scrollTop + residual,
          "navigation-residual"
        );
        const receipt = virtualizedWriteReceipts.get(operation.operationEpoch);
        if (receipt === undefined) {
          resolve({ kind: "canceled" });
          return;
        }
        resolve({ kind: "written", receipt, residual });
      });
      if (!scheduled) {
        window.requestAnimationFrame(attempt);
      }
    };
    attempt();
  });
}

async function settleVirtualizedProgrammaticNavigationTarget(
  input: { descriptor: WindowTargetDescriptor; viewportOffsetY: number },
  generation: number,
  operation: VirtualizedScrollOperation
): Promise<void> {
  let afterEmission = 0;
  let pass = 0;
  let previousResidualAbs = Number.POSITIVE_INFINITY;
  let settlement: CurrentGeometrySettlement | null = null;
  while (generation === virtualizedProgrammaticNavigationGeneration && operation.isCurrent()) {
    if (settlement === null) {
      const outcome = await waitForCurrentVirtualizedGeometry(operation, afterEmission);
      if (outcome.status === "canceled") {
        return;
      }
      settlement = outcome;
    }
    const frame = await scheduleVirtualizedProgrammaticNavigationFrame(
      input,
      generation,
      operation,
      settlement,
      pass,
      previousResidualAbs
    );
    if (frame.kind === "canceled") {
      return;
    }
    if (frame.kind === "missing-target") {
      releaseVirtualizedProgrammaticNavigationOperation(operation, true);
      return;
    }
    if (frame.kind === "geometry-changed") {
      afterEmission = settlement.emission;
      settlement = null;
      continue;
    }
    if (frame.kind === "non-converged") {
      postPerfMark("mm-virt-navigation-failed", {
        descriptorKind: input.descriptor.kind,
        geometryEpoch: settlement.payload.geometryEpoch,
        passCount: pass,
        reason: "residual-non-converged",
        residual: frame.residual,
      });
      releaseVirtualizedProgrammaticNavigationOperation(operation, true);
      return;
    }
    if (frame.kind === "written") {
      const write = await frame.receipt.result;
      if (write.status !== "committed") {
        return;
      }
      previousResidualAbs = Math.abs(frame.residual);
      pass++;
      afterEmission = settlement.emission;
      settlement = null;
      continue;
    }
    const confirmation = await awaitConfirmedVirtualizedGeometry(operation, settlement);
    if (confirmation.status === "canceled") {
      return;
    }
    if (confirmation.status === "changed") {
      settlement = confirmation.settlement;
      continue;
    }
    const plane = scrollOwnershipControlPlane;
    if (!operation.isCurrent() || generation !== virtualizedProgrammaticNavigationGeneration) {
      return;
    }
    if (plane?.holds(operation.lease, confirmation.confirmation.payload.geometryEpoch) !== true) {
      afterEmission = confirmation.confirmation.emission;
      settlement = null;
      continue;
    }
    finishVirtualizedProgrammaticNavigationCorrection(generation, operation, {
      descriptor: input.descriptor,
      passCount: pass,
      residual: frame.residual,
    });
    return;
  }
}

function landVirtualizedProgrammaticNavigation(input: {
  context: WindowTargetContext;
  descriptor: WindowTargetDescriptor;
  operation: VirtualizedScrollOperation;
  viewportOffsetY: number;
}): void {
  startVirtualizedProgrammaticNavigationSettle({
    descriptor: input.descriptor,
    initialContext: input.context,
    operation: input.operation,
    viewportOffsetY: input.viewportOffsetY,
  });
}

function tryRestoreVirtualizedReadingAnchor(
  readingAnchor: ReadingAnchor | null,
  fallbackBlockIndex: number | null
): boolean {
  const anchor = readingAnchor ?? (
    fallbackBlockIndex !== null && Number.isFinite(fallbackBlockIndex)
      ? { blockIndex: fallbackBlockIndex, intraOffsetPx: 0 }
      : null
  );
  if (
    !virtualizationEnabled
    || anchor === null
    || virtualizedDocumentWindowModel === null
    || virtualizedDocumentWindowController === null
  ) {
    return false;
  }

  const main = document.querySelector<HTMLElement>("main.mm-document");
  if (main === null) {
    return false;
  }
  const operation = acquireVirtualizedScrollOperation("cache-restore", "supersede-programmatic");
  if (operation === null) {
    return false;
  }

  const descriptor: WindowTargetDescriptor = { kind: "block", blockIndex: anchor.blockIndex };
  void renderWindowTargetThenAct({
    action: () => {
      operation.requestScrollTop(
        scrollTopForReadingAnchor(virtualizedDocumentWindowModel!, anchor) ?? 0,
        "cache-restore-retry"
      );
      return true;
    },
    actionKind: "navigate",
    controller: virtualizedDocumentWindowController,
    descriptor,
    legacyAction: () => {
      operation.requestScrollTop(0, "cache-restore-retry-anchor-missing");
      return true;
    },
    main,
    model: virtualizedDocumentWindowModel,
    operation,
    ownerWindow: window,
    root: getDocumentScrollRoot(),
    virtualizationEnabled,
  }).then(() => {
    releaseVirtualizedScrollOperationAfterWrite(operation);
  }).catch(error => {
    postPerfMark("mm-virt-cache-restore-retry-error", {
      message: error instanceof Error ? error.message : String(error),
    });
    releaseVirtualizedScrollOperationAfterWrite(operation);
  });
  return true;
}

function rememberVirtualizedProgrammaticNavigationPostSettleTarget(input: {
  descriptor: WindowTargetDescriptor;
  viewportOffsetY: number;
}): void {
  virtualizedProgrammaticNavigationPostSettleTarget = input;
}

function clearVirtualizedProgrammaticNavigationPostSettleTarget(): void {
  virtualizedProgrammaticNavigationPostSettleTarget = null;
}

function cancelVirtualizedProgrammaticNavigationState(): void {
  virtualizedProgrammaticNavigationInProgress = false;
  virtualizedProgrammaticNavigationGeneration++;
  virtualizedProgrammaticNavigationOperation = null;
  virtualizedProgrammaticNavigationPostSettleTarget = null;
}

function reassertVirtualizedProgrammaticNavigationPostSettleTarget(): void {
  const target = virtualizedProgrammaticNavigationPostSettleTarget;
  if (target === null) {
    return;
  }

  const operation = virtualizedProgrammaticNavigationOperation;
  if (operation === null || !operation.isCurrent()) {
    return;
  }
  forceRenderVirtualizedNavigationTarget(target.descriptor);
  correctVirtualizedProgrammaticNavigationResidual({ ...target, operation });
}

function alignVirtualizedProgrammaticNavigationPostSettleTarget(): void {
  const target = virtualizedProgrammaticNavigationPostSettleTarget;
  const operation = virtualizedProgrammaticNavigationOperation;
  if (target !== null && operation !== null && operation.isCurrent()) {
    correctVirtualizedProgrammaticNavigationResidual({ ...target, operation });
  }
}

function getVirtualizedProgrammaticNavigationPostSettleSectionIndex(): number | null {
  const target = virtualizedProgrammaticNavigationPostSettleTarget;
  return target === null ? null : resolveVirtualizedNavigationTargetSectionIndex(target.descriptor);
}

function startVirtualizedProgrammaticNavigationSettle(input: {
  descriptor: WindowTargetDescriptor;
  initialContext?: WindowTargetContext;
  operation: VirtualizedScrollOperation;
  viewportOffsetY: number;
}): void {
  if (!virtualizationEnabled || virtualizedDocumentWindowModel === null || virtualizedDocumentWindowController === null) {
    return;
  }

  const generation = ++virtualizedProgrammaticNavigationGeneration;
  virtualizedProgrammaticNavigationInProgress = true;
  virtualizedProgrammaticNavigationOperation = input.operation;
  virtualizedProgrammaticNavigationExternalShiftCount = 0;
  cancelVirtualizedCalibration();
  if (input.initialContext !== undefined) {
    applyVirtualizedProgrammaticNavigationContext(
      input.initialContext,
      input.descriptor,
      input.viewportOffsetY,
      input.operation
    );
  }
  rememberVirtualizedProgrammaticNavigationPostSettleTarget(input);
  void settleVirtualizedProgrammaticNavigationTarget(input, generation, input.operation);
}

function cancelVirtualizedCalibration(): void {
  if (virtualizedCalibrationHandle !== null) {
    if (virtualizedCalibrationHandle.kind === "idle") {
      (window as Window & { cancelIdleCallback?: (id: number) => void }).cancelIdleCallback?.(virtualizedCalibrationHandle.id);
    } else {
      window.clearTimeout(virtualizedCalibrationHandle.id);
    }
    virtualizedCalibrationHandle = null;
  }
  endVirtualizedGeometryWork(virtualizedCalibrationGeometryTicket);
  virtualizedCalibrationGeometryTicket = null;
}

function resetVirtualizedDocumentWindow(resetCalibrator = true): void {
  cancelVirtualizedCalibration();
  if (virtualizedDocumentWindowModel !== null) {
    cancelModelRenderedContentCoordinator("stale-model");
  }
  virtualizedWindowMathController?.cancel();
  virtualizedWindowMathController = null;
  virtualizedDocumentWindowController?.dispose();
  virtualizedDocumentWindowController = null;
  virtualizedDocumentWindowModel = null;
  endVirtualizedGeometryWork(virtualizedMeasuredHeightGeometryTicket);
  virtualizedMeasuredHeightGeometryTicket = null;
  finishVirtualizedMeasuredHeightTerminalSubscribers();
  endVirtualizedGeometryWork(virtualizedWindowFontGeometryTicket);
  virtualizedWindowFontGeometryTicket = null;
  virtualizedWindowMountGeneration++;
  virtualizedMeasureFrameRequested = false;
  virtualizedProgrammaticNavigationInProgress = false;
  virtualizedProgrammaticNavigationGeneration++;
  virtualizedProgrammaticNavigationOperation = null;
  virtualizedProgrammaticNavigationExternalShiftCount = 0;
  virtualizedProgrammaticNavigationPostSettleTarget = null;
  if (resetCalibrator) {
    virtualizedIntrinsicCalibrator.reset();
  }
}

function refreshVirtualizedFindHighlights(): void {
  virtualizedFindProvider?.refreshVisibleHighlights();
}

function prepareVirtualizedInsertedContent(root: ParentNode, mountGeneration: number): void {
  const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
  renderCodeBlocks(root);
  disconnectMermaidLazyObserver();
  mermaidRenderGeneration++;

  virtualizedWindowMathController?.cancel();
  const mathGeometryTicket = root.querySelector(".math-inline, .math-display") === null
    ? null
    : beginVirtualizedGeometryWork("window-math", mountGeneration);
  const mathController = renderMathInit({
    katex: hostWindow.katex ?? undefined,
    documentRoot: root,
  });
  virtualizedWindowMathController = mathController;

  const scheduleAfterRichContent = () => {
    if (
      virtualizedWindowMathController !== mathController
      || mathController.isCancelled()
      || mountGeneration !== virtualizedWindowMountGeneration
      || documentEpoch === undefined
      || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true
    ) {
      return;
    }

    invalidateSourceLineAnchors({
      reassertPendingTarget: virtualizedProgrammaticNavigationPostSettleTarget === null,
    });
    refreshVirtualizedFindHighlights();
    scheduleVirtualizedMeasuredHeightAdoption();
  };
  mathController.initialVisibleReady.then(scheduleAfterRichContent, scheduleAfterRichContent);
  const finishMathGeometry = () => {
    scheduleAfterRichContent();
    if (
      mathGeometryTicket !== null
      && virtualizedWindowMathController === mathController
      && !mathController.isCancelled()
      && mountGeneration === virtualizedWindowMountGeneration
      && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(mathGeometryTicket.documentEpoch) === true
    ) {
      mutateVirtualizedGeometry(mathGeometryTicket);
      scheduleVirtualizedMeasuredHeightAdoption();
    }
    endVirtualizedGeometryWork(mathGeometryTicket);
  };
  mathController.allMathRendered.then(finishMathGeometry, finishMathGeometry);

  const mermaid = hostWindow.mermaid;
  if (!mermaid) {
    return;
  }

  const mermaidNodes = Array.from(root.querySelectorAll<HTMLElement>("pre.mm-mermaid"));
  if (mermaidNodes.length === 0) {
    return;
  }

  void renderMermaidNodes(
    mermaidNodes,
    mermaid,
    "mm-mermaid-virt-window",
    "window-mermaid",
    mountGeneration
  )
    .finally(scheduleAfterRichContent);
}

function scheduleVirtualizedWindowFontReadiness(mountGeneration: number): void {
  if (!virtualizationEnabled || mountGeneration !== virtualizedWindowMountGeneration) {
    return;
  }
  endVirtualizedGeometryWork(virtualizedWindowFontGeometryTicket);
  const ticket = beginVirtualizedGeometryWork("window-fonts", mountGeneration);
  virtualizedWindowFontGeometryTicket = ticket;
  const ready = document.fonts?.ready;
  if (ready === undefined) {
    endVirtualizedGeometryWork(ticket);
    if (virtualizedWindowFontGeometryTicket === ticket) {
      virtualizedWindowFontGeometryTicket = null;
    }
    return;
  }

  const finish = () => {
    if (virtualizedWindowFontGeometryTicket !== ticket) {
      endVirtualizedGeometryWork(ticket);
      return;
    }
    if (
      ticket !== null
      && mountGeneration === virtualizedWindowMountGeneration
      && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(ticket.documentEpoch) === true
    ) {
      mutateVirtualizedGeometry(ticket);
      scheduleVirtualizedMeasuredHeightAdoption(() => {
        endVirtualizedGeometryWork(ticket);
        if (virtualizedWindowFontGeometryTicket === ticket) {
          virtualizedWindowFontGeometryTicket = null;
        }
      });
      return;
    }
    endVirtualizedGeometryWork(ticket);
    if (virtualizedWindowFontGeometryTicket === ticket) {
      virtualizedWindowFontGeometryTicket = null;
    }
  };
  void ready.then(finish, finish);
}

function initializeVirtualizedDocumentWindow(): void {
  if (!virtualizationEnabled) {
    return;
  }

  const main = document.querySelector<HTMLElement>("main.mm-document");
  if (!main) {
    resetVirtualizedDocumentWindow(false);
    return;
  }

  const blocks = collectLiveDocumentSectionElements(main);
  if (blocks.length === 0) {
    resetVirtualizedDocumentWindow(false);
    return;
  }

  const root = getDocumentScrollRoot();
  const models = buildDocumentWindowModelsFromLiveBlocks(
    blocks,
    readIntrinsicSizeMetrics(main),
    root.scrollHeight,
    { intrinsicSizeCalibrator: virtualizedIntrinsicCalibrator });
  virtualizedDocumentWindowModel = models.estimateOnlyModel;
  const documentEpoch = scrollOwnershipControlPlane!.captureDocumentEpoch();
  virtualizedDocumentWindowController = createVirtualizedDocumentWindowController({
    beginWindowGeometryWork: mountGeneration => {
      const ticket = beginVirtualizedGeometryWork("window-render", mountGeneration);
      return ticket === null ? null : {
        end: () => { endVirtualizedGeometryWork(ticket); },
        mutated: () => { mutateVirtualizedGeometry(ticket); },
      };
    },
    documentEpoch,
    isCurrentDocumentEpoch: epoch => scrollOwnershipControlPlane?.isCurrentDocumentEpoch(epoch) === true,
    main,
    model: virtualizedDocumentWindowModel,
    ownerWindow: window,
    onRealizationReady: mountGeneration => {
      if (mountGeneration === virtualizedWindowMountGeneration) {
        scheduleVirtualizedMeasuredHeightAdoption();
      }
    },
    onWindowMounted: mountGeneration => {
      virtualizedWindowMountGeneration = mountGeneration;
      rebuildActiveHeadingObserverFromLiveDocument();
      scheduleVirtualizedWindowFontReadiness(mountGeneration);
    },
    prepareInsertedContent: prepareVirtualizedInsertedContent,
    readMeasuredHeights: readLiveBlockOffsetMeasuredHeights,
    // The delegated contentvisibilityautostatechange listener is installed only
    // by this flag-on controller path.
    realization: { enabled: true },
    root,
  });

  const initialOperation = acquireVirtualizedScrollOperation("initial-window", "supersede-programmatic");
  if (initialOperation !== null) {
    const controller = virtualizedDocumentWindowController;
    pendingInitialVirtualizedWindowWork = operation => {
      if (!operation.isCurrent() || controller !== virtualizedDocumentWindowController) {
        return;
      }
      controller.updateWindowForScroll();
      refreshVirtualizedFindHighlights();
      invalidateTopVisibleBlockIndexCache();
      scheduleVirtualizedMeasuredHeightAdoption();
    };
    if (!initialOperation.scheduleFrameTransaction(() => {
      consumePendingInitialVirtualizedWindow(initialOperation);
      releaseVirtualizedScrollOperationAfterWrite(initialOperation);
    })) {
      releaseVirtualizedScrollOperation(initialOperation);
    }
  }
  postPerfMark("mm-virt-window-built", {
    estimateMeanAbsError: models.estimateHeightError.meanAbsError,
    sectionCount: virtualizedDocumentWindowModel.getSectionCount(),
    totalHeight: virtualizedDocumentWindowModel.getTotalHeight(),
  });
}

function updateVirtualizedWindowForScroll(options: { force?: boolean } = {}): void {
  if (!virtualizationEnabled || virtualizedDocumentWindowController === null) {
    return;
  }

  if (options.force !== true && isVirtualizedProgrammaticNavigationInProgress()) {
    return;
  }

  const controller = virtualizedDocumentWindowController;
  scheduleVirtualizedMaintenance("scroll-window", operation => {
    if (controller !== virtualizedDocumentWindowController) {
      return;
    }
    if (controller.updateWindowForScroll({ ...options, operation })) {
      invalidateTopVisibleBlockIndexCache();
      invalidateSourceLineAnchors({
        reassertPendingTarget: virtualizedProgrammaticNavigationPostSettleTarget === null,
      });
      refreshVirtualizedFindHighlights();
      scheduleVirtualizedMeasuredHeightAdoption();
    }
  });
}

function finishVirtualizedMeasuredHeightTerminalSubscribers(): void {
  const subscribers = [...virtualizedMeasuredHeightTerminalSubscribers];
  virtualizedMeasuredHeightTerminalSubscribers.clear();
  for (const subscriber of subscribers) {
    subscriber();
  }
}

function scheduleVirtualizedMeasuredHeightAdoption(onTerminal?: () => void): void {
  if (!virtualizationEnabled || virtualizedDocumentWindowController === null) {
    onTerminal?.();
    return;
  }

  if (onTerminal !== undefined) {
    virtualizedMeasuredHeightTerminalSubscribers.add(onTerminal);
  }

  if (virtualizedMeasureFrameRequested) {
    return;
  }

  virtualizedMeasureFrameRequested = true;
  const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
  endVirtualizedGeometryWork(virtualizedMeasuredHeightGeometryTicket);
  const ticket = beginVirtualizedGeometryWork("measured-height-adoption");
  virtualizedMeasuredHeightGeometryTicket = ticket;
  window.requestAnimationFrame(() => {
    virtualizedMeasureFrameRequested = false;
    if (documentEpoch === undefined || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
      endVirtualizedGeometryWork(ticket);
      if (virtualizedMeasuredHeightGeometryTicket === ticket) {
        virtualizedMeasuredHeightGeometryTicket = null;
      }
      finishVirtualizedMeasuredHeightTerminalSubscribers();
      return;
    }
    adoptVirtualizedRenderedHeights(ticket);
  });
}

function adoptVirtualizedRenderedHeights(ticket: GeometryWorkTicket | null): void {
  if (!virtualizationEnabled || virtualizedDocumentWindowController === null) {
    endVirtualizedGeometryWork(ticket);
    if (virtualizedMeasuredHeightGeometryTicket === ticket) {
      virtualizedMeasuredHeightGeometryTicket = null;
    }
    finishVirtualizedMeasuredHeightTerminalSubscribers();
    return;
  }

  const controller = virtualizedDocumentWindowController;
  const closeTicket = (terminal?: VirtualizedMaintenanceTerminal): void => {
    endVirtualizedGeometryWork(ticket);
    if (virtualizedMeasuredHeightGeometryTicket === ticket) {
      virtualizedMeasuredHeightGeometryTicket = null;
    }
    if (terminal?.reason !== "coalesced") {
      finishVirtualizedMeasuredHeightTerminalSubscribers();
    }
  };
  const scheduled = scheduleVirtualizedMaintenance("measured-height-adoption", operation => {
    if (controller !== virtualizedDocumentWindowController) {
      return;
    }
    const postSettleTarget = virtualizedProgrammaticNavigationPostSettleTarget;
    const preserveSectionIndex = getVirtualizedProgrammaticNavigationPostSettleSectionIndex();
    const result = controller.adoptRenderedHeights(
      preserveSectionIndex === null
        ? { operation }
        : {
          operation,
          preserveSectionIndex,
          ...(postSettleTarget === null ? {} : { reanchor: false }),
        }
    );
    if (hasMeasuredHeightGeometryDelta(result)) {
      mutateVirtualizedGeometry(ticket);
    }
    applyVirtualizedRenderedHeightAdoptionEffects(result, {
      alignPostSettleTarget: postSettleTarget === null,
    });
    if (postSettleTarget !== null && operation.isCurrent()) {
      correctVirtualizedProgrammaticNavigationResidual({
        ...postSettleTarget,
        operation,
      });
    }
  }, closeTicket);
  if (!scheduled) {
    closeTicket();
  }
}

function scheduleVirtualizedCalibration(): void {
  if (!virtualizationEnabled || virtualizedDocumentWindowModel === null) {
    return;
  }

  if (virtualizedCalibrationGeometryTicket !== null) {
    return;
  }

  const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
  const ticket = beginVirtualizedGeometryWork("calibration");
  virtualizedCalibrationGeometryTicket = ticket;
  if (documentEpoch === undefined || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
    endVirtualizedGeometryWork(ticket);
    virtualizedCalibrationGeometryTicket = null;
    return;
  }
  runVirtualizedCalibration(documentEpoch, ticket);
}

function runVirtualizedCalibration(documentEpoch: number, ticket: GeometryWorkTicket | null): void {
  const model = virtualizedDocumentWindowModel;
  const controller = virtualizedDocumentWindowController;
  if (!virtualizationEnabled || model === null || controller === null) {
    endVirtualizedGeometryWork(ticket);
    if (virtualizedCalibrationGeometryTicket === ticket) {
      virtualizedCalibrationGeometryTicket = null;
    }
    return;
  }

  const closeTicket = (): void => {
    endVirtualizedGeometryWork(ticket);
    if (virtualizedCalibrationGeometryTicket === ticket) {
      virtualizedCalibrationGeometryTicket = null;
    }
  };
  const scheduled = scheduleVirtualizedMaintenance("calibration", operation => {
    if (
      !operation.isCurrent()
      || operation.documentEpoch !== documentEpoch
      || model !== virtualizedDocumentWindowModel
      || controller !== virtualizedDocumentWindowController
    ) {
      return;
    }
    const preserveSectionIndex = getVirtualizedProgrammaticNavigationPostSettleSectionIndex();
    const postSettleTarget = virtualizedProgrammaticNavigationPostSettleTarget;
    const anchor = preserveSectionIndex === null ? captureCurrentVirtualizedReadingAnchor() : null;
    const recordedCount = model.recordIntrinsicSizeCalibrationSamples(virtualizedIntrinsicCalibrator);
    if (recordedCount === 0) {
      return;
    }
    const result = model.updateEstimatedHeightsFromCalibration(virtualizedIntrinsicCalibrator);
    if (!hasMeasuredHeightGeometryDelta(result)) {
      return;
    }
    mutateVirtualizedGeometry(ticket);
    const target = preserveSectionIndex !== null
      ? model.sectionTop(preserveSectionIndex)
      : scrollTopForReadingAnchor(model, anchor) ?? 0;
    controller.updateWindowForScroll({ desiredScrollTop: target, force: true });
    if (postSettleTarget === null) {
      operation.requestScrollTop(target, "calibration");
    } else {
      correctVirtualizedProgrammaticNavigationResidual({
        ...postSettleTarget,
        operation,
      });
    }
    invalidateTopVisibleBlockIndexCache();
    invalidateSourceLineAnchors({ reassertPendingTarget: postSettleTarget === null });
    postPerfMark("mm-virt-window-calibrated", {
      maxAbsDelta: result.maxAbsDelta,
      recordedCount,
      totalDelta: result.totalDelta,
      updatedCount: result.updatedCount,
    });
  }, closeTicket);
  if (!scheduled) {
    closeTicket();
  }
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
  scheduleVirtualizationShadowValidation();
}

function refreshSourceLineAnchors(): void {
  const main = getLiveDocumentRoot();
  sourceLineAnchors = main === null ? [] : readSourceLineAnchors(main);
}

function readVirtualizedModelSourceLineAnchors(): SourceLineAnchor[] {
  return virtualizedDocumentWindowModel?.getSourceLineAnchors().map(anchor => ({
    endLine: anchor.endLine,
    sourceLine: anchor.sourceLine,
    top: anchor.top,
  })) ?? [];
}

function scrollToSourceLineInCurrentWindow(sourceLine: number): void {
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

  pendingSourceLineTarget = sourceLine;
  suppressPreviewSourceLinePost();
  // ONE sync contract: place the target line at the same 38%-viewport anchor
  // the read side samples (window.scrollY + getViewportAnchorY()). Writing the
  // line to the viewport TOP while reading it at 38% made the two panes settle
  // on different chunks by exactly the anchor offset.
  window.scrollTo({
    left: 0,
    top: Math.max(0, scrollTop - getViewportAnchorY()),
    behavior: "instant" as ScrollBehavior
  });
}

function scheduleVirtualizedSourceLineLanding(
  operation: VirtualizedScrollOperation,
  sourceLine: number
): boolean {
  if (sourceLineAnchors.length === 0) {
    refreshSourceLineAnchors();
  }
  const scrollTop = findScrollTopForSourceLine(sourceLineAnchors, sourceLine);
  if (scrollTop === null) {
    releaseVirtualizedScrollOperation(operation);
    return false;
  }
  pendingSourceLineTarget = sourceLine;
  suppressPreviewSourceLinePost();
  return scheduleExistingVirtualizedOperation(operation, () => {
    operation.requestScrollTop(
      Math.max(0, scrollTop - getViewportAnchorY()),
      "source-line-live-fallback"
    );
  }, true);
}

function scrollToSourceLine(sourceLine: number): void {
  if (!Number.isFinite(sourceLine) || sourceLine < 0) {
    return;
  }

  if (!virtualizationEnabled) {
    scrollToSourceLineInCurrentWindow(sourceLine);
    return;
  }
  const operation = acquireVirtualizedScrollOperation("source-line-navigation", "supersede-programmatic");
  if (operation === null) {
    return;
  }

  const main = document.querySelector<HTMLElement>("main.mm-document");
  if (main === null || virtualizedDocumentWindowModel === null || virtualizedDocumentWindowController === null) {
    scheduleVirtualizedSourceLineLanding(operation, sourceLine);
    return;
  }

  void renderWindowTargetThenAct({
    action: (context) => {
      pendingSourceLineTarget = sourceLine;
      suppressPreviewSourceLinePost();
      landVirtualizedProgrammaticNavigation({
        context,
        descriptor: { kind: "source-line", sourceLine },
        operation,
        viewportOffsetY: getViewportAnchorY(),
      });
      return true;
    },
    actionKind: "navigate",
    controller: virtualizedDocumentWindowController,
    descriptor: { kind: "source-line", sourceLine },
    legacyAction: () => scheduleVirtualizedSourceLineLanding(operation, sourceLine),
    main,
    model: virtualizedDocumentWindowModel,
    operation,
    ownerWindow: window,
    root: getDocumentScrollRoot(),
    virtualizationEnabled: true,
  });
}

// Anchor positions are measured lazily and cached; every layout-affecting pass
// (math/mermaid inflation, resize, fonts) must invalidate so the next lookup
// re-reads REAL geometry — positions never come from stale or estimated
// heights (the reload-viewport contract philosophy).
function invalidateSourceLineAnchors(
  options: { reassertPendingTarget?: boolean } = {}
): void {
  sourceLineAnchors = [];
  // Geometry changed under a live programmatic target: re-assert it so the
  // target line returns to the anchor with FRESH measurements (idempotent —
  // re-suppresses its own echo; a real user scroll cleared the target).
  if (pendingSourceLineTarget !== null && options.reassertPendingTarget !== false) {
    const target = pendingSourceLineTarget;
    if (virtualizationEnabled) {
      if (isVirtualizedProgrammaticNavigationInProgress()) {
        return;
      }
      const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
      window.requestAnimationFrame(() => {
        if (
          pendingSourceLineTarget !== target
          || documentEpoch === undefined
          || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true
        ) {
          return;
        }
        scrollToSourceLine(target);
      });
      return;
    }

    window.requestAnimationFrame(() => {
      if (pendingSourceLineTarget === target) {
        scrollToSourceLine(target);
      }
    });
  }
}

function suppressPreviewSourceLinePost(): void {
  const sequence = ++suppressPreviewSourceLineSequence;
  const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
  suppressPreviewSourceLineEmit = true;
  window.requestAnimationFrame(() => {
    window.requestAnimationFrame(() => {
      if (
        sequence === suppressPreviewSourceLineSequence
        && (
          documentEpoch === undefined
          || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) === true
        )
      ) {
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
  const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
  window.requestAnimationFrame(() => {
    previewSourceLineFrameRequested = false;
    if (documentEpoch !== undefined && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
      return;
    }
    if (suppressPreviewSourceLineEmit || !documentScrollEnabled) {
      return;
    }

    // A scroll surviving the suppress window is REAL user scroll: the user
    // takes over, the programmatic line target dies.
    pendingSourceLineTarget = null;

    if (virtualizationEnabled) {
      updateVirtualizedWindowForScroll();
    }

    if (sourceLineAnchors.length === 0) {
      refreshSourceLineAnchors();
    }

    const documentY = window.scrollY + getViewportAnchorY();
    const sourceLine = virtualizationEnabled && virtualizedDocumentWindowModel !== null
      ? findSourceLineAtDocumentYWithFallback(
        sourceLineAnchors,
        readVirtualizedModelSourceLineAnchors,
        documentY)
      : findSourceLineAtDocumentY(sourceLineAnchors, documentY);
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

function postLayoutReady(renderId: number | null): void {
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
      ...scrollState,
      renderId
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
    ? virtualizationEnabled
      ? { ...getScrollState(), topBlockIndex: findTopVisibleBlockIndex() }
      : { ...cachedLayoutState }
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
    cached: true,
    renderId: currentDocumentRenderId
  });
  postPerfMark("mm-layout-ready", { cached: true });
  flushPostLayoutReadyWork();
  if (!virtualizationEnabled && cachedLayoutState !== null) {
    queueCachedGeometryRefresh(
      cachedLayoutState.readingAnchor ?? null,
      cachedLayoutState.topBlockIndex
    );
  }
}

function queueCachedGeometryRefresh(
  readingAnchor: ReadingAnchor | null,
  topBlockIndex: number | null
): void {
  const cacheKey = currentDocumentCacheKey;
  const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
  window.clearTimeout(cachedGeometryRefreshTimer);
  cachedGeometryRefreshTimer = window.setTimeout(() => {
    cachedGeometryRefreshTimer = undefined;
    if (
      cacheKey !== currentDocumentCacheKey
      || (
        documentEpoch !== undefined
        && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true
      )
    ) {
      return;
    }

    if (tryRestoreVirtualizedReadingAnchor(readingAnchor, topBlockIndex)) {
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
  const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
  const workItems = postLayoutReadyWorkQueue.filter(item => item.generation === flushGeneration);
  postLayoutReadyWorkQueue = postLayoutReadyWorkQueue.filter(item => item.generation !== flushGeneration);
  const delayMs = viewerChromeEnabled ? 0 : POST_LAYOUT_READY_EDIT_PREVIEW_DELAY_MS;
  if (delayMs > 0) {
    postPerfMark("post-ready-enhancements-deferred", { delayMs, viewerChromeEnabled });
  }
  window.setTimeout(() => {
    if (
      flushGeneration !== layoutReadyGeneration
      || (
        documentEpoch !== undefined
        && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true
      )
    ) {
      return;
    }

    for (const item of workItems) {
      item.work();
    }
  }, delayMs);
}

function restoreCachedScrollPosition(): void {
  const layoutState = restoredCachedLayoutState ?? lastKnownLayoutState;
  if (!virtualizationEnabled) {
    window.scrollTo({
      left: 0,
      top: layoutState.scrollTop,
      behavior: "instant" as ScrollBehavior,
    });
    updateVirtualizedWindowForScroll({ force: true });
    return;
  }
  const operation = acquireVirtualizedScrollOperation("cache-restore", "supersede-programmatic");
  if (operation === null) {
    postPerfMark("mm-virt-cache-restore-terminal", {
      reason: "lease-unavailable",
      status: "canceled",
    });
    cachedScrollRestoreCompletion = Promise.resolve();
    return;
  }
  const documentEpoch = operation.documentEpoch;
  cachedScrollRestoreCompletion = new Promise(resolve => {
    let completed = false;
    const semanticAnchorReadyReason = "semantic-anchor-agreed";
    const publishCachedRestoreReady = (
      reason: string,
      geometryStatus: "canceled" | "failed" | "non-converged" | "settled"
    ): void => {
      const liveGeometry = getScrollState();
      postPerfMark("mm-virt-cache-restore-ready-terminal", {
        documentEpoch,
        geometryStatus,
        reason,
        scrollHeight: liveGeometry.scrollHeight,
        scrollTop: liveGeometry.scrollTop,
        topBlockIndex: findTopVisibleBlockIndex(),
      });
    };
    const finish = (
      status: "canceled" | "committed" | "failed",
      reason: string,
      geometryStatus: "canceled" | "failed" | "non-converged" | "settled" = status === "committed"
        ? "settled"
        : status
    ): void => {
      if (completed) {
        return;
      }
      completed = true;
      if (finishCachedScrollRestore === finish) {
        finishCachedScrollRestore = null;
      }
      if (operation.isCurrent()) {
        releaseVirtualizedScrollOperation(operation);
      }
      const plane = scrollOwnershipControlPlane;
      if (plane?.isCurrentDocumentEpoch(documentEpoch) === true) {
        publishCachedRestoreReady(reason, geometryStatus);
      }
      postPerfMark("mm-virt-cache-restore-terminal", {
        documentEpoch,
        geometryStatus,
        reason,
        status,
      });
      resolve();
    };
    finishCachedScrollRestore = finish;
    const scheduleFrameWork = (work: () => void): Promise<boolean> => new Promise(completedWork => {
      const attempt = (): void => {
        const plane = scrollOwnershipControlPlane;
        if (plane === null || !plane.isCurrentDocumentEpoch(documentEpoch)) {
          finish("canceled", "stale-document");
          completedWork(false);
          return;
        }
        if (!operation.isCurrent()) {
          finish("canceled", "user-supersession", "canceled");
          completedWork(false);
          return;
        }
        const scheduled = operation.scheduleFrameTransaction(() => {
          if (!operation.isCurrent() || !plane.isCurrentDocumentEpoch(documentEpoch)) {
            finish("canceled", "stale-document");
            completedWork(false);
            return;
          }
          try {
            work();
            completedWork(true);
          } catch {
            finish("failed", "frame-work-failed", "failed");
            completedWork(false);
          }
        });
        if (!scheduled) {
          window.requestAnimationFrame(attempt);
        }
      };
      attempt();
    });
    const scheduleWrite = (
      target: number,
      writer: string
    ): Promise<ScrollWriteReceipt | null> => new Promise(completedWrite => {
      const attempt = (): void => {
        const plane = scrollOwnershipControlPlane;
        if (plane === null || !plane.isCurrentDocumentEpoch(documentEpoch)) {
          finish("canceled", "stale-document");
          completedWrite(null);
          return;
        }
        if (!operation.isCurrent()) {
          finish("canceled", "user-supersession", "canceled");
          completedWrite(null);
          return;
        }
        const scheduled = operation.scheduleFrameTransaction(() => {
          if (!operation.isCurrent() || !plane.isCurrentDocumentEpoch(documentEpoch)) {
            finish("canceled", "stale-document");
            completedWrite(null);
            return;
          }
          try {
            operation.requestScrollTop(target, writer);
          } catch {
            finish("failed", "frame-work-failed", "failed");
            completedWrite(null);
            return;
          }
          completedWrite(virtualizedWriteReceipts.get(operation.operationEpoch) ?? null);
        });
        if (!scheduled) {
          window.requestAnimationFrame(attempt);
        }
      };
      attempt();
    });

    void (async () => {
      const anchor = layoutState.readingAnchor ?? null;
      let model = virtualizedDocumentWindowModel;
      let controller = virtualizedDocumentWindowController;
      let entry = anchor === null ? undefined : model?.getEntryByBlockIndex(anchor.blockIndex);
      let target = model !== null && controller !== null && entry !== undefined
        ? scrollTopForReadingAnchor(model, anchor)
        : null;
      const prepared = await scheduleFrameWork(() => {
        consumePendingInitialVirtualizedWindow(operation);
        if (target !== null && entry !== undefined) {
          controller?.ensureSectionRendered(entry.sectionIndex, {
            force: true,
            operation,
            preserveAnchor: false,
          });
        }
      });
      if (!prepared || completed) {
        return;
      }

      const firstSettlement = await waitForCurrentVirtualizedGeometry(operation, 0);
      if (firstSettlement.status === "canceled") {
        const plane = scrollOwnershipControlPlane;
        if (firstSettlement.reason === "stale-document" || plane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
          finish("canceled", "stale-document");
        } else if (firstSettlement.reason === "non-converged") {
          finish("failed", "non-converged", "non-converged");
        } else {
          finish("canceled", "user-supersession", "canceled");
        }
        return;
      }

      model = virtualizedDocumentWindowModel;
      controller = virtualizedDocumentWindowController;
      entry = anchor === null ? undefined : model?.getEntryByBlockIndex(anchor.blockIndex);
      target = model !== null && controller !== null && entry !== undefined
        ? scrollTopForReadingAnchor(model, anchor)
        : null;
      const coldTop = target === null;
      const initialReceipt = await scheduleWrite(
        target ?? 0,
        coldTop ? "cache-cold-top" : "cache-restore"
      );
      if (initialReceipt === null || completed) {
        return;
      }
      const initialWrite = await initialReceipt.result;
      if (initialWrite.status !== "committed") {
        finish("failed", initialWrite.reason, "failed");
        return;
      }

      let afterEmission = firstSettlement.emission;
      let settlement: CurrentGeometrySettlement | null = null;
      while (!completed) {
        const plane = scrollOwnershipControlPlane;
        if (plane === null || !plane.isCurrentDocumentEpoch(documentEpoch)) {
          finish("canceled", "stale-document");
          return;
        }
        if (!operation.isCurrent()) {
          finish("canceled", "user-supersession", "canceled");
          return;
        }
        if (settlement === null) {
          const outcome = await waitForCurrentVirtualizedGeometry(operation, afterEmission);
          if (outcome.status === "canceled") {
            if (outcome.reason === "stale-document" || !plane.isCurrentDocumentEpoch(documentEpoch)) {
              finish("canceled", "stale-document");
            } else if (outcome.reason === "non-converged") {
              finish("failed", "non-converged", "non-converged");
            } else {
              finish("canceled", "user-supersession", "canceled");
            }
            return;
          }
          settlement = outcome;
        }
        model = virtualizedDocumentWindowModel;
        controller = virtualizedDocumentWindowController;
        entry = anchor === null ? undefined : model?.getEntryByBlockIndex(anchor.blockIndex);
        target = model !== null && controller !== null && entry !== undefined
          ? scrollTopForReadingAnchor(model, anchor)
          : null;
        const expectedTarget = target ?? 0;
        if (Math.abs(getDocumentScrollRoot().scrollTop - expectedTarget) > VIRTUALIZED_NAVIGATION_CORRECTION_TOLERANCE_PX) {
          const correctionReceipt = await scheduleWrite(
            expectedTarget,
            target === null ? "cache-cold-top" : "cache-restore-correction"
          );
          if (correctionReceipt === null || completed) {
            return;
          }
          const correction = await correctionReceipt.result;
          if (correction.status !== "committed") {
            finish("failed", correction.reason, "failed");
            return;
          }
          afterEmission = settlement.emission;
          settlement = null;
          continue;
        }
        const confirmation = await awaitConfirmedVirtualizedGeometry(operation, settlement);
        if (confirmation.status === "canceled") {
          if (confirmation.reason === "stale-document" || !plane.isCurrentDocumentEpoch(documentEpoch)) {
            finish("canceled", "stale-document");
          } else if (confirmation.reason === "non-converged") {
            finish("failed", "non-converged", "non-converged");
          } else {
            finish("canceled", "user-supersession", "canceled");
          }
          return;
        }
        if (confirmation.status === "changed") {
          settlement = confirmation.settlement;
          continue;
        }
        if (!plane.holds(operation.lease, confirmation.confirmation.payload.geometryEpoch)) {
          afterEmission = confirmation.confirmation.emission;
          settlement = null;
          continue;
        }
        finish("committed", target === null ? "cold-top-agreed" : semanticAnchorReadyReason, "settled");
        return;
      }
    })().catch(() => {
      finish("failed", "restore-pipeline-failed", "failed");
    });
  });
}

function scheduleLayoutReady(skipFrameWait = false): void {
  const generation = ++layoutReadyGeneration;
  const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
  const isCurrentLayoutDocument = () => documentEpoch === undefined
    || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) === true;
  // Capture the render this layout-ready belongs to NOW, so the host can gate a
  // stale layout-ready by renderId even when a later render mutated the global
  // currentDocumentRenderId before this scheduled callback fired.
  const scheduledRenderId = currentDocumentRenderId;
  let completed = false;
  let posted = false;
  let frameFallbackTimer: number | undefined;
  if (layoutReadyTimer !== undefined) {
    window.clearTimeout(layoutReadyTimer);
  }

  const post = (path: "skip-frame-wait" | "raf" | "frame-fallback") => {
    if (posted || generation !== layoutReadyGeneration || !isCurrentLayoutDocument()) {
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
    postLayoutReady(scheduledRenderId);
  };

  const complete = () => {
    if (completed || generation !== layoutReadyGeneration || !isCurrentLayoutDocument()) {
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
        if (generation === layoutReadyGeneration && isCurrentLayoutDocument()) {
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

function getModelMinimapSource(): DocumentWindowModel | null {
  return virtualizationEnabled ? virtualizedDocumentWindowModel : null;
}

function syncModelMinimapCloneMetadata(): void {
  const model = getModelMinimapSource();
  if (model === null) {
    return;
  }

  minimapDocumentHeight = model.getTotalHeight();
}

function getCurrentMinimapDocumentHeight(): number {
  return getModelMinimapSource()?.getTotalHeight() ?? getDocumentScrollMetrics().documentHeight;
}

function shouldBuildDetailedMinimapContent(): { allowed: boolean; reason?: string; documentHeight: number } {
  const source = document.querySelector<HTMLElement>(".mm-document");
  const metrics = getDocumentScrollMetrics();
  const documentHeight = getModelMinimapSource()?.getTotalHeight() ?? metrics.documentHeight;
  const viewportHeight = metrics.viewportHeight;
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

type MinimapCloneBlockRecord = {
  blockIndex: number;
  element: HTMLElement;
  height: number | null;
  top: number | null;
};

type MinimapCloneMetadata = {
  blocks: MinimapCloneBlockRecord[];
  blocksByIndex: Map<number, MinimapCloneBlockRecord>;
};

const minimapCloneMetadata = new WeakMap<HTMLElement, MinimapCloneMetadata>();
const minimapCloneBlockIndexes = new WeakMap<HTMLElement, number>();

function parseMinimapBlockIndex(value: string | undefined): number | null {
  if (value === undefined || value.trim() === "") {
    return null;
  }

  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : null;
}

function readPositivePxValue(value: string): number | null {
  let parsed: number | null = null;
  for (const match of value.matchAll(/([0-9]+(?:\.[0-9]+)?)px/g)) {
    const next = Number.parseFloat(match[1] ?? "");
    if (Number.isFinite(next) && next > 0) {
      parsed = next;
    }
  }
  return parsed;
}

function readMinimapSourceBlockHeight(element: HTMLElement): number | null {
  if (element.isConnected && element.offsetHeight > 0) {
    return element.offsetHeight;
  }

  return readPositivePxValue(element.style.minHeight)
    ?? readPositivePxValue(element.style.height)
    ?? readPositivePxValue(element.style.containIntrinsicSize);
}

function buildTopLevelMinimapBlockMetrics(source: HTMLElement, clone: HTMLElement): Map<HTMLElement, { height: number; top: number }> {
  const metrics = new Map<HTMLElement, { height: number; top: number }>();
  const sourceChildren = Array.from(source.children);
  const cloneChildren = Array.from(clone.children);
  let top = 0;

  for (let index = 0; index < sourceChildren.length; index++) {
    const sourceChild = sourceChildren[index];
    const cloneChild = cloneChildren[index];
    if (!(sourceChild instanceof HTMLElement) || !(cloneChild instanceof HTMLElement)) {
      continue;
    }

    const height = readMinimapSourceBlockHeight(sourceChild);
    if (height === null) {
      continue;
    }

    metrics.set(cloneChild, { height, top });
    top += height;
  }

  return metrics;
}

function registerMinimapCloneMetadata(source: HTMLElement, clone: HTMLElement): void {
  const sourceBlocks = Array.from(source.querySelectorAll<HTMLElement>("[data-mm-block-index]"));
  const cloneBlocks = Array.from(clone.querySelectorAll<HTMLElement>("[data-mm-block-index]"));
  const topLevelMetrics = buildTopLevelMinimapBlockMetrics(source, clone);
  const blocks: MinimapCloneBlockRecord[] = [];
  const blocksByIndex = new Map<number, MinimapCloneBlockRecord>();

  for (let index = 0; index < cloneBlocks.length; index++) {
    const cloneBlock = cloneBlocks[index]!;
    const sourceBlock = sourceBlocks[index];
    const blockIndex = parseMinimapBlockIndex(cloneBlock.dataset["mmBlockIndex"]);
    if (blockIndex === null) {
      continue;
    }

    const topLevelMetric = topLevelMetrics.get(cloneBlock);
    const record: MinimapCloneBlockRecord = {
      blockIndex,
      element: cloneBlock,
      height: topLevelMetric?.height ?? (sourceBlock ? readMinimapSourceBlockHeight(sourceBlock) : null),
      top: topLevelMetric?.top ?? null,
    };
    minimapCloneBlockIndexes.set(cloneBlock, blockIndex);
    blocks.push(record);
    if (!blocksByIndex.has(blockIndex)) {
      blocksByIndex.set(blockIndex, record);
    }
  }

  minimapCloneMetadata.set(clone, { blocks, blocksByIndex });
}

function getMinimapCloneBlockIndex(block: HTMLElement): number | null {
  return minimapCloneBlockIndexes.get(block) ?? parseMinimapBlockIndex(block.dataset["mmBlockIndex"]);
}

function getMinimapCloneBlockRecord(clone: HTMLElement, block: HTMLElement): MinimapCloneBlockRecord | null {
  const metadata = minimapCloneMetadata.get(clone);
  if (!metadata) {
    return null;
  }

  const blockIndex = getMinimapCloneBlockIndex(block);
  return blockIndex === null ? null : (metadata.blocksByIndex.get(blockIndex) ?? null);
}

function findMinimapCloneBlock(clone: HTMLElement, blockIndex: string): HTMLElement | null {
  const parsed = parseMinimapBlockIndex(blockIndex);
  if (parsed === null) {
    return null;
  }

  return minimapCloneMetadata.get(clone)?.blocksByIndex.get(parsed)?.element
    ?? clone.querySelector<HTMLElement>(`[data-mm-block-index="${blockIndex}"]`);
}

function getMinimapCloneBlocks(clone: HTMLElement): MinimapCloneBlockRecord[] {
  return minimapCloneMetadata.get(clone)?.blocks
    ?? Array.from(clone.querySelectorAll<HTMLElement>("[data-mm-block-index]"))
      .flatMap((element): MinimapCloneBlockRecord[] => {
        const blockIndex = getMinimapCloneBlockIndex(element);
        return blockIndex === null
          ? []
          : [{ blockIndex, element, height: null, top: null }];
      });
}

function sanitizeMinimapCloneTree(root: ParentNode): void {
  const nodes = [
    ...(root instanceof Element ? [root] : []),
    ...Array.from(root.querySelectorAll<Element>("*")),
  ];
  nodes.forEach((node) => {
    const isHtml = node.namespaceURI === "http://www.w3.org/1999/xhtml" || node.namespaceURI === null;
    if (isHtml && node.hasAttribute("id")) node.removeAttribute("id");
    if (node.hasAttribute("data-tex")) node.removeAttribute("data-tex");
    for (const attribute of Array.from(node.attributes)) {
      if (attribute.name.startsWith("data-mm-")) {
        node.removeAttribute(attribute.name);
      }
    }
    const tag = node.tagName;
    if (tag === "A" || tag === "BUTTON" || tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") {
      node.setAttribute("tabindex", "-1");
      node.removeAttribute("href");
    }
  });
}

function applyMinimapClonePaintHeightLimit(clone: HTMLElement, documentHeight: number): boolean {
  const maximumPaintHeight = minimapPolicy?.maxDetailedDocumentHeight;
  if (maximumPaintHeight === undefined || documentHeight <= maximumPaintHeight) {
    return false;
  }

  const nextMaximumHeight = `${maximumPaintHeight}px`;
  if (clone.style.maxHeight === nextMaximumHeight
    && clone.style.overflowY === "hidden"
    && clone.style.contain.includes("paint")) {
    return false;
  }

  // Explicit-on mode may request detail above the automatic policy bound, and
  // a width-matched clone may also reflow taller than its source measurement.
  // Bound the painted layer at that policy seam instead of shrinking width.
  clone.style.maxHeight = nextMaximumHeight;
  clone.style.overflowY = "hidden";
  clone.style.contain = "paint";
  return true;
}

function cloneDocumentElementForMinimap(
  source: HTMLElement,
  sourceStyle: CSSStyleDeclaration,
  documentHeight: number
): HTMLElement {
  const clone = source.cloneNode(true) as HTMLElement;
  minimapSourceReady = true;
  clone.removeAttribute("id");
  clone.setAttribute("aria-hidden", "true");
  clone.inert = true;
  clone.style.paddingTop = sourceStyle.paddingTop;
  clone.style.paddingRight = "0";
  clone.style.paddingBottom = sourceStyle.paddingBottom;
  clone.style.paddingLeft = "0";
  applyMinimapClonePaintHeightLimit(clone, documentHeight);
  registerMinimapCloneMetadata(source, clone);
  // Minimap clone invariant: HTML nodes inside `.mm-minimap-content` must not
  // carry lookup-visible identity (`id`, `data-mm-*`, `data-tex`). SVG IDs stay
  // available for paint-local references such as `url(#...)`. Text stays intact
  // because the rail is scaled real content; search paths must exclude the
  // minimap subtree at their own root/filter instead of destroying clone paint.
  sanitizeMinimapCloneTree(clone);
  return clone;
}

function cloneDocumentForMinimap(documentHeight: number): HTMLElement | null {
  const source = document.querySelector<HTMLElement>(".mm-document");
  if (!source) {
    minimapSourceReady = false;
    return null;
  }
  return cloneDocumentElementForMinimap(source, getComputedStyle(source), documentHeight);
}

function cloneModelDocumentForMinimap(model: DocumentWindowModel, documentHeight: number): HTMLElement | null {
  const liveSource = document.querySelector<HTMLElement>(".mm-document");
  if (!liveSource) {
    minimapSourceReady = false;
    return null;
  }

  const source = document.createElement(liveSource.localName) as HTMLElement;
  source.className = liveSource.className;
  source.dataset["mmMinimapSource"] = "model-fragment";
  source.dataset["mmModelMinimapSectionCount"] = String(model.getSectionCount());
  source.dataset["mmModelMinimapTotalHeight"] = String(model.getTotalHeight());
  source.append(createFullDocumentFragmentFromWindowModel(document, model));
  const clone = cloneDocumentElementForMinimap(source, getComputedStyle(liveSource), documentHeight);
  return clone;
}

function refreshMinimapContent(phase: "A" | "B" = "A"): void {
  cancelDeferredMinimapContentRefresh();
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
    releaseMinimapRenderedContentLease();
    minimapSourceReady = false;
    minimapDocumentHeight = buildDecision.documentHeight;
    currentMinimapLayout = null;
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
  currentMinimapLayout = null;
  const model = getModelMinimapSource();
  if (model !== null && model.getRenderedContentState() === "unprepared") {
    const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    if (
      minimapRenderedContentLease !== null
      && minimapRenderedContentLease.model === model
      && minimapRenderedContentLease.documentEpoch === documentEpoch
    ) {
      return;
    }
    releaseMinimapRenderedContentLease();
    const lease = acquireCurrentModelRenderedContentLease("minimap-detail");
    if (lease === null) {
      emitMark("mm-minimap-refresh-end", { phase, skipped: "model-rendered-content-unavailable" });
      postPerfMark("mm-minimap-refresh-end", { phase, skipped: "model-rendered-content-unavailable" });
      return;
    }
    minimapRenderedContentLease = lease;
    void lease.readiness.then(status => {
      if (minimapRenderedContentLease === lease) {
        minimapRenderedContentLease = null;
        lease.release();
      }
      if (
        !isTerminalModelRenderedContentStatus(status)
        || getModelMinimapSource() !== model
        || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(lease.documentEpoch) !== true
        || !shouldBuildDetailedMinimapContent().allowed
      ) {
        return;
      }
      refreshMinimapContent(phase);
    });
    return;
  }
  const clone = model === null
    ? cloneDocumentForMinimap(buildDecision.documentHeight)
    : cloneModelDocumentForMinimap(model, buildDecision.documentHeight);
  if (!clone) {
    emitMark("mm-minimap-refresh-end", { phase, skipped: "no-source" });
    postPerfMark("mm-minimap-refresh-end", { phase, skipped: "no-source" });
    return;
  }
  const root = document.scrollingElement ?? document.documentElement;
  minimapDocumentHeight = model === null ? root.scrollHeight : model.getTotalHeight();
  if (isPolicyHeavyMinimapDocument()) {
    minimapContent.style.width = `${calculateDocumentContentWidthFromCssModel(true)}px`;
  }
  minimapContent.replaceChildren(clone);
  syncModelMinimapCloneMetadata();
  updateMinimapVisibility(true);
  updateMinimapViewport({ skipVisibilityUpdate: true });
  const source = model === null ? "live-dom" : "model-fragment";
  emitMark("mm-minimap-refresh-end", { phase, documentHeight: minimapDocumentHeight, source });
  postPerfMark("mm-minimap-refresh-end", { phase, documentHeight: minimapDocumentHeight, source });
  scheduleCurrentProcessedDocumentCacheClone();
}

function ensureDetailedMinimapContentForVisiblePath(phase: "A" | "B" = "A"): void {
  if (minimapSourceReady) {
    return;
  }
  if (!shouldBuildDetailedMinimapContent().allowed) {
    releaseMinimapRenderedContentLease();
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

  minimapDocumentHeight = getCurrentMinimapDocumentHeight();
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
  const restoredClone = minimapContent?.firstElementChild;
  if (restoredClone instanceof HTMLElement) {
    applyMinimapClonePaintHeightLimit(restoredClone, restored.documentHeight);
  }
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

function addHeadingSegment(
  segments: HeadingSegmentPayload[],
  kind: HeadingSegmentPayload["kind"],
  text: string | null | undefined
): void {
  if (!text) {
    return;
  }

  const previous = segments.length > 0 ? segments[segments.length - 1] : undefined;
  if (previous?.kind === kind) {
    previous.text += text;
    return;
  }

  segments.push({ kind, text });
}

function extractHeadingSegments(root: HTMLElement): HeadingSegmentPayload[] {
  const segments: HeadingSegmentPayload[] = [];

  const visit = (node: Node): void => {
    if (node.nodeType === Node.TEXT_NODE) {
      addHeadingSegment(segments, "text", node.textContent);
      return;
    }

    if (!(node instanceof Element)) {
      return;
    }

    if (node instanceof HTMLElement && node.classList.contains("math-inline")) {
      addHeadingSegment(segments, "math", node.dataset.tex ?? node.getAttribute("data-tex") ?? node.textContent);
      return;
    }

    node.childNodes.forEach(visit);
  };

  root.childNodes.forEach(visit);
  return segments;
}

function readHeadingPayload(
  node: HTMLHeadingElement,
  metadata: { blockIndex?: number; sectionIndex?: number } = {}
): HeadingPayload | null {
  const id = node.id;
  if (!id) {
    return null;
  }

  const tag = node.tagName.toUpperCase();
  const level = Number.parseInt(tag.slice(1), 10);
  if (!Number.isFinite(level) || level < 1 || level > 6) {
    return null;
  }

  const segments = extractHeadingSegments(node);
  const text = segments.length > 0
    ? segments.map(segment => segment.text).join("").trim()
    : (node.textContent ?? "").trim();
  const heading: HeadingPayload = { id, level, text, segments };
  const includeModelMetadata = metadata.blockIndex !== undefined || metadata.sectionIndex !== undefined;
  const blockIndex = includeModelMetadata ? readClosestBlockIndex(node) ?? metadata.blockIndex : undefined;
  if (blockIndex !== undefined) {
    heading.blockIndex = blockIndex;
  }
  if (metadata.sectionIndex !== undefined) {
    heading.sectionIndex = metadata.sectionIndex;
  }
  return heading;
}

function readLiveHeadingNodes(main: HTMLElement): HTMLHeadingElement[] {
  return Array.from(
    main.querySelectorAll<HTMLHeadingElement>("h1, h2, h3, h4, h5, h6")
  );
}

function rebuildActiveHeadingObserverFromLiveDocument(): void {
  const main = document.querySelector<HTMLElement>("main.mm-document");
  const nodes = main === null ? [] : readLiveHeadingNodes(main).filter((node) => !!node.id);
  rebuildActiveHeadingObserver(nodes);
}

function readLiveHeadingPayloads(main: HTMLElement): { headings: HeadingPayload[]; nodes: HTMLHeadingElement[] } {
  const nodes = readLiveHeadingNodes(main);
  return {
    headings: nodes
      .map(node => readHeadingPayload(node))
      .filter((heading): heading is HeadingPayload => heading !== null),
    nodes,
  };
}

function readModelHeadingPayloads(model: DocumentWindowModel): HeadingPayload[] {
  const headings: HeadingPayload[] = [];
  for (const entry of model.sections) {
    if (!entry.html) {
      continue;
    }

    const template = document.createElement("template");
    template.innerHTML = entry.html;
    const nodes = Array.from(
      template.content.querySelectorAll<HTMLHeadingElement>("h1, h2, h3, h4, h5, h6")
    );
    for (const node of nodes) {
      const heading = readHeadingPayload(node, {
        blockIndex: entry.blockIndex,
        sectionIndex: entry.sectionIndex,
      });
      if (heading !== null) {
        headings.push(heading);
      }
    }
  }

  return headings;
}

function readClosestBlockIndex(node: HTMLElement): number | undefined {
  const block = node.closest<HTMLElement>("[data-mm-block-index]");
  const raw = block?.dataset["mmBlockIndex"];
  if (raw === undefined || raw.trim() === "") {
    return undefined;
  }

  const parsed = Number.parseInt(raw, 10);
  return Number.isFinite(parsed) ? parsed : undefined;
}

function extractAndPostHeadings(): void {
  const main = document.querySelector<HTMLElement>("main.mm-document");
  if (!main) {
    postHostMessage({ type: "headings-updated", headings: [] });
    lastExtractedHeadings = [];
    lastPostedActiveHeadingId = null;
    return;
  }

  const live = readLiveHeadingPayloads(main);
  const headings = virtualizationEnabled && virtualizedDocumentWindowModel !== null
    ? readModelHeadingPayloads(virtualizedDocumentWindowModel)
    : live.headings;

  lastExtractedHeadings = headings.map(cloneHeadingPayload);
  postHostMessage({ type: "headings-updated", headings });
  rebuildActiveHeadingObserver(live.nodes.filter((n) => !!n.id));
}

function postCachedHeadings(): void {
  const cachedHeadings = restoredCachedHeadings;
  restoredCachedHeadings = null;

  if (cachedHeadings === null || cachedHeadings.length === 0) {
    extractAndPostHeadings();
    return;
  }

  const headings = cachedHeadings.map(cloneHeadingPayload);
  lastExtractedHeadings = headings.map(cloneHeadingPayload);
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
  const observer = new IntersectionObserver(callback, {
    rootMargin: "0px 0px -85% 0px",
    threshold: [0, 1],
  });
  activeHeadingObserver = observer;
  for (const node of headingNodes) {
    observer.observe(node);
  }

  // Emit an initial active-heading guess so the TOC highlights the right
  // row before the user scrolls.
  const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
  window.requestAnimationFrame(() => {
    if (activeHeadingObserver !== observer) {
      return;
    }
    if (
      documentEpoch !== undefined
      && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true
    ) {
      return;
    }
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
  const metrics = getDocumentScrollMetrics();
  const documentHeight = getCurrentMinimapDocumentHeight();
  const viewportHeight = metrics.viewportHeight;
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

type MinimapViewportUpdateOptions = {
  skipVisibilityUpdate?: boolean;
};

// [block-anchor] Sum offsetTop up the offsetParent chain to the container.
// Transform-independent (offsetTop ignores the clone's scale()/translateY()).
// Returns null when the walk does not terminate at the container.
function cloneSpaceTop(el: HTMLElement, container: HTMLElement): number | null {
  const recordTop = getMinimapCloneBlockRecord(container, el)?.top;
  if (recordTop !== undefined && recordTop !== null) {
    return recordTop;
  }

  let y = 0;
  let n: HTMLElement | null = el;
  while (n && n !== container) {
    y += n.offsetTop;
    n = n.offsetParent as HTMLElement | null;
  }
  return n === container ? y : null;
}

// [block-anchor] Map a document client-Y at/above a document block to the
// clone-space Y of the same-index clone block. Within the block: fraction ×
// clone height (block heights differ doc↔clone under content-visibility). In
// the padding/gap above the block (offset <= 0): raw px, 1:1 — the clone
// restores vertical padding (renderer.ts cloneDocumentForMinimap) and inter-
// block gaps are structural copies. Returns null → caller falls back.
function cloneYForDocBlock(docBlock: HTMLElement, clone: HTMLElement, rect: DOMRect, clientY: number): number | null {
  const idx = docBlock.dataset["mmBlockIndex"];
  if (idx === undefined) return null;
  const cln = findMinimapCloneBlock(clone, idx);
  if (!cln) return null;
  const top = cloneSpaceTop(cln, clone);
  if (top === null) return null;
  const cloneHeight = getMinimapCloneBlockRecord(clone, cln)?.height ?? cln.offsetHeight;
  const offset = clientY - rect.top;
  const contribution = offset <= 0
    ? offset
    : (rect.height > 0 ? (offset / rect.height) * cloneHeight : 0);
  return top + contribution;
}

// [block-anchor forward] Clone-space Y of the document viewport's TOP edge via
// the block index shared between document and clone — drift-free under content-
// visibility (unlike root.scrollHeight). Drives the minimap POSITION only; the
// thumb HEIGHT stays on the stable document viewport height (see caller), NOT a
// clone-space viewport span: during fast ("accelerated") scroll/drag content-
// visibility lags, so on-screen blocks are collapsed in the document (~120px)
// but full in the clone (~hundreds px); a clone-space span would then inflate
// and stretch the thumb — up to the whole minimap. Null → caller falls back.
function getDocumentViewportTopCloneY(clone: HTMLElement): number | null {
  const docRoot = document.querySelector<HTMLElement>("body > main.mm-document");
  if (!docRoot) return null;
  for (const b of Array.from(docRoot.querySelectorAll<HTMLElement>("[data-mm-block-index]"))) {
    const r = b.getBoundingClientRect();
    // Skip zero-box blocks. A display:none element (e.g. a mermaid `<pre>` that is
    // hidden once its SVG has rendered) reports rect (0,0,0,0) — so rect.bottom===0
    // would FALSELY match as the top block at any scroll, and its clone twin has no
    // offsetParent, so cloneYForDocBlock returns null and the forward map drops to
    // the legacy fallback (observed: minimap leads the document at the very bottom).
    // Anchor on the first VISIBLE block whose clone twin also resolves.
    if (r.height > 0 && r.bottom >= 0) {
      const y = cloneYForDocBlock(b, clone, r, 0);
      if (y !== null) return y;
    }
  }
  return null;
}

// [block-anchor inverse] Clone block whose range contains clone-space Y (or the
// gap/tail around it). Mirror of the forward map. Returns null when the clone
// has no annotated blocks.
function cloneBlockAtCloneY(clone: HTMLElement, y: number):
    { block: HTMLElement; blockIndex: number; mode: "gap" | "frac" | "tail"; value: number } | null {
  let prev: MinimapCloneBlockRecord | null = null;
  let prevTop = 0;
  for (const record of getMinimapCloneBlocks(clone)) {
    const b = record.element;
    const top = record.top ?? cloneSpaceTop(b, clone);
    if (top === null) continue;
    const h = record.height ?? b.offsetHeight;
    if (y < top) return { block: b, blockIndex: record.blockIndex, mode: "gap", value: y - top };
    if (y < top + h) return { block: b, blockIndex: record.blockIndex, mode: "frac", value: h > 0 ? (y - top) / h : 0 };
    prev = record;
    prevTop = top;
  }
  if (prev) return {
    block: prev.element,
    blockIndex: prev.blockIndex,
    mode: "tail",
    value: y - (prevTop + (prev.height ?? prev.element.offsetHeight)),
  };
  return null;
}

// [block-anchor inverse] Document scrollTop that places clone-space Y at the
// viewport top. In frac mode the document block height may be the c-v estimate;
// the click caller refines after the target block renders. Returns null → fall back.
function docScrollTopForCloneY(root: Element, y: number): number | null {
  if (!minimapContent) return null;
  const hit = cloneBlockAtCloneY(minimapContent, y);
  if (!hit) return null;
  const idx = String(hit.blockIndex);
  const blockIndex = hit.blockIndex;
  const docBlock = document.querySelector<HTMLElement>(`body > main.mm-document [data-mm-block-index="${idx}"]`);
  let scrollTop: number;
  if (docBlock) {
    const r = docBlock.getBoundingClientRect();
    const contribution = hit.mode === "gap"
      ? hit.value
      : hit.mode === "tail"
        ? r.height + hit.value
        : hit.value * r.height;
    scrollTop = root.scrollTop + r.top + contribution;
  } else if (virtualizationEnabled && virtualizedDocumentWindowModel !== null && Number.isFinite(blockIndex)) {
    const entry = virtualizedDocumentWindowModel.getEntryContainingBlockIndex(blockIndex);
    if (entry === undefined) {
      return null;
    }

    const sectionHeight = virtualizedDocumentWindowModel.sectionEffectiveHeight(entry.sectionIndex);
    const contribution = hit.mode === "gap"
      ? hit.value
      : hit.mode === "tail"
        ? sectionHeight + hit.value
        : hit.value * sectionHeight;
    scrollTop = entry.cumulativeTop + contribution;
  } else {
    return null;
  }

  const maxScrollTop = Math.max(0, root.scrollHeight - root.clientHeight);
  return Math.max(0, Math.min(maxScrollTop, scrollTop));
}

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
  // RESTORE (455c485): progress must track the LIVE scroll range. The cached
  // minimapDocumentHeight (c-v estimate, fixed at build) goes stale as content-visibility
  // grows the document during through-scroll, freezing the minimap in the lower portion.
  const documentScrollHeight = root.scrollHeight;
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

  // Content height must be the CLONE's true rendered height (455c485). The minimap clone is
  // intentionally NOT content-visibility-covered, so it renders every block at full height;
  // root.scrollHeight is the c-v ESTIMATE and underestimates a heavy document until every
  // block has scrolled into view, which stops the content short of the bottom. Lay the clone
  // out at the source width first (a no-op on plain scroll, so the read stays cheap), then
  // measure it.
  const nextContentWidth = `${documentWidth}px`;
  if (minimapContent.style.width !== nextContentWidth) {
    minimapContent.style.width = nextContentWidth;
  }
  let measuredContentHeight = minimapContent.scrollHeight;
  const renderedClone = minimapContent.firstElementChild;
  if (renderedClone instanceof HTMLElement
    && applyMinimapClonePaintHeightLimit(renderedClone, measuredContentHeight)) {
    measuredContentHeight = minimapContent.scrollHeight;
  }
  const contentHeight = measuredContentHeight > 0 ? measuredContentHeight : documentScrollHeight;

  // Map document→clone position through the BLOCK INDEX (identical in document
  // and clone), which is drift-free under content-visibility — unlike the
  // floating root.scrollHeight, whose live reshaping made a single dimensionless
  // scrollProgress mis-place both content and thumb (runtime-measured: up to
  // ~11k px drift, sign-flipping by scroll direction). The legacy floating-
  // progress path is kept as the fallback when no block anchor is available
  // (no annotated blocks, missing/mismatched clone block during a rebuild).
  let layout: MinimapViewportLayout | null;
  // Block-anchor drives the POSITION (anchor.topY); the thumb HEIGHT stays on the
  // stable document viewport height so it cannot jump/stretch during fast drag.
  const anchorTopY = getDocumentViewportTopCloneY(minimapContent);
  if (anchorTopY !== null) {
    layout = calculateMinimapViewportLayout({
      minimapWidth,
      minimapHeight,
      documentWidth,
      documentHeight: contentHeight,
      viewportHeight,
      scrollTop: anchorTopY
    });
  } else {
    const realMaxScrollTop = Math.max(0, documentScrollHeight - viewportHeight);
    const scrollProgress = realMaxScrollTop > 0
      ? Math.min(1, Math.max(0, root.scrollTop / realMaxScrollTop))
      : 0;
    const contentScrollTop = scrollProgress * Math.max(0, contentHeight - viewportHeight);
    layout = calculateMinimapViewportLayout({
      minimapWidth,
      minimapHeight,
      documentWidth,
      documentHeight: contentHeight,
      viewportHeight,
      scrollTop: contentScrollTop
    });
  }
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

  // [block-anchor inverse] Click-jump: invert the forward minimap-space mapping
  // (rawThumbTop = scrollTop*scale + contentTranslateY) to the clone-space Y
  // under the cursor, then resolve that to the document block at that Y, so the
  // click lands on the block the user actually pointed at (the linear inverse
  // below would land on the wrong block now that the forward map is non-linear).
  // A bounded rAF settle re-aims once an off-screen content-visibility-collapsed
  // target block renders its true height (the first pass used the c-v estimate).
  // NOTE: the pan/grab DRAG (handleMinimapPointerMove) intentionally stays on the
  // legacy linear math — a continuous gesture with live feedback, no regression.
  if (currentMinimapLayout && minimapContent) {
    const cloneYTarget = (minimapY - currentMinimapLayout.contentTranslateY) / currentMinimapLayout.scale;
    const firstTarget = docScrollTopForCloneY(root, cloneYTarget);
    if (firstTarget !== null) {
      if (!virtualizationEnabled) {
        window.scrollTo({ top: firstTarget, behavior: "instant" as ScrollBehavior });
        let attempts = 0;
        const refine = () => {
          if (++attempts > 3) {
            return;
          }
          const next = docScrollTopForCloneY(root, cloneYTarget);
          if (next !== null && Math.abs(next - root.scrollTop) > 2) {
            window.scrollTo({ top: next, behavior: "instant" as ScrollBehavior });
            window.requestAnimationFrame(refine);
          }
        };
        window.requestAnimationFrame(refine);
      } else if (requestMinimapScrollTarget(firstTarget, "minimap-click")) {
        const operation = minimapScrollOperation;
        if (operation !== null) {
          void settleMinimapScrollOperation(operation, () => docScrollTopForCloneY(root, cloneYTarget));
        }
      }
      return;
    }
  }

  // Fallback (legacy linear, bit-identical to pre-block-anchor): cursor at
  // minimap_y maps linearly to scrollTop so the viewport indicator's top ends
  // up at minimap_y. Used when no block-anchor layout/clone is available.
  const thumbTravel = getCurrentMinimapThumbTravel();
  const maxScrollTop = Math.max(0, root.scrollHeight - root.clientHeight);
  const targetScrollTop = (Math.min(minimapY, thumbTravel) / thumbTravel) * maxScrollTop;
  const clamped = Math.max(0, Math.min(maxScrollTop, targetScrollTop));
  if (virtualizationEnabled) {
    if (requestMinimapScrollTarget(clamped, "minimap-click-fallback")) {
      const operation = minimapScrollOperation;
      if (operation !== null) {
        void settleMinimapScrollOperation(operation);
      }
    }
  } else {
    window.scrollTo({ top: clamped, behavior: "instant" as ScrollBehavior });
  }
}

function scrollToProgress(progressPercent: number): void {
  const root = document.scrollingElement ?? document.documentElement;
  const maximum = Math.max(0, root.scrollHeight - root.clientHeight);
  const progress = Number.isFinite(progressPercent) ? Math.max(0, Math.min(100, progressPercent)) : 0;
  if (virtualizationEnabled) {
    scheduleVirtualizedStandaloneOperation("host-progress", "supersede-as-user", operation => {
      operation.requestScrollTop(maximum * (progress / 100), "host-progress");
    });
  } else {
    window.scrollTo({ top: maximum * (progress / 100), behavior: "instant" as ScrollBehavior });
  }
}

function requestMinimapScrollTarget(target: number, writer: string): boolean {
  const operation = minimapScrollOperation;
  if (operation === null || !operation.isCurrent()) {
    return false;
  }
  operation.requestScrollTop(target, writer);
  operation.scheduleFrameTransaction(() => undefined);
  return true;
}

async function settleMinimapScrollOperation(
  operation: VirtualizedScrollOperation,
  readRefinedTarget?: () => number | null
): Promise<void> {
  let afterEmission = 0;
  let settlement: CurrentGeometrySettlement | null = null;
  while (operation.isCurrent() && minimapScrollOperation === operation) {
    if (settlement === null) {
      const outcome = await waitForCurrentVirtualizedGeometry(operation, afterEmission);
      if (outcome.status === "canceled") {
        return;
      }
      settlement = outcome;
    }
    const refinedTarget = readRefinedTarget?.() ?? null;
    if (
      refinedTarget !== null
      && Number.isFinite(refinedTarget)
      && Math.abs(refinedTarget - getDocumentScrollRoot().scrollTop) > VIRTUALIZED_NAVIGATION_CORRECTION_TOLERANCE_PX
    ) {
      if (!requestMinimapScrollTarget(refinedTarget, "minimap-refine")) {
        return;
      }
      const receipt = virtualizedWriteReceipts.get(operation.operationEpoch);
      if (receipt === undefined || (await receipt.result).status !== "committed") {
        return;
      }
      afterEmission = settlement.emission;
      settlement = null;
      continue;
    }
    const confirmation = await awaitConfirmedVirtualizedGeometry(operation, settlement);
    if (confirmation.status === "canceled") {
      return;
    }
    if (confirmation.status === "changed") {
      settlement = confirmation.settlement;
      continue;
    }
    const plane = scrollOwnershipControlPlane;
    if (plane?.holds(operation.lease, confirmation.confirmation.payload.geometryEpoch) !== true) {
      afterEmission = confirmation.confirmation.emission;
      settlement = null;
      continue;
    }
    finishMinimapScrollOperation(operation);
    return;
  }
}

function finishMinimapScrollOperation(operation = minimapScrollOperation): void {
  if (minimapScrollOperation === operation) {
    minimapScrollOperation = null;
  }
  if (operation !== null) {
    releaseVirtualizedScrollOperationAfterWrite(operation);
  }
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
  if (virtualizationEnabled) {
    minimapScrollOperation = acquireVirtualizedScrollOperation("minimap-gesture", "supersede-as-user");
  }
  minimapDragging = true;
  minimapDragStartClientY = event.clientY;
  const root = document.scrollingElement ?? document.documentElement;
  minimapDragStartScrollTop = root.scrollTop;
  minimapDragMode = "tentative";
  // Record where inside the viewport indicator the user grabbed (minimap-local Y)
  // so panning can keep that point under the cursor instead of drifting.
  minimapDragGrabOffset = 0;
  if (minimapRoot && minimapViewport) {
    const rootTop = minimapRoot.getBoundingClientRect().top;
    const thumbTop = minimapViewport.getBoundingClientRect().top - rootTop;
    minimapDragGrabOffset = (event.clientY - rootTop) - thumbTop;
  }
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

  const root = document.scrollingElement ?? document.documentElement;
  const maxScrollTop = Math.max(0, root.scrollHeight - root.clientHeight);

  // Block-anchor drag: keep the grabbed point of the viewport indicator under the
  // cursor. The indicator's top is thumbTop = anchorTopY * thumbSlope (the UNCLAMPED
  // forward map in minimapLayout.ts; thumbSlope = scale - overflowHeight/maxScroll,
  // NOT scale, because contentTranslateY itself moves with the scroll). So to put the
  // thumb top at desiredThumbTop we need anchorTopY = desiredThumbTop / thumbSlope,
  // then resolve that clone-Y to a document scrollTop through the block-index map.
  // (The earlier attempt used the click "content-under-cursor" inverse — divide by
  // scale, subtract a constant contentTranslateY — which has the WRONG slope and made
  // the drift worse the farther you dragged.)
  if (minimapRoot && minimapViewport && currentMinimapLayout && minimapContent && currentMinimapLayout.thumbSlope > 0) {
    const rootTop = minimapRoot.getBoundingClientRect().top;
    const desiredThumbTop = event.clientY - rootTop - minimapDragGrabOffset;
    const cloneY = desiredThumbTop / currentMinimapLayout.thumbSlope;
    const target = docScrollTopForCloneY(root, cloneY);
    if (target !== null) {
      if (virtualizationEnabled) {
        requestMinimapScrollTarget(Math.max(0, Math.min(maxScrollTop, target)), "minimap-drag");
      } else {
        window.scrollTo({ top: Math.max(0, Math.min(maxScrollTop, target)), behavior: "instant" as ScrollBehavior });
      }
      // Optimistic pin: paint the indicator at the cursor's clamped position THIS frame
      // so a fast flick has no one-frame lag. The scroll-driven updateMinimapViewport
      // reconciles it to the canonical thumbTop next frame (≈ identical — the slope is
      // exact), so this is a transient gesture overlay, not a second source of truth.
      const pinnedTop = Math.max(0, Math.min(currentMinimapLayout.thumbTravel, desiredThumbTop));
      minimapViewport.style.transform = `translateY(${pinnedTop}px)`;
      event.preventDefault();
      return;
    }
  }

  // Fallback (legacy linear): no block-anchor layout/clone available.
  const thumbTravel = getCurrentMinimapThumbTravel();
  const scrollDelta = delta * (maxScrollTop / thumbTravel);
  const clampedScrollTop = Math.max(0, Math.min(maxScrollTop, minimapDragStartScrollTop + scrollDelta));
  if (virtualizationEnabled) {
    requestMinimapScrollTarget(clampedScrollTop, "minimap-drag-fallback");
  } else {
    window.scrollTo({ top: clampedScrollTop, behavior: "instant" as ScrollBehavior });
  }
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
  } else {
    const operation = minimapScrollOperation;
    if (operation !== null) {
      void settleMinimapScrollOperation(operation);
    }
  }
}

function queueMinimapViewportUpdate(perfMarkName?: string): void {
  if (minimapViewportFrameRequested) {
    return;
  }

  minimapViewportFrameRequested = true;
  const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
  window.requestAnimationFrame(() => {
    minimapViewportFrameRequested = false;
    if (documentEpoch !== undefined && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true) {
      return;
    }
    updateMinimapVisibility();
    updateMinimapViewport();
    if (perfMarkName) {
      postPerfMark(perfMarkName);
    }
  });
}

function cancelMinimapRefreshAfterLayoutSettles(): void {
  if (minimapRefreshTimer !== undefined) {
    window.clearTimeout(minimapRefreshTimer);
    minimapRefreshTimer = undefined;
  }
}

function queueMinimapRefreshAfterLayoutSettles(): void {
  cancelMinimapRefreshAfterLayoutSettles();
  const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
  minimapRefreshTimer = window.setTimeout(() => {
    minimapRefreshTimer = undefined;
    if (
      documentEpoch !== undefined
      && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true
    ) {
      return;
    }
    // Stage 2 decoupling: only viewport update on resize; content rebuild
    // is driven by content-change OR Phase B (Task 15) — not by layout settling.
    queueMinimapViewportUpdate();
  }, MINIMAP_REFRESH_DEBOUNCE_MS);
}

function cancelDeferredMinimapContentRefresh(invalidate = true): void {
  if (invalidate) {
    ++progressiveMinimapRefreshGeneration;
  }

  const handle = minimapDeferredContentRefreshHandle;
  minimapDeferredContentRefreshHandle = null;
  if (!handle) {
    return;
  }

  if (handle.kind === "idle") {
    (window as Window & { cancelIdleCallback?: (id: number) => void }).cancelIdleCallback?.(handle.id);
  } else {
    window.clearTimeout(handle.id);
  }
}

function queueMinimapContentRefreshAfterLayoutSettles(phase: "A" | "B" = "A"): void {
  cancelDeferredMinimapContentRefresh();
  window.clearTimeout(minimapContentRefreshTimer);
  minimapContentRefreshTimer = window.setTimeout(() => {
    minimapContentRefreshTimer = undefined;
    refreshMinimapContent(phase);
  }, MINIMAP_REFRESH_DEBOUNCE_MS);
}

function queueProgressiveMinimapAppendRefresh(message: Extract<HostMessage, { type: "append-document" }>): void {
  if (message.html.length === 0) {
    return;
  }

  const generation = ++progressiveMinimapRefreshGeneration;
  const renderId = message.renderId;
  cancelDeferredMinimapContentRefresh(false);

  const run = () => {
    minimapDeferredContentRefreshHandle = null;
    if (generation !== progressiveMinimapRefreshGeneration) {
      return;
    }

    if (
      renderId !== undefined
      && currentDocumentRenderId !== null
      && renderId !== currentDocumentRenderId
    ) {
      postPerfMark("mm-minimap-progressive-append-stale", {
        renderId,
        currentRenderId: currentDocumentRenderId
      });
      return;
    }

    emitMark("mm-minimap-progressive-append-start", { renderId: renderId ?? null });
    postPerfMark("mm-minimap-progressive-append-start", { renderId: renderId ?? null });
    // RESTORE (455c485): rebuild the FULL clone from the completed document so it holds every
    // block. The incremental append only added the final chunk, leaving the clone stuck at its
    // early partial block count, so its measured height and content stopped far short.
    refreshMinimapContent("A");
    emitMark("mm-minimap-progressive-append-end", {
      renderId: renderId ?? null,
      documentHeight: minimapDocumentHeight,
    });
    postPerfMark("mm-minimap-progressive-append-end", {
      renderId: renderId ?? null,
      documentHeight: minimapDocumentHeight,
    });
  };

  const requestIdle = (window as Window & {
    requestIdleCallback?: (callback: () => void, options?: { timeout?: number }) => number;
  }).requestIdleCallback;
  if (requestIdle) {
    minimapDeferredContentRefreshHandle = {
      kind: "idle",
      id: requestIdle(run, { timeout: 1200 }),
    };
    return;
  }

  minimapDeferredContentRefreshHandle = {
    kind: "timeout",
    id: window.setTimeout(run, 160),
  };
}

// Coalesces resize-time reactive work to at most one synchronous-layout pass
// per frame. Both `window.addEventListener("resize", ...)` and the
// ResizeObserver watching .mm-document + body route through this. Without
// coalescing, a fast window-edge drag fires getBoundingClientRect() reads on
// every event, causing the width-handle to snap visibly each tick. The actual
// minimap viewport update is itself rAF-coalesced via queueMinimapViewportUpdate
// (single token); coalescing the synchronous-layout reads here is the missing
// piece for stable chrome positions during drag.
function scheduleResizeReactions(
  documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch()
): void {
  if (resizeReactFrameRequested) {
    return;
  }

  if (modeRevealPrepared) {
    return;
  }

  resizeReactFrameRequested = true;
  window.requestAnimationFrame(() => {
    resizeReactFrameRequested = false;
    if (
      documentEpoch !== undefined
      && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true
    ) {
      return;
    }
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
  if (!next.viewerChromeEnabled || next.minimapMode === "off") {
    releaseMinimapRenderedContentLease();
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
  if (!shouldBuildDetailedMinimapContent().allowed) {
    releaseMinimapRenderedContentLease();
  }

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
    invalidateVirtualizationShadowModel();
    if (!minimapSourceReady && shouldBuildDetailedMinimapContent().allowed) {
      queueMinimapContentRefreshAfterLayoutSettles();
    } else {
      if (!shouldBuildDetailedMinimapContent().allowed) {
        releaseMinimapRenderedContentLease();
      }
      scheduleHeavyLiveUpdate();
    }
  }

  const suppressFirstPrefsBootstrap =
    !hadHostPreferences
    && firstPrefsBootstrapSuppressedByLoadGeneration === initialRenderPipelineGeneration;
  if (!hadHostPreferences) {
    firstPrefsBootstrapSuppressedByLoadGeneration = null;
  }
  if (!hadHostPreferences && !suppressFirstPrefsBootstrap) {
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
        // Echo the C#-authoritative renderId (stored on load-document /
        // load-cached-document) so the host's reveal gate
        // (HandlePostReadyEnhancementsComplete, which rejects renderId-less
        // messages) accepts it. Without this the initial-render-pipeline path
        // posted no renderId → _postReadyEnhancementsComplete never set →
        // document-reveal-ready never fired → startup cover released only by
        // the 15s fallback (~16-18s startup on docs requiring post-ready).
        // `?? undefined` preserves the prior behavior if no load set the id.
        postPostReadyEnhancementsComplete(currentDocumentRenderId ?? undefined, undefined, undefined);
      },
      isCurrent: () => pipelineGeneration === initialRenderPipelineGeneration,
    });
  }

  // Live updates intentionally do NOT call scheduleLayoutReady(). The host's
  // SendReadingPreferences no longer resets _awaitingLayoutReady on live
  // updates, so re-emitting it would only add per-frame IPC noise.
}

function cancelHeavyLiveUpdate(): void {
  if (heavyLiveUpdateTimer !== undefined) {
    window.clearTimeout(heavyLiveUpdateTimer);
    heavyLiveUpdateTimer = undefined;
  }
}

function scheduleHeavyLiveUpdate(): void {
  cancelHeavyLiveUpdate();
  const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
  heavyLiveUpdateTimer = window.setTimeout(() => {
    heavyLiveUpdateTimer = undefined;
    if (
      documentEpoch !== undefined
      && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true
    ) {
      return;
    }
    queueMinimapViewportUpdate();
  }, HEAVY_LIVE_UPDATE_DEBOUNCE_MS);
}

function scrollLegacyHeadingAnchor(anchor: string, options: ScrollIntoViewOptions): void {
  document.getElementById(anchor)?.scrollIntoView(options);
}

function scrollToHeadingAnchor(anchor: string, options: ScrollIntoViewOptions): void {
  if (anchor.length === 0) {
    return;
  }

  if (!virtualizationEnabled) {
    scrollLegacyHeadingAnchor(anchor, options);
    return;
  }
  const operation = acquireVirtualizedScrollOperation("heading-navigation", "supersede-programmatic");
  if (operation === null) {
    return;
  }

  const main = getLiveDocumentRoot();
  if (main === null || virtualizedDocumentWindowModel === null || virtualizedDocumentWindowController === null) {
    scheduleVirtualizedElementLanding(
      operation,
      findLiveDocumentElementById(anchor),
      "heading-live-fallback"
    );
    return;
  }

  const descriptor: WindowTargetDescriptor = { anchor, kind: "heading-anchor" };
  void renderWindowTargetThenAct({
    action: (context) => {
      landVirtualizedProgrammaticNavigation({
        context,
        descriptor,
        operation,
        viewportOffsetY: 0,
      });
      return true;
    },
    actionKind: "navigate",
    controller: virtualizedDocumentWindowController,
    descriptor,
    legacyAction: () => scheduleVirtualizedElementLanding(
      operation,
      findLiveDocumentElementById(anchor),
      "heading-resolver-fallback"
    ),
    main,
    model: virtualizedDocumentWindowModel,
    operation,
    ownerWindow: window,
    root: getDocumentScrollRoot(),
    virtualizationEnabled: true,
  });
}

function readCurrentHashAnchor(): string | null {
  const hash = window.location.hash;
  if (hash.length <= 1) {
    return null;
  }

  const raw = hash.slice(1);
  try {
    return decodeURIComponent(raw);
  } catch {
    return raw;
  }
}

function handleCurrentHashNavigation(): void {
  const anchor = readCurrentHashAnchor();
  if (anchor !== null) {
    scrollToHeadingAnchor(anchor, { block: "start" });
  }
}

function handleHostMessage(raw: unknown): void {
  const message = raw as HostMessage;
  if (message.type === "host-shortcuts-reset") {
    resetHostShortcutsForModeSwitch?.();
    return;
  }

  if (message.type === "theme") {
    if (initialRenderPipelineCompleted) {
      void handleThemeChange(message.theme, message.requestId);
    } else {
      // Pre-pipeline theme — just set the attribute; the pipeline will
      // re-initialize Mermaid with the right theme when it runs.
      document.documentElement.dataset.theme = message.theme;
      postThemeAppliedAfterPaint(message.theme, message.requestId);
    }
    return;
  }

  if (message.type === "minimap-policy") {
    minimapPolicy = message.minimapPolicy;
    if (!minimapSourceReady && shouldBuildDetailedMinimapContent().allowed) {
      queueMinimapContentRefreshAfterLayoutSettles();
    } else {
      if (!shouldBuildDetailedMinimapContent().allowed) {
        releaseMinimapRenderedContentLease();
      }
      queueMinimapViewportUpdate();
    }
    return;
  }

  if (message.type === "reading-preferences") {
    applyReadingPreferences(message);
    return;
  }

  if (message.type === "scroll-to") {
    scrollToHeadingAnchor(message.anchor, { block: "start" });
    return;
  }

  if (message.type === "scroll-to-heading") {
    // Avalonia-side TOC click handler. The heading id matches the slug
    // used by MarkdownHeadingAnchorSlugger when generating <h1..h6 id="...">
    // in ApplicateHtmlMarkdownRenderer, so getElementById resolves the
    // exact heading the user clicked in the host-side TOC panel.
    scrollToHeadingAnchor(message.id, { behavior: "smooth", block: "start" });
    return;
  }

  if (message.type === "scroll-to-source-line") {
    scrollToSourceLine(message.sourceLine);
    return;
  }

  if (message.type === "find-results") {
    virtualizedFindProvider?.handleFindResults(message);
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
    if (virtualizationEnabled) {
      const root = getDocumentScrollRoot();
      scheduleVirtualizedStandaloneOperation("host-scroll-by", "supersede-as-user", operation => {
        operation.requestScrollTop(root.scrollTop + message.deltaY, "host-scroll-by");
      });
    } else {
      window.scrollBy({ top: message.deltaY, behavior: "instant" as ScrollBehavior });
    }
    return;
  }

  if (message.type === "scroll-to-block") {
    if (!virtualizationEnabled) {
      const target = document.querySelector<HTMLElement>(
        `[data-mm-block-index="${message.blockIndex}"]`
      );
      if (target) {
        target.scrollIntoView({ block: "start", behavior: "instant" as ScrollBehavior });
      }
      return;
    }
    const operation = acquireVirtualizedScrollOperation("block-navigation", "supersede-programmatic");
    if (operation === null) {
      return;
    }

    const main = document.querySelector<HTMLElement>("main.mm-document");
    if (main === null || virtualizedDocumentWindowModel === null || virtualizedDocumentWindowController === null) {
      scheduleVirtualizedElementLanding(
        operation,
        findLiveDocumentBlockElement(message.blockIndex),
        "block-live-fallback"
      );
      return;
    }

    const descriptor: WindowTargetDescriptor = { blockIndex: message.blockIndex, kind: "block" };
    void renderWindowTargetThenAct({
      action: (context) => {
        landVirtualizedProgrammaticNavigation({
          context,
          descriptor,
          operation,
          viewportOffsetY: 0,
        });
        return true;
      },
      actionKind: "navigate",
      controller: virtualizedDocumentWindowController,
      descriptor,
      legacyAction: () => scheduleVirtualizedElementLanding(
        operation,
        findLiveDocumentBlockElement(message.blockIndex),
        "block-resolver-fallback"
      ),
      main,
      model: virtualizedDocumentWindowModel,
      operation,
      ownerWindow: window,
      root: getDocumentScrollRoot(),
      virtualizationEnabled: true,
    });
    return;
  }

  if (message.type === "load-document") {
    currentDocumentRenderId = message.renderId ?? null;
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
    if (message.cacheKey === null) {
      // Host intentionally disabled renderer-document caching for a partial
      // progressive first load. The full key arrives with append-document.
      loadMessage.cacheKey = null;
    } else if (typeof message.cacheKey === "string" && message.cacheKey.length > 0) {
      loadMessage.cacheKey = message.cacheKey;
    } else {
      loadMessage.cacheKey = createProcessedDocumentCacheKey(
        message.html,
        message.theme ?? getCurrentTheme());
    }
    applyLoadDocument(loadMessage, buildLoadDocumentDeps());
    handleCurrentHashNavigation();
    return;
  }

  if (message.type === "append-document") {
    appendProgressiveDocumentHtml(message);
    return;
  }

  if (message.type === "load-cached-document") {
    currentDocumentRenderId = message.renderId ?? null;
    const loadMessage: import("./loadDocument").LoadDocumentMessage = {
      cacheKey: message.cacheKey,
    };
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
    if (message.hasMermaid !== undefined) {
      loadMessage.hasMermaid = message.hasMermaid;
    }
    if (message.hasHljs !== undefined) {
      loadMessage.hasHljs = message.hasHljs;
    }
    applyLoadDocument(loadMessage, buildLoadDocumentDeps());
    handleCurrentHashNavigation();
    return;
  }

  if (message.type === "clear-document") {
    currentDocumentRenderId = null;
    clearModeRevealShield();
    clearDocumentState(buildLoadDocumentDeps());
    return;
  }

  if (message.type === "invalidate-document-cache-key") {
    // In-place update channel (task-toggle commit): the DOM now shows content
    // whose hash differs from the load-time key. Null the key so a later
    // tab-away cannot store this DOM under the stale key (cache poisoning);
    // the next tab-return then does a full truthful render.
    currentDocumentCacheKey = null;
    cancelProcessedDocumentCacheClone();
    return;
  }

  if (message.type === "set-task-checkbox") {
    // Surgical single-checkbox revert: programmatic .checked fires no change
    // event, so this cannot loop back into a task-toggle post.
    const box = document.querySelector<HTMLInputElement>(
      `input.mm-task-checkbox[data-task-line="${message.line}"]`
    );
    if (box) {
      box.checked = message.checked;
    }
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
    const settleSequence = ++modeToggleSettleSequence;
    const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    const isCurrentProbe = () => settleSequence === modeToggleSettleSequence
      && (
        documentEpoch === undefined
        || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) === true
      );
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
  finishCachedScrollRestore?.("canceled", "stale-document");
  cancelPendingVirtualizedMaintenance("stale-document");
  cancelModelRenderedContentCoordinator("stale-document");
  scrollOwnershipControlPlane?.invalidateDocument();
  virtualizedWriteReceipts.clear();
  pendingInitialVirtualizedWindowWork = null;
  cachedScrollRestoreCompletion = null;
  minimapScrollOperation = null;
  ++initialRenderPipelineGeneration;
  ++processedDocumentCacheCloneGeneration;
  ++progressiveMinimapRefreshGeneration;
  cancelProcessedDocumentCacheClone();
  if (virtualizationEnabled) {
    cancelProgressiveDeferredEnhancements();
  }
  cancelDeferredMinimapContentRefresh(false);
  cancelMinimapRefreshAfterLayoutSettles();
  cancelHeavyLiveUpdate();
  resizeReactFrameRequested = false;
  initialRenderPipelineCompleted = false;
  firstPrefsBootstrapSuppressedByLoadGeneration = null;
  postReadyEnhancementsCompleted = false;
  currentController?.cancel();
  currentController = null;
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
  currentMinimapLayout = null;
  // Polish #5 — reset the width-handle reveal gate so the next document's
  // initialVisibleReady has to fire again before the handle becomes visible
  // at its (now-correct) post-settle x. Without this reset, every doc after
  // the first would skip the gate (it stays true from the prior doc) and the
  // pre-layout updateWidthHandlePosition call in ensureChromeNodes would
  // briefly show the handle at the wrong x — the same jitter the gate was
  // added to prevent, just on the second-and-subsequent doc loads.
  hasInitialLayoutSettled = false;
  resetVirtualizedDocumentWindow();
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
  invalidateTopVisibleBlockIndexCache();
  previewSourceLineFrameRequested = false;
  suppressPreviewSourceLineEmit = false;
  lastPostedPreviewSourceLine = null;
  pendingSourceLineTarget = null;
}

type EnsureChromeNodesOptions = {
  allowVirtualization?: boolean;
  refreshMinimap?: boolean;
};

function ensureChromeNodes(useCachedDocumentState = false, options: EnsureChromeNodesOptions = {}): void {
  ensureMinimap();
  ensureWidthHandle();
  ensureDropOverlay();
  if (useCachedDocumentState && virtualizationEnabled) {
    const main = document.querySelector<HTMLElement>("main.mm-document");
    if (main !== null) {
      reclaimClonedMermaidProxyLifecycles(main);
    }
  }
  if (options.allowVirtualization === false) {
    resetVirtualizedDocumentWindow(false);
  } else {
    initializeVirtualizedDocumentWindow();
  }
  refreshTopVisibleBlockIndexCache();
  // Width-handle X depends on the new .mm-document bounding rect after innerHTML
  // swap; ensureWidthHandle only ensures the node exists.
  updateWidthHandlePositionForCurrentLayout();
  // Cold path still needs a synchronous Phase A seed because cancelled async
  // pipelines may never reach initialVisibleReady. Cache hits carry the already
  // rendered minimap DOM, so restore it here and defer geometry reconciliation
  // out of the cache-hit layout-ready path.
  if (options.refreshMinimap === false) {
    updateMinimapVisibility(true);
    updateMinimapViewport({ skipVisibilityUpdate: true });
  } else if (!useCachedDocumentState || !restoreCachedMinimapContent()) {
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

async function runLoadDocumentInitialRenderPipeline(
  hasMermaid?: boolean,
  skipFrameWait?: boolean,
  renderId?: number,
  hasHljs?: boolean,
  suppressFirstPrefsBootstrap = false
): Promise<void> {
  const pipelineGeneration = ++initialRenderPipelineGeneration;
  firstPrefsBootstrapSuppressedByLoadGeneration = suppressFirstPrefsBootstrap
    ? pipelineGeneration
    : null;
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
        mathCount: getLiveDocumentMathCount()
      });
    },
    hasMermaid,
    postPerfMark,
    notifyPostReadyEnhancementsComplete: () => {
      postPostReadyEnhancementsComplete(renderId, hasMermaid, hasHljs);
    },
    isCurrent: () => pipelineGeneration === initialRenderPipelineGeneration,
  });
}

function buildLoadDocumentDeps(): import("./loadDocument").LoadDocumentDeps {
  return {
    // PE r2 item G — accept the per-document `hasMermaid` so the pipeline
    // skips mermaid init+render for docs without mermaid blocks. Undefined
    // passes through to the pipeline's `!== false` default, preserving the
    // pre-G behavior for any caller that doesn't carry the flag.
    runInitialRenderPipeline: (hasMermaid, skipFrameWait, renderId, hasHljs, ownsCompleteFreshBody) =>
      runLoadDocumentInitialRenderPipeline(
        hasMermaid,
        skipFrameWait,
        renderId,
        hasHljs,
        ownsCompleteFreshBody === true),
    cancelCurrentMathController: () => { currentController?.cancel(); },
    resetModuleGlobals: resetModuleGlobalsForLoadDocument,
    scrollWindowToTop: () => {
      // The swap's own scroll-to-top must NOT post preview-source-line≈0 —
      // that echo zeroed the editor on every cold load (entry, tab switch,
      // keystroke/checkbox re-render; rule-0 probe P1 caught it live).
      // resetModuleGlobals cleared the flag earlier in this load, so
      // re-suppress right before the write.
      suppressPreviewSourceLinePost();
      if (virtualizationEnabled) {
        scheduleVirtualizedStandaloneOperation("cold-load-reset", "supersede-programmatic", operation => {
          consumePendingInitialVirtualizedWindow(operation);
          operation.requestScrollTop(0, "cold-load-reset");
        });
      } else {
        window.scrollTo({ left: 0, top: 0, behavior: "instant" as ScrollBehavior });
      }
    },
    // Mirror selected renderer-side perf marks into the host's
    // [renderer-perf] stream. Only `mm-load-document` is bridged from this
    // path per round-2 plan item C; other marks are bridged at their own
    // emission sites in renderer.ts so the bridging is colocated with the
    // semantic anchor rather than centralized here.
    emitMark: (name, detail) => {
      emitMark(name, detail);
      if (name === "mm-load-document"
        || name === "mm-load-document-cache-hit"
        || name === "mm-load-document-cache-miss") {
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
    completeCachedDocumentLoad: (renderId, hasMermaid, hasHljs, skipFrameWait) => {
      if (hasUnrenderedDocumentMath()) {
        void runLoadDocumentInitialRenderPipeline(hasMermaid, skipFrameWait, renderId, hasHljs, false);
        return;
      }

      const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
      const complete = () => {
        if (
          documentEpoch !== undefined
          && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true
        ) {
          return;
        }
        initialRenderPipelineCompleted = true;
        hasInitialLayoutSettled = true;
        postReadyEnhancementsCompleted = true;
        postHostMessage({
          type: "document-ready",
          mathCount: getLiveDocumentMathCount()
        });
        postCachedLayoutReady();
        postPostReadyEnhancementsComplete(renderId, hasMermaid, hasHljs);
        scheduleCachedMermaidResume(hasMermaid);
      };
      const restoreCompletion = cachedScrollRestoreCompletion;
      cachedScrollRestoreCompletion = null;
      if (restoreCompletion === null) {
        complete();
      } else {
        void restoreCompletion.then(complete);
      }
    },
    notifyDocumentCacheMiss: (renderId, cacheKey) => {
      const message: RendererMessage = {
        type: "document-cache-miss",
      };
      if (renderId !== undefined) {
        message.renderId = renderId;
      }
      if (cacheKey !== undefined) {
        message.cacheKey = cacheKey;
      }
      postHostMessage(message);
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

function wireTaskCheckboxes(): void {
  // GFM task-list checkbox: after the native toggle, ask the host to flip
  // [ ]/[x] on the source line. The host writes the file (or the edit buffer)
  // and reloads, re-rendering the checkbox from the authoritative source.
  // Delegated on document so it survives document re-renders.
  document.addEventListener("change", (event) => {
    const target = event.target;
    if (
      !(target instanceof HTMLInputElement) ||
      !target.classList.contains("mm-task-checkbox")
    ) {
      return;
    }
    const lineAttr = target.getAttribute("data-task-line");
    if (lineAttr === null) {
      return;
    }
    const line = Number.parseInt(lineAttr, 10);
    if (Number.isNaN(line)) {
      return;
    }
    // Identity key of the item's raw source line (host-computed). The host
    // refuses the write when it no longer matches the disk line (stale view
    // after an external edit); missing key → host refuses (fail-closed).
    const key = target.getAttribute("data-task-key");
    postHostMessage({ type: "task-toggle", line, checked: target.checked, key });
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

    // Ctrl/Cmd + wheel is the browser zoom gesture — leave it to Chromium's
    // built-in zoom (WebView2 IsZoomControlEnabled is on by default) instead of
    // consuming it as a document scroll. Without this the proxy preventDefault'd
    // every wheel event, swallowing ctrl+wheel zoom (the "zoom disappeared" bug).
    if (event.ctrlKey || event.metaKey) {
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

// Ctrl+F → in-document find bar. The default path is the original pure-DOM
// search; MARKMELLO_VIRTUALIZATION injects a host-backed provider so counts
// come from full-document block plaintext while live highlights stay windowed.
// Limitation: the keystroke only fires when WebView2 has
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
  virtualizedFindProvider = virtualizationEnabled
    ? createVirtualizedFindProvider({
      postHostMessage: message => {
        requestRenderedFindModelReadiness();
        postHostMessage(message);
      },
      readContext: readVirtualizedFindContext,
    })
    : null;
  findBarController = createFindBar(virtualizedFindProvider ?? undefined);
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

function runLegacyResizeObserverWork(documentEpoch: number | undefined): void {
  if (widthHandleDragging) {
    return;
  }
  queueMinimapRefreshAfterLayoutSettles();
  scheduleResizeReactions(documentEpoch);
  invalidateSourceLineAnchors({
    reassertPendingTarget: virtualizedProgrammaticNavigationPostSettleTarget === null,
  });
  scheduleVirtualizedMeasuredHeightAdoption();
  window.requestAnimationFrame(() => {
    if (
      documentEpoch === undefined
      || scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) === true
    ) {
      postScroll();
    }
  });
}

function runLegacyDocumentFontsReadyWork(documentEpoch: number | undefined): void {
  if (
    documentEpoch !== undefined
    && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true
  ) {
    return;
  }
  queueMinimapRefreshAfterLayoutSettles();
  invalidateSourceLineAnchors({
    reassertPendingTarget: virtualizedProgrammaticNavigationPostSettleTarget === null,
  });
  scheduleVirtualizedMeasuredHeightAdoption();
}

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
  wireTaskCheckboxes();
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
    mathCount: getLiveDocumentMathCount()
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
      const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
      if (!virtualizationEnabled) {
        runLegacyResizeObserverWork(documentEpoch);
        return;
      }
      const ticket = beginVirtualizedGeometryWork("resize-observer");
      mutateVirtualizedGeometry(ticket);
      try {
        runLegacyResizeObserverWork(documentEpoch);
      } finally {
        endVirtualizedGeometryWork(ticket);
      }
    });
    resizeObserver.observe(documentElement);
    resizeObserver.observe(document.body);
  }

  const fontsDocumentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
  document.fonts?.ready.then(() => {
    if (!virtualizationEnabled) {
      runLegacyDocumentFontsReadyWork(fontsDocumentEpoch);
      return;
    }
    const ticket = beginVirtualizedGeometryWork("document-fonts-ready");
    mutateVirtualizedGeometry(ticket);
    try {
      runLegacyDocumentFontsReadyWork(fontsDocumentEpoch);
    } finally {
      endVirtualizedGeometryWork(ticket);
    }
  }).catch(() => undefined);
});

const queuePostScroll = createScrollCoalescer({
  postScroll: () => {
    updateVirtualizedWindowForScroll();
    postScroll();
    queueMinimapViewportUpdate();
  },
  schedule: (cb) => {
    const documentEpoch = scrollOwnershipControlPlane?.captureDocumentEpoch();
    window.requestAnimationFrame(() => {
      if (
        documentEpoch !== undefined
        && scrollOwnershipControlPlane?.isCurrentDocumentEpoch(documentEpoch) !== true
      ) {
        return;
      }
      cb();
    });
  },
});

document.addEventListener("scroll", () => {
  if (scrollOwnershipControlPlane !== null) {
    const classification = scrollOwnershipControlPlane.classifyNativeScroll(
      getDocumentScrollRoot().scrollTop,
      "native-scroll"
    );
    if (classification.kind === "user-supersession") {
      cancelPendingVirtualizedMaintenance("user-supersession");
      cancelVirtualizedProgrammaticNavigationState();
      queuePreviewSourceLinePost();
    } else if (classification.kind === "unattributed-failure") {
      cancelPendingVirtualizedMaintenance("unattributed-failure");
      virtualizedProgrammaticNavigationExternalShiftCount++;
      cancelVirtualizedProgrammaticNavigationState();
    }
  } else {
    const programmaticNavigationScroll = isVirtualizedProgrammaticNavigationInProgress();
    if (!programmaticNavigationScroll) {
      clearVirtualizedProgrammaticNavigationPostSettleTarget();
    }
    queuePostScroll();
    queuePreviewSourceLinePost();
    return;
  }
  queuePostScroll();
}, { passive: true });

window.addEventListener("hashchange", handleCurrentHashNavigation);
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
