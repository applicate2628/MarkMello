import { afterEach, describe, expect, it } from "vitest";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import {
  readVirtualizationFlag,
  readRendererBooleanFlag,
} from "../src/virtualizationFlags";
import { readVirtualizationShadowFlag } from "../src/virtualizationShadow";

let localStorageValues = new Map<string, string>();
const TASK_5_PARENT = "7de62689420a87df65b23fd938a09bc67104c973";

function readRendererSource(): string {
  return readFileSync("RendererWeb/src/renderer.ts", "utf8");
}

function sliceBetween(source: string, start: string, end: string): string {
  const startIndex = source.indexOf(start);
  const endIndex = source.indexOf(end, startIndex + start.length);
  expect(startIndex).toBeGreaterThanOrEqual(0);
  expect(endIndex).toBeGreaterThan(startIndex);
  return source.slice(startIndex, endIndex);
}

Object.defineProperty(window, "localStorage", {
  configurable: true,
  value: {
    getItem: (key: string) => localStorageValues.get(key) ?? null,
    setItem: (key: string, value: string) => {
      localStorageValues.set(key, value);
    },
    clear: () => {
      localStorageValues = new Map<string, string>();
    },
  },
});

describe("renderer virtualization flags", () => {
  afterEach(() => {
    window.localStorage.clear();
    document.documentElement.removeAttribute("data-markmello-virtualization");
    document.documentElement.removeAttribute("data-markmello-virt-shadow");
    delete (window as Window & { MARKMELLO_VIRTUALIZATION?: unknown }).MARKMELLO_VIRTUALIZATION;
    delete (window as Window & { MARKMELLO_VIRT_SHADOW?: unknown }).MARKMELLO_VIRT_SHADOW;
  });

  it("keeps MARKMELLO_VIRTUALIZATION owned by host-injected sources, not localStorage", () => {
    expect(readVirtualizationFlag(window, document)).toBe(false);

    window.localStorage.setItem("MARKMELLO_VIRTUALIZATION", "1");
    expect(readVirtualizationFlag(window, document)).toBe(false);

    window.localStorage.setItem("MARKMELLO_VIRTUALIZATION", "off");
    document.documentElement.dataset.markmelloVirtualization = "true";
    expect(readVirtualizationFlag(window, document)).toBe(true);

    document.documentElement.dataset.markmelloVirtualization = "0";
    (window as Window & { MARKMELLO_VIRTUALIZATION?: unknown }).MARKMELLO_VIRTUALIZATION = true;
    expect(readVirtualizationFlag(window, document)).toBe(true);
  });

  it("keeps the windowing flag separate from the shadow validation flag", () => {
    window.localStorage.setItem("MARKMELLO_VIRT_SHADOW", "true");

    expect(readVirtualizationShadowFlag(window, document)).toBe(true);
    expect(readVirtualizationFlag(window, document)).toBe(false);

    window.localStorage.setItem("MARKMELLO_VIRTUALIZATION", "yes");
    window.localStorage.setItem("MARKMELLO_VIRT_SHADOW", "0");

    expect(readVirtualizationFlag(window, document)).toBe(false);
    expect(readVirtualizationShadowFlag(window, document)).toBe(false);
  });

  it("uses the shared true-value parser for window globals, data attributes, and localStorage", () => {
    expect(readRendererBooleanFlag({
      dataKey: "exampleFeature",
      globalName: "MARKMELLO_EXAMPLE",
      ownerDocument: document,
      ownerWindow: window,
      storageName: "MARKMELLO_EXAMPLE",
    })).toBe(false);

    document.documentElement.dataset.exampleFeature = "on";
    expect(readRendererBooleanFlag({
      dataKey: "exampleFeature",
      globalName: "MARKMELLO_EXAMPLE",
      ownerDocument: document,
      ownerWindow: window,
      storageName: "MARKMELLO_EXAMPLE",
    })).toBe(true);
  });

  it("keeps virtualization disabled by default so flag-off installs no controller machinery", () => {
    expect(readVirtualizationFlag(window, document)).toBe(false);
    expect(document.documentElement.dataset.markmelloVirtualization).toBeUndefined();
    expect((window as Window & { MARKMELLO_VIRTUALIZATION?: unknown }).MARKMELLO_VIRTUALIZATION)
      .toBeUndefined();
  });

  it("flags unset install no control plane tickets listeners watches reservations font tickets or H3 observer", () => {
    const source = readRendererSource();
    const initialization = sliceBetween(
      source,
      "function initializeVirtualizedDocumentWindow()",
      "function updateVirtualizedWindowForScroll"
    );

    expect(initialization).toContain("if (!virtualizationEnabled)");
    expect(initialization).toContain("beginVirtualizedGeometryWork");
    expect(source).not.toContain("H3DiagnosticObserver");
    expect(source).not.toContain("mm-virt-h3-unregistered-mover");
    expect(readVirtualizationFlag(window, document)).toBe(false);
  });

  it("flag off preserves resize fonts image and mermaid shared-owner output", () => {
    const current = readRendererSource();
    const baseline = execFileSync(
      "git",
      ["show", `${TASK_5_PARENT}:src/MarkMello.Applicate.Desktop/RendererWeb/src/renderer.ts`],
      { encoding: "utf8" }
    );
    const currentShared = sliceBetween(
      current,
      "  const documentElement = document.querySelector<HTMLElement>(\".mm-document\");",
      "const queuePostScroll"
    );
    const baselineShared = sliceBetween(
      baseline,
      "  const documentElement = document.querySelector<HTMLElement>(\".mm-document\");",
      "const queuePostScroll"
    );

    const currentResizeOwner = sliceBetween(
      current,
      "function runLegacyResizeObserverWork",
      "function runLegacyDocumentFontsReadyWork"
    );
    const currentFontsOwner = sliceBetween(
      current,
      "function runLegacyDocumentFontsReadyWork",
      "document.addEventListener(\"DOMContentLoaded\""
    );
    const orderedSignature = (source: string, statements: readonly string[]) => {
      let cursor = -1;
      return statements.map(statement => {
        cursor = source.indexOf(statement, cursor + 1);
        expect(cursor).toBeGreaterThanOrEqual(0);
        return statement;
      });
    };
    const resizeStatements = [
      "queueMinimapRefreshAfterLayoutSettles();",
      "scheduleResizeReactions(documentEpoch);",
      "invalidateSourceLineAnchors({",
      "scheduleVirtualizedMeasuredHeightAdoption();",
      "window.requestAnimationFrame(() => {",
      "postScroll();",
    ] as const;
    const fontStatements = [
      "queueMinimapRefreshAfterLayoutSettles();",
      "invalidateSourceLineAnchors({",
      "scheduleVirtualizedMeasuredHeightAdoption();",
    ] as const;

    expect(orderedSignature(currentResizeOwner, resizeStatements))
      .toEqual(orderedSignature(baselineShared, resizeStatements));
    expect(orderedSignature(currentFontsOwner, fontStatements))
      .toEqual(orderedSignature(baselineShared, fontStatements));
    expect(currentShared).toContain("if (!virtualizationEnabled) {\n        runLegacyResizeObserverWork(documentEpoch);");
    expect(currentShared).toContain("if (!virtualizationEnabled) {\n      runLegacyDocumentFontsReadyWork(fontsDocumentEpoch);");
    expect(current.match(/virtualizationEnabled \? \{ manageVirtualizedProxyLifecycle: true \} : undefined/g))
      .toEqual(baseline.match(/virtualizationEnabled \? \{ manageVirtualizedProxyLifecycle: true \} : undefined/g));
  });
});
