import { describe, it, expect } from "vitest";
import {
  findScrollTopForSourceLine,
  findSourceLineAtDocumentY,
  findSourceLineAtDocumentYWithFallback,
  readSourceLineAnchors,
  type SourceLineAnchor
} from "../src/sourceLineSync";

describe("sourceLineSync", () => {
  it("interpolates preview scroll positions between source-line anchors", () => {
    const anchors: SourceLineAnchor[] = [
      { sourceLine: 0, endLine: 0, top: 0 },
      { sourceLine: 10, endLine: 10, top: 100 },
    ];

    expect(findScrollTopForSourceLine(anchors, 5)).toBe(50);
  });

  it("interpolates source lines inside multi-line rendered blocks", () => {
    const anchors: SourceLineAnchor[] = [
      { sourceLine: 10, endLine: 20, top: 100 },
      { sourceLine: 30, endLine: 30, top: 300 },
    ];

    expect(findSourceLineAtDocumentY(anchors, 200)).toBe(15);
  });

  it("uses full-document anchors only when live window edge interpolation is missing", () => {
    const liveAnchors: SourceLineAnchor[] = [
      { sourceLine: 100, endLine: 100, top: 1000 },
      { sourceLine: 110, endLine: 110, top: 1100 },
    ];
    const modelAnchors: SourceLineAnchor[] = [
      { sourceLine: 0, endLine: 0, top: 0 },
      { sourceLine: 100, endLine: 100, top: 1000 },
      { sourceLine: 200, endLine: 200, top: 2000 },
    ];

    expect(findSourceLineAtDocumentYWithFallback(liveAnchors, () => modelAnchors, 1050)).toBe(105);
    expect(findSourceLineAtDocumentYWithFallback(liveAnchors, () => modelAnchors, 500)).toBe(50);
    expect(findSourceLineAtDocumentYWithFallback(liveAnchors, () => modelAnchors, 1500)).toBe(150);
  });

  it("reads source-line anchors from rendered DOM metadata", () => {
    document.body.innerHTML = `
      <main class="mm-document">
        <h1 data-mm-source-line="0" data-mm-source-end-line="0">Title</h1>
        <p data-mm-source-line="3" data-mm-source-end-line="5">Body</p>
      </main>`;
    const nodes = Array.from(document.querySelectorAll<HTMLElement>("[data-mm-source-line]"));
    nodes[0]!.getBoundingClientRect = () => ({ top: 20 } as DOMRect);
    nodes[1]!.getBoundingClientRect = () => ({ top: 80 } as DOMRect);

    expect(readSourceLineAnchors(document, 10)).toEqual([
      { sourceLine: 0, endLine: 0, top: 30 },
      { sourceLine: 3, endLine: 5, top: 90 },
    ]);
  });
});
