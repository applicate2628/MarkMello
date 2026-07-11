import { afterEach, describe, expect, it, vi } from "vitest";
import {
  isStrictlyViewportIntersecting,
  readBlockIndex,
  readCollapsedBorderBoxHeightPx,
  readContainIntrinsicBlockSizePx,
  readOccupiedBlockHeight,
  rangesStrictlyIntersect,
  reachesViewportTopInclusive,
} from "../src/blockGeometryMeasurement";

afterEach(() => {
  document.body.replaceChildren();
  vi.restoreAllMocks();
});

describe("block geometry measurement", () => {
  it("computes the H1 padded pre collapsed border box", () => {
    const element = document.createElement("pre");
    element.style.setProperty("contain-intrinsic-size", "auto 152.65px");
    stubComputedStyle(element, {
      "border-bottom-width": "1px",
      "border-top-width": "1px",
      "padding-bottom": "16px",
      "padding-top": "16px",
    });

    expect(readCollapsedBorderBoxHeightPx(element)).toBeCloseTo(186.65, 5);
  });

  it("reads occupied top-to-next-top height including adjacency margins", () => {
    const element = geometryElement(100, 40);
    const next = geometryElement(172, 30);
    document.body.append(element, next);

    expect(readOccupiedBlockHeight(element)).toBe(72);
    expect(readOccupiedBlockHeight(next)).toBeNull();
  });

  it("excludes exact viewport-edge contact from strict intersection", () => {
    setViewport(100, 200);
    const above = geometryElement(60, 40);
    const below = geometryElement(300, 40);
    const crossingTop = geometryElement(61, 40);
    const crossingBottom = geometryElement(299, 40);

    expect(isStrictlyViewportIntersecting(above)).toBe(false);
    expect(isStrictlyViewportIntersecting(below)).toBe(false);
    expect(isStrictlyViewportIntersecting(crossingTop)).toBe(true);
    expect(isStrictlyViewportIntersecting(crossingBottom)).toBe(true);
  });

  it("keeps top-anchor edge contact inclusive", () => {
    expect(reachesViewportTopInclusive(60, 40, 100)).toBe(true);
    expect(reachesViewportTopInclusive(59, 40, 100)).toBe(false);
  });

  it("uses exclusive boundaries for strict range intersection", () => {
    expect(rangesStrictlyIntersect(60, 100, 100, 300)).toBe(false);
    expect(rangesStrictlyIntersect(300, 340, 100, 300)).toBe(false);
    expect(rangesStrictlyIntersect(99, 101, 100, 300)).toBe(true);
  });

  it("preserves model and tracker contain-intrinsic selection differences", () => {
    const element = document.createElement("div");
    const inlineReader = vi.spyOn(element.style, "getPropertyValue");
    inlineReader.mockImplementation(propertyName => propertyName === "contain-intrinsic-size" ? " " : "");
    stubComputedStyle(element, {
      "border-bottom-width": "0px",
      "border-top-width": "0px",
      "contain-intrinsic-size": "auto 120px",
      "padding-bottom": "0px",
      "padding-top": "0px",
    });

    expect(readContainIntrinsicBlockSizePx(element)).toBe(120);
    expect(readCollapsedBorderBoxHeightPx(element)).toBeNull();
  });

  it("parses finite block indexes and rejects missing values", () => {
    const element = document.createElement("section");
    expect(readBlockIndex(element)).toBeNull();

    element.dataset.mmBlockIndex = " 42tail";
    expect(readBlockIndex(element)).toBe(42);
  });
});

function geometryElement(top: number, height: number): HTMLElement {
  const element = document.createElement("div");
  Object.defineProperty(element, "offsetTop", { configurable: true, get: () => top });
  Object.defineProperty(element, "offsetHeight", { configurable: true, get: () => height });
  return element;
}

function setViewport(scrollTop: number, clientHeight: number): void {
  const root = document.documentElement;
  Object.defineProperty(document, "scrollingElement", { configurable: true, get: () => root });
  Object.defineProperty(root, "scrollTop", { configurable: true, get: () => scrollTop });
  Object.defineProperty(root, "clientHeight", { configurable: true, get: () => clientHeight });
}

function stubComputedStyle(element: HTMLElement, values: Readonly<Record<string, string>>): void {
  const original = window.getComputedStyle.bind(window);
  vi.spyOn(window, "getComputedStyle").mockImplementation(target => {
    if (target !== element) {
      return original(target);
    }
    return {
      getPropertyValue: propertyName => values[propertyName] ?? "",
    } as CSSStyleDeclaration;
  });
}
