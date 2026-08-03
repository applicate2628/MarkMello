import { describe, expect, it } from "vitest";
import { calculateMinimapViewportLayout } from "../src/minimapLayout";

describe("calculateMinimapViewportLayout", () => {
  it("uses uniform scale so minimap text keeps document proportions", () => {
    const layout = calculateMinimapViewportLayout({
      minimapWidth: 136,
      minimapHeight: 600,
      documentWidth: 820,
      documentHeight: 20000,
      viewportHeight: 900,
      scrollTop: 1000
    });

    expect(layout).not.toBeNull();
    expect(layout!.contentWidth).toBe(820);
    expect(layout!.scale).toBe(136 / 820);
    expect(layout!.transform).toBe(`translateY(${layout!.contentTranslateY}px) scale(${136 / 820})`);
    expect(layout!.contentTranslateY).toBeLessThan(0);
  });

  it("maps viewport overlay through uniform scale and vertical translation", () => {
    const layout = calculateMinimapViewportLayout({
      minimapWidth: 136,
      minimapHeight: 600,
      documentWidth: 820,
      documentHeight: 20000,
      viewportHeight: 900,
      scrollTop: 1000
    });

    expect(layout).not.toBeNull();
    expect(layout!.thumbHeight).toBe(900 * (136 / 820));
    expect(layout!.thumbTop).toBeGreaterThan(0);
    expect(layout!.thumbTop).toBeLessThan(600 - layout!.thumbHeight);
  });

  it("keeps minimum viewport overlay height for very long documents", () => {
    const layout = calculateMinimapViewportLayout({
      minimapWidth: 136,
      minimapHeight: 600,
      documentWidth: 820,
      documentHeight: 120000,
      viewportHeight: 20,
      scrollTop: 0
    });

    expect(layout).not.toBeNull();
    expect(layout!.thumbHeight).toBe(22);
  });

  it("does not translate content when scaled document fits minimap height", () => {
    const layout = calculateMinimapViewportLayout({
      minimapWidth: 136,
      minimapHeight: 600,
      documentWidth: 820,
      documentHeight: 1200,
      viewportHeight: 900,
      scrollTop: 100
    });

    expect(layout).not.toBeNull();
    expect(layout!.contentTranslateY).toBe(0);
    expect(layout!.thumbTop).toBe(100 * (136 / 820));
  });
});
