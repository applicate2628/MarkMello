import { describe, it, expect } from "vitest";
import {
  calculateMinimapDocumentWidth,
  calculateMinimapViewportLayout,
} from "../src/minimapLayout";

describe("calculateMinimapDocumentWidth", () => {
  it("uses the document content box so minimap reservation padding does not widen the clone", () => {
    expect(calculateMinimapDocumentWidth({
      borderBoxWidth: 888,
      paddingLeft: 64,
      paddingRight: 232,
    })).toBe(592);
  });

  it("falls back to one pixel for degenerate padding measurements", () => {
    expect(calculateMinimapDocumentWidth({
      borderBoxWidth: 120,
      paddingLeft: 80,
      paddingRight: 80,
    })).toBe(1);
  });
});

describe("calculateMinimapViewportLayout", () => {
  it("maps short-document drag travel to the rendered viewport travel, not the empty minimap gutter", () => {
    const layout = calculateMinimapViewportLayout({
      minimapWidth: 136,
      minimapHeight: 600,
      documentWidth: 820,
      documentHeight: 1200,
      viewportHeight: 900,
      scrollTop: 100,
    });

    expect(layout).not.toBeNull();
    expect(layout!.contentTranslateY).toBe(0);
    expect(layout!.thumbTravel).toBeCloseTo((1200 - 900) * (136 / 820));
  });

  it("keeps a very tall model-fragment clone width-fit and scrolls it inside the rail", () => {
    const layout = calculateMinimapViewportLayout({
      minimapWidth: 136,
      minimapHeight: 709,
      documentWidth: 756,
      documentHeight: 3_892_435,
      viewportHeight: 709,
      scrollTop: (3_892_435 - 709) / 2,
    });

    expect(layout).not.toBeNull();
    expect(layout!.scale).toBeCloseTo(136 / 756);
    expect(layout!.contentWidth * layout!.scale).toBeCloseTo(136);
    expect(layout!.contentTranslateY).toBeLessThan(0);
    expect(3_892_435 * layout!.scale).toBeGreaterThan(709);
    expect(layout!.thumbTop).toBeCloseTo(layout!.thumbTravel / 2);
  });

  it("keeps a realistic 50k document at shipped full-rail width", () => {
    const layout = calculateMinimapViewportLayout({
      minimapWidth: 136,
      minimapHeight: 635,
      documentWidth: 726,
      documentHeight: 50_000,
      viewportHeight: 900,
      scrollTop: 24_550,
    });

    expect(layout).not.toBeNull();
    expect(layout!.scale).toBeCloseTo(136 / 726);
    expect(layout!.scale).toBeCloseTo(0.1873, 3);
    expect(layout!.contentWidth * layout!.scale).toBeCloseTo(136);
    expect(layout!.contentTranslateY).toBeLessThan(0);
  });
});
