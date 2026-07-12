import { execFileSync } from "node:child_process";
import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
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
    expect(source).toContain("initializeVirtualizedDocumentWindow(useCachedDocumentState);");
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
    const initializeStart = source.indexOf("function initializeVirtualizedDocumentWindow(");
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

  it("realization tracker is the sole placeholder and realization authority", () => {
    const source = readRendererSource();
    const windowSource = readFileSync("RendererWeb/src/virtualizedDocumentWindow.ts", "utf8");
    const createStart = source.indexOf("createVirtualizedDocumentWindowController({");
    const createEnd = source.indexOf("});", createStart);
    const controllerDeps = source.slice(createStart, createEnd);

    expect(controllerDeps).toContain("readMeasuredHeights:");
    expect(controllerDeps).toContain("realization:");
    const readProductionTypeScript = (directory: string): Array<{ path: string; source: string }> =>
      readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
        const path = join(directory, entry.name);
        return entry.isDirectory()
          ? readProductionTypeScript(path)
          : entry.isFile() && entry.name.endsWith(".ts")
            ? [{ path, source: readFileSync(path, "utf8") }]
            : [];
      });
    const detector = /(?:Math\.abs\([^)]*(?:offsetHeight[^)]*(?:intrinsic|fallbackBorderBoxHeight)|(?:intrinsic|fallbackBorderBoxHeight)[^)]*offsetHeight)[^)]*\)|offsetHeight\s*(?:===?|!==?|[<>]=?)\s*[^;\n]*(?:intrinsic|fallbackBorderBoxHeight)|(?:intrinsic|fallbackBorderBoxHeight)[^;\n]*?\s*(?:===?|!==?|[<>]=?)\s*offsetHeight)/g;
    const comparisons = readProductionTypeScript("RendererWeb/src").flatMap(file =>
      Array.from(file.source.matchAll(detector), match => ({ path: file.path, text: match[0] }))
    );
    expect(comparisons).toHaveLength(1);
    expect(comparisons.every(candidate => candidate.path.endsWith("virtualizedDocumentWindow.ts"))).toBe(true);
    expect("offsetHeight > intrinsicSize".match(detector)).not.toBeNull();
    expect(windowSource).toContain("const filterRealizedUpdates = (");
    expect(windowSource).toContain("watch.state !== \"real-ready\"");
    expect(windowSource).toContain("realizationTracker?.filterRealizedUpdates(blocks, updates) ?? updates");
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
    const baselineObservableBranch = baselineListener.slice(baselineListener.indexOf("  queuePostScroll();"));

    expect(source).toContain("if (!virtualizationEnabled) {\n    scrollToSourceLineInCurrentWindow(sourceLine);");
    expect(normalizeStatements(currentOffBranch)).toBe(normalizeStatements(baselineObservableBranch));
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
    const resizeOwner = sliceBetween(
      source,
      "function runLegacyResizeObserverWork",
      "function runLegacyDocumentFontsReadyWork"
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
    expect(domReady).toContain("runLegacyResizeObserverWork(documentEpoch)");
    expect(resizeOwner).toContain("scheduleResizeReactions(documentEpoch)");
    expect(resizeOwner).toContain("isCurrentDocumentEpoch(documentEpoch)");
    expect(heavyLive).toContain("captureDocumentEpoch");
    expect(heavyLive).toContain("isCurrentDocumentEpoch");
    expect(reset).toContain("cancelProgressiveDeferredEnhancements()");
    expect(reset).toContain("cancelMinimapRefreshAfterLayoutSettles()");
    expect(reset).toContain("cancelHeavyLiveUpdate()");
  });

  it("terminates cache restoration and retries occupied maintenance through explicit owners", () => {
    const source = readRendererSource();
    const coordinator = readSource("virtualizedScrollCoordination.ts");
    const cacheRestore = sliceBetween(source, "function restoreCachedScrollPosition", "function scheduleLayoutReady");

    expect(cacheRestore).toContain("finishCachedScrollRestore");
    expect(cacheRestore).toContain("catch");
    expect(cacheRestore).toContain("mm-virt-cache-restore-terminal");
    expect(coordinator).toContain("frame-transaction-occupied");
    expect(source).not.toContain("scheduleVirtualizedMaintenanceRetry");
    expect(source).not.toContain("VirtualizedMaintenanceRetryReasonProvider");
  });

  it("centralizes renderer-owned frame transaction policies in the coordinator", () => {
    const source = readRendererSource();
    const coordinator = readSource("virtualizedScrollCoordination.ts");

    for (const forbidden of [
      "scheduleVirtualizedStandaloneOperation",
      "scheduleExistingVirtualizedOperation",
      "scheduleVirtualizedElementLanding",
      "scheduleFrameTransaction(() => undefined",
      "scheduleVirtualizedMaintenanceRetry",
      "VirtualizedMaintenanceRetryReasonProvider",
    ]) {
      expect(source).not.toContain(forbidden);
    }

    expect(coordinator).toContain("runFrameTransaction");
    for (const policy of [
      "standalone-release-after-write",
      "existing-release-after-write",
      "element-landing-release-after-write",
    ]) {
      expect(coordinator).toContain(policy);
    }
    expect(coordinator).toContain("requestHeldOperationTarget");
    expect(source).not.toContain("empty-commit-retain-operation");
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
      .toBeLessThan(cacheRestore.indexOf("controller?.ensureSectionRangeRendered"));
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

  it("tickets every admitted geometry producer at scheduling and recensuses before quiet candidates", () => {
    const renderer = readRendererSource();
    const plane = readSource("scrollOwnershipControlPlane.ts");
    const windowController = readSource("virtualizedDocumentWindow.ts");
    const insertedContent = sliceBetween(
      renderer,
      "function prepareVirtualizedInsertedContent",
      "function scheduleVirtualizedWindowFontReadiness"
    );

    for (const source of [
      "window-render",
      "measured-height-adoption",
      "calibration",
      "window-math",
      "window-fonts",
      "resize-observer",
    ]) {
      expect(renderer).toContain(`beginVirtualizedGeometryWork("${source}"`);
    }
    expect(insertedContent).toContain('"window-mermaid",');
    expect(insertedContent).toContain("mountGeneration\n  )");
    expect(insertedContent).toContain("mountGeneration === virtualizedWindowMountGeneration");
    expect(insertedContent).toContain("virtualizedWindowMathController === mathController");
    expect(renderer).toContain("beginVirtualizedGeometryWork(geometrySource, geometryMountGeneration)");
    expect(renderer).toContain("geometryMutated(ticket)");
    expect(plane).toContain("prepareGeometrySettleCandidate");
    expect(windowController).toContain("recensusRealizationWatches");
  });

  it("holds scroll-window and source-line reassert only for active restore mode", () => {
    const source = readRendererSource();
    const scrollWindow = sliceBetween(
      source,
      "function updateVirtualizedWindowForScroll",
      "function finishVirtualizedMeasuredHeightTerminalSubscribers"
    );
    const sourceLine = sliceBetween(
      source,
      "function invalidateSourceLineAnchors",
      "function suppressPreviewSourceLinePost"
    );

    expect(source).toContain("function isVirtualizedHeldRestoreInProgress");
    expect(source).toContain('readActive()?.mode === "restore"');
    expect(scrollWindow).toContain("isVirtualizedHeldRestoreInProgress()");
    expect(sourceLine).toContain("isVirtualizedHeldRestoreInProgress()");
    expect(source.match(
      /reassertPendingTarget: !hasVirtualizedNavigationRegistration\(\)/g
    )).toHaveLength(5);
    expect(source).not.toContain("virtualizedProgrammaticNavigationPostSettleTarget");
  });

  it("navigation cache and minimap consume same-epoch confirmation settlement", () => {
    const source = readRendererSource();
    const cacheRestore = sliceBetween(source, "function restoreCachedScrollPosition", "function scheduleLayoutReady");
    const cachedReady = sliceBetween(source, "function postCachedLayoutReady", "function flushPostLayoutReadyWork");

    expect(source).toContain("awaitConfirmedVirtualizedGeometry");
    expect(source).toContain("waitForGeometrySettled(");
    expect(source).toContain("operation.operationEpoch");
    expect(source).toContain("plane.holds(operation.lease, confirmation.payload.geometryEpoch)");
    expect(cacheRestore).not.toContain("queueCachedGeometryRefresh(");
    expect(cachedReady).toContain("if (!virtualizationEnabled && cachedLayoutState !== null)");
  });

  it("flag-on resolved images carry mount-stable intrinsic ratio", () => {
    const fixture = JSON.parse(readFileSync("RendererWeb/vitest/fixtures/hostImageMarkup.json", "utf8")) as {
      flagOn: string;
      intrinsicHeight: number;
      intrinsicWidth: number;
    };
    const template = document.createElement("template");
    template.innerHTML = fixture.flagOn;
    const image = template.content.querySelector<HTMLImageElement>("img")!;
    const readRatio = (candidate: HTMLImageElement): number | null => {
      const width = Number(candidate.getAttribute("width"));
      const height = Number(candidate.getAttribute("height"));
      return candidate.hasAttribute("width")
        && candidate.hasAttribute("height")
        && Number.isFinite(width)
        && Number.isFinite(height)
        && width > 0
        && height > 0
        ? width / height
        : null;
    };

    expect(readRatio(image))
      .toBe(fixture.intrinsicWidth / fixture.intrinsicHeight);
    image.removeAttribute("height");
    expect(readRatio(image)).toBeNull();
  });

  it("keeps the H3 diagnostic observer test-only and out of production", () => {
    const source = readRendererSource();
    expect(source).not.toContain("H3DiagnosticObserver");
    expect(source).not.toContain("mm-virt-h3-unregistered-mover");
  });
});
