import type { DocumentWindowModel } from "./documentWindow";

export const ACTIVE_HEADING_VIEWPORT_ANCHOR_OFFSET_PX = 10;

export class ModelActiveHeadingPublisher {
  private activeModel: DocumentWindowModel | null = null;
  private lastPostedHeadingId: string | null = null;

  constructor(private readonly post: (id: string) => void) { }

  update(model: DocumentWindowModel | null, scrollY: number): string | null {
    if (model !== this.activeModel) {
      this.activeModel = model;
      this.lastPostedHeadingId = null;
    }
    if (model === null) {
      return null;
    }

    const anchorY = (Number.isFinite(scrollY) ? scrollY : 0)
      + ACTIVE_HEADING_VIEWPORT_ANCHOR_OFFSET_PX;
    const sectionIndex = model.sectionIndexAtDocumentY(anchorY);
    const headingId = model.headingAnchorAtOrBeforeSectionIndex(sectionIndex);
    if (headingId !== null && headingId !== this.lastPostedHeadingId) {
      this.lastPostedHeadingId = headingId;
      this.post(headingId);
    }
    return headingId;
  }
}
