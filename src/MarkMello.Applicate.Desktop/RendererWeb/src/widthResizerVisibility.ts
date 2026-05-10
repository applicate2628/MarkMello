export type WidthResizerVisibility = "always" | "on-hover";

export function normalizeWidthResizerVisibility(raw: unknown): WidthResizerVisibility {
  return raw === "always" ? "always" : "on-hover";
}

export function getWidthResizerVisibilityClasses(visibility: WidthResizerVisibility): { alwaysClass: boolean } {
  return {
    alwaysClass: visibility === "always"
  };
}
