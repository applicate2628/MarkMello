import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

// The `:not(.is-rendered)` invariant, enforced instead of remembered.
//
// A rendered Mermaid <pre> keeps its `data-mm-block-index` but is display:none
// (renderer.css `pre.mm-mermaid.is-rendered { display:none }`). A live-block
// selector that fails to exclude it puts a zero-height element into a list whose
// consumers assume monotonic document bottoms — the block-index drift that
// shipped once already and was fixed in 0.3.21. The project note on that fix says
// a future block-hiding feature that forgets the exclusion makes the drift return
// SILENTLY, which is exactly what this file exists to prevent.
//
// topVisibleBlockIndex.test.ts (G-M8) already pins the two OWNER selectors byte
// for byte, and topVisibleBlockIndex's own behavioural tests fail if either loses
// the exclusion. What was unguarded until now is everything OUTSIDE that owner:
// a new selector added elsewhere, or an exclusion dropped from a Mermaid sweep,
// changed no test. Verified 2026-08-04 by mutation — deleting `:not(.is-rendered)`
// from the sweep at renderer.ts:1143 left all 434 tests green.
//
// These are inventory guards, not behaviour guards: they fail on ANY change to
// the selector population, including a legitimate one. That is the intent — the
// fix for a legitimate addition is to add it here WITH its exclusion decision
// written down, which is the review moment the convention otherwise lacks.
//
// Ambient inputs: none. This is a pure synchronous read of committed source
// text — no clock, locale, timezone, randomness, network, filesystem ordering,
// or parallel scheduling participates, so no pinning is applicable.

const RENDERER_SOURCES = [
  "RendererWeb/src/renderer.ts",
  "RendererWeb/src/topVisibleBlockIndex.ts",
  "RendererWeb/src/mermaidSurface.ts",
  "RendererWeb/src/mathRenderInit.ts",
] as const;

// The load-bearing fragment of a block-index selector: the attribute test plus
// any exclusion bound directly to it. Matching this rather than the whole source
// line keeps the guard stable under reformatting while still failing the moment
// an exclusion appears or disappears.
const BLOCK_INDEX_SELECTOR = /\[data-mm-block-index(?:="[^"]*")?\](?::not\(\.is-rendered\))?/g;

function codeOnly(source: string): string {
  return source
    .split("\n")
    .filter((line) => {
      const trimmed = line.trim();
      return !(trimmed.startsWith("//") || trimmed.startsWith("*") || trimmed.startsWith("/*"));
    })
    .join("\n");
}

function blockIndexSelectorsIn(file: string): string[] {
  const source = codeOnly(readFileSync(file, "utf8"));
  return Array.from(source.matchAll(BLOCK_INDEX_SELECTOR), (match) => match[0]);
}

describe("live-block selector invariant", () => {
  // Every `data-mm-block-index` selector in the renderer, in source order, with
  // its exclusion decision. A new selector, a deleted one, or a flipped exclusion
  // all fail here. When that happens, decide deliberately which list the new
  // selector belongs to before updating this inventory.
  it("pins every block-index selector and whether it excludes rendered Mermaid", () => {
    expect(blockIndexSelectorsIn("RendererWeb/src/topVisibleBlockIndex.ts")).toEqual([
      // The live-block set. Consumers binary-search it for monotonic bottoms, so
      // a display:none member is the drift bug itself. MUST exclude.
      "[data-mm-block-index]:not(.is-rendered)",
      // Single-block scroll anchor, same contract as the set above. MUST exclude.
      '[data-mm-block-index="${blockIndex}"]:not(.is-rendered)',
    ]);

    expect(blockIndexSelectorsIn("RendererWeb/src/renderer.ts")).toEqual([
      // rebuildMinimapCloneBlockElementIndex — runs over the minimap CLONE, whose
      // geometry mapping needs the hidden twins present. Correctly unexcluded.
      "[data-mm-block-index]",
      // docScrollTopForCloneY — deliberately rendered-INCLUSIVE; it measures the
      // hidden box on purpose. Locked behaviourally by
      // rendererMinimapCloneLookup.test.ts. Correctly unexcluded.
      '[data-mm-block-index="${idx}"]',
      // scroll-to-block IPC handler. Unscoped AND unexcluded, but currently
      // unreachable: ApplicateWebMarkdownDocumentView.ScrollToBlock has no caller
      // (virtualization-era leftover). If that message is ever wired up, this
      // selector needs BOTH `body > main.mm-document` scoping and the exclusion —
      // prefer liveBlockSelectorForIndex() from topVisibleBlockIndex.ts.
      '[data-mm-block-index="${message.blockIndex}"]',
    ]);

    expect(blockIndexSelectorsIn("RendererWeb/src/mermaidSurface.ts")).toEqual([
      // resolveMermaidMirrors — resolves a diagram's twin inside the clone. The
      // twin is is-rendered by design, so excluding would break mirroring.
      '[data-mm-block-index="${key}"]',
    ]);

    expect(blockIndexSelectorsIn("RendererWeb/src/mathRenderInit.ts")).toEqual([
      // closest() ancestor walk from a math node up to its block. Not a live-block
      // sweep and not index arithmetic; exclusion is not applicable.
      "[data-mm-block-index]",
    ]);
  });

  // Guards a hole in handleHostMessageLoadDocument.test.ts's sweep check: there the
  // `(:not\(\.is-rendered\))?` group is OPTIONAL and its value is never asserted,
  // so that test pins the sweep COUNT and ROOT but not the exclusion. Dropping the
  // exclusion from any of the three lazy sweeps kept it green (verified by
  // mutation, 2026-08-04). Here the per-sweep exclusion is the assertion.
  it("pins which Mermaid sweeps exclude already-rendered diagrams", () => {
    const source = codeOnly(readFileSync("RendererWeb/src/renderer.ts", "utf8"));
    const sweeps = Array.from(
      source.matchAll(
        /querySelectorAll<HTMLElement>\(\s*"pre\.mm-mermaid(:not\(\.is-rendered\))?"/g,
      ),
      (match) => match[1] ?? "NO-EXCLUSION",
    );

    expect(sweeps).toEqual([
      // renderMermaid — theme refresh and post-ready re-render every diagram,
      // including already-rendered ones. Correctly and necessarily unexcluded.
      "NO-EXCLUSION",
      // recoverMermaidBarrierFailure — re-renders only what is still missing.
      ":not(.is-rendered)",
      // scheduleCachedMermaidResume — resumes only unrendered diagrams.
      ":not(.is-rendered)",
      // driveFullRenderBarrier — waits only on unrendered diagrams.
      ":not(.is-rendered)",
    ]);
  });
});
