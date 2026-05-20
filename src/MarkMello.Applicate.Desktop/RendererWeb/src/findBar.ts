// Find-in-document feature (Ctrl+F). Renderer-side MVP — no host changes.
//
// Why window.find() (deprecated/non-standard but Chromium-supported)?
// WebView2 is Chromium, so window.find() works. The "blessed" path
// would be CoreWebView2.FindController, but Avalonia.Controls.WebView
// 12.0.x does not expose the managed CoreWebView2 surface yet (same
// limitation that gates PreferredColorScheme + host-level Ctrl+F
// forwarding in v0.3 backlog). When that lands, this module is
// replaced by a host-bridged FindController call.
//
// Match counting:
// window.find() returns boolean (found / not found), not index/total.
// We DOM-walk on each query change (debounced 150ms) to compute total
// matches; "current index" is derived by counting how many matches
// occur before the current Selection's anchor node.
//
// Match styling:
// We rely on the browser's built-in selection highlight (the
// platform-default cyan/yellow). We do NOT wrap matches in <mark> —
// that would require DOM mutation that conflicts with the in-progress
// TOC feature's IntersectionObserver tracking and could disturb math
// rendering / mermaid SVG / hljs decorations.
//
// Per-document reset:
// renderer.ts's `resetModuleGlobalsForLoadDocument` invokes `close()`
// on the active controller so the bar disappears + state clears when
// the user switches docs.

const FIND_BAR_CLASS = "mm-find-bar";
const FIND_INPUT_CLASS = "mm-find-input";
const FIND_COUNT_CLASS = "mm-find-count";
const FIND_BTN_CLASS = "mm-find-btn";
const FIND_DEBOUNCE_MS = 150;

// Tags whose text is decoration, not searchable content. We skip them
// in the DOM walker so that hidden chrome (minimap clone, drop-overlay
// label, code-block line numbers etc) is not counted.
const SKIP_TAGS = new Set([
  "SCRIPT",
  "STYLE",
  "NOSCRIPT",
  "ASIDE", // minimap aside
]);

const SKIP_CLASSES = new Set<string>([
  "mm-minimap",
  "mm-minimap-viewport",
  "mm-width-handle",
  "mm-drop-overlay",
  FIND_BAR_CLASS,
]);

export type FindBarController = {
  /** Open the find bar; focuses the input. If already open, refocuses. */
  open: () => void;
  /** Close the find bar, clear selection, reset state. */
  close: () => void;
  /** Toggle: open if closed, close if open. */
  toggle: () => void;
  /** True when the bar is visible. */
  readonly isOpen: boolean;
};

type State = {
  bar: HTMLDivElement;
  input: HTMLInputElement;
  count: HTMLSpanElement;
  prevBtn: HTMLButtonElement;
  nextBtn: HTMLButtonElement;
  closeBtn: HTMLButtonElement;
  debounceTimer: number | null;
  /** Last query for which we have a match-count. */
  lastSearched: string;
  /** Total matches for `lastSearched`. */
  totalMatches: number;
  /** Whether the user has navigated at least once for the current query. */
  hasNavigated: boolean;
};

/**
 * Count how many case-insensitive matches of `needle` exist in the
 * given root's descendant text nodes. Skips decorative subtrees.
 * O(n) over text length; debounced by caller.
 */
export function countMatchesInRoot(root: Node, needle: string): number {
  if (needle.length === 0) {
    return 0;
  }
  const lowered = needle.toLowerCase();
  let total = 0;

  // TreeWalker filter rejects entire subtrees by returning FILTER_REJECT
  // on the element node itself.
  const walker = document.createTreeWalker(
    root,
    NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT,
    {
      acceptNode(node: Node): number {
        if (node.nodeType === Node.ELEMENT_NODE) {
          const el = node as Element;
          if (SKIP_TAGS.has(el.tagName)) {
            return NodeFilter.FILTER_REJECT;
          }
          // classList check — element may have multiple classes
          for (const cls of SKIP_CLASSES) {
            if (el.classList.contains(cls)) {
              return NodeFilter.FILTER_REJECT;
            }
          }
          return NodeFilter.FILTER_SKIP; // descend, don't count element itself
        }
        // Text node
        return NodeFilter.FILTER_ACCEPT;
      },
    }
  );

  let current = walker.nextNode();
  while (current !== null) {
    const text = (current.nodeValue ?? "").toLowerCase();
    if (text.length >= lowered.length) {
      let idx = text.indexOf(lowered);
      while (idx !== -1) {
        total++;
        idx = text.indexOf(lowered, idx + lowered.length);
      }
    }
    current = walker.nextNode();
  }

  return total;
}

/**
 * Compute the 1-based index of the current selection within all matches
 * of `needle` in `root`. Returns 0 if no selection / not found.
 */
export function currentMatchIndex(root: Node, needle: string): number {
  if (needle.length === 0) {
    return 0;
  }
  const sel = window.getSelection();
  if (sel === null || sel.rangeCount === 0) {
    return 0;
  }
  const range = sel.getRangeAt(0);
  if (range.collapsed) {
    return 0;
  }

  const lowered = needle.toLowerCase();
  let count = 0;
  let found = false;

  const walker = document.createTreeWalker(
    root,
    NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT,
    {
      acceptNode(node: Node): number {
        if (node.nodeType === Node.ELEMENT_NODE) {
          const el = node as Element;
          if (SKIP_TAGS.has(el.tagName)) {
            return NodeFilter.FILTER_REJECT;
          }
          for (const cls of SKIP_CLASSES) {
            if (el.classList.contains(cls)) {
              return NodeFilter.FILTER_REJECT;
            }
          }
          return NodeFilter.FILTER_SKIP;
        }
        return NodeFilter.FILTER_ACCEPT;
      },
    }
  );

  let current = walker.nextNode();
  while (current !== null && !found) {
    const text = (current.nodeValue ?? "").toLowerCase();
    if (text.length >= lowered.length) {
      let idx = text.indexOf(lowered);
      while (idx !== -1) {
        count++;
        // Did the selection start at (this text node, this idx)?
        if (
          current === range.startContainer &&
          idx === range.startOffset
        ) {
          found = true;
          break;
        }
        idx = text.indexOf(lowered, idx + lowered.length);
      }
    }
    current = walker.nextNode();
  }

  return found ? count : 0;
}

/**
 * Create a find-bar controller. The bar lives as a fixed-position
 * sibling of <main> under <body> (matches minimap / width-handle /
 * drop-overlay pattern; survives the load-document innerHTML swap on
 * <main> alone, though we still call `close()` on doc-swap to reset
 * state).
 */
export function createFindBar(): FindBarController {
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
    // Unicode up-arrow; matches the down-arrow on next.
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
    closeBtn.textContent = "×"; // multiplication sign — fits monoline UI

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
      totalMatches: 0,
      hasNavigated: false,
    };
  }

  function updateCountDisplay(s: State): void {
    const query = s.input.value;
    if (query.length === 0) {
      s.count.textContent = "";
      s.bar.classList.remove("mm-find-no-match");
      return;
    }
    if (s.totalMatches === 0) {
      s.count.textContent = "0 of 0";
      s.bar.classList.add("mm-find-no-match");
      return;
    }
    s.bar.classList.remove("mm-find-no-match");
    if (s.hasNavigated) {
      const idx = currentMatchIndex(document.body, s.lastSearched);
      if (idx > 0) {
        s.count.textContent = `${idx} of ${s.totalMatches}`;
        return;
      }
    }
    // Query has matches but user hasn't navigated yet (or the live
    // selection no longer aligns with a match — e.g. user clicked
    // elsewhere). Show total only.
    s.count.textContent = `${s.totalMatches} match${s.totalMatches === 1 ? "" : "es"}`;
  }

  function runSearch(s: State, query: string): void {
    s.lastSearched = query;
    if (query.length === 0) {
      s.totalMatches = 0;
      s.hasNavigated = false;
      window.getSelection()?.removeAllRanges();
      updateCountDisplay(s);
      return;
    }
    s.totalMatches = countMatchesInRoot(document.body, query);
    s.hasNavigated = false;
    // Try to find first match if any exist. window.find moves the
    // selection forward from the document's current caret; we reset
    // by collapsing to body start before searching.
    if (s.totalMatches > 0) {
      // Collapse selection to start of <main> so window.find() begins
      // from the top on each fresh query. Skipping this means
      // consecutive queries inherit the previous match's position.
      const main = document.querySelector("main.mm-document") ?? document.body;
      const range = document.createRange();
      range.setStart(main, 0);
      range.collapse(true);
      const sel = window.getSelection();
      if (sel !== null) {
        sel.removeAllRanges();
        sel.addRange(range);
      }
      navigate(s, "next", /* initialPlacement */ true);
    } else {
      window.getSelection()?.removeAllRanges();
    }
    updateCountDisplay(s);
  }

  function navigate(
    s: State,
    direction: "next" | "prev",
    initialPlacement: boolean = false
  ): void {
    if (s.lastSearched.length === 0 || s.totalMatches === 0) {
      return;
    }
    // window.find(query, caseSensitive, backwards, wrapAround,
    //             wholeWord, searchInFrames, showDialog)
    // Non-standard Chromium API; WebView2 is Chromium so this is
    // supported. Returns true if a match was found.
    type FindFn = (
      query: string,
      caseSensitive: boolean,
      backwards: boolean,
      wrapAround: boolean,
      wholeWord: boolean,
      searchInFrames: boolean,
      showDialog: boolean
    ) => boolean;
    const find = (window as Window & { find?: FindFn }).find;
    if (typeof find !== "function") {
      // Defensive: non-Chromium engine. Should never hit in
      // WebView2, but keeps the code robust if dev tools test
      // outside Chromium.
      return;
    }
    const backwards = direction === "prev";
    find(s.lastSearched, false, backwards, true, false, false, false);
    s.hasNavigated = true;
    // initialPlacement: this navigate call is part of runSearch
    // bootstrapping; the count display will be updated by runSearch's
    // own updateCountDisplay call. Skip the redundant update here to
    // avoid a flash of "N matches" → "1 of N".
    if (!initialPlacement) {
      updateCountDisplay(s);
    }
  }

  function attachListeners(s: State): void {
    s.input.addEventListener("input", () => {
      const query = s.input.value;
      if (s.debounceTimer !== null) {
        window.clearTimeout(s.debounceTimer);
      }
      s.debounceTimer = window.setTimeout(() => {
        s.debounceTimer = null;
        runSearch(s, query);
      }, FIND_DEBOUNCE_MS);
    });

    s.input.addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        event.preventDefault();
        // If a debounced search is still pending, flush it first.
        if (s.debounceTimer !== null) {
          window.clearTimeout(s.debounceTimer);
          s.debounceTimer = null;
          runSearch(s, s.input.value);
          return;
        }
        navigate(s, event.shiftKey ? "prev" : "next");
      } else if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
        close();
      }
    });

    s.prevBtn.addEventListener("click", () => {
      navigate(s, "prev");
      s.input.focus();
    });
    s.nextBtn.addEventListener("click", () => {
      navigate(s, "next");
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
    // Pre-select existing value so user can type-to-replace.
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
    state.bar.classList.remove("mm-find-bar-open");
    state.input.value = "";
    state.lastSearched = "";
    state.totalMatches = 0;
    state.hasNavigated = false;
    state.count.textContent = "";
    state.bar.classList.remove("mm-find-no-match");
    window.getSelection()?.removeAllRanges();
    // Keep node in DOM for fast reopen; only re-build on first open.
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
