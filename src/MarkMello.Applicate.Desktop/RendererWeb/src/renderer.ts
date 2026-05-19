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
import { renderMermaidNode, type MermaidApiLike } from "./mermaidRender";
import { normalizeHljsLanguage } from "./hljsLanguage";
import { runInitialRenderPipeline, type MathReadinessController, type RendererTheme } from "./initialRenderPipeline";
import { applyLoadDocument, clearDocumentState } from "./loadDocument";
import { renderMath as renderMathInit } from "./mathRenderInit";
import { schedulePhaseBRebuild } from "./schematicMinimap";
import { emitMark, installLongTaskObserver, recordScrollIpc, getReport, getFpsSampler } from "./performanceMarks";
import { createScrollCoalescer } from "./scrollCoalescer";
import { calculateWidthHandleLeft } from "./widthHandleLayout";

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
    };
  };
  invokeCSharpAction?: (message: string) => void;
};

type RendererMessage =
  | { type: "document-ready"; mathCount: number }
  | { type: "layout-ready"; scrollTop: number; scrollHeight: number; clientHeight: number }
  | { type: "link-clicked"; href: string; button: number; ctrlKey: boolean; shiftKey: boolean; altKey: boolean; metaKey: boolean }
  | { type: "minimap-state"; visible: boolean; reservedWidth: number }
  | { type: "scroll"; scrollTop: number; scrollHeight: number; clientHeight: number; topBlockIndex: number | null }
  | { type: "viewer-interaction" }
  | { type: "wheel"; deltaY: number; deltaMode: number }
  | { type: "width-drag"; phase: "start" | "move" | "end"; deltaX: number }
  | { type: "drag-hover"; hovering: boolean }
  | { type: "drop-file"; name: string; text: string }
  | { type: "host-shortcut"; combo: string }
  | { type: "debug-log"; text: string }
  | { type: "debug-log"; message: string }
  | { type: "csp-violation"; blockedURI: string; violatedDirective: string; sourceFile: string; lineNumber: number; columnNumber: number };

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
    }
  | { type: "scroll-by"; deltaY: number }
  | { type: "scroll-to-block"; blockIndex: number }
  | { type: "scroll-to"; anchor: string }
  | { type: "scroll-to-progress"; progressPercent: number }
  | { type: "load-document"; html: string; documentName?: string; theme?: RendererTheme; hasMermaid?: boolean; hasHljs?: boolean; renderId?: number }
  | { type: "clear-document" }
  | { type: "host-scrollbar"; active: boolean };

const hostWindow = window as RendererWindow;
const MINIMAP_CLASS = "mm-minimap";
const MINIMAP_VIEWPORT_CLASS = "mm-minimap-viewport";
const MINIMAP_VISIBLE_CLASS = "mm-has-minimap";
const MINIMAP_REFRESH_DEBOUNCE_MS = 100;
const WIDTH_HANDLE_CLASS = "mm-width-handle";
const WIDTH_HANDLE_DRAGGING_CLASS = "mm-dragging";
const WIDTH_RESIZER_ALWAYS_CLASS = "mm-width-resizer-always";

let minimapMode: MinimapMode = "off";
let hasReceivedHostPreferences = false;
let minimapViewportFrameRequested = false;
let minimapRefreshTimer: number | undefined;
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
let initialRenderPipelineCompleted = false;
let currentController: MathReadinessController | null = null;
const MAX_MERMAID_DIAGRAMS = 50;
const MERMAID_PER_DIAGRAM_TIMEOUT_MS = 3000;
const MERMAID_WATCHDOG_MS = 15_000;
let widthResizerVisibility: WidthResizerVisibility = "on-hover";
let viewerChromeEnabled = false;
let documentScrollEnabled = true;
let wheelProxyEnabled = false;
let widthHandleRoot: HTMLElement | null = null;
let widthHandleDragging = false;
let widthHandleStartClientX = 0;
let widthHandleStartMaxWidth = 0;
let pendingWidthDragDeltaX = 0;
let widthDragFrameRequested = false;
let widthDragApplyFrameRequested = false;
let layoutReadyGeneration = 0;
let layoutReadyTimer: number | undefined;
let lastPostedMinimapState: PostedMinimapState = { hasPosted: false, visible: false, reservedWidth: 0 };
// F-07 fix: the host (ApplicateWebMarkdownDocumentView.SendMinimapPolicy)
// always pushes the canonical ApplicateDocumentMinimapBuildPolicy values
// before any user document loads (SendMinimapPolicy is invoked alongside
// SendReadingPreferences in the document-ready / shell-ready paths).
// minimapPolicy stays null until that message lands; shouldShowMinimap
// gates on this so the renderer cannot make a minimap decision against
// stale literals that drifted from C#.
let minimapPolicy: MinimapPolicy | null = null;

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
  currentController = controller;
  schedulePhaseBRebuild({
    allMathRendered: controller.allMathRendered,
    getCurrentDocumentHeight: () => (document.scrollingElement ?? document.documentElement).scrollHeight,
    getCachedDocumentHeight: () => minimapDocumentHeight,
    refresh: refreshMinimapContent,
  });
  // Lifecycle marks read failed-counts from the controller's frozen set (single
  // source of truth — no duplicate classification). For all-math, walk all
  // [data-tex] nodes since IO may have rendered nodes outside the frozen set.
  controller.initialVisibleReady.then(() => {
    emitMark("mm-initial-visible-ready", {
      visibleCount: controller.initialVisibleNodes.size,
      failedCount: countFailedInSet(controller.initialVisibleNodes),
    });
    // Phase A minimap rebuild now happens here — once initial-visible math has
    // reached terminal state (heights stable), clone the document for the minimap.
    // This replaces the old katexHasRun gate without racing the rAF in queueMinimapRefresh.
    refreshMinimapContent("A");
  });
  controller.allMathRendered.then(() => {
    const allMathNodes = Array.from(document.querySelectorAll<HTMLElement>("[data-tex]"));
    emitMark("mm-all-math-rendered", {
      totalCount: controller.totalMathCount,
      failedCount: countFailedInSet(allMathNodes),
      cancelled: controller.isCancelled(),
    });
  });
  return controller;
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

async function renderMermaid(): Promise<void> {
  const mermaid = hostWindow.mermaid;
  if (!mermaid) return;

  const allNodes = Array.from(document.querySelectorAll<HTMLElement>("pre.mm-mermaid"));
  const nodes = allNodes.slice(0, MAX_MERMAID_DIAGRAMS);
  if (nodes.length === 0) return;

  const generation = ++mermaidRenderGeneration;
  const watchdog = window.setTimeout(() => {
    if (generation === mermaidRenderGeneration) {
      ++mermaidRenderGeneration;
    }
  }, MERMAID_WATCHDOG_MS);

  try {
    for (const node of nodes) {
      await renderMermaidNode(node, generation, () => mermaidRenderGeneration, mermaid, MERMAID_PER_DIAGRAM_TIMEOUT_MS);
      if (generation !== mermaidRenderGeneration) return;
    }
  } finally {
    window.clearTimeout(watchdog);
  }
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

async function handleThemeChange(theme: RendererTheme): Promise<void> {
  applyTheme(theme);
  initMermaidWithTheme(theme);
  await renderMermaid();
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
  recordScrollIpc();
  postHostMessage({
    type: "scroll",
    ...getScrollState(),
    topBlockIndex: findTopVisibleBlockIndex()
  });
}

function postLayoutReady(): void {
  postScroll();
  postHostMessage({
    type: "layout-ready",
    ...getScrollState()
  });
}

function scheduleLayoutReady(): void {
  const generation = ++layoutReadyGeneration;
  let completed = false;
  if (layoutReadyTimer !== undefined) {
    window.clearTimeout(layoutReadyTimer);
  }

  const complete = () => {
    if (completed || generation !== layoutReadyGeneration) {
      return;
    }

    completed = true;
    if (layoutReadyTimer !== undefined) {
      window.clearTimeout(layoutReadyTimer);
      layoutReadyTimer = undefined;
    }

    window.requestAnimationFrame(() => {
      window.requestAnimationFrame(() => {
        if (generation === layoutReadyGeneration) {
          postLayoutReady();
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

  widthHandleRoot.hidden = !hasReceivedHostPreferences || !viewerChromeEnabled;
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

function postWidthDragMove(): void {
  if (widthDragFrameRequested) {
    return;
  }

  widthDragFrameRequested = true;
  window.requestAnimationFrame(() => {
    widthDragFrameRequested = false;
    postHostMessage({ type: "width-drag", phase: "move", deltaX: pendingWidthDragDeltaX });
  });
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
    const previewMaxWidth = Math.max(hostMinMaxWidth, widthHandleStartMaxWidth + 2 * pendingWidthDragDeltaX);
    document.documentElement.style.setProperty("--mm-document-max-width", `${previewMaxWidth}px`);
    // Canonical handle re-alignment. ONE source of truth — reads actual
    // .mm-document.right + paddingRight after the CSS var change, places
    // handle at textRight + hit-area. The previous synthetic delta-math
    // (`startLeft + columnDelta/2`) was wrong whenever the rendered column
    // width disagreed with `previewMaxWidth`: viewport clamps, content
    // min-width forced by wide formulas/code blocks/tables, or scrollbar-
    // gutter all break the assumption. Symptom was "handle drifts onto
    // text during drag, snaps back on release" — the snap was the
    // canonical re-sync at pointerUp telling the truth. Cost: one forced
    // layout flush per rAF; the previous path also forced one (via the
    // minimap reserved-width read), so net cost is unchanged.
    updateWidthHandlePosition();
    // Live minimap update — track the source's new wrap during drag. Cost
    // is one updateMinimapViewport per rAF (sync layout on source +
    // minimap clone). With content-visibility on source blocks the source
    // side is ~5%-cost; clone is not c-v-covered so reflow cost depends on
    // clone block count. If perf regresses on heavy docs, throttle to
    // every Nth frame here.
    queueMinimapViewportUpdate();
  });
}

function handleWidthHandlePointerUp(event: PointerEvent): void {
  if (!widthHandleDragging) {
    return;
  }

  const deltaX = event.clientX - widthHandleStartClientX;
  widthHandleDragging = false;
  widthHandleRoot?.classList.remove(WIDTH_HANDLE_DRAGGING_CLASS);
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
  ensureMinimap();
  if (!minimapContent || !minimapRoot) {
    emitMark("mm-minimap-refresh-end", { phase, skipped: "no-mount" });
    return;
  }
  const clone = cloneDocumentForMinimap();
  if (!clone) {
    emitMark("mm-minimap-refresh-end", { phase, skipped: "no-source" });
    return;
  }
  const root = document.scrollingElement ?? document.documentElement;
  minimapDocumentHeight = root.scrollHeight;
  minimapContent.replaceChildren(clone);
  updateMinimapVisibility(true);
  updateMinimapViewport();
  emitMark("mm-minimap-refresh-end", { phase, documentHeight: minimapDocumentHeight });
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

  if (documentHeight > minimapPolicy.maxDetailedDocumentHeight) {
    return false;
  }

  if (minimapMode === "on") {
    return true;
  }

  return window.innerWidth >= minimapPolicy.minHostWidth
    && documentHeight >= viewportHeight * minimapPolicy.minScrollableViewportRatio;
}

function updateMinimapVisibility(forcePostState = false): void {
  ensureMinimap();
  if (!minimapRoot) {
    return;
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
  if (wasVisible !== visible || hadClass !== visible) {
    updateWidthHandlePosition();
  }
}

function getCurrentMinimapReservedWidth(): number {
  if (!minimapRoot || minimapRoot.hidden) {
    return 0;
  }

  const minimapWidth = minimapRoot.getBoundingClientRect().width || readRootPixelVariable("--mm-minimap-width", 0);
  const minimapGap = readRootPixelVariable("--mm-minimap-gap", 0);
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

function updateMinimapViewport(): void {
  ensureMinimap();
  if (!minimapRoot || !minimapContent || !minimapViewport) {
    return;
  }

  const root = document.scrollingElement ?? document.documentElement;
  const source = document.querySelector<HTMLElement>(".mm-document");
  if (!source) {
    return;
  }

  const minimapHeight = minimapRoot.clientHeight;
  const minimapWidth = minimapRoot.clientWidth;
  // Use root coordinates (root.scrollHeight, root.scrollTop) so the scrollbar
  // thumb and minimap viewport always agree with the actual user scroll state.
  // Previous attempt to switch to .mm-document basis for "precision" caused a
  // misalignment in viewer mode: when body has top/bottom padding, source.
  // scrollHeight < root.scrollHeight, but the user's root.scrollTop can reach
  // values beyond (source.scrollHeight - viewportHeight), making scrollProgress
  // > 1 at the bottom of the document. Stick with root coords — the documented
  // line-by-line drift is acceptable; thumb correctness is not negotiable.
  const documentHeight = root.scrollHeight;
  const sourceStyle = getComputedStyle(source);
  const documentWidth = calculateMinimapDocumentWidth({
    borderBoxWidth: source.clientWidth || source.getBoundingClientRect().width,
    paddingLeft: readPixelValue(sourceStyle.paddingLeft),
    paddingRight: readPixelValue(sourceStyle.paddingRight),
  });
  const viewportHeight = root.clientHeight;
  if (minimapHeight <= 0 || minimapWidth <= 0 || documentHeight <= 0 || viewportHeight <= 0) {
    return;
  }

  const layout = calculateMinimapViewportLayout({
    minimapWidth,
    minimapHeight,
    documentWidth,
    documentHeight,
    viewportHeight,
    scrollTop: root.scrollTop
  });
  if (!layout) {
    currentMinimapLayout = null;
    return;
  }

  currentMinimapLayout = layout;
  minimapContent.style.transform = layout.transform;
  minimapContent.style.width = `${layout.contentWidth}px`;
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

function queueMinimapViewportUpdate(): void {
  if (minimapViewportFrameRequested) {
    return;
  }

  minimapViewportFrameRequested = true;
  window.requestAnimationFrame(() => {
    minimapViewportFrameRequested = false;
    updateMinimapVisibility();
    updateMinimapViewport();
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
  pendingReadingPreferences = {
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
  if (applyPrefsFrameRequested) return;
  applyPrefsFrameRequested = true;
  requestAnimationFrame(flushPendingReadingPreferences);
}

function flushPendingReadingPreferences(): void {
  applyPrefsFrameRequested = false;
  const next = pendingReadingPreferences;
  pendingReadingPreferences = null;
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
    scheduleHeavyLiveUpdate();
  }

  if (!hadHostPreferences && !initialRenderPipelineCompleted) {
    // First reading-preferences — run the full Mermaid/code-block pipeline
    // before emitting layout-ready. Pipeline schedules its own layout-ready.
    void runInitialRenderPipeline({
      getCurrentTheme,
      applyTheme,
      initMermaidWithTheme,
      renderMath,
      renderMermaid,
      renderCodeBlocks,
      scheduleLayoutReady: () => {
        initialRenderPipelineCompleted = true;
        scheduleLayoutReady();
      }
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
    queueMinimapViewportUpdate();
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
    applyLoadDocument(loadMessage, buildLoadDocumentDeps());
    return;
  }

  if (message.type === "clear-document") {
    clearDocumentState(buildLoadDocumentDeps());
    return;
  }
}

function resetModuleGlobalsForLoadDocument(): void {
  initialRenderPipelineCompleted = false;
  currentController?.cancel();
  currentController = null;
  // INCREMENT, not reset-to-0 — invalidates in-flight mermaid render callbacks
  // that compare against the old generation token. Resetting to 0 would let a
  // stale callback whose generation === 0 pass the check, painting an
  // old-document diagram onto the new document. (Codex review 2026-05-15.)
  ++mermaidRenderGeneration;
  minimapDocumentHeight = 0;
  lastPostedMinimapState = { hasPosted: false, visible: false, reservedWidth: 0 };
  minimapSourceReady = false;
}

function ensureChromeNodes(): void {
  ensureMinimap();
  ensureWidthHandle();
  ensureDropOverlay();
  // Width-handle X depends on the new .mm-document bounding rect after innerHTML
  // swap; ensureWidthHandle only ensures the node exists.
  updateWidthHandlePosition();
  // SYNCHRONOUS per-document minimap source rebuild. loadDocument.ts:55 calls
  // ensureChromeNodes right after main.innerHTML swap and BEFORE the async
  // runInitialRenderPipeline. Verified 2026-05-19: F-04 multi-fire from C#
  // sends two load-documents per tab click, each calls cancelCurrentMathController
  // and resetModuleGlobals (which sets minimapSourceReady=false). Both async
  // pipelines hang at `await mathController.initialVisibleReady` (controller
  // cancelled mid-flight), never reach scheduleLayoutReady, so the async
  // refresh (in scheduleLayoutReady callback) never runs. Without this sync
  // call, minimapSourceReady stays false forever after tab switch → minimap
  // hidden. Initial launch was unaffected because module-init's own
  // controller.initialVisibleReady.then(refreshMinimapContent) wiring at
  // boot fires once and seeds the state; only subsequent loads were broken.
  // Position-after-host-preferences arrival: at initial launch the host has
  // already sent reading-preferences by the time the FIRST load-document
  // arrives (theme + minimap-policy + reading-preferences all fire before
  // load-document per ApplicateWebMarkdownDocumentView shell-ready handler),
  // so chrome/mode are populated when this call fires.
  refreshMinimapContent("A");
}

function buildLoadDocumentDeps(): import("./loadDocument").LoadDocumentDeps {
  return {
    runInitialRenderPipeline: () => runInitialRenderPipeline({
      getCurrentTheme,
      applyTheme,
      initMermaidWithTheme,
      renderMath,
      renderMermaid,
      renderCodeBlocks,
      scheduleLayoutReady: () => {
        initialRenderPipelineCompleted = true;
        scheduleLayoutReady();
        // Re-emit document-ready so the host's _hasLoadedDocument state
        // machine restarts for the new document.
        postHostMessage({
          type: "document-ready",
          mathCount: document.querySelectorAll("[data-tex]").length
        });
      }
    }),
    cancelCurrentMathController: () => { currentController?.cancel(); },
    resetModuleGlobals: resetModuleGlobalsForLoadDocument,
    scrollWindowToTop: () => { window.scrollTo({ left: 0, top: 0, behavior: "instant" as ScrollBehavior }); },
    emitMark,
    ensureChromeNodes,
    applyTheme,
    debugLog: postDebugLog,
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
function wireHostShortcuts(): void {
  const hostShortcuts = new Set<string>([
    "ctrl+e",
    "ctrl+o",
    "ctrl+s",
    "ctrl+shift+s",
    "ctrl+n",
    "ctrl+r",
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
      postHostMessage({ type: "host-shortcut", combo });
    },
    { capture: true }
  );
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
  requestAnimationFrame(() => emitMark("mm-doc-painted"));
  installLongTaskObserver();
  applyViewerChromeState();
  applyDocumentScrollState();
  // Defer renderMath / renderMermaid / renderCodeBlocks to runInitialRenderPipeline,
  // which is triggered by the first reading-preferences message from the host.
  wireLinks();
  wireViewerInteraction();
  wireWheelProxy();
  wireFileDrop();
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
  // Gated during drag because scheduleWidthDragApply already calls
  // updateWidthHandlePosition canonically each rAF — we don't want to
  // double-update during drag AND we want to skip the heavy minimap rebuild
  // until drag settles. Minimap visibility toggles call updateWidthHandle-
  // Position directly (see updateMinimapVisibility) so they don't depend on
  // this observer landing in the same frame as the body class change.
  const documentElement = document.querySelector<HTMLElement>(".mm-document");
  if (documentElement) {
    const resizeObserver = new ResizeObserver(() => {
      if (widthHandleDragging) {
        return;
      }
      queueMinimapRefreshAfterLayoutSettles();
      updateWidthHandlePosition();
      window.requestAnimationFrame(postScroll);
    });
    resizeObserver.observe(documentElement);
    resizeObserver.observe(document.body);
  }

  document.fonts?.ready.then(() => queueMinimapRefreshAfterLayoutSettles()).catch(() => undefined);
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
}, { passive: true });

window.addEventListener("message", (event) => handleHostMessage(event.data));
window.addEventListener("resize", () => {
  updateWidthHandlePosition();
  queueMinimapViewportUpdate();
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
