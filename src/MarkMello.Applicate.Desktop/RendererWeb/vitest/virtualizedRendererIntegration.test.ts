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

  it("documents every known off-window integration that remains deferred under the flag", () => {
    const renderer = readRendererSource();
    const findBar = readFileSync("RendererWeb/src/findBar.ts", "utf8");
    const sourceLineSync = readFileSync("RendererWeb/src/sourceLineSync.ts", "utf8");
    const combined = `${renderer}\n${findBar}\n${sourceLineSync}`;

    for (const expected of [
      "VIRT-TODO(integration): find-in-page",
      "VIRT-TODO(integration): minimap",
    ]) {
      expect(combined).toContain(expected);
    }
    for (const resolved of [
      "VIRT-TODO(integration): TOC",
      "VIRT-TODO(integration): scroll-to-heading",
      "VIRT-TODO(integration): anchor-link scroll-to",
      "VIRT-TODO(integration): scroll-to-block",
      ["VIRT-TODO(integration):", "source-line sync"].join(" "),
    ]) {
      expect(combined).not.toContain(resolved);
    }
  });
});
