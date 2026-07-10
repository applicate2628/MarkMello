import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

function readRendererSource(): string {
  return readFileSync("RendererWeb/src/renderer.ts", "utf8");
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
});
