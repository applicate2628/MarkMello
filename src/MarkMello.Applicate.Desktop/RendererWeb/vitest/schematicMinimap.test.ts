import { describe, it, expect, vi } from "vitest";
import {
  walkDocumentBlocks,
  renderSchematicSvg,
  schedulePhaseBGeometryRefresh,
  type DocumentBlock,
  type DocumentBlockKind,
} from "../src/schematicMinimap";

describe("walkDocumentBlocks", () => {
  it("emits blocks for headings, paragraphs, code, math-display, mermaid, table, list, quote, hr", () => {
    const root = document.createElement("div");
    root.className = "mm-document";
    root.innerHTML = `
      <h1>Title</h1>
      <p>Some paragraph</p>
      <h2>Section</h2>
      <pre><code>code block</code></pre>
      <div class="math-display"></div>
      <pre class="mm-mermaid"></pre>
      <table><tr><td>x</td></tr></table>
      <ul><li>i</li></ul>
      <blockquote>q</blockquote>
      <hr>
    `;
    document.body.appendChild(root);
    const blocks: DocumentBlock[] = walkDocumentBlocks({ documentRoot: root, documentHeight: 1000 });
    const kinds: DocumentBlockKind[] = blocks.map((b: DocumentBlock) => b.kind);
    expect(kinds).toEqual<DocumentBlockKind[]>([
      "heading-1",
      "paragraph",
      "heading-2",
      "code",
      "math-display",
      "mermaid",
      "table",
      "list",
      "quote",
      "hr",
    ]);
    document.body.removeChild(root);
  });

  it("emits empty array for empty .mm-document", () => {
    const root = document.createElement("div");
    root.className = "mm-document";
    document.body.appendChild(root);
    const blocks: DocumentBlock[] = walkDocumentBlocks({
      documentRoot: root,
      documentHeight: 0,
    });
    expect(blocks).toEqual([]);
    document.body.removeChild(root);
  });

  it("classifies a markdown table scroll wrapper as a table block", () => {
    const root = document.createElement("div");
    root.className = "mm-document";
    root.innerHTML = `
      <div class="mm-table-scroll" data-mm-block-kind="table">
        <table><tr><td>x</td></tr></table>
      </div>
    `;
    document.body.appendChild(root);

    const blocks: DocumentBlock[] = walkDocumentBlocks({ documentRoot: root, documentHeight: 1000 });

    expect(blocks.map((b: DocumentBlock) => b.kind)).toEqual<DocumentBlockKind[]>(["table"]);
    document.body.removeChild(root);
  });
});

describe("renderSchematicSvg", () => {
  it("produces SVG in document coordinates with rect per block", () => {
    const blocks: DocumentBlock[] = [
      { kind: "heading-1", top: 0, height: 30 },
      { kind: "paragraph", top: 40, height: 60 },
      { kind: "math-display", top: 110, height: 80 },
    ];
    const svg = renderSchematicSvg(blocks, 800, 200);
    expect(svg.tagName.toLowerCase()).toBe("svg");
    expect(svg.getAttribute("viewBox")).toBe("0 0 800 200");
    expect(svg.getAttribute("preserveAspectRatio")).toBe("none");
    expect(svg.style.width).toBe("800px");
    expect(svg.style.height).toBe("200px");
    const rects = svg.querySelectorAll("rect");
    expect(rects).toHaveLength(3);
    expect(rects[0]!.getAttribute("y")).toBe("0");
    expect(rects[0]!.getAttribute("height")).toBe("30");
  });

  it("empty blocks produces empty SVG (no crash)", () => {
    const svg = renderSchematicSvg([], 800, 0);
    expect(svg.querySelectorAll("rect")).toHaveLength(0);
  });
});

describe("integration", () => {
  it("walk + render produces matching block count", () => {
    const root = document.createElement("div");
    root.className = "mm-document";
    root.innerHTML = "<h1>A</h1><p>B</p><div class=\"math-display\"></div>";
    document.body.appendChild(root);
    const blocks = walkDocumentBlocks({ documentRoot: root, documentHeight: 100 });
    const svg = renderSchematicSvg(blocks, 800, 100);
    expect(svg.querySelectorAll("rect")).toHaveLength(blocks.length);
    document.body.removeChild(root);
  });
});

describe("schedulePhaseBGeometryRefresh (real renderer seam)", () => {
  it("calls refresh('B') exactly once, unconditionally, after allMathRendered", async () => {
    // The height-delta gate is gone with the rebuild. It compared two LIVE
    // document heights and never inspected the clone, so it gated the geometry
    // re-measure on a staleness proxy that does not describe the clone. A stable
    // document height must no longer suppress the re-measure.
    const refresh = vi.fn();
    let resolveMath: () => void = () => {};
    const allMathRendered = new Promise<void>(r => { resolveMath = r; });

    const phaseBReady = schedulePhaseBGeometryRefresh({ allMathRendered, refresh });

    expect(refresh).not.toHaveBeenCalled();
    resolveMath();
    await phaseBReady;
    expect(refresh).toHaveBeenCalledTimes(1);
    expect(refresh).toHaveBeenCalledWith("B");
  });

  it("runs on the promise continuation with no timer or idle deferral", async () => {
    // NO TIMERS guard. `Omit` strips the DOM lib's own (required, non-optional)
    // `requestIdleCallback` declaration before re-adding it as optional --
    // intersecting straight onto `typeof window` would keep it required (the real
    // declaration wins), making it neither deletable nor safely unassignable under
    // `exactOptionalPropertyTypes`.
    const win = window as Omit<typeof window, "requestIdleCallback"> & {
      requestIdleCallback?: (cb: () => void, opts?: { timeout: number }) => number;
    };
    const originalRequestIdleCallback = win.requestIdleCallback;
    const idleCallback = vi.fn(() => 1);
    win.requestIdleCallback = idleCallback;
    const setTimeoutSpy = vi.spyOn(window, "setTimeout");
    try {
      const refresh = vi.fn();
      let resolveMath: () => void = () => {};
      const allMathRendered = new Promise<void>(r => { resolveMath = r; });

      const phaseBReady = schedulePhaseBGeometryRefresh({ allMathRendered, refresh });
      resolveMath();
      // One microtask turn. The retired idle/setTimeout deferral could not have run
      // the refresh by here; a plain promise continuation has.
      await Promise.resolve();
      expect(refresh).toHaveBeenCalledWith("B");

      // And neither deferral primitive was reached at all, so the timers are
      // deleted rather than relocated behind a different name.
      expect(idleCallback).not.toHaveBeenCalled();
      expect(setTimeoutSpy).not.toHaveBeenCalled();
      await phaseBReady;
    } finally {
      setTimeoutSpy.mockRestore();
      if (originalRequestIdleCallback === undefined) {
        delete win.requestIdleCallback;
      } else {
        win.requestIdleCallback = originalRequestIdleCallback;
      }
    }
  });
});
