import type { ModelMinimapBlockProjection } from "./documentWindow";
import type { SectionKind } from "./sectionIntrinsicSize";

export type ModelMinimapBand = ModelMinimapBlockProjection;

export type ModelMinimapCanvasInput = {
  ownerDocument: Document;
  bands: readonly ModelMinimapBand[];
  documentHeight: number;
  width: number;
  height: number;
  pixelRatio?: number;
};

export type ModelMinimapLayoutInput = {
  documentHeight: number;
  width: number;
  height: number;
  viewportHeight: number;
  scrollTop: number;
  minimumThumbHeight?: number;
};

export type ModelMinimapLayout = {
  documentHeight: number;
  width: number;
  height: number;
  viewportHeight: number;
  scrollTop: number;
  maximumScrollTop: number;
  thumbTop: number;
  thumbHeight: number;
  thumbTravel: number;
};

const DEFAULT_MINIMUM_THUMB_HEIGHT = 22;
const MAX_CANVAS_PIXEL_RATIO = 2;

const KIND_FILL: Partial<Record<SectionKind, string>> = {
  code: "rgba(76, 90, 115, 0.72)",
  heading: "rgba(212, 109, 61, 0.88)",
  image: "rgba(90, 112, 129, 0.62)",
  list: "rgba(76, 83, 91, 0.72)",
  math: "rgba(121, 91, 154, 0.72)",
  paragraph: "rgba(63, 63, 63, 0.68)",
  quote: "rgba(86, 99, 92, 0.68)",
  rule: "rgba(96, 96, 96, 0.56)",
  table: "rgba(94, 105, 122, 0.72)",
  unknown: "rgba(68, 68, 68, 0.62)",
};

export function renderModelMinimapCanvas(input: ModelMinimapCanvasInput): HTMLCanvasElement {
  const canvas = input.ownerDocument.createElement("canvas");
  const width = Math.max(1, Math.round(input.width));
  const height = Math.max(1, Math.round(input.height));
  const pixelRatio = normalizePixelRatio(input.pixelRatio);
  canvas.width = Math.max(1, Math.round(width * pixelRatio));
  canvas.height = Math.max(1, Math.round(height * pixelRatio));
  canvas.style.display = "block";
  canvas.style.width = `${width}px`;
  canvas.style.height = `${height}px`;
  canvas.dataset["mmModelMinimap"] = "true";
  canvas.dataset["mmModelMinimapSectionCount"] = String(input.bands.length);
  canvas.dataset["mmModelMinimapTotalHeight"] = String(input.documentHeight);
  canvas.dataset["mmModelMinimapWidth"] = String(width);
  canvas.dataset["mmModelMinimapHeight"] = String(height);

  const context = canvas.getContext("2d");
  if (context === null || input.documentHeight <= 0) {
    return canvas;
  }

  context.resetTransform?.();
  context.scale(pixelRatio, pixelRatio);
  context.clearRect(0, 0, width, height);
  const scaleY = height / input.documentHeight;
  for (const band of input.bands) {
    if (!Number.isFinite(band.top) || !Number.isFinite(band.height) || band.height <= 0) {
      continue;
    }

    const y = clamp(band.top * scaleY, 0, height);
    const scaledHeight = Math.max(0.5, band.height * scaleY);
    const bandHeight = Math.min(height - y, scaledHeight);
    if (bandHeight <= 0) {
      continue;
    }

    context.fillStyle = fillForBand(band);
    context.fillRect(0, y, width, bandHeight);
  }

  return canvas;
}

export function calculateModelMinimapLayout(input: ModelMinimapLayoutInput): ModelMinimapLayout | null {
  if (input.documentHeight <= 0
    || input.width <= 0
    || input.height <= 0
    || input.viewportHeight <= 0) {
    return null;
  }

  const minimumThumbHeight = input.minimumThumbHeight ?? DEFAULT_MINIMUM_THUMB_HEIGHT;
  const maximumScrollTop = Math.max(0, input.documentHeight - input.viewportHeight);
  const thumbHeight = maximumScrollTop <= 0
    ? input.height
    : Math.min(input.height, Math.max(minimumThumbHeight, input.height * (input.viewportHeight / input.documentHeight)));
  const thumbTravel = Math.max(0, input.height - thumbHeight);
  const scrollTop = clamp(input.scrollTop, 0, maximumScrollTop);
  const scrollProgress = maximumScrollTop > 0 ? scrollTop / maximumScrollTop : 0;
  return {
    documentHeight: input.documentHeight,
    height: input.height,
    maximumScrollTop,
    scrollTop,
    thumbHeight,
    thumbTop: thumbTravel * scrollProgress,
    thumbTravel,
    viewportHeight: input.viewportHeight,
    width: input.width,
  };
}

export function scrollTopForModelMinimapY(layout: ModelMinimapLayout, minimapY: number): number {
  const documentY = (clamp(minimapY, 0, layout.height) / layout.height) * layout.documentHeight;
  return clamp(documentY, 0, layout.maximumScrollTop);
}

export function scrollTopForModelMinimapThumbTop(layout: ModelMinimapLayout, thumbTop: number): number {
  if (layout.thumbTravel <= 0 || layout.maximumScrollTop <= 0) {
    return 0;
  }

  return (clamp(thumbTop, 0, layout.thumbTravel) / layout.thumbTravel) * layout.maximumScrollTop;
}

function fillForBand(band: ModelMinimapBand): string {
  if (band.kind === "heading") {
    const opacity = band.headingLevel <= 1 ? 0.94 : band.headingLevel <= 3 ? 0.86 : 0.76;
    return `rgba(212, 109, 61, ${opacity})`;
  }

  return KIND_FILL[band.kind] ?? "rgba(68, 68, 68, 0.62)";
}

function normalizePixelRatio(pixelRatio: number | undefined): number {
  if (pixelRatio === undefined || !Number.isFinite(pixelRatio) || pixelRatio <= 0) {
    return 1;
  }

  return Math.max(1, Math.min(MAX_CANVAS_PIXEL_RATIO, pixelRatio));
}

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.max(minimum, Math.min(maximum, value));
}
