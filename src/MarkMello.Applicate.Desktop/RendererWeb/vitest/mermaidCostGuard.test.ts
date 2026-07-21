import { describe, it, expect } from "vitest";
import {
  assessMermaidRenderCost,
  MERMAID_MAX_LAYOUT_ELEMENTS,
} from "../src/mermaidCostGuard";

// Reconstructions of the runtime probe corpus (.scratch/mermaid-precond/inputs*.py),
// so the guard's verdicts are pinned against the diagrams whose render times were
// actually measured over CDP on the live viewer host (bug file, 2026-07-21 evening).

/** classDiagram with all-pairs directed relations among N classes: N(N-1)/2 edges. */
function classDense(n: number): string {
  const s = ["classDiagram"];
  for (let i = 0; i < n; i++) {
    for (let j = i + 1; j < n; j++) s.push(`  C${i} --> C${j}`);
  }
  return s.join("\n");
}

/** erDiagram all-pairs relations among N entities. */
function erDense(n: number): string {
  const s = ["erDiagram"];
  for (let i = 0; i < n; i++) {
    for (let j = i + 1; j < n; j++) s.push(`  E${i} ||--o{ E${j} : r`);
  }
  return s.join("\n");
}

/** mindmap with a single root and k sibling children. */
function mindmapWide(k: number): string {
  const s = ["mindmap", "root((r))"];
  for (let i = 0; i < k; i++) s.push(`  c${i}`);
  return s.join("\n");
}

describe("assessMermaidRenderCost — threshold anchored at mermaid's own maxEdges (500)", () => {
  it("exposes the cap as mermaid 11.15.0's DEFAULT_CONFIG.maxEdges value", () => {
    expect(MERMAID_MAX_LAYOUT_ELEMENTS).toBe(500);
  });

  it("refuses one edge over the cap and allows exactly at the cap (classDiagram)", () => {
    const s = ["classDiagram"];
    for (let i = 0; i < 500; i++) s.push(`  A${i} --> B${i}`);
    const at = assessMermaidRenderCost(s.join("\n"));
    expect(at).toMatchObject({ refuse: false, count: 500, kind: "edges", diagramType: "classdiagram" });

    s.push("  Z --> Y");
    const over = assessMermaidRenderCost(s.join("\n"));
    expect(over).toMatchObject({ refuse: true, count: 501, limit: 500, kind: "edges" });
  });
});

describe("assessMermaidRenderCost — refuses every measured freeze/hang", () => {
  it("classDiagram 60/1770 (measured 20 s freeze) is refused", () => {
    const v = assessMermaidRenderCost(classDense(60));
    expect(v.count).toBe((60 * 59) / 2); // 1770
    expect(v.refuse).toBe(true);
  });

  it("classDiagram 80/3160 and 120/7140 (measured HANG) are refused", () => {
    expect(assessMermaidRenderCost(classDense(80)).refuse).toBe(true);
    expect(assessMermaidRenderCost(classDense(120)).count).toBe((120 * 119) / 2); // 7140
    expect(assessMermaidRenderCost(classDense(120)).refuse).toBe(true);
  });

  it("erDiagram 90/4005 (measured HANG) is refused, counted as edges", () => {
    const v = assessMermaidRenderCost(erDense(90));
    expect(v.count).toBe((90 * 89) / 2); // 4005
    expect(v).toMatchObject({ refuse: true, kind: "edges", diagramType: "erdiagram" });
  });

  it("mindmap 2000 nodes (measured HANG) and 1000 (measured 29 s) are refused, counted as nodes", () => {
    const wide2000 = assessMermaidRenderCost(mindmapWide(2000));
    expect(wide2000).toMatchObject({ refuse: true, kind: "nodes", diagramType: "mindmap" });
    expect(wide2000.count).toBe(2001); // root + 2000 children
    expect(assessMermaidRenderCost(mindmapWide(1000)).refuse).toBe(true);
  });

  it("the small/plausible freeze — classDiagram 60, a 24 KB diagram — is refused", () => {
    const src = classDense(60);
    expect(src.length).toBeGreaterThan(20000);
    expect(src.length).toBeLessThan(100000); // well under the app's maxTextSize
    expect(assessMermaidRenderCost(src).refuse).toBe(true);
  });
});

describe("assessMermaidRenderCost — never refuses ordinary or cheap diagrams", () => {
  it("an ordinary small classDiagram renders", () => {
    const src = [
      "classDiagram",
      "  Animal <|-- Dog",
      "  Animal <|-- Cat",
      "  Dog --> Bone",
    ].join("\n");
    expect(assessMermaidRenderCost(src)).toMatchObject({ refuse: false, count: 3, kind: "edges" });
  });

  it("a verbose-but-small classDiagram (many method/attribute lines, few edges) is NOT refused", () => {
    // 30 classes each with 15 member lines = 450 body lines, but only 40 relations.
    // A raw line count would wrongly refuse this; edge counting must not.
    const s = ["classDiagram"];
    for (let c = 0; c < 30; c++) {
      s.push(`  class C${c} {`);
      for (let m = 0; m < 15; m++) s.push(`    +int field${m}`);
      s.push("  }");
    }
    for (let e = 0; e < 40; e++) s.push(`  C${e % 30} --> C${(e + 1) % 30}`);
    const v = assessMermaidRenderCost(s.join("\n"));
    expect(v.count).toBe(40);
    expect(v.refuse).toBe(false);
  });

  it("a large sequenceDiagram (3000 messages, measured cheap) is never capped", () => {
    const s = ["sequenceDiagram", "  participant A", "  participant B"];
    for (let i = 0; i < 3000; i++) {
      s.push(`  A->>B: m${i}`);
      s.push(`  B-->>A: r${i}`);
    }
    expect(assessMermaidRenderCost(s.join("\n"))).toMatchObject({
      refuse: false,
      kind: "none",
      diagramType: "sequencediagram",
    });
  });

  it("large gantt / gitGraph / timeline (measured cheap) are never capped", () => {
    const gantt = ["gantt", "  dateFormat YYYY-MM-DD"];
    for (let i = 0; i < 2500; i++) gantt.push(`  task${i} :t${i}, 2020-01-01, 3d`);
    expect(assessMermaidRenderCost(gantt.join("\n")).refuse).toBe(false);

    const git = ["gitGraph"];
    for (let i = 0; i < 2000; i++) git.push(`  commit id: "c${i}"`);
    expect(assessMermaidRenderCost(git.join("\n")).refuse).toBe(false);
  });

  it("an ordinary mindmap (tens of nodes) is not refused", () => {
    expect(assessMermaidRenderCost(mindmapWide(30)).refuse).toBe(false);
  });
});

describe("assessMermaidRenderCost — detection robustness", () => {
  it("detects the type after a YAML front-matter block", () => {
    const src = ["---", "title: My Diagram", "---", "classDiagram"]
      .concat(Array.from({ length: 600 }, (_, i) => `  A${i} --> B${i}`))
      .join("\n");
    expect(assessMermaidRenderCost(src)).toMatchObject({
      refuse: true,
      diagramType: "classdiagram",
      count: 600,
    });
  });

  it("ignores %% comment / init-directive lines for detection and counting", () => {
    const src = [
      "%%{init: {'theme':'dark'}}%%",
      "%% a note",
      "classDiagram",
      "%% another note",
      "  A --> B",
      "  B --> C",
    ].join("\n");
    expect(assessMermaidRenderCost(src)).toMatchObject({
      refuse: false,
      diagramType: "classdiagram",
      count: 2, // the two %% lines are not counted
    });
  });

  it("treats stateDiagram-v2, classDiagram-v2, requirement and flowchart as relation types", () => {
    expect(assessMermaidRenderCost("stateDiagram-v2\n  A --> B").kind).toBe("edges");
    expect(assessMermaidRenderCost("classDiagram-v2\n  A --> B").kind).toBe("edges");
    expect(assessMermaidRenderCost("requirementDiagram\n  R - satisfies -> E").kind).toBe("edges");
    expect(assessMermaidRenderCost("flowchart TD\n  A --> B").kind).toBe("edges");
    expect(assessMermaidRenderCost("graph TD\n  A --> B").kind).toBe("edges");
  });

  it("empty or unrecognized sources are not refused", () => {
    expect(assessMermaidRenderCost("").refuse).toBe(false);
    expect(assessMermaidRenderCost("   \n  \n").refuse).toBe(false);
    expect(assessMermaidRenderCost("pie\n  \"a\": 1").refuse).toBe(false);
  });
});
