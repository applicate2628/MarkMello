import { describe, expect, it, vi } from "vitest";
import {
  calculateModelMinimapLayout,
  renderModelMinimapCanvas,
  scrollTopForModelMinimapThumbTop,
  scrollTopForModelMinimapY,
  type ModelMinimapBand,
} from "../src/modelMinimap";

type DrawCall = {
  height: number;
  width: number;
  x: number;
  y: number;
};

function installCanvasDrawSpy(): DrawCall[] {
  const calls: DrawCall[] = [];
  Object.defineProperty(window.HTMLCanvasElement.prototype, "getContext", {
    configurable: true,
    value(type: string) {
      if (type !== "2d") {
        return null;
      }

      return {
        clearRect: vi.fn(),
        fillRect: vi.fn((x: number, y: number, width: number, height: number) => {
          calls.push({ height, width, x, y });
        }),
        resetTransform: vi.fn(),
        scale: vi.fn(),
        set fillStyle(_value: string) {
          // Test canvas only records geometry.
        },
      };
    },
  });
  return calls;
}

describe("model minimap", () => {
  it("renders every model band into a fixed-size canvas surface", () => {
    const drawCalls = installCanvasDrawSpy();
    const bands: ModelMinimapBand[] = [
      { headingLevel: 2, height: 100, kind: "heading", top: 0 },
      { headingLevel: 0, height: 400, kind: "paragraph", top: 100 },
      { headingLevel: 0, height: 500, kind: "code", top: 500 },
    ];

    const canvas = renderModelMinimapCanvas({
      bands,
      documentHeight: 1_000,
      height: 320,
      ownerDocument: document,
      pixelRatio: 1,
      width: 136,
    });

    expect(canvas.dataset.mmModelMinimap).toBe("true");
    expect(canvas.dataset.mmModelMinimapSectionCount).toBe("3");
    expect(canvas.dataset.mmModelMinimapTotalHeight).toBe("1000");
    expect(canvas.dataset.mmModelMinimapHeight).toBe("320");
    expect(canvas.width).toBe(136);
    expect(canvas.height).toBe(320);
    expect(drawCalls).toEqual([
      { height: 32, width: 136, x: 0, y: 0 },
      { height: 128, width: 136, x: 0, y: 32 },
      { height: 160, width: 136, x: 0, y: 160 },
    ]);
  });

  it("maps bottom scroll and pointer positions through bounded minimap coordinates", () => {
    const layout = calculateModelMinimapLayout({
      documentHeight: 5_000,
      height: 320,
      scrollTop: 4_500,
      viewportHeight: 500,
      width: 136,
    });

    expect(layout).not.toBeNull();
    expect(layout!.thumbTop + layout!.thumbHeight).toBe(320);
    expect(scrollTopForModelMinimapY(layout!, 320)).toBe(4_500);
    expect(scrollTopForModelMinimapThumbTop(layout!, layout!.thumbTravel)).toBe(4_500);
  });
});
