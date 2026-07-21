// Pre-render cost guard for mermaid.render.
//
// WHY THIS EXISTS (runtime-confirmed 2026-07-21, CDP probe over the live viewer host):
// mermaid 11.15.0 enforces its `maxEdges` cap (DEFAULT_CONFIG.maxEdges = 500, the
// `Edge limit exceeded` throw) ONLY for flowchart. classDiagram, erDiagram,
// stateDiagram(-v2), requirementDiagram and mindmap run dagre / hierarchy layout with
// no element bound at all; `maxTextSize` (the app sets 100_000) bounds source LENGTH,
// not layout COST -- over the limit mermaid swaps in a tiny placeholder graph rather
// than throwing. So a diagram a user can actually author drives mermaid.render into a
// SYNCHRONOUS, CPU-bound freeze of the renderer's only JS thread:
//
//   classDiagram, dense all-pairs:  40 classes /  780 relations -> 4.0 s
//                                    60 classes / 1770 relations -> 20.0 s (24 KB source)
//                                    80 classes / 3160 relations -> HANG >63 s
//                                   120 classes / 7140 relations -> HANG 152.8 s
//   erDiagram tracks the same curve; mindmap 2000 nodes -> HANG, 1000 nodes -> 29 s.
//
// Because the freeze is synchronous the message pump is blocked, so NOTHING can cancel
// it once it starts -- a host->renderer cancel message is never processed. The only
// fix is to refuse the render BEFORE calling mermaid, from a cheap estimate of layout
// cost computed off the raw source. That estimate and its threshold live here so they
// are unit-testable without a real mermaid.
//
// THRESHOLD. Anchored to mermaid's OWN number: 500. For the relationship families this
// is exactly mermaid's flowchart `maxEdges`, generalized to the types where mermaid
// forgot to apply it -- at 500 relations the measured render is ~2 s (well under the
// 4 s seen at 780), while the 20 s freeze at 1770 sits 3.5x above the cap. For mindmap
// the 500 counts NODES; 500 nodes measured 5.7 s (the last point below the freeze
// zone), and 1000 nodes 29 s sits above. Ordinary hand-authored diagrams carry tens of
// edges/nodes -- 5x-10x below the cap -- so none is ever refused.
//
// WHAT IS COUNTED. A cheap, type-aware, parse-free proxy over the raw source lines:
//   - relationship families (class / er / state / requirement / flowchart|graph):
//     the number of lines carrying an edge connector (`-->`, `..>`, `||--o{`, `- x ->`,
//     ...). Body/attribute lines (`+String name`, `id: 1`, `risk: high`) carry no
//     connector and are ignored, so a verbose-but-small diagram is never refused.
//   - mindmap (no connectors; cost is node count): non-blank content lines = nodes.
//   - every other detected type (sequence, gantt, git, timeline, sankey, ...): probe-
//     confirmed cheap even at extreme sizes -> never capped.
// The proxy over-counts only in the fail-safe direction (a stray `--` in a label) and
// only for already-large diagrams; it never under-counts an ordinary one into a freeze.
//
// RESIDUAL (not caught by this proxy, documented rather than glossed): pathological
// NESTING depth with few edges -- e.g. 250-deep nested composite states, 400-deep
// nested flowchart subgraphs -- which the probe found SETTLE (<=9.3 s / <=4.4 s), never
// hang, and which no cheap line proxy can separate from a verbose small diagram without
// regressing the latter. All CONFIRMED hangs are edge- or node-dense and are caught.

export const MERMAID_MAX_LAYOUT_ELEMENTS = 500; // == mermaid 11.15.0 DEFAULT_CONFIG.maxEdges

export interface MermaidCostVerdict {
  /** True when the diagram is too large to lay out and must be refused before render. */
  refuse: boolean;
  /** Elements counted: edges for relationship types, nodes for mindmap, 0 otherwise. */
  count: number;
  /** The cap `count` was compared against. */
  limit: number;
  /** What `count` measures. */
  kind: "edges" | "nodes" | "none";
  /** The detected mermaid type keyword (lower-cased), or "" when unrecognized. */
  diagramType: string;
}

// Relationship (dagre / elk) families that mermaid leaves unbounded. Matched by the
// same header keywords mermaid's own detectors use (`/^\s*classDiagram/` etc.); a
// lower-cased startsWith is intentionally over-inclusive (a mis-cased or -v2 variant is
// still guarded, and mermaid rejects a genuinely invalid keyword anyway).
const RELATION_TYPE_PREFIXES = [
  "classdiagram",
  "erdiagram",
  "statediagram",
  "requirement", // requirement | requirementDiagram
  "flowchart",   // flowchart | flowchart-elk
  "graph",
] as const;

const HIERARCHY_TYPE_PREFIXES = ["mindmap"] as const;

// A line carries a graph edge if it contains any relationship connector. Longer tokens
// are listed first for readability; this is only ever used as a boolean line test.
const EDGE_CONNECTOR =
  /-->|---|--o|--\*|--\||<\|--|\*--|o--|\.\.>|\.\.\||<\.\.|\|\|--|\}o|o\{|\}\||\|\{|==>|===|-\.->|-\.-|-\.|->|<-|--|\.\./;

const FRONT_MATTER_FENCE = /^\s*---\s*$/;
const COMMENT_OR_DIRECTIVE = /^\s*%%/;

// Drop a leading YAML front-matter block and every `%%`-prefixed comment / init
// directive line, mirroring mermaid's own pre-detection cleanup closely enough that the
// first surviving line is the type keyword and body counting is not skewed by comments.
function meaningfulLines(source: string): string[] {
  let lines = source.split(/\r?\n/);
  let start = 0;
  while (start < lines.length && lines[start]!.trim() === "") start++;
  if (start < lines.length && FRONT_MATTER_FENCE.test(lines[start]!)) {
    let end = start + 1;
    while (end < lines.length && !FRONT_MATTER_FENCE.test(lines[end]!)) end++;
    // Drop the whole fenced block (including the closing fence when present).
    lines = lines.slice(end < lines.length ? end + 1 : end);
  } else if (start > 0) {
    lines = lines.slice(start);
  }
  return lines.filter(line => !COMMENT_OR_DIRECTIVE.test(line));
}

function classify(header: string): "relation" | "hierarchy" | "other" {
  for (const prefix of RELATION_TYPE_PREFIXES) {
    if (header.startsWith(prefix)) return "relation";
  }
  for (const prefix of HIERARCHY_TYPE_PREFIXES) {
    if (header.startsWith(prefix)) return "hierarchy";
  }
  return "other";
}

export function assessMermaidRenderCost(
  source: string,
  limit: number = MERMAID_MAX_LAYOUT_ELEMENTS
): MermaidCostVerdict {
  const lines = meaningfulLines(source);

  // First non-blank line is the type declaration; the rest is the body to count.
  let headerIndex = -1;
  for (let i = 0; i < lines.length; i++) {
    if (lines[i]!.trim() !== "") {
      headerIndex = i;
      break;
    }
  }
  if (headerIndex === -1) {
    return { refuse: false, count: 0, limit, kind: "none", diagramType: "" };
  }

  const diagramType = lines[headerIndex]!.trim().toLowerCase().split(/\s+/)[0] ?? "";
  const family = classify(diagramType);
  const body = lines.slice(headerIndex + 1);

  if (family === "relation") {
    let count = 0;
    for (const line of body) {
      if (EDGE_CONNECTOR.test(line)) count++;
    }
    return { refuse: count > limit, count, limit, kind: "edges", diagramType };
  }

  if (family === "hierarchy") {
    let count = 0;
    for (const line of body) {
      if (line.trim() !== "") count++;
    }
    return { refuse: count > limit, count, limit, kind: "nodes", diagramType };
  }

  return { refuse: false, count: 0, limit, kind: "none", diagramType };
}
