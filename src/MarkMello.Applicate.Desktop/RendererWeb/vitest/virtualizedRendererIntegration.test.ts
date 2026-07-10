import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const TASK_4_BASELINE = "e75df2cbc53407420bfce82d0dfb94fc43e5c684";

function readRendererSource(): string {
  return readFileSync("RendererWeb/src/renderer.ts", "utf8");
}

function readSource(path: string): string {
  return readFileSync(`RendererWeb/src/${path}`, "utf8");
}

function readBaselineRendererSource(): string {
  return execFileSync(
    "git",
    ["show", `${TASK_4_BASELINE}:src/MarkMello.Applicate.Desktop/RendererWeb/src/renderer.ts`],
    { encoding: "utf8" }
  );
}

function sliceBetween(source: string, startMarker: string, endMarker: string): string {
  const start = source.indexOf(startMarker);
  const end = source.indexOf(endMarker, start + startMarker.length);
  expect(start).toBeGreaterThanOrEqual(0);
  expect(end).toBeGreaterThan(start);
  return source.slice(start + startMarker.length, end);
}

function normalizeStatements(source: string): string {
  return source
    .split("\n")
    .map(line => line.trim())
    .filter(line => line.length > 0 && line !== "return;")
    .join("\n");
}

describe("renderer virtualization wiring", () => {
  it("uses the separate MARKMELLO_VIRTUALIZATION flag and not the shadow flag for DOM windowing", () => {
    const source = readRendererSource();

    expect(source).toContain("readVirtualizationFlag(window, document)");
    expect(source).toContain("createVirtualizedDocumentWindowController");
    expect(source).toContain("initializeVirtualizedDocumentWindow();");
  });

  it("updates the virtualized window before scroll IPC reads the top visible block", () => {
    const source = readRendererSource();
    const scrollCoalescerStart = source.indexOf("const queuePostScroll = createScrollCoalescer({");
    const scrollCoalescerEnd = source.indexOf("document.addEventListener(\"scroll\"", scrollCoalescerStart);
    const scrollCoalescer = source.slice(scrollCoalescerStart, scrollCoalescerEnd);

    expect(scrollCoalescerStart).toBeGreaterThanOrEqual(0);
    expect(scrollCoalescerEnd).toBeGreaterThan(scrollCoalescerStart);
    const updateIndex = scrollCoalescer.indexOf("updateVirtualizedWindowForScroll();");
    const postIndex = scrollCoalescer.indexOf("postScroll();");
    expect(updateIndex).toBeGreaterThanOrEqual(0);
    expect(postIndex).toBeGreaterThanOrEqual(0);
    expect(updateIndex).toBeLessThan(postIndex);
  });

  it("keeps the C2 integration surface free of deferred VIRT-TODO markers", () => {
    const renderer = readRendererSource();
    const findBar = readFileSync("RendererWeb/src/findBar.ts", "utf8");
    const sourceLineSync = readFileSync("RendererWeb/src/sourceLineSync.ts", "utf8");
    const combined = `${renderer}\n${findBar}\n${sourceLineSync}`;
    const minimapMarker = ["VIRT-TODO(integration)", "minimap"].join(": ");

    for (const resolved of [
      minimapMarker,
      "VIRT-TODO(integration): find-in-page",
      "VIRT-TODO(integration): TOC",
      "VIRT-TODO(integration): scroll-to-heading",
      "VIRT-TODO(integration): anchor-link scroll-to",
      "VIRT-TODO(integration): scroll-to-block",
      ["VIRT-TODO(integration):", "source-line sync"].join(" "),
    ]) {
      expect(combined).not.toContain(resolved);
    }
  });

  it("installs event-realization machinery only through the flag-on controller path", () => {
    const source = readRendererSource();
    const initializeStart = source.indexOf("function initializeVirtualizedDocumentWindow()");
    const initializeEnd = source.indexOf("function updateVirtualizedWindowForScroll", initializeStart);
    const initializeBody = source.slice(initializeStart, initializeEnd);
    const resetStart = source.indexOf("function resetVirtualizedDocumentWindow");
    const resetEnd = source.indexOf("function initializeVirtualizedDocumentWindow", resetStart);
    const resetBody = source.slice(resetStart, resetEnd);

    expect(initializeStart).toBeGreaterThanOrEqual(0);
    expect(initializeEnd).toBeGreaterThan(initializeStart);
    expect(initializeBody).toContain("if (!virtualizationEnabled)");
    expect(initializeBody).toContain("realization:");
    expect(initializeBody).toContain("contentvisibilityautostatechange");
    expect(resetBody).toContain("virtualizedDocumentWindowController?.dispose()");
    expect(source).not.toContain("document.addEventListener(\"contentvisibilityautostatechange\"");
  });

  it("keeps the realization tracker as the sole virtualized height adoption authority", () => {
    const source = readRendererSource();
    const createStart = source.indexOf("createVirtualizedDocumentWindowController({");
    const createEnd = source.indexOf("});", createStart);
    const controllerDeps = source.slice(createStart, createEnd);

    expect(controllerDeps).toContain("readMeasuredHeights:");
    expect(controllerDeps).toContain("realization:");
    expect(source).not.toContain("offsetHeight !==");
    expect(source).not.toContain("Math.abs(item.height - intrinsicSize) > CONTENT_VISIBILITY_PLACEHOLDER_TOLERANCE_PX");
  });

  it("enables Mermaid proxy lifecycle ownership only through the virtualization flag", () => {
    const source = readRendererSource();

    expect(source).toContain(
      "virtualizationEnabled ? { manageVirtualizedProxyLifecycle: true } : undefined"
    );
    expect(source).not.toContain("manageVirtualizedProxyLifecycle: false");
  });

  it("composes one scroll ownership plane only for the true flag and invalidates it before document state", () => {
    const source = readRendererSource();
    const resetStart = source.indexOf("function resetModuleGlobalsForLoadDocument()");
    const resetEnd = source.indexOf("type EnsureChromeNodesOptions", resetStart);
    const resetBody = source.slice(resetStart, resetEnd);

    expect(source.match(/createScrollOwnershipControlPlane\(/g) ?? []).toHaveLength(1);
    expect(source).toContain("virtualizationEnabled\n  ? createScrollOwnershipControlPlane");
    expect(source).toContain('mmVirtualizationActive = "true"');
    expect(resetBody.indexOf("scrollOwnershipControlPlane?.invalidateDocument()"))
      .toBeLessThan(resetBody.indexOf("++initialRenderPipelineGeneration"));
  });

  it("keeps the control-plane drain as the only flag-on programmatic root writer", () => {
    for (const path of [
      "virtualizedDocumentWindow.ts",
      "windowTargetResolver.ts",
      "virtualizedFindProvider.ts",
    ]) {
      const source = readSource(path);
      expect(source.match(/\.scrollTop\s*=|\.scrollIntoView\(|\bwindow\.scrollTo\(|\bwindow\.scrollBy\(/g) ?? [])
        .toEqual([]);
    }

    const renderer = readRendererSource();
    for (const forbidden of [
      "root.scrollTop = nextScrollTop",
      "root.scrollTop = model.sectionTop",
      "root.scrollTop = model.scrollTopForAnchor",
    ]) {
      expect(renderer).not.toContain(forbidden);
    }
  });

  it("keeps calibration terminal and preserves the cold layout-ready fallback", () => {
    const source = readRendererSource();
    const calibrationStart = source.indexOf("function runVirtualizedCalibration(");
    const calibrationEnd = source.indexOf("function postScroll()", calibrationStart);
    const calibration = source.slice(calibrationStart, calibrationEnd);
    const layoutReadyStart = source.indexOf("function scheduleLayoutReady(");
    const layoutReadyEnd = source.indexOf("function flushPostLayoutReadyWork()", layoutReadyStart);
    const layoutReady = source.slice(layoutReadyStart, layoutReadyEnd);

    expect(calibration).toContain('scheduleVirtualizedMaintenance("calibration"');
    expect(calibration).toContain('operation.requestScrollTop(target, "calibration")');
    expect(calibration.match(/\.scrollTop\s*=|\bwindow\.scrollTo\(|\bwindow\.scrollBy\(/g) ?? []).toEqual([]);
    expect(layoutReady).toContain("}, 120);");
    expect(layoutReady).not.toContain("waitForGeometrySettled");
  });

  it("preserves the baseline flag-off scroll-listener semantics and statement order", () => {
    const source = readRendererSource();
    const baseline = readBaselineRendererSource();
    const baselineListener = sliceBetween(
      baseline,
      'document.addEventListener("scroll", () => {',
      '}, { passive: true });'
    );
    const currentListener = sliceBetween(
      source,
      'document.addEventListener("scroll", () => {',
      '}, { passive: true });'
    );
    const currentOffBranch = sliceBetween(currentListener, "  } else {", "    return;\n  }");

    expect(source).toContain("if (!virtualizationEnabled) {\n    scrollToSourceLineInCurrentWindow(sourceLine);");
    expect(normalizeStatements(currentOffBranch)).toBe(normalizeStatements(baselineListener));
    expect(currentListener.indexOf("classifyNativeScroll")).toBeLessThan(currentListener.indexOf("queuePostScroll();"));
  });

  it("gates every reviewed document-owned continuation and cancels owned timers on reset", () => {
    const source = readRendererSource();
    const renderMath = sliceBetween(source, "function renderMath()", "function getCurrentTheme()");
    const progressive = sliceBetween(
      source,
      "function scheduleProgressiveDeferredEnhancements",
      "function getViewportHeightForMermaid"
    );
    const activeHeading = sliceBetween(
      source,
      "function rebuildActiveHeadingObserver",
      "function shouldShowMinimap"
    );
    const minimapSettle = sliceBetween(
      source,
      "function queueMinimapRefreshAfterLayoutSettles",
      "function cancelDeferredMinimapContentRefresh"
    );
    const resizeReactions = sliceBetween(
      source,
      "function scheduleResizeReactions",
      "type AppliedReadingPreferences"
    );
    const heavyLive = sliceBetween(source, "function scheduleHeavyLiveUpdate", "function scrollLegacyHeadingAnchor");
    const domReady = sliceBetween(
      source,
      'document.addEventListener("DOMContentLoaded"',
      "const queuePostScroll"
    );
    const reset = sliceBetween(source, "function resetModuleGlobalsForLoadDocument", "type EnsureChromeNodesOptions");

    expect(renderMath.match(/captureDocumentEpoch/g)?.length ?? 0).toBeGreaterThanOrEqual(1);
    expect(renderMath.match(/isCurrentDocumentEpoch/g)?.length ?? 0).toBeGreaterThanOrEqual(3);
    expect(progressive).toContain("captureDocumentEpoch");
    expect(progressive).toContain("isCurrentDocumentEpoch");
    expect(progressive).toContain("progressiveDeferredEnhancementHandle");
    expect(activeHeading).toContain("captureDocumentEpoch");
    expect(activeHeading).toContain("isCurrentDocumentEpoch");
    expect(minimapSettle).toContain("captureDocumentEpoch");
    expect(minimapSettle).toContain("isCurrentDocumentEpoch");
    expect(resizeReactions).toContain("documentEpoch");
    expect(resizeReactions).toContain("isCurrentDocumentEpoch");
    expect(domReady).toContain("scheduleResizeReactions(documentEpoch)");
    expect(heavyLive).toContain("captureDocumentEpoch");
    expect(heavyLive).toContain("isCurrentDocumentEpoch");
    expect(reset).toContain("cancelProgressiveDeferredEnhancements()");
    expect(reset).toContain("cancelMinimapRefreshAfterLayoutSettles()");
    expect(reset).toContain("cancelHeavyLiveUpdate()");
  });

  it("terminates cache restoration and retries occupied maintenance through explicit owners", () => {
    const source = readRendererSource();
    const cacheRestore = sliceBetween(source, "function restoreCachedScrollPosition", "function scheduleLayoutReady");
    const maintenance = sliceBetween(
      source,
      "function scheduleVirtualizedMaintenance",
      "function captureCurrentVirtualizedReadingAnchor"
    );

    expect(cacheRestore).toContain("finishCachedScrollRestore");
    expect(cacheRestore).toContain("catch");
    expect(cacheRestore).toContain("mm-virt-cache-restore-terminal");
    expect(maintenance).toContain("scheduleVirtualizedMaintenanceRetry");
    expect(maintenance).toContain("frame-transaction-occupied");
  });

  it("consumes initial-window reconciliation only inside an owning frame transaction", () => {
    const source = readRendererSource();
    const initialize = sliceBetween(
      source,
      "function initializeVirtualizedDocumentWindow",
      "function updateVirtualizedWindowForScroll"
    );
    const cacheRestore = sliceBetween(source, "function restoreCachedScrollPosition", "function scheduleLayoutReady");
    const coldLoad = sliceBetween(source, "scrollWindowToTop: () => {", "emitMark: (name, detail)");

    expect(initialize).toContain("pendingInitialVirtualizedWindowWork = operation =>");
    expect(initialize).toContain("initialOperation.scheduleFrameTransaction(() => {");
    expect(initialize).toContain("consumePendingInitialVirtualizedWindow(initialOperation);");
    expect(cacheRestore.indexOf("consumePendingInitialVirtualizedWindow(operation)"))
      .toBeLessThan(cacheRestore.indexOf("controller.ensureSectionRendered"));
    expect(coldLoad.indexOf("consumePendingInitialVirtualizedWindow(operation)"))
      .toBeLessThan(coldLoad.indexOf('operation.requestScrollTop(0, "cold-load-reset")'));
  });

  it("disables browser anchoring only on the actual flag-on scroll root", () => {
    const css = readFileSync("RendererWeb/assets/renderer.css", "utf8");

    expect(css).toContain('[data-mm-virtualization-active="true"]');
    expect(css).toMatch(/\[data-mm-virtualization-active="true"\]\s*\{[^}]*overflow-anchor:\s*none;/s);
    expect(css).not.toMatch(/(?:^|[},])\s*(?:html|:root)\s*\{[^}]*overflow-anchor:\s*none;/s);

    document.documentElement.innerHTML = `<head><style>${css}</style></head><body></body>`;
    const root = document.documentElement;
    root.dataset.mmVirtualizationActive = "true";
    expect(getComputedStyle(root).overflowAnchor).toBe("none");
    delete root.dataset.mmVirtualizationActive;
    expect(getComputedStyle(root).overflowAnchor).not.toBe("none");
  });
});
