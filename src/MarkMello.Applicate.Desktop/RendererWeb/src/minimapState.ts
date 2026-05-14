export type MinimapState = {
  visible: boolean;
  reservedWidth: number;
};

export type PostedMinimapState = MinimapState & {
  hasPosted: boolean;
};

const DEFAULT_MINIMAP_POST_EPSILON = 0.5;

export function shouldPostMinimapState(
  previous: PostedMinimapState,
  next: MinimapState,
  force = false,
  epsilon = DEFAULT_MINIMAP_POST_EPSILON): boolean {
  if (force || !previous.hasPosted || previous.visible !== next.visible) {
    return true;
  }

  return Math.abs(next.reservedWidth - previous.reservedWidth) >= epsilon;
}
