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

// `thumbSlope` is the true slope d(thumbTop)/d(scrollTop) of the UNCLAMPED forward
// map, and the quantity drag-to-pan inverts to keep the grabbed point under the
// cursor. Per the contract on `MinimapViewportLayout.thumbSlope`:
//
//   rawThumbTop       = scrollTop*scale + contentTranslateY
//   contentTranslateY = -(scrollTop/maximumScrollTop) * overflowHeight
//   => thumbSlope     = scale - overflowHeight/maximumScrollTop     (NOT `scale`)
//
// Every expectation below is derived by hand from that relation, never from the
// function's own output.
describe("calculateMinimapViewportLayout thumbSlope", () => {
  // Derivation:
  //   scale            = min(1, 100/1000)      = 0.1
  //   projectedHeight  = 10000 * 0.1           = 1000
  //   maximumScrollTop = max(0, 10000 - 2000)  = 8000
  //   overflowHeight   = max(0, 1000 - 400)    = 600
  //   thumbSlope       = 0.1 - 600/8000        = 0.025
  // scale (0.1) and thumbSlope (0.025) differ by 4x, so this case cannot pass for
  // an implementation that returns `scale` — the mistake the contract warns about.
  // overflowHeight (600) is deliberately kept distinct from minimapHeight (400) so
  // the expected value pins the right height rather than passing by coincidence.
  it("is scale minus overflowHeight/maximumScrollTop, not scale, when content overflows the minimap", () => {
    const layout = calculateMinimapViewportLayout({
      minimapWidth: 100,
      minimapHeight: 400,
      documentWidth: 1000,
      documentHeight: 10000,
      viewportHeight: 2000,
      scrollTop: 2000
    });

    expect(layout).not.toBeNull();
    expect(layout!.scale).toBeCloseTo(0.1, 12);
    expect(layout!.thumbSlope).toBeCloseTo(0.025, 12);
    // The whole point of the field: here it is NOT the scale.
    expect(layout!.thumbSlope).not.toBeCloseTo(layout!.scale, 6);
  });

  // Independent cross-check of the documented claim: thumbSlope must equal the
  // MEASURED slope of thumbTop over scrollTop, not merely restate the formula.
  // Same geometry as above, where thumbTravel = 200 and rawThumbTop spans 50..150
  // across these two samples, so both lie strictly inside the clamp and
  // thumbTop === rawThumbTop.
  it("equals the measured d(thumbTop)/d(scrollTop) across the unclamped range", () => {
    const geometry = {
      minimapWidth: 100,
      minimapHeight: 400,
      documentWidth: 1000,
      documentHeight: 10000,
      viewportHeight: 2000
    };
    const lower = calculateMinimapViewportLayout({ ...geometry, scrollTop: 2000 });
    const upper = calculateMinimapViewportLayout({ ...geometry, scrollTop: 6000 });

    expect(lower).not.toBeNull();
    expect(upper).not.toBeNull();
    // A clamped sample would make the finite difference meaningless, so pin that
    // both samples really are inside the travel range before differencing them.
    expect(lower!.thumbTop).toBeGreaterThan(0);
    expect(upper!.thumbTop).toBeLessThan(upper!.thumbTravel);

    const measuredSlope = (upper!.thumbTop - lower!.thumbTop) / (6000 - 2000);
    expect(measuredSlope).toBeCloseTo(0.025, 12);
    expect(lower!.thumbSlope).toBeCloseTo(measuredSlope, 12);
  });

  // Derivation:
  //   scale            = min(1, 100/1000)     = 0.1
  //   projectedHeight  = 5000 * 0.1           = 500
  //   overflowHeight   = max(0, 500 - 600)    = 0      (scaled document fits)
  //   maximumScrollTop = max(0, 5000 - 1000)  = 4000   (still > 0)
  //   thumbSlope       = 0.1 - 0/4000         = 0.1    = scale
  it("collapses to scale when the scaled document fits the minimap height", () => {
    const layout = calculateMinimapViewportLayout({
      minimapWidth: 100,
      minimapHeight: 600,
      documentWidth: 1000,
      documentHeight: 5000,
      viewportHeight: 1000,
      scrollTop: 500
    });

    expect(layout).not.toBeNull();
    expect(layout!.contentTranslateY).toBe(0);
    expect(layout!.thumbSlope).toBe(0.1);
    expect(layout!.thumbSlope).toBe(layout!.scale);
  });

  // maximumScrollTop === 0 is the division guard. Without it the expression is
  // 0/0 = NaN when nothing overflows and scale - n/0 = -Infinity when it does;
  // either would propagate into drag-to-pan as a non-finite scroll target.
  it("returns scale without dividing when the document cannot scroll", () => {
    // documentHeight (800) <= viewportHeight (1000) => maximumScrollTop = 0, and
    // overflowHeight = max(0, 800*0.25 - 600) = 0, so unguarded this is 0/0 = NaN.
    const unscrollable = calculateMinimapViewportLayout({
      minimapWidth: 100,
      minimapHeight: 600,
      documentWidth: 400,
      documentHeight: 800,
      viewportHeight: 1000,
      scrollTop: 0
    });

    expect(unscrollable).not.toBeNull();
    expect(Number.isFinite(unscrollable!.thumbSlope)).toBe(true);
    expect(unscrollable!.thumbSlope).toBe(0.25);
    expect(unscrollable!.thumbSlope).toBe(unscrollable!.scale);

    // maximumScrollTop = 0 while overflowHeight = max(0, 1000 - 600) = 400 > 0, so
    // the unguarded expression would be 1 - 400/0 = -Infinity.
    const unscrollableWithOverflow = calculateMinimapViewportLayout({
      minimapWidth: 1000,
      minimapHeight: 600,
      documentWidth: 1000,
      documentHeight: 1000,
      viewportHeight: 1000,
      scrollTop: 0
    });

    expect(unscrollableWithOverflow).not.toBeNull();
    expect(Number.isFinite(unscrollableWithOverflow!.thumbSlope)).toBe(true);
    expect(unscrollableWithOverflow!.thumbSlope).toBe(1);
  });
});
