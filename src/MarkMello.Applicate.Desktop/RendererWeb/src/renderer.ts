import { shouldPostMinimapState, type PostedMinimapState } from "./minimapState";
import { calculateMinimapViewportLayout, type MinimapViewportLayout } from "./minimapLayout";
import {
  getWidthResizerVisibilityClasses,
  normalizeWidthResizerVisibility,
  type WidthResizerVisibility
} from "./widthResizerVisibility";
import { renderMermaidNode, type MermaidApiLike } from "./mermaidRender";
import { normalizeHljsLanguage } from "./hljsLanguage";
import { runInitialRenderPipeline, type MathReadinessController } from "./initialRenderPipeline";
import { renderMath as renderMathInit } from "./mathRenderInit";
import { schedulePhaseBRebuild } from "./schematicMinimap";
import { emitMark, installLongTaskObserver, recordScrollIpc, getReport, getFpsSampler } from "./performanceMarks";
import { createScrollCoalescer } from "./scrollCoalescer";

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
  | { type: "csp-violation"; blockedURI: string; violatedDirective: string; sourceFile: string; lineNumber: number; columnNumber: number };

type MinimapMode = "auto" | "on" | "off";

type MinimapPolicy = {
  minHostWidth: number;
  minScrollableViewportRatio: number;
  maxDetailedDocumentHeight: number;
};

type FontFamilyMode = "serif" | "sans" | "mono";

type HostMessage =
  | { type: "theme"; theme: "light" | "dark" }
  | { type: "minimap-policy"; minimapPolicy: MinimapPolicy }
  | {
      type: "reading-preferences";
      fontSize: number;
      lineHeight: number;
      maxWidth: number;
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
  | { type: "scroll-to-progress"; progressPercent: number };

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
let layoutReadyGeneration = 0;
let layoutReadyTimer: number | undefined;
let lastPostedMinimapState: PostedMinimapState = { hasPosted: false, visible: false, reservedWidth: 0 };
let minimapPolicy: MinimapPolicy = {
  // Mirrors ApplicateDocumentMinimapBuildPolicy until the host sends minimap-policy.
  // WebView uses CSS scrollHeight while Native uses Avalonia visual height; keep
  // this shared value intentionally permissive until WebView-specific tuning exists.
  minHostWidth: 1100,
  minScrollableViewportRatio: 1.5,
  maxDetailedDocumentHeight: 240000
};

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

function getCurrentTheme(): "light" | "dark" {
  return document.documentElement.dataset.theme === "dark" ? "dark" : "light";
}

function applyTheme(theme: "light" | "dark"): void {
  document.documentElement.dataset.theme = theme;
}

function initMermaidWithTheme(theme: "light" | "dark"): void {
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

async function handleThemeChange(theme: "light" | "dark"): Promise<void> {
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
  // Reveal the document body now that math + mermaid + code blocks
  // have finished rendering and layout has settled. Renderer.css holds
  // `main.mm-document` at opacity 0 until this class is added, which
  // hides the ~130ms "fallback" state (web fonts not yet swapped,
  // KaTeX `\[ ... \]` placeholders, raw mermaid source) that would
  // otherwise be visible during a tab switch or fresh launch. See the
  // .mm-rendered rule in renderer.css for the reveal transition.
  document.querySelector("main.mm-document")?.classList.add("mm-rendered");
  // Also reveal the minimap aside. CSS keeps it at opacity 0 until this
  // class is added so the user does not see the Phase A → Phase B
  // minimap rebuild flash (Phase A captures the doc immediately after
  // initialVisibleReady, Phase B re-clones after allMathRendered if
  // heights changed). With this gate the minimap fades in once with
  // the body.
  document.querySelector("aside.mm-minimap")?.classList.add("mm-rendered");
  // Also reveal the width-resizer handle. Without this fade, the handle
  // is positioned during the blank-fade window using a documentRect with
  // partial layout (math/mermaid not yet rendered), producing a vertical
  // bar at the wrong X coordinate that flashes for a frame during the
  // transition.
  document.querySelector("div.mm-width-handle")?.classList.add("mm-rendered");
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
  // documentRect already includes the 72px padding around the text. Push the
  // hit area further right by a small grace so the visible track sits in
  // the gap between the document container and the minimap (or the viewport
  // edge when minimap is hidden) instead of biting into the right padding
  // band that occasionally surfaces wide characters at narrow widths.
  const trackGraceRight = 4;
  const idealHandleLeft = documentRect.right + trackGraceRight;
  const minimapLeftEdge = window.innerWidth - minimapReservedWidth;
  const maxLeftBeforeMinimap = Math.max(0, minimapLeftEdge - hitArea);
  const clampedLeft = Math.max(0, Math.min(maxLeftBeforeMinimap, idealHandleLeft));
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

  pendingWidthDragDeltaX = event.clientX - widthHandleStartClientX;
  // Live local preview: compute new maxWidth from deltaX and apply directly.
  // Document is centered, so handle moves by deltaX implies column width
  // changes by 2*deltaX. Bypass host round-trip — renderer owns the visual
  // during drag, host gets final value on release.
  const previewMaxWidth = Math.max(200, widthHandleStartMaxWidth + 2 * pendingWidthDragDeltaX);
  document.documentElement.style.setProperty("--mm-document-max-width", `${previewMaxWidth}px`);
  // Position handle from real document layout (after maxWidth reflow). This
  // matches the steady-state logic and inherits the minimap-clamping baked
  // into updateWidthHandlePosition — no overlap with the minimap area.
  updateWidthHandlePosition();
  postWidthDragMove();
  event.preventDefault();
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

  // Local preview already applied final maxWidth via the move handler.
  // Host's reading-preferences echo (with its own clamped value) arrives after
  // width-drag end and will overwrite the preview — small visual snap if host
  // clamps differently, but it's a single reflow.
  updateWidthHandlePosition();

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
  const clone = source.cloneNode(true) as HTMLElement;
  minimapSourceReady = true;
  clone.removeAttribute("id");
  clone.setAttribute("aria-hidden", "true");
  clone.inert = true;
  clone.querySelectorAll<HTMLElement>("[id]").forEach((node) => node.removeAttribute("id"));
  clone.querySelectorAll<HTMLElement>("*").forEach((node) => {
    for (const attribute of Array.from(node.attributes)) {
      if (attribute.name === "role"
        || attribute.name === "name"
        || attribute.name === "for"
        || (attribute.name.startsWith("aria-") && attribute.name !== "aria-hidden")) {
        node.removeAttribute(attribute.name);
      }
    }
  });
  clone.querySelectorAll<HTMLElement>("a, button, input, textarea, select").forEach((node) => {
    node.setAttribute("tabindex", "-1");
    node.removeAttribute("href");
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
  if (!hasReceivedHostPreferences
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

  const visible = shouldShowMinimap();
  minimapRoot.hidden = !visible;
  document.body.classList.toggle(MINIMAP_VISIBLE_CLASS, visible);
  postMinimapState(visible, forcePostState);
  updateWidthHandlePosition();
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

  const sourceStyle = getComputedStyle(source);
  const sourcePaddingLeft = Number.parseFloat(sourceStyle.paddingLeft) || 0;
  const sourcePaddingRight = Number.parseFloat(sourceStyle.paddingRight) || 0;
  const minimapHeight = minimapRoot.clientHeight;
  const minimapWidth = minimapRoot.clientWidth;
  const documentHeight = root.scrollHeight;
  const documentWidth = Math.max(
    source.scrollWidth - sourcePaddingLeft - sourcePaddingRight,
    source.clientWidth - sourcePaddingLeft - sourcePaddingRight,
    1);
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

function scrollFromMinimapClientY(clientY: number): void {
  if (!minimapRoot) {
    return;
  }

  const root = document.scrollingElement ?? document.documentElement;
  const rect = minimapRoot.getBoundingClientRect();
  const minimapY = Math.max(0, Math.min(rect.height, clientY - rect.top));
  const documentY = currentMinimapLayout
    ? (minimapY - currentMinimapLayout.contentTranslateY) / currentMinimapLayout.scale
    : (minimapY / Math.max(1, rect.height)) * root.scrollHeight;
  const target = documentY - root.clientHeight / 2;
  const maximum = Math.max(0, root.scrollHeight - root.clientHeight);
  window.scrollTo({ top: Math.max(0, Math.min(maximum, target)), behavior: "instant" as ScrollBehavior });
}

function scrollToProgress(progressPercent: number): void {
  const root = document.scrollingElement ?? document.documentElement;
  const maximum = Math.max(0, root.scrollHeight - root.clientHeight);
  const progress = Number.isFinite(progressPercent) ? Math.max(0, Math.min(100, progressPercent)) : 0;
  window.scrollTo({ top: maximum * (progress / 100), behavior: "instant" as ScrollBehavior });
}

function handleMinimapPointerDown(event: PointerEvent): void {
  minimapDragging = true;
  minimapRoot?.setPointerCapture(event.pointerId);
  scrollFromMinimapClientY(event.clientY);
  event.preventDefault();
}

function handleMinimapPointerMove(event: PointerEvent): void {
  if (!minimapDragging) {
    return;
  }

  scrollFromMinimapClientY(event.clientY);
  event.preventDefault();
}

function handleMinimapPointerUp(event: PointerEvent): void {
  minimapDragging = false;
  try {
    minimapRoot?.releasePointerCapture(event.pointerId);
  } catch {
    // Pointer capture may already be gone after WebView focus changes.
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

  // Width handle anchor depends on max-width / chrome / resizer-visibility;
  // recompute when any of those changed.
  if (maxWidthChanged || viewerChromeChanged || widthResizerVisibilityChanged) {
    updateWidthHandlePosition();
  }

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
  }
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
      type: "link-clicked",
      href: target.href,
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
  postHostMessage({
    type: "document-ready",
    mathCount: document.querySelectorAll("[data-tex]").length
  });
  postScroll();

  const documentElement = document.querySelector<HTMLElement>(".mm-document");
  if (documentElement) {
    const resizeObserver = new ResizeObserver(() => {
      queueMinimapRefreshAfterLayoutSettles();
      updateWidthHandlePosition();
      window.requestAnimationFrame(postScroll);
    });
    resizeObserver.observe(documentElement);
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
