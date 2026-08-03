import { describe, expect, it } from "vitest";
import {
  getWidthResizerVisibilityClasses,
  normalizeWidthResizerVisibility
} from "../src/widthResizerVisibility";

describe("normalizeWidthResizerVisibility", () => {
  it("normalizes known width resizer visibility values", () => {
    expect(normalizeWidthResizerVisibility("always")).toBe("always");
    expect(normalizeWidthResizerVisibility("on-hover")).toBe("on-hover");
  });

  it("falls back to on-hover for missing or unknown width resizer visibility", () => {
    expect(normalizeWidthResizerVisibility(undefined)).toBe("on-hover");
    expect(normalizeWidthResizerVisibility("other")).toBe("on-hover");
  });
});

describe("getWidthResizerVisibilityClasses", () => {
  it("uses a body class only for always-visible width resizer", () => {
    expect(getWidthResizerVisibilityClasses("always")).toStrictEqual({
      alwaysClass: true
    });
    expect(getWidthResizerVisibilityClasses("on-hover")).toStrictEqual({
      alwaysClass: false
    });
  });
});
