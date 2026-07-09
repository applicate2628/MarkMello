import { describe, it, expect } from "vitest";
import { findCaseInsensitiveMatchOffsets } from "../src/findBar";

// The match-offset logic — where the reported crash lived — is extracted into
// the pure findCaseInsensitiveMatchOffsets and covered directly below.

describe("findCaseInsensitiveMatchOffsets", () => {
  it("returns case-insensitive offsets valid in the original string", () => {
    expect(findCaseInsensitiveMatchOffsets("Hello WORLD hello", "hello")).toEqual([
      [0, 5],
      [12, 17],
    ]);
  });

  it("returns nothing for an empty needle or a too-short haystack", () => {
    expect(findCaseInsensitiveMatchOffsets("abc", "")).toEqual([]);
    expect(findCaseInsensitiveMatchOffsets("ab", "abc")).toEqual([]);
  });

  it("never yields an offset past the original length when toLowerCase expands a char", () => {
    // 'İ' (U+0130) lowercases to two code units ("i" + combining dot U+0307),
    // so an offset computed in the LOWERCASED text can exceed the ORIGINAL
    // length. The old code applied such an offset to the DOM text node and threw
    // IndexSizeError; the helper must never return an out-of-bounds offset.
    const haystack = "aİb"; // length 3; "aİb".toLowerCase() === "ai̇b" (length 4)
    for (const [start, end] of findCaseInsensitiveMatchOffsets(haystack, "b")) {
      expect(start).toBeGreaterThanOrEqual(0);
      expect(end).toBeLessThanOrEqual(haystack.length);
    }
  });
});

describe("the crash mechanism this fixes (DOM Range bounds)", () => {
  it("the OLD lowercased offset overshoots the original node and throws", () => {
    // Pins the cause in a real DOM environment: the old buildMatches computed the
    // match offset in the lowercased text and applied it to the ORIGINAL node.
    document.body.replaceChildren();
    const p = document.createElement("p");
    p.textContent = "aİb"; // length 3
    document.body.appendChild(p);
    const textNode = p.firstChild as Text;

    const lowered = "aİb".toLowerCase(); // "ai̇b" length 4
    const badEndOffset = lowered.indexOf("b") + 1; // 4 — what the old code produced
    const range = document.createRange();
    range.setStart(textNode, lowered.indexOf("b")); // 3

    expect(() => range.setEnd(textNode, badEndOffset)).toThrow();

    // The fix's helper never produces that offset, so no range is created for it.
    expect(findCaseInsensitiveMatchOffsets("aİb", "b")).toEqual([]);
  });
});
