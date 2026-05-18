import { describe, expect, it } from "vitest";
import { calculateWidthHandleLeft, clampWidthHandleLeft } from "../src/widthHandleLayout";

describe("calculateWidthHandleLeft", () => {
  it("keeps the resizer track clear of padded full-width render blocks when minimap is hidden", () => {
    const hitArea = 24;
    const trackMarginRight = 6;
    const dragTrackWidth = 7;
    const paddedBlockBleedRight = 17;

    const left = calculateWidthHandleLeft({
      documentRight: 900,
      documentPaddingRight: 72,
      hitArea,
      minimapReservedWidth: 0,
      viewportWidth: 1200,
    });
    const textRight = 900 - 72;
    const trackLeft = left + hitArea - trackMarginRight - dragTrackWidth;

    expect(trackLeft - textRight).toBeGreaterThan(paddedBlockBleedRight);
  });

  it("clamps before the minimap reservation", () => {
    expect(clampWidthHandleLeft({
      candidateLeft: 1100,
      hitArea: 24,
      minimapReservedWidth: 168,
      viewportWidth: 1200,
    })).toBe(1008);
  });
});
