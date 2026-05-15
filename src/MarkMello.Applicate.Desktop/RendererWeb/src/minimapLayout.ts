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
};

const DEFAULT_MINIMUM_THUMB_HEIGHT = 22;

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
  const thumbTop = Math.max(0, Math.min(input.minimapHeight - thumbHeight, rawThumbTop));

  return {
    contentWidth: input.documentWidth,
    scale,
    contentTranslateY,
    transform: `translateY(${contentTranslateY}px) scale(${scale})`,
    thumbTop,
    thumbHeight
  };
}
