"use strict";
(() => {
  // RendererWeb/src/minimapState.ts
  var DEFAULT_MINIMAP_POST_EPSILON = 0.5;
  function shouldPostMinimapState(previous, next, force = false, epsilon = DEFAULT_MINIMAP_POST_EPSILON) {
    if (force || !previous.hasPosted || previous.visible !== next.visible) {
      return true;
    }
    return Math.abs(next.reservedWidth - previous.reservedWidth) >= epsilon;
  }

  // RendererWeb/src/minimapLayout.ts
  var DEFAULT_MINIMUM_THUMB_HEIGHT = 22;
  function calculateMinimapViewportLayout(input) {
    if (input.minimapWidth <= 0 || input.minimapHeight <= 0 || input.documentWidth <= 0 || input.documentHeight <= 0 || input.viewportHeight <= 0) {
      return null;
    }
    const minimumThumbHeight = input.minimumThumbHeight ?? DEFAULT_MINIMUM_THUMB_HEIGHT;
    const scale = input.minimapWidth / input.documentWidth;
    const projectedDocumentHeight = input.documentHeight * scale;
    const maximumScrollTop = Math.max(0, input.documentHeight - input.viewportHeight);
    const scrollProgress = maximumScrollTop > 0 ? Math.max(0, Math.min(1, input.scrollTop / maximumScrollTop)) : 0;
    const overflowHeight = Math.max(0, projectedDocumentHeight - input.minimapHeight);
    const contentTranslateY = overflowHeight > 0 ? -scrollProgress * overflowHeight : 0;
    const thumbHeight = Math.min(
      input.minimapHeight,
      Math.max(minimumThumbHeight, input.viewportHeight * scale)
    );
    const rawThumbTop = input.scrollTop * scale + contentTranslateY;
    const thumbTop = Math.max(0, Math.min(input.minimapHeight - thumbHeight, rawThumbTop));
    return {
      contentWidth: input.documentWidth,
      scale,
      contentTranslateY,
      transform: `translateY(${contentTranslateY}px) scale(${scale})`,
      thumbTop,
      thumbHeight
    };
  }

  // RendererWeb/src/widthResizerVisibility.ts
  function normalizeWidthResizerVisibility(raw) {
    return raw === "always" ? "always" : "on-hover";
  }
  function getWidthResizerVisibilityClasses(visibility) {
    return {
      alwaysClass: visibility === "always"
    };
  }

  // RendererWeb/src/renderer.ts
  var hostWindow = window;
  var MINIMAP_CLASS = "mm-minimap";
  var MINIMAP_VIEWPORT_CLASS = "mm-minimap-viewport";
  var MINIMAP_VISIBLE_CLASS = "mm-has-minimap";
  var MINIMAP_REFRESH_DEBOUNCE_MS = 100;
  var WIDTH_HANDLE_CLASS = "mm-width-handle";
  var WIDTH_HANDLE_DRAGGING_CLASS = "mm-dragging";
  var WIDTH_RESIZER_ALWAYS_CLASS = "mm-width-resizer-always";
  var minimapMode = "off";
  var hasReceivedHostPreferences = false;
  var minimapFrameRequested = false;
  var minimapViewportFrameRequested = false;
  var minimapRefreshTimer;
  var minimapRoot = null;
  var minimapContent = null;
  var minimapViewport = null;
  var currentMinimapLayout = null;
  var minimapDragging = false;
  var lastMinimapDocumentHeight = 0;
  var minimapSourceReady = false;
  var katexHasRun = false;
  var widthResizerVisibility = "on-hover";
  var viewerChromeEnabled = false;
  var widthHandleRoot = null;
  var widthHandleDragging = false;
  var widthHandleStartClientX = 0;
  var pendingWidthDragDeltaX = 0;
  var widthDragFrameRequested = false;
  var layoutReadyGeneration = 0;
  var layoutReadyTimer;
  var lastPostedMinimapState = { hasPosted: false, visible: false, reservedWidth: 0 };
  var minimapPolicy = {
    // Mirrors ApplicateDocumentMinimapBuildPolicy until the host sends minimap-policy.
    // WebView uses CSS scrollHeight while Native uses Avalonia visual height; keep
    // this shared value intentionally permissive until WebView-specific tuning exists.
    minHostWidth: 1100,
    minScrollableViewportRatio: 1.5,
    maxDetailedDocumentHeight: 24e4
  };
  function applyViewerChromeState() {
    document.documentElement.dataset.mmChrome = viewerChromeEnabled ? "on" : "off";
    if (!viewerChromeEnabled) {
      window.scrollTo({ left: 0, top: 0, behavior: "instant" });
    }
  }
  function postHostMessage(message) {
    const serialized = JSON.stringify(message);
    if (hostWindow.chrome?.webview) {
      hostWindow.chrome.webview.postMessage(message);
      return;
    }
    hostWindow.invokeCSharpAction?.(serialized);
  }
  function renderMath() {
    const mathNodes = Array.from(document.querySelectorAll("[data-tex]"));
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
  function getScrollState() {
    const root = document.scrollingElement ?? document.documentElement;
    return {
      scrollTop: root.scrollTop,
      scrollHeight: root.scrollHeight,
      clientHeight: root.clientHeight
    };
  }
  function postScroll() {
    postHostMessage({
      type: "scroll",
      ...getScrollState()
    });
  }
  function postLayoutReady() {
    postScroll();
    postHostMessage({
      type: "layout-ready",
      ...getScrollState()
    });
  }
  function scheduleLayoutReady() {
    const generation = ++layoutReadyGeneration;
    let completed = false;
    if (layoutReadyTimer !== void 0) {
      window.clearTimeout(layoutReadyTimer);
    }
    const complete = () => {
      if (completed || generation !== layoutReadyGeneration) {
        return;
      }
      completed = true;
      if (layoutReadyTimer !== void 0) {
        window.clearTimeout(layoutReadyTimer);
        layoutReadyTimer = void 0;
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
  function readRootPixelVariable(name, fallback) {
    const raw = getComputedStyle(document.documentElement).getPropertyValue(name);
    const parsed = Number.parseFloat(raw);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
  }
  function ensureWidthHandle() {
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
  function updateWidthHandlePosition() {
    ensureWidthHandle();
    if (!widthHandleRoot) {
      return;
    }
    widthHandleRoot.hidden = !hasReceivedHostPreferences || !viewerChromeEnabled;
    if (widthHandleRoot.hidden) {
      return;
    }
    const documentElement = document.querySelector(".mm-document");
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
  function postWidthDragMove() {
    if (widthDragFrameRequested) {
      return;
    }
    widthDragFrameRequested = true;
    window.requestAnimationFrame(() => {
      widthDragFrameRequested = false;
      postHostMessage({ type: "width-drag", phase: "move", deltaX: pendingWidthDragDeltaX });
    });
  }
  function handleWidthHandlePointerDown(event) {
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
  function handleWidthHandlePointerMove(event) {
    if (!widthHandleDragging) {
      return;
    }
    pendingWidthDragDeltaX = event.clientX - widthHandleStartClientX;
    postWidthDragMove();
    event.preventDefault();
  }
  function handleWidthHandlePointerUp(event) {
    if (!widthHandleDragging) {
      return;
    }
    const deltaX = event.clientX - widthHandleStartClientX;
    widthHandleDragging = false;
    widthHandleRoot?.classList.remove(WIDTH_HANDLE_DRAGGING_CLASS);
    try {
      widthHandleRoot?.releasePointerCapture(event.pointerId);
    } catch {
    }
    postHostMessage({ type: "width-drag", phase: "end", deltaX });
    event.preventDefault();
  }
  function handleWidthHandlePointerCaptureLost() {
    cancelWidthHandleDrag();
  }
  function cancelWidthHandleDrag() {
    if (!widthHandleDragging) {
      return;
    }
    widthHandleDragging = false;
    widthHandleRoot?.classList.remove(WIDTH_HANDLE_DRAGGING_CLASS);
    postHostMessage({ type: "width-drag", phase: "end", deltaX: pendingWidthDragDeltaX });
  }
  function ensureMinimap() {
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
  function cloneDocumentForMinimap() {
    const source = document.querySelector(".mm-document");
    if (!source || !katexHasRun) {
      minimapSourceReady = false;
      return null;
    }
    const clone = source.cloneNode(true);
    minimapSourceReady = true;
    clone.removeAttribute("id");
    clone.setAttribute("aria-hidden", "true");
    clone.inert = true;
    clone.querySelectorAll("[id]").forEach((node) => node.removeAttribute("id"));
    clone.querySelectorAll("*").forEach((node) => {
      for (const attribute of Array.from(node.attributes)) {
        if (attribute.name === "role" || attribute.name === "name" || attribute.name === "for" || attribute.name.startsWith("aria-") && attribute.name !== "aria-hidden") {
          node.removeAttribute(attribute.name);
        }
      }
    });
    clone.querySelectorAll("a, button, input, textarea, select").forEach((node) => {
      node.setAttribute("tabindex", "-1");
      node.removeAttribute("href");
    });
    return clone;
  }
  function refreshMinimapContent() {
    ensureMinimap();
    if (!minimapContent || !minimapRoot) {
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
  }
  function shouldShowMinimap() {
    const root = document.scrollingElement ?? document.documentElement;
    const documentHeight = root.scrollHeight;
    const viewportHeight = root.clientHeight;
    if (!hasReceivedHostPreferences || !viewerChromeEnabled || !minimapSourceReady || minimapMode === "off" || viewportHeight <= 0 || documentHeight <= viewportHeight) {
      return false;
    }
    if (documentHeight > minimapPolicy.maxDetailedDocumentHeight) {
      return false;
    }
    if (minimapMode === "on") {
      return true;
    }
    return window.innerWidth >= minimapPolicy.minHostWidth && documentHeight >= viewportHeight * minimapPolicy.minScrollableViewportRatio;
  }
  function updateMinimapVisibility(forcePostState = false) {
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
  function getCurrentMinimapReservedWidth() {
    if (!minimapRoot || minimapRoot.hidden) {
      return 0;
    }
    const minimapWidth = minimapRoot.getBoundingClientRect().width || readRootPixelVariable("--mm-minimap-width", 0);
    const minimapGap = readRootPixelVariable("--mm-minimap-gap", 0);
    return Math.max(0, minimapWidth + minimapGap * 2);
  }
  function postMinimapState(visible, force = false) {
    const reservedWidth = visible ? getCurrentMinimapReservedWidth() : 0;
    const nextState = { visible, reservedWidth };
    if (!shouldPostMinimapState(lastPostedMinimapState, nextState, force)) {
      return;
    }
    lastPostedMinimapState = { ...nextState, hasPosted: true };
    postHostMessage({ type: "minimap-state", visible, reservedWidth });
  }
  function updateMinimapViewport() {
    ensureMinimap();
    if (!minimapRoot || !minimapContent || !minimapViewport) {
      return;
    }
    const root = document.scrollingElement ?? document.documentElement;
    const source = document.querySelector(".mm-document");
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
      1
    );
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
  function scrollFromMinimapClientY(clientY) {
    if (!minimapRoot) {
      return;
    }
    const root = document.scrollingElement ?? document.documentElement;
    const rect = minimapRoot.getBoundingClientRect();
    const minimapY = Math.max(0, Math.min(rect.height, clientY - rect.top));
    const documentY = currentMinimapLayout ? (minimapY - currentMinimapLayout.contentTranslateY) / currentMinimapLayout.scale : minimapY / Math.max(1, rect.height) * root.scrollHeight;
    const target = documentY - root.clientHeight / 2;
    const maximum = Math.max(0, root.scrollHeight - root.clientHeight);
    window.scrollTo({ top: Math.max(0, Math.min(maximum, target)), behavior: "instant" });
  }
  function scrollToProgress(progressPercent) {
    const root = document.scrollingElement ?? document.documentElement;
    const maximum = Math.max(0, root.scrollHeight - root.clientHeight);
    const progress = Number.isFinite(progressPercent) ? Math.max(0, Math.min(100, progressPercent)) : 0;
    window.scrollTo({ top: maximum * (progress / 100), behavior: "instant" });
  }
  function handleMinimapPointerDown(event) {
    minimapDragging = true;
    minimapRoot?.setPointerCapture(event.pointerId);
    scrollFromMinimapClientY(event.clientY);
    event.preventDefault();
  }
  function handleMinimapPointerMove(event) {
    if (!minimapDragging) {
      return;
    }
    scrollFromMinimapClientY(event.clientY);
    event.preventDefault();
  }
  function handleMinimapPointerUp(event) {
    minimapDragging = false;
    try {
      minimapRoot?.releasePointerCapture(event.pointerId);
    } catch {
    }
  }
  function queueMinimapViewportUpdate() {
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
  function queueMinimapRefresh() {
    if (minimapFrameRequested) {
      return;
    }
    minimapFrameRequested = true;
    window.requestAnimationFrame(() => {
      minimapFrameRequested = false;
      refreshMinimapContent();
    });
  }
  function queueMinimapRefreshAfterLayoutSettles() {
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
  function applyReadingPreferences(message) {
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
    scheduleLayoutReady();
  }
  function handleHostMessage(raw) {
    const message = raw;
    if (message.type === "theme") {
      document.documentElement.dataset.theme = message.theme;
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
  function wireLinks() {
    document.addEventListener("click", (event) => {
      const target = event.target instanceof Element ? event.target.closest("a[href]") : null;
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
  function wireViewerInteraction() {
    document.addEventListener("pointerdown", (event) => {
      if (event.button === 0) {
        postHostMessage({ type: "viewer-interaction" });
      }
    }, true);
  }
  function wireWheelProxy() {
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
  document.addEventListener("DOMContentLoaded", () => {
    applyViewerChromeState();
    renderMath();
    wireLinks();
    wireViewerInteraction();
    wireWheelProxy();
    postHostMessage({
      type: "document-ready",
      mathCount: document.querySelectorAll("[data-tex]").length
    });
    postScroll();
    const documentElement = document.querySelector(".mm-document");
    if (documentElement) {
      const resizeObserver = new ResizeObserver(() => {
        queueMinimapRefreshAfterLayoutSettles();
        updateWidthHandlePosition();
        window.requestAnimationFrame(postScroll);
      });
      resizeObserver.observe(documentElement);
    }
    document.fonts?.ready.then(() => queueMinimapRefreshAfterLayoutSettles()).catch(() => void 0);
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
})();
