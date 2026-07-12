import { walkVisibleTextNodes } from "./findVisibleText";
import type { LegacyScrollWriter } from "./legacyScrollWriter";

// Find-in-document feature (Ctrl+F). Renderer-side, CSS Custom Highlight API.
//
// Why CSS Custom Highlight API (not window.find / document Selection)?
// The previous MVP drove the single document Selection for THREE jobs at once —
// the find input's text caret, the match highlight, and window.find's navigation
// cursor — which conflict. Runtime-proven 2026-06-13 (probe3/4/5.log):
//   * window.find()/Selection moves the caret OUT of the <input>, so keydown +
//     keypress fire but beforeinput/input do not → typing dies after one char.
//   * the prev/next handlers' input.focus() collapses the Selection, so the next
//     window.find restarts from the top → navigation sticks (idx 0->3 forever).
//   * a TreeWalker count disagreed with window.find's reachable set → "3 of 10".
// CSS.highlights paints matches via Range objects in a side registry WITHOUT
// touching the document Selection, so the input keeps its caret and navigation
// is a deterministic index over an ordered Range[]. WebView2 149 (Chromium 149;
// CSS.highlights shipped Chromium 105). Future: when Avalonia.Controls.WebView
// exposes managed CoreWebView2, swap the match source behind this module's
// Range[]/index seam to CoreWebView2.FindController — the DOM/CSS surface here
// does not change.
//
// Match styling: ::highlight(mm-find-all) + ::highlight(mm-find-current) in
// renderer.css. No <mark> DOM mutation (keeps math / mermaid SVG / hljs
// decorations / the TOC IntersectionObserver intact).
//
// Hidden-text exclusion: some text is in the DOM (so a TreeWalker reaches it)
// but not visible, so window.find skipped it — if we matched it we would emit
// phantom matches that inflate the count and strand navigation on invisible
// nodes. We reject those subtrees: `.katex-mathml` (clip-hidden raw TeX source,
// KaTeX htmlAndMathml default) and `pre.mm-mermaid.is-rendered` (display:none
// mermaid source after the SVG renders). These two plus the minimap aside are
// the only invisible-text surfaces in the renderer.
//
// Freshness: matches are live Range objects, and the DOM mutates after
// layout-ready (KaTeX, Highlight.js, lazy Mermaid on scroll). A MutationObserver
// — active ONLY while the bar is open — flags the match set stale; it is rebuilt
// lazily on the next search/navigate. The observer callback ONLY sets the flag
// (it never rebuilds, re-applies, or scrolls), so a find-navigate scroll that
// reveals a lazy block cannot feed back into a rebuild storm.
//
// Per-document reset: renderer.ts's `resetModuleGlobalsForLoadDocument` invokes
// `close()` on doc swap, which disconnects the observer and clears highlights.

const FIND_BAR_CLASS = "mm-find-bar";
const FIND_INPUT_CLASS = "mm-find-input";
const FIND_COUNT_CLASS = "mm-find-count";
const FIND_BTN_CLASS = "mm-find-btn";
const FIND_DEBOUNCE_MS = 150;

// Well-known CSS.highlights registry names. Safe because there is exactly ONE
// find-bar controller (createFindBar() is called once at renderer.ts wireFindBar)
// so these global names cannot collide. A future second find surface MUST
// namespace these.
const HIGHLIGHT_ALL = "mm-find-all";
const HIGHLIGHT_CURRENT = "mm-find-current";

export type FindBarController = {
  /** Open the find bar; focuses the input. If already open, refocuses. */
  open: () => void;
  /** Close the find bar, clear highlights, reset state. */
  close: () => void;
  /** Toggle: open if closed, close if open. */
  toggle: () => void;
  /** True when the bar is visible. */
  readonly isOpen: boolean;
};

export type FindNavigationDirection = "next" | "prev";

export type FindProviderStatus = {
  query: string;
  totalCount: number;
  shownCount: number;
  skippedCount: number;
  truncated: boolean;
  currentIndex: number;
};

export type FindProviderView = {
  updateStatus: (status: FindProviderStatus) => void;
};

export type FindProvider = {
  setView: (view: FindProviderView) => void;
  search: (query: string) => void;
  navigate: (direction: FindNavigationDirection) => void;
  close: () => void;
};

type State = {
  bar: HTMLDivElement;
  input: HTMLInputElement;
  count: HTMLSpanElement;
  prevBtn: HTMLButtonElement;
  nextBtn: HTMLButtonElement;
  closeBtn: HTMLButtonElement;
  debounceTimer: number | null;
  /** Last query the match set was built for. */
  lastSearched: string;
  /** Ordered (document order) match ranges for `lastSearched`. */
  matches: Range[];
  /** 0-based index of the current match; -1 when none. */
  currentIndex: number;
  /** Set by the MutationObserver; consumed lazily before search/navigate. */
  matchesDirty: boolean;
  /** Active only while the bar is open. */
  observer: MutationObserver | null;
  legacyScrollWriter: LegacyScrollWriter;
};

// --- CSS Custom Highlight API access (typed defensively so tsc passes even when
// the lib target predates the API; behaviour is feature-detected at runtime) ---

interface HighlightRegistryLike {
  set(name: string, highlight: unknown): void;
  delete(name: string): void;
}

function getHighlightRegistry(): HighlightRegistryLike | null {
  const css = (window as unknown as {
    CSS?: { highlights?: HighlightRegistryLike };
  }).CSS;
  return css?.highlights ?? null;
}

function makeHighlight(ranges: Range[]): unknown | null {
  const ctor = (window as unknown as {
    Highlight?: new (...ranges: Range[]) => unknown;
  }).Highlight;
  if (ctor === undefined || ranges.length === 0) {
    return null;
  }
  return new ctor(...ranges);
}

/**
 * Case-insensitive match offsets for `needle` within `haystack`, returned as
 * [start, end) index pairs that are VALID in `haystack` (the ORIGINAL string) —
 * so a caller may pass them straight to `Range.setStart`/`setEnd` on the text
 * node whose value is `haystack`.
 *
 * `String.prototype.toLowerCase()` can change a string's length: 'İ' (U+0130,
 * common in Turkish text) lowercases to 'i̇' (two code units), and other locale
 * expansions exist. The old code computed `indexOf` offsets in the LOWERCASED
 * text and applied them to the ORIGINAL DOM text node; once a length-expanding
 * character preceded a match, the offset overshot the node length and
 * `setEnd` threw `IndexSizeError`, which propagated out of the search and broke
 * find entirely (no try/catch on the build path).
 *
 * When lowercasing preserves the length (all ASCII and the overwhelming
 * majority of real text) the lowercased offset IS the original offset — the
 * fast path. When it does not, a lowercased offset has no unambiguous original
 * mapping, so those matches are skipped rather than mis-placed or thrown — a
 * bounded, documented limitation like the cross-text-node-boundary one above.
 */
export function findCaseInsensitiveMatchOffsets(
  haystack: string,
  needle: string
): Array<[number, number]> {
  const out: Array<[number, number]> = [];
  if (needle.length === 0) {
    return out;
  }
  const lowered = needle.toLowerCase();
  const text = haystack.toLowerCase();
  // Skip when lowercasing changed the length (offsets would be invalid in the
  // original node) or the node is shorter than the needle.
  if (text.length !== haystack.length || text.length < lowered.length) {
    return out;
  }
  let idx = text.indexOf(lowered);
  while (idx !== -1) {
    out.push([idx, idx + lowered.length]);
    idx = text.indexOf(lowered, idx + lowered.length);
  }
  return out;
}

/**
 * Build an ordered list of match Ranges for `needle` under `root`. Document
 * order, case-insensitive, per-text-node (a match spanning a text-node boundary
 * — e.g. a word split by an inline element — is not found; pre-existing
 * limitation). Skips decorative and hidden-source subtrees.
 */
export function buildMatches(root: Node, needle: string): Range[] {
  const out: Range[] = [];
  if (needle.length === 0) {
    return out;
  }

  for (const node of walkVisibleTextNodes(root)) {
    // Offsets come back valid in node.nodeValue (the ORIGINAL text), so
    // setStart/setEnd can never overshoot the node — the old lowercased-offset
    // math threw IndexSizeError on text containing length-expanding characters.
    for (const [start, end] of findCaseInsensitiveMatchOffsets(node.nodeValue ?? "", needle)) {
      const range = document.createRange();
      range.setStart(node, start);
      range.setEnd(node, end);
      out.push(range);
    }
  }

  return out;
}

function applyHighlights(s: State): void {
  applyFindHighlights(s.matches, s.matches[s.currentIndex]);
}

export function applyFindHighlights(ranges: Range[], currentRange?: Range): void {
  const reg = getHighlightRegistry();
  if (reg === null) {
    return;
  }
  if (ranges.length === 0) {
    reg.delete(HIGHLIGHT_ALL);
    reg.delete(HIGHLIGHT_CURRENT);
    return;
  }
  const all = makeHighlight(ranges);
  if (all !== null) {
    reg.set(HIGHLIGHT_ALL, all);
  }
  if (currentRange !== undefined) {
    const current = makeHighlight([currentRange]);
    if (current !== null) {
      reg.set(HIGHLIGHT_CURRENT, current);
    }
  } else {
    reg.delete(HIGHLIGHT_CURRENT);
  }
}

export function clearFindHighlights(): void {
  const reg = getHighlightRegistry();
  if (reg !== null) {
    reg.delete(HIGHLIGHT_ALL);
    reg.delete(HIGHLIGHT_CURRENT);
  }
}

function clearHighlights(): void {
  clearFindHighlights();
}

function rebuildMatches(s: State): void {
  s.matches = buildMatches(document.body, s.lastSearched);
  s.matchesDirty = false;
  if (s.matches.length === 0) {
    s.currentIndex = -1;
  } else {
    // Preserve position across a mutation-driven rebuild by clamping.
    s.currentIndex = Math.min(Math.max(s.currentIndex, 0), s.matches.length - 1);
  }
}

function ensureFresh(s: State): void {
  if (s.matchesDirty) {
    rebuildMatches(s);
  }
}

/**
 * Scroll the current match into view. Two-step because every top-level document
 * block is `content-visibility:auto` (renderer.css): a collapsed block skips
 * paint and its descendants have no geometry, so we first scroll the nearest
 * c-v-owning block (a direct child of `main.mm-document`) to force it to render,
 * then — once the match Range has real geometry — re-aim on the match itself.
 * No DOM mutation (so the observer is not tripped, and other Ranges are not
 * invalidated by node splitting).
 */
function scrollToCurrent(s: State): void {
  const range = s.matches[s.currentIndex];
  if (range === undefined) {
    return;
  }
  const host = range.startContainer.parentElement;
  const block = host?.closest("main.mm-document > *") ?? host;
  // Step 1 — reveal the (possibly collapsed) c-v block.
  if (block !== null && block !== undefined) {
    s.legacyScrollWriter.legacyScrollIntoView(block, { block: "center" });
  }

  // Step 2 — bounded rAF settle: once the block paints, the Range gets real
  // geometry; re-center on it if it is off-screen. Same proven shape as
  // scrollFromMinimapClientY's c-v settle (renderer.ts), replicated locally.
  let attempts = 0;
  const reaim = (): void => {
    if (++attempts > 3) {
      return;
    }
    const rect = range.getBoundingClientRect();
    if (rect.height === 0 && rect.width === 0) {
      // Block not painted yet — wait another frame.
      window.requestAnimationFrame(reaim);
      return;
    }
    const viewport = window.innerHeight || document.documentElement.clientHeight;
    if (rect.top < 0 || rect.bottom > viewport) {
      const target = window.scrollY + rect.top - viewport / 2 + rect.height / 2;
      s.legacyScrollWriter.legacyScrollTo(Math.max(0, target), { behavior: "instant" as ScrollBehavior });
      window.requestAnimationFrame(reaim);
    }
  };
  window.requestAnimationFrame(reaim);
}

function updateCountDisplay(s: State): void {
  if (s.input.value.length === 0) {
    s.count.textContent = "";
    s.bar.classList.remove("mm-find-no-match");
    return;
  }
  if (s.matches.length === 0) {
    s.count.textContent = "0 of 0";
    s.bar.classList.add("mm-find-no-match");
    return;
  }
  s.bar.classList.remove("mm-find-no-match");
  s.count.textContent = `${s.currentIndex + 1} of ${s.matches.length}`;
}

function updateProviderCountDisplay(s: State, status: FindProviderStatus): void {
  if (s.input.value.length === 0 || status.query.length === 0) {
    s.count.textContent = "";
    s.bar.classList.remove("mm-find-no-match");
    return;
  }
  const shownCount = normalizeProviderCount(status.shownCount);
  if (shownCount === 0) {
    s.count.textContent = formatProviderCountText(status, shownCount);
    s.bar.classList.add("mm-find-no-match");
    return;
  }
  s.bar.classList.remove("mm-find-no-match");
  s.count.textContent = formatProviderCountText(status, shownCount);
}

function normalizeProviderCount(value: number): number {
  return Number.isFinite(value) ? Math.max(0, Math.floor(value)) : 0;
}

function formatProviderCountText(status: FindProviderStatus, shownCount: number): string {
  const currentPosition = status.currentIndex >= 0 && shownCount > 0
    ? Math.min(status.currentIndex + 1, shownCount)
    : 0;
  const base = `${currentPosition} of ${shownCount}`;
  const totalCount = normalizeProviderCount(status.totalCount);
  const skippedCount = normalizeProviderCount(status.skippedCount);
  const details: string[] = [];

  if (status.truncated || skippedCount > 0 || totalCount !== shownCount) {
    details.push(`${totalCount} total`);
  }
  if (skippedCount > 0) {
    details.push(`${skippedCount} skipped`);
  }
  if (status.truncated) {
    details.push("truncated");
  }

  return details.length === 0
    ? base
    : `${base} shown (${details.join(", ")})`;
}

function runSearch(s: State, query: string): void {
  s.lastSearched = query;
  if (query.length === 0) {
    s.matches = [];
    s.currentIndex = -1;
    s.matchesDirty = false;
    applyHighlights(s);
    updateCountDisplay(s);
    return;
  }
  s.matches = buildMatches(document.body, query);
  s.matchesDirty = false;
  s.currentIndex = s.matches.length > 0 ? 0 : -1;
  applyHighlights(s);
  if (s.currentIndex >= 0) {
    scrollToCurrent(s);
  }
  updateCountDisplay(s);
}

function navigate(s: State, direction: "next" | "prev"): void {
  ensureFresh(s);
  const n = s.matches.length;
  if (n === 0) {
    applyHighlights(s);
    updateCountDisplay(s);
    return;
  }
  if (s.currentIndex < 0) {
    s.currentIndex = direction === "next" ? 0 : n - 1;
  } else {
    s.currentIndex = (s.currentIndex + (direction === "next" ? 1 : -1) + n) % n;
  }
  applyHighlights(s);
  scrollToCurrent(s);
  updateCountDisplay(s);
}

/**
 * Create a find-bar controller. The bar lives as a fixed-position sibling of
 * <main> under <body> (matches minimap / width-handle / drop-overlay pattern;
 * survives the load-document innerHTML swap on <main>, though we still call
 * `close()` on doc-swap to reset state).
 */
export function createFindBar(
  legacyScrollWriter: LegacyScrollWriter,
  provider?: FindProvider
): FindBarController {
  let state: State | null = null;

  function buildDom(): State {
    const bar = document.createElement("div");
    bar.className = FIND_BAR_CLASS;
    bar.setAttribute("role", "search");
    bar.setAttribute("aria-label", "Find in document");

    const input = document.createElement("input");
    input.type = "search";
    input.className = FIND_INPUT_CLASS;
    input.setAttribute("aria-label", "Find in document");
    input.placeholder = "Find in document";
    input.spellcheck = false;
    input.autocomplete = "off";

    const count = document.createElement("span");
    count.className = FIND_COUNT_CLASS;
    count.setAttribute("aria-live", "polite");
    count.textContent = "";

    const prevBtn = document.createElement("button");
    prevBtn.type = "button";
    prevBtn.className = `${FIND_BTN_CLASS} ${FIND_BTN_CLASS}-prev`;
    prevBtn.setAttribute("aria-label", "Previous match");
    prevBtn.title = "Previous match (Shift+Enter)";
    prevBtn.textContent = "↑";

    const nextBtn = document.createElement("button");
    nextBtn.type = "button";
    nextBtn.className = `${FIND_BTN_CLASS} ${FIND_BTN_CLASS}-next`;
    nextBtn.setAttribute("aria-label", "Next match");
    nextBtn.title = "Next match (Enter)";
    nextBtn.textContent = "↓";

    const closeBtn = document.createElement("button");
    closeBtn.type = "button";
    closeBtn.className = `${FIND_BTN_CLASS} ${FIND_BTN_CLASS}-close`;
    closeBtn.setAttribute("aria-label", "Close find bar");
    closeBtn.title = "Close (Esc)";
    closeBtn.textContent = "×";

    bar.appendChild(input);
    bar.appendChild(count);
    bar.appendChild(prevBtn);
    bar.appendChild(nextBtn);
    bar.appendChild(closeBtn);

    return {
      bar,
      input,
      count,
      prevBtn,
      nextBtn,
      closeBtn,
      debounceTimer: null,
      lastSearched: "",
      matches: [],
      currentIndex: -1,
      matchesDirty: false,
      observer: null,
      legacyScrollWriter,
    };
  }

  function connectObserver(s: State): void {
    if (s.observer !== null) {
      return;
    }
    const main = document.querySelector("main.mm-document");
    if (main === null) {
      return;
    }
    // Flag-only callback: never rebuild/re-apply/scroll here, so a navigate
    // scroll that triggers a lazy Mermaid render cannot feed back into a
    // rebuild storm. The flag is consumed lazily by the next search/navigate.
    // characterData is intentionally omitted: this is a read-only viewer; the
    // mutations that change the match set (KaTeX / hljs / Mermaid) are childList.
    s.observer = new MutationObserver(() => {
      s.matchesDirty = true;
    });
    s.observer.observe(main, { childList: true, subtree: true });
  }

  function attachListeners(s: State): void {
    provider?.setView({
      updateStatus: status => updateProviderCountDisplay(s, status),
    });

    s.input.addEventListener("input", () => {
      const query = s.input.value;
      if (s.debounceTimer !== null) {
        window.clearTimeout(s.debounceTimer);
      }
      s.debounceTimer = window.setTimeout(() => {
        s.debounceTimer = null;
        if (provider) {
          provider.search(query);
        } else {
          runSearch(s, query);
        }
      }, FIND_DEBOUNCE_MS);
    });

    s.input.addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        event.preventDefault();
        // Flush a pending debounced search first.
        if (s.debounceTimer !== null) {
          window.clearTimeout(s.debounceTimer);
          s.debounceTimer = null;
          if (provider) {
            provider.search(s.input.value);
          } else {
            runSearch(s, s.input.value);
          }
          return;
        }
        if (provider) {
          provider.navigate(event.shiftKey ? "prev" : "next");
        } else {
          navigate(s, event.shiftKey ? "prev" : "next");
        }
      } else if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
        close();
      }
    });

    // Re-focusing the input after navigation is now safe: the highlight is a CSS
    // registry entry, not the document Selection, so focusing the input does not
    // disturb it. Keeping focus in the input lets the user keep typing.
    s.prevBtn.addEventListener("click", () => {
      if (provider) {
        provider.navigate("prev");
      } else {
        navigate(s, "prev");
      }
      s.input.focus();
    });
    s.nextBtn.addEventListener("click", () => {
      if (provider) {
        provider.navigate("next");
      } else {
        navigate(s, "next");
      }
      s.input.focus();
    });
    s.closeBtn.addEventListener("click", () => {
      close();
    });
  }

  function open(): void {
    if (state === null) {
      state = buildDom();
      attachListeners(state);
      document.body.appendChild(state.bar);
    } else if (state.bar.parentNode === null) {
      document.body.appendChild(state.bar);
    }
    state.bar.classList.add("mm-find-bar-open");
    if (!provider) {
      connectObserver(state);
    }
    state.input.focus();
    state.input.select();
  }

  function close(): void {
    if (state === null) {
      return;
    }
    if (state.debounceTimer !== null) {
      window.clearTimeout(state.debounceTimer);
      state.debounceTimer = null;
    }
    if (state.observer !== null) {
      state.observer.disconnect();
      state.observer = null;
    }
    state.bar.classList.remove("mm-find-bar-open");
    state.input.value = "";
    state.lastSearched = "";
    state.matches = [];
    state.currentIndex = -1;
    state.matchesDirty = false;
    state.count.textContent = "";
    state.bar.classList.remove("mm-find-no-match");
    provider?.close();
    clearHighlights();
    // Keep the node in the DOM for fast reopen; only re-build on first open.
  }

  function toggle(): void {
    if (state !== null && state.bar.classList.contains("mm-find-bar-open")) {
      close();
    } else {
      open();
    }
  }

  return {
    open,
    close,
    toggle,
    get isOpen(): boolean {
      return state !== null && state.bar.classList.contains("mm-find-bar-open");
    },
  };
}
