import { describe, expect, it } from "vitest";
import { shouldPostMinimapState, type PostedMinimapState } from "../src/minimapState";

describe("shouldPostMinimapState", () => {
  it("posts first minimap state even when hidden and width is zero", () => {
    const previous: PostedMinimapState = { hasPosted: false, visible: false, reservedWidth: 0 };

    expect(
      shouldPostMinimapState(previous, { visible: false, reservedWidth: 0 }))
      .toBe(true);
  });

  it("force posts minimap state even when visible width did not change", () => {
    const previous: PostedMinimapState = { hasPosted: true, visible: true, reservedWidth: 168 };

    expect(
      shouldPostMinimapState(previous, { visible: true, reservedWidth: 168 }, true))
      .toBe(true);
  });

  it("skips unchanged non-forced minimap state", () => {
    const previous: PostedMinimapState = { hasPosted: true, visible: true, reservedWidth: 168 };

    expect(
      shouldPostMinimapState(previous, { visible: true, reservedWidth: 168 }))
      .toBe(false);
  });

  it("posts minimap state when width changes past epsilon", () => {
    const previous: PostedMinimapState = { hasPosted: true, visible: true, reservedWidth: 168 };

    expect(
      shouldPostMinimapState(previous, { visible: true, reservedWidth: 169 }))
      .toBe(true);
  });
});
