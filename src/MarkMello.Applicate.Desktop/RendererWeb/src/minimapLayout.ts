export type MinimapViewportLayoutInput = {
  minimapWidth: number;
  minimapHeight: number;
  documentWidth: number;
  documentHeight: number;
  viewportHeight: number;
  scrollTop: number;
  minimumThumbHeight?: number;
};

export type MinimapViewportLayout = {
  contentWidth: number;
  scale: number;
  contentTranslateY: number;
  transform: string;
  thumbTop: number;
  thumbHeight: number;
  thumbTravel: number;
  // True slope d(thumbTop)/d(scrollTop) of the UNCLAMPED forward map. thumbTop is
  // linear in scrollTop through the origin: rawThumbTop = scrollTop*scale +
  // contentTranslateY, and contentTranslateY = -(scrollTop/maxScroll)*overflowHeight,
  // so the effective slope is (scale - overflowHeight/maxScroll), NOT scale. Drag-to-pan
  // must invert with THIS slope to keep the grabbed point under the cursor.
  thumbSlope: number;
};

const DEFAULT_MINIMUM_THUMB_HEIGHT = 22;

export type MinimapDocumentWidthInput = {
  borderBoxWidth: number;
  paddingLeft: number;
  paddingRight: number;
};

export function calculateMinimapDocumentWidth(input: MinimapDocumentWidthInput): number {
  const width = input.borderBoxWidth - input.paddingLeft - input.paddingRight;
  return Number.isFinite(width) && width > 0 ? width : 1;
}

export function calculateMinimapViewportLayout(input: MinimapViewportLayoutInput): MinimapViewportLayout | null {
  if (input.minimapWidth <= 0
    || input.minimapHeight <= 0
    || input.documentWidth <= 0
    || input.documentHeight <= 0
    || input.viewportHeight <= 0) {
    return null;
  }

  const minimumThumbHeight = input.minimumThumbHeight ?? DEFAULT_MINIMUM_THUMB_HEIGHT;
  // Cap scale at 1.0 — the minimap is an OVERVIEW, never magnified.
  // When the document is narrower than the minimap area (user dragged the
  // width-handle far left), a raw ratio would scale content UP, making
  // minimap text larger than the source — the opposite of a minimap's purpose.
  const scale = Math.min(1, input.minimapWidth / input.documentWidth);
  const projectedDocumentHeight = input.documentHeight * scale;
  const maximumScrollTop = Math.max(0, input.documentHeight - input.viewportHeight);
  const scrollProgress = maximumScrollTop > 0
    ? Math.max(0, Math.min(1, input.scrollTop / maximumScrollTop))
    : 0;
  const overflowHeight = Math.max(0, projectedDocumentHeight - input.minimapHeight);
  const contentTranslateY = overflowHeight > 0 ? -scrollProgress * overflowHeight : 0;
  const thumbHeight = Math.min(
    input.minimapHeight,
    Math.max(minimumThumbHeight, input.viewportHeight * scale));
  const rawThumbTop = input.scrollTop * scale + contentTranslateY;
  const maximumRawThumbTop = Math.max(0, maximumScrollTop * scale - overflowHeight);
  const maximumClampedThumbTop = Math.max(0, input.minimapHeight - thumbHeight);
  const thumbTravel = Math.min(maximumClampedThumbTop, maximumRawThumbTop);
  const thumbTop = Math.max(0, Math.min(thumbTravel, rawThumbTop));
  const thumbSlope = maximumScrollTop > 0 ? scale - (overflowHeight / maximumScrollTop) : scale;

  return {
    contentWidth: input.documentWidth,
    scale,
    contentTranslateY,
    transform: `translateY(${contentTranslateY}px) scale(${scale})`,
    thumbTop,
    thumbHeight,
    thumbTravel,
    thumbSlope
  };
}
