export type WidthHandleClampInput = {
  candidateLeft: number;
  hitArea: number;
  minimapReservedWidth: number;
  viewportWidth: number;
};

export type WidthHandlePositionInput = {
  documentRight: number;
  documentPaddingRight: number;
  hitArea: number;
  minimapReservedWidth: number;
  viewportWidth: number;
};

export function clampWidthHandleLeft(input: WidthHandleClampInput): number {
  const hitArea = Math.max(0, input.hitArea);
  const minimapReservedWidth = Math.max(0, input.minimapReservedWidth);
  const viewportWidth = Math.max(0, input.viewportWidth);
  const minimapLeftEdge = viewportWidth - minimapReservedWidth;
  const maxLeftBeforeMinimap = Math.max(0, minimapLeftEdge - hitArea);
  return Math.max(0, Math.min(maxLeftBeforeMinimap, input.candidateLeft));
}

export function calculateWidthHandleLeft(input: WidthHandlePositionInput): number {
  const hitArea = Math.max(0, input.hitArea);
  const visibleTextRight = input.documentRight - Math.max(0, input.documentPaddingRight);
  const candidateLeft = visibleTextRight + hitArea;

  return clampWidthHandleLeft({
    candidateLeft,
    hitArea,
    minimapReservedWidth: input.minimapReservedWidth,
    viewportWidth: input.viewportWidth,
  });
}
