import { describe, expect, it, vi } from "vitest";
import { DocumentWindowModel, type SectionModelEntry } from "../src/documentWindow";
import { ModelActiveHeadingPublisher } from "../src/modelActiveHeading";

function entry(
  sectionIndex: number,
  headingAnchor?: string
): SectionModelEntry {
  return {
    blockIndex: sectionIndex,
    cumulativeTop: 0,
    estimatedHeight: 100,
    headingAnchors: headingAnchor === undefined ? [] : [headingAnchor],
    headingLevel: headingAnchor === undefined ? 0 : 2,
    kind: headingAnchor === undefined ? "paragraph" : "heading",
    measuredHeight: undefined,
    sectionIndex,
  };
}

describe("ModelActiveHeadingPublisher", () => {
  it("posts the nearest preceding heading and updates after same-scroll geometry changes", () => {
    const model = new DocumentWindowModel([
      entry(0, "heading-0"),
      entry(1),
      entry(2),
      entry(3),
      entry(4, "heading-4"),
      entry(5),
      entry(6),
      entry(7),
      entry(8, "heading-8"),
      entry(9),
    ]);
    const post = vi.fn<(id: string) => void>();
    const publisher = new ModelActiveHeadingPublisher(post);
    const scrollY = 750;

    publisher.update(model, scrollY);
    expect(model.sectionIndexAtDocumentY(scrollY + 10)).toBe(7);
    expect(post).toHaveBeenLastCalledWith("heading-4");

    model.updateMeasuredHeightsByBlockIndex([
      { blockIndex: 0, measuredHeight: 200 },
      { blockIndex: 1, measuredHeight: 200 },
      { blockIndex: 2, measuredHeight: 200 },
      { blockIndex: 3, measuredHeight: 200 },
    ]);
    publisher.update(model, scrollY);

    expect(model.sectionIndexAtDocumentY(scrollY + 10)).toBe(3);
    expect(post.mock.calls.map(([id]) => id)).toEqual(["heading-4", "heading-0"]);
  });
});
