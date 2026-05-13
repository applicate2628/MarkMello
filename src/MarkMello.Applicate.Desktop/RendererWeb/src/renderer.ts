import { shouldPostMinimapState, type PostedMinimapState } from "./minimapState";
import { calculateMinimapViewportLayout, type MinimapViewportLayout } from "./minimapLayout";
import {
  getWidthResizerVisibilityClasses,
  normalizeWidthResizerVisibility,
  type WidthResizerVisibility
} from "./widthResizerVisibility";
import { renderMermaidNode, type MermaidApiLike } from "./mermaidRender";
import { normalizeHljsLanguage } from "./hljsLanguage";
import { runInitialRenderPipeline } from "./initialRenderPipeline";
import { markStart, markEnd, emitMark, installLongTaskObserver, recordScrollIpc, getReport, getFpsSampler } from "./performanceMarks";

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
  | { type: "scroll"; scrollTop: number; scrollHeight: number; clientHeight: number }
  | { type: "viewer-interaction" }
  | { type: "wheel"; deltaY: number; deltaMode: number }
  | { type: "width-drag"; phase: "start" | "move" | "end"; deltaX: number }
  | { type: "csp-violation"; blockedURI: string; violatedDirective: string; sourceFile: string; lineNumber: number; columnNumber: number };

type MinimapMode = "auto" | "on" | "off";

type MinimapPolicy = {
  minHostWidth: number;
  minScrollableViewportRatio: number;
  maxDetailedDocumentHeight: number;
};

type HostMessage =
  | { type: "theme"; theme: "light" | "dark" }
  | { type: "minimap-policy"; minimapPolicy: MinimapPolicy }
  | {
      type: "reading-preferences";
      fontSize: number;
      lineHeight: number;
      maxWidth: number;
      minimapMode: MinimapMode;
      viewerChromeEnabled?: boolean;
      widthResizerVisibility?: WidthResizerVisibility;
    }
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
let minimapFrameRequested = false;
let minimapViewportFrameRequested = false;
let minimapRefreshTimer: number | undefined;
let minimapRoot: HTMLElement | null = null;
let minimapContent: HTMLElement | null = null;
let minimapViewport: HTMLElement | null = null;
let currentMinimapLayout: MinimapViewportLayout | null = null;
let minimapDragging = false;
let lastMinimapDocumentHeight = 0;
let minimapSourceReady = false;
let katexHasRun = false;
let mermaidRenderGeneration = 0;
let initialRenderPipelineCompleted = false;
const MAX_MERMAID_DIAGRAMS = 50;
const MERMAID_PER_DIAGRAM_TIMEOUT_MS = 3000;
const MERMAID_WATCHDOG_MS = 15_000;
let widthResizerVisibility: WidthResizerVisibility = "on-hover";
let viewerChromeEnabled = false;
let widthHandleRoot: HTMLElement | null = null;
let widthHandleDragging = false;
let widthHandleStartClientX = 0;
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
  if (!viewerChromeEnabled) {
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

function renderMath(): void {
  emitMark("mm-render-math-start", { mathCount: document.querySelectorAll("[data-tex]").length });
  const mathNodes = Array.from(document.querySelectorAll<HTMLElement>("[data-tex]"));
  const katex = hostWindow.katex;
  if (!katex) {
    katexHasRun = mathNodes.length === 0;
    return;
  }

  mathNodes.forEach((node) => {
    const tex = node.dataset.tex;
    if (!tex) {
      return;
    }

    katex.render(tex, node, {
      throwOnError: false,
      displayMode: node.classList.contains("math-display"),
      strict: "warn",
      trust: false
    });
  });
  katexHasRun = true;
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

function postScroll(): void {
  recordScrollIpc();
  postHostMessage({
    type: "scroll",
    ...getScrollState()
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
  const documentColumnRight = documentRect.right - minimapReservedWidth;
  const maxLeftBeforeMinimap = window.innerWidth - minimapReservedWidth - hitArea;
  const maxLeft = Math.max(0, Math.min(window.innerWidth - hitArea, maxLeftBeforeMinimap));
  const clampedLeft = Math.max(0, Math.min(maxLeft, documentColumnRight));
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

function cloneDocumentForMinimap(): HTMLElement | null {
  const source = document.querySelector<HTMLElement>(".mm-document");
  if (!source || !katexHasRun) {
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

function refreshMinimapContent(): void {
  emitMark("mm-minimap-refresh-start", { phase: "legacy" });
  ensureMinimap();
  if (!minimapContent || !minimapRoot) {
    emitMark("mm-minimap-refresh-end", { phase: "legacy" });
    return;
  }

  const clone = cloneDocumentForMinimap();
  minimapContent.replaceChildren();
  if (clone) {
    minimapContent.append(clone);
  }

  const root = document.scrollingElement ?? document.documentElement;
  lastMinimapDocumentHeight = root.scrollHeight;
  updateMinimapVisibility(true);
  updateMinimapViewport();
  emitMark("mm-minimap-refresh-end", { phase: "legacy" });
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

function queueMinimapRefresh(): void {
  if (minimapFrameRequested) {
    return;
  }

  minimapFrameRequested = true;
  window.requestAnimationFrame(() => {
    minimapFrameRequested = false;
    refreshMinimapContent();
  });
}

function queueMinimapRefreshAfterLayoutSettles(): void {
  window.clearTimeout(minimapRefreshTimer);
  minimapRefreshTimer = window.setTimeout(() => {
    const root = document.scrollingElement ?? document.documentElement;
    if (Math.abs(root.scrollHeight - lastMinimapDocumentHeight) < 1) {
      queueMinimapViewportUpdate();
      return;
    }

    lastMinimapDocumentHeight = root.scrollHeight;
    queueMinimapRefresh();
  }, MINIMAP_REFRESH_DEBOUNCE_MS);
}

function applyReadingPreferences(message: Extract<HostMessage, { type: "reading-preferences" }>): void {
  document.documentElement.style.setProperty("--mm-document-font-size", `${message.fontSize}px`);
  document.documentElement.style.setProperty("--mm-document-line-height", `${message.lineHeight}`);
  document.documentElement.style.setProperty("--mm-document-max-width", `${message.maxWidth}px`);
  minimapMode = message.minimapMode;
  viewerChromeEnabled = message.viewerChromeEnabled ?? true;
  applyViewerChromeState();
  widthResizerVisibility = normalizeWidthResizerVisibility(message.widthResizerVisibility);
  const widthResizerClasses = getWidthResizerVisibilityClasses(widthResizerVisibility);
  document.body.classList.toggle(WIDTH_RESIZER_ALWAYS_CLASS, widthResizerClasses.alwaysClass);
  const hadHostPreferences = hasReceivedHostPreferences;
  hasReceivedHostPreferences = true;
  if (hadHostPreferences) {
    queueMinimapViewportUpdate();
  } else {
    queueMinimapRefresh();
  }

  updateWidthHandlePosition();

  if (!hadHostPreferences && !initialRenderPipelineCompleted) {
    // First reading-preferences message — run the full Mermaid/code-block pipeline
    // before emitting layout-ready. Suppress duplicate scheduleLayoutReady calls
    // from this code path; the pipeline emits its own scheduleLayoutReady at end.
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
    return;
  }

  if (initialRenderPipelineCompleted) {
    scheduleLayoutReady();
  }
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
    if (viewerChromeEnabled) {
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
  // Defer renderMath / renderMermaid / renderCodeBlocks to runInitialRenderPipeline,
  // which is triggered by the first reading-preferences message from the host.
  wireLinks();
  wireViewerInteraction();
  wireWheelProxy();
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

document.addEventListener("scroll", () => {
  window.requestAnimationFrame(() => {
    postScroll();
    queueMinimapViewportUpdate();
  });
}, { passive: true });

window.addEventListener("message", (event) => handleHostMessage(event.data));
window.addEventListener("resize", () => {
  updateWidthHandlePosition();
  queueMinimapViewportUpdate();
});

(window as unknown as { __mmPerfReport: typeof getReport; __mmFpsSampler: ReturnType<typeof getFpsSampler> }).__mmPerfReport = getReport;
(window as unknown as { __mmPerfReport: typeof getReport; __mmFpsSampler: ReturnType<typeof getFpsSampler> }).__mmFpsSampler = getFpsSampler();
