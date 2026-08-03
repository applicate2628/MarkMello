import { readFileSync } from "node:fs";
import { afterEach, describe, expect, it, vi } from "vitest";
import * as rendererModule from "../src/renderer";

type TableCellTestApi = typeof rendererModule & {
  __testPrepareEditableTableCellsForTesting?: (root?: ParentNode) => void;
  __testWireTableCellEditingForTesting?: () => void;
};

type HostLoad = (message: unknown) => void;

const rendererForTesting = rendererModule as TableCellTestApi;

afterEach(() => {
  document.body.replaceChildren();
  window.getSelection()?.removeAllRanges();
  delete (window as typeof window & { chrome?: unknown }).chrome;
  vi.restoreAllMocks();
});

describe("editable table cells", () => {
  it("prepares a newly appended editable cell through module-owned discovery", async () => {
    expect(typeof rendererForTesting.__testWireTableCellEditingForTesting).toBe("function");
    rendererForTesting.__testWireTableCellEditingForTesting?.();

    const { cell, assignedModes } = createCell({ support: "plaintext-only" });
    await new Promise<void>((resolve) => window.setTimeout(resolve, 0));

    expect(assignedModes[0]).toBe("plaintext-only");
    expect(cell.contentEditable).toBe("plaintext-only");
  });

  it("feature-detects plaintext-only and leaves non-editable cells untouched", () => {
    const { cell, assignedModes } = createCell({ support: "plaintext-only" });
    const plainCell = document.createElement("td");
    cell.parentElement!.append(plainCell);

    prepareEditableCells();

    expect(assignedModes[0]).toBe("plaintext-only");
    expect(cell.contentEditable).toBe("plaintext-only");
    expect(cell.dataset.mmCellPlaintextFallback).toBeUndefined();
    expect(plainCell.getAttribute("contenteditable")).toBeNull();
  });

  it("falls back to contenteditable=true when plaintext-only is unsupported", () => {
    const { cell, assignedModes } = createCell({ support: "fallback" });

    prepareEditableCells();

    expect(assignedModes).toEqual(["plaintext-only", "true"]);
    expect(cell.contentEditable).toBe("true");
    expect(cell.dataset.mmCellPlaintextFallback).toBe("true");
  });

  // A cached document's nodes are MOVED back into the DOM by
  // preserveCurrentProcessedDocument, not cloned, so the discovery observer
  // re-finds the very same cells still carrying the mark from their previous
  // preparation. Assigning contentEditable there forces one style
  // recalculation per cell for no change in value.
  it("skips the contentEditable write on a cell that already carries the mark", () => {
    const { cell, assignedModes } = createCell({ support: "plaintext-only" });
    cell.setAttribute("contenteditable", "plaintext-only");

    prepareEditableCells();

    expect(assignedModes).toEqual([]);
    expect(cell.getAttribute("contenteditable")).toBe("plaintext-only");
    expect(cell.dataset.mmCellPlaintextFallback).toBeUndefined();
  });

  it("re-prepares a marked cell that still carries a stale fallback flag", () => {
    const { cell, assignedModes } = createCell({ support: "plaintext-only" });
    cell.setAttribute("contenteditable", "plaintext-only");
    cell.dataset.mmCellPlaintextFallback = "true";

    prepareEditableCells();

    expect(assignedModes).toEqual(["plaintext-only"]);
    expect(cell.dataset.mmCellPlaintextFallback).toBeUndefined();
  });

  it("re-prepares a cell left in the fallback state rather than skipping it", () => {
    const { cell, assignedModes } = createCell({ support: "fallback" });
    cell.setAttribute("contenteditable", "true");
    cell.dataset.mmCellPlaintextFallback = "true";

    prepareEditableCells();

    expect(assignedModes).toEqual(["plaintext-only", "true"]);
    expect(cell.contentEditable).toBe("true");
    expect(cell.dataset.mmCellPlaintextFallback).toBe("true");
  });

  it("sanitizes fallback beforeinput and paste to single-line literal text", () => {
    const { cell } = createCell({ support: "fallback", text: "" });
    prepareEditableCells();

    const format = new InputEvent("beforeinput", {
      bubbles: true,
      cancelable: true,
      inputType: "formatBold",
    });
    cell.dispatchEvent(format);
    expect(format.defaultPrevented).toBe(true);

    const paragraph = new InputEvent("beforeinput", {
      bubbles: true,
      cancelable: true,
      inputType: "insertParagraph",
    });
    cell.dispatchEvent(paragraph);
    expect(paragraph.defaultPrevented).toBe(true);

    const paste = new Event("paste", { bubbles: true, cancelable: true });
    Object.defineProperty(paste, "clipboardData", {
      value: { getData: (formatName: string) => formatName === "text/plain" ? "<b>literal</b>\r\nnext" : "" },
    });
    cell.dispatchEvent(paste);

    expect(paste.defaultPrevented).toBe(true);
    expect(cell.textContent).toBe("<b>literal</b> next");
    expect(cell.querySelector("b")).toBeNull();
  });

  it("posts the exact table-cell-edit payload on blur using fallback innerText", () => {
    const postMessage = captureHostPosts();
    const { cell } = createCell({ support: "fallback", line: 12, cellIndex: 3, key: "deadbeef" });
    prepareEditableCells();
    focusCell(cell);
    cell.innerHTML = "<b>Changed</b>";

    blurCell(cell);

    expect(postMessage).toHaveBeenCalledTimes(1);
    expect(postMessage).toHaveBeenCalledWith({
      type: "table-cell-edit",
      line: 12,
      cellIndex: 3,
      text: "Changed",
      key: "deadbeef",
      // Plain cell: no data-mm-cell-raw, so the host applies literal escaping.
      raw: false,
      // Currency stamp; null here because no document was loaded in the test.
      renderId: null,
    });
  });

  it("posts nothing when an unmodified cell is blurred", () => {
    const postMessage = captureHostPosts();
    const { cell } = createCell({ text: "Unchanged" });
    prepareEditableCells();
    focusCell(cell);
    blurCell(cell);

    expect(postMessage).not.toHaveBeenCalled();
  });

  it("posts once on Enter and does not duplicate the post on the following blur", () => {
    const postMessage = captureHostPosts();
    const { cell } = createCell({ text: "Original" });
    prepareEditableCells();
    focusCell(cell);
    cell.textContent = "Changed";

    const enter = new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true });
    cell.dispatchEvent(enter);
    blurCell(cell);

    expect(enter.defaultPrevented).toBe(true);
    expect(postMessage).toHaveBeenCalledTimes(1);
    expect(postMessage.mock.calls[0]?.[0]).toMatchObject({ type: "table-cell-edit", text: "Changed" });
  });

  it("does not submit a composing Enter", () => {
    const postMessage = captureHostPosts();
    const { cell } = createCell({ text: "Composing" });
    prepareEditableCells();
    focusCell(cell);

    const enter = new KeyboardEvent("keydown", {
      key: "Enter",
      bubbles: true,
      cancelable: true,
      isComposing: true,
    });
    cell.dispatchEvent(enter);

    expect(enter.defaultPrevented).toBe(false);
    expect(postMessage).not.toHaveBeenCalled();
  });

  it("restores the focus-time innerHTML on Escape and posts nothing", () => {
    const postMessage = captureHostPosts();
    const { cell } = createCell({ text: "A & B" });
    prepareEditableCells();
    const originalHtml = cell.innerHTML;
    focusCell(cell);
    cell.innerHTML = "Changed";

    const escape = new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true });
    cell.dispatchEvent(escape);
    blurCell(cell);

    expect(escape.defaultPrevented).toBe(true);
    expect(cell.innerHTML).toBe(originalHtml);
    expect(postMessage).not.toHaveBeenCalled();
  });

  it("hands a rich cell its RAW markdown to edit instead of the rendered content", () => {
    // The reported gap: a cell holding a formula was not editable at all. Its DOM
    // holds KaTeX markup, so editing THAT would commit rendered glyphs — the cell
    // must present its source instead.
    const { cell } = createCell({
      raw: "$x^2$",
      html: '<span class="math-inline" data-tex="x^2">rendered</span>',
    });
    prepareEditableCells();

    focusCell(cell);

    expect(cell.textContent).toBe("$x^2$");
    expect(cell.querySelector("[data-tex]")).toBeNull();
  });

  it("reverts a rich cell to its SOURCE on Escape and to RENDERED once the caret leaves", () => {
    const postMessage = captureHostPosts();
    const renderedHtml = '<span class="math-inline" data-tex="x^2">rendered</span>';
    const { cell } = createCell({ raw: "$x^2$", html: renderedHtml });
    prepareEditableCells();
    focusCell(cell);
    cell.textContent = "$y^2$";

    const escape = new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true });
    cell.dispatchEvent(escape);

    // The caret is still in the cell, so it must hold the reverted MARKDOWN.
    expect(escape.defaultPrevented).toBe(true);
    expect(cell.textContent).toBe("$x^2$");
    expect(cell.querySelector("[data-tex]")).toBeNull();

    blurCell(cell);

    expect(cell.innerHTML).toBe(renderedHtml);
    expect(postMessage).not.toHaveBeenCalled();
  });

  it("does not commit rendered glyphs when the user keeps typing after Escape", () => {
    // THE corruption case. Escape used to hand the rendered word back under a
    // live caret and latch the submit guard on it, so one further keystroke made
    // the cell read `bold!` — and the blur posted that with raw:true, splicing it
    // over `**bold**` and destroying the markup with a gesture meaning "cancel".
    const postMessage = captureHostPosts();
    const { cell } = createCell({
      line: 4,
      cellIndex: 0,
      key: "bold-key",
      raw: "**bold**",
      html: "<strong>bold</strong>",
    });
    prepareEditableCells();
    focusCell(cell);
    cell.textContent = "**bolder**";

    cell.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
    // The user types on instead of leaving: whatever is in the cell now is what
    // the blur will commit, so it MUST be markdown, never the rendered glyphs.
    cell.textContent = `${cell.textContent}!`;
    blurCell(cell);

    expect(postMessage).toHaveBeenCalledTimes(1);
    expect(postMessage).toHaveBeenCalledWith(
      expect.objectContaining({ type: "table-cell-edit", text: "**bold**!", raw: true }),
    );
    // Never the rendered text: that is the byte sequence that eats the `**`.
    expect(postMessage).not.toHaveBeenCalledWith(expect.objectContaining({ text: "bold!" }));
  });

  it("does not commit rendered glyphs when the user keeps typing after a validation refusal", () => {
    // Same defect, more reachable: a validation refusal is visually silent — the
    // cell just snaps back — so typing on is the natural response.
    const postMessage = captureHostPosts();
    const { cell } = createCell({
      line: 4,
      cellIndex: 0,
      key: "bold-key",
      raw: "**bold**",
      html: "<strong>bold</strong>",
    });
    prepareEditableCells();
    focusCell(cell);
    cell.textContent = "**bolder**";

    const enter = new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true });
    cell.dispatchEvent(enter);
    expect(postMessage).toHaveBeenCalledTimes(1);

    // Validation refusal: no reason, so the stash is restored under a live caret.
    loadHostMessage({ type: "table-cell-updated", line: 4, cellIndex: 0, ok: false });

    expect(cell.textContent).toBe("**bold**");
    cell.textContent = `${cell.textContent}!`;
    blurCell(cell);

    expect(postMessage).toHaveBeenCalledTimes(2);
    expect(postMessage).toHaveBeenLastCalledWith(
      expect.objectContaining({ type: "table-cell-edit", text: "**bold**!", raw: true }),
    );
    expect(postMessage).not.toHaveBeenCalledWith(expect.objectContaining({ text: "bold!" }));
  });

  it("posts the edited RAW markdown of a rich cell under the raw flag", () => {
    const postMessage = captureHostPosts();
    const { cell } = createCell({
      line: 9,
      cellIndex: 1,
      key: "rich-key",
      raw: "$x^2$",
      html: '<span class="math-inline" data-tex="x^2">rendered</span>',
    });
    prepareEditableCells();
    focusCell(cell);
    cell.textContent = "$x^3$";

    blurCell(cell);

    expect(postMessage).toHaveBeenCalledTimes(1);
    expect(postMessage).toHaveBeenCalledWith({
      type: "table-cell-edit",
      line: 9,
      cellIndex: 1,
      // The user's MARKDOWN, not the rendered glyphs.
      text: "$x^3$",
      key: "rich-key",
      raw: true,
      renderId: null,
    });
  });

  it("posts nothing when a rich cell is focused and blurred untouched", () => {
    // The raw swap itself must not read as an edit, or merely clicking a formula
    // cell would rewrite the document.
    const postMessage = captureHostPosts();
    const { cell } = createCell({
      raw: "$x^2$",
      html: '<span class="math-inline" data-tex="x^2">rendered</span>',
    });
    prepareEditableCells();

    focusCell(cell);
    blurCell(cell);

    expect(postMessage).not.toHaveBeenCalled();
  });

  it("settles a rich cell back to RENDERED html and re-stamps raw + key", () => {
    const { cell } = createCell({
      line: 9,
      cellIndex: 1,
      key: "old-key",
      raw: "$x^2$",
      html: '<span class="math-inline" data-tex="x^2">old</span>',
    });
    prepareEditableCells();
    focusCell(cell);
    cell.textContent = "$x^3$";
    blurCell(cell);

    loadHostMessage({
      type: "table-cell-updated",
      line: 9,
      cellIndex: 1,
      ok: true,
      text: "$x^3$",
      key: "new-key",
      html: '<span class="math-inline" data-tex="x^3">new</span>',
    });

    // Requirement: the cell ends up RE-RENDERED, never showing literal markdown.
    expect(cell.innerHTML).toBe('<span class="math-inline" data-tex="x^3">new</span>');
    expect(cell.getAttribute("data-mm-cell-raw")).toBe("$x^3$");
    expect(cell.dataset.mmCellKey).toBe("new-key");
  });

  it("hands the source back when a raw commit settles while the caret is still in the cell", () => {
    // Enter commits without blurring. If the settle left rendered content under a
    // live caret, the next blur would post those glyphs back as markdown.
    const { cell } = createCell({
      line: 9,
      cellIndex: 1,
      raw: "$x^2$",
      html: '<span class="math-inline" data-tex="x^2">old</span>',
    });
    prepareEditableCells();
    focusCell(cell);
    cell.textContent = "$x^3$";

    loadHostMessage({
      type: "table-cell-updated",
      line: 9,
      cellIndex: 1,
      ok: true,
      text: "$x^3$",
      key: "new-key",
      html: '<span class="math-inline" data-tex="x^3">new</span>',
    });

    expect(document.activeElement).toBe(cell);
    expect(cell.textContent).toBe("$x^3$");

    // Escape now reverts to the SETTLED state, not the pre-edit stash — and
    // because the caret is still here it must revert to that state's SOURCE.
    // Handing back the rendered glyphs would make the next keystroke commit
    // them as markdown.
    const escape = new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true });
    cell.dispatchEvent(escape);
    expect(cell.textContent).toBe("$x^3$");
    expect(cell.querySelector("[data-tex]")).toBeNull();

    // Once the caret leaves, the settled RENDERED content comes back.
    blurCell(cell);
    expect(cell.innerHTML).toBe('<span class="math-inline" data-tex="x^3">new</span>');
  });

  it("drops raw mode when a rich cell settles to PLAIN markdown", () => {
    // Editing `$x^2$` down to `hello` leaves a plain cell. A retained
    // data-mm-cell-raw would hold it on the raw lane for the whole session, so
    // a later `**bold**` would reach the file UNESCAPED — while a cold reload of
    // the same document refuses it. The mode must follow the committed content.
    const postMessage = captureHostPosts();
    const { cell } = createCell({
      line: 9,
      cellIndex: 1,
      key: "old-key",
      raw: "$x^2$",
      html: '<span class="math-inline" data-tex="x^2">old</span>',
    });
    prepareEditableCells();
    focusCell(cell);
    cell.textContent = "hello";
    blurCell(cell);

    loadHostMessage({
      type: "table-cell-updated",
      line: 9,
      cellIndex: 1,
      ok: true,
      text: "hello",
      key: "new-key",
      // The host's rendered fragment for plain markdown carries no element.
      html: "hello",
    });

    expect(cell.hasAttribute("data-mm-cell-raw")).toBe(false);

    postMessage.mockClear();
    focusCell(cell);
    cell.textContent = "**bold**";
    blurCell(cell);

    // The literal contract is back: the host escapes this, it does not splice it.
    expect(postMessage).toHaveBeenCalledWith(
      expect.objectContaining({ type: "table-cell-edit", text: "**bold**", raw: false }),
    );
  });

  it("keeps raw mode when a rich cell settles to markdown that is still rich", () => {
    const { cell } = createCell({
      line: 9,
      cellIndex: 1,
      raw: "$x^2$",
      html: '<span class="math-inline" data-tex="x^2">old</span>',
    });
    prepareEditableCells();

    loadHostMessage({
      type: "table-cell-updated",
      line: 9,
      cellIndex: 1,
      ok: true,
      text: "**b**",
      key: "new-key",
      html: "<strong>b</strong>",
    });

    expect(cell.getAttribute("data-mm-cell-raw")).toBe("**b**");
  });

  it("keeps a plain cell on the literal path with no raw attribute", () => {
    // No regression: a plain cell posts raw:false and settles as text.
    const postMessage = captureHostPosts();
    const { cell } = createCell({ line: 3, cellIndex: 0, key: "plain-key", text: "Before" });
    prepareEditableCells();
    focusCell(cell);
    cell.textContent = "After";
    blurCell(cell);

    expect(postMessage).toHaveBeenCalledWith(
      expect.objectContaining({ text: "After", raw: false }),
    );

    loadHostMessage({
      type: "table-cell-updated",
      line: 3,
      cellIndex: 0,
      ok: true,
      text: "After",
      key: "plain-key-2",
    });

    expect(cell.textContent).toBe("After");
    expect(cell.hasAttribute("data-mm-cell-raw")).toBe(false);
  });

  it("ignores editable cells with malformed or missing coordinates", () => {
    const postMessage = captureHostPosts();
    const missingLine = createCell({ line: null }).cell;
    const malformedLine = createCell({ line: "12x" }).cell;
    const missingIndex = createCell({ cellIndex: null }).cell;
    const negativeIndex = createCell({ cellIndex: -1 }).cell;
    prepareEditableCells();

    for (const cell of [missingLine, malformedLine, missingIndex, negativeIndex]) {
      focusCell(cell);
      blurCell(cell);
    }

    expect(postMessage).not.toHaveBeenCalled();
  });

  it("applies canonical success text and re-stamps the canonical key", () => {
    const { cell } = createCell({ line: 7, cellIndex: 2, text: "Before", key: "old-key" });
    prepareEditableCells();
    focusCell(cell);
    cell.textContent = "Draft";

    loadHostMessage({
      type: "table-cell-updated",
      line: 7,
      cellIndex: 2,
      ok: true,
      text: "Canonical & plain",
      key: "new-key",
    });

    expect(cell.textContent).toBe("Canonical & plain");
    expect(cell.innerHTML).toBe("Canonical &amp; plain");
    expect(cell.dataset.mmCellKey).toBe("new-key");
  });

  it("restores the focus-time stash on a failed acknowledgement", () => {
    const { cell } = createCell({ line: 7, cellIndex: 2, text: "Before & exact" });
    prepareEditableCells();
    const originalHtml = cell.innerHTML;
    focusCell(cell);
    cell.innerHTML = "Draft";

    loadHostMessage({ type: "table-cell-updated", line: 7, cellIndex: 2, ok: false });

    expect(cell.innerHTML).toBe(originalHtml);
  });

  it("keeps the typed text on a busy refusal so a re-blur can retry", () => {
    // Race: a second cell edit blurred during the first commit's IO window is
    // refused by the serializer with reason "busy". The typed text must survive
    // (NOT be restored to the pre-focus stash), or the second edit is lost.
    const { cell } = createCell({ line: 5, cellIndex: 1, text: "Original", key: "k" });
    prepareEditableCells();
    focusCell(cell);
    cell.textContent = "Second edit";
    blurCell(cell);

    loadHostMessage({ type: "table-cell-updated", line: 5, cellIndex: 1, ok: false, reason: "busy" });

    expect(cell.textContent).toBe("Second edit");
  });

  it("ignores malformed or incomplete acknowledgements without mutating the cell", () => {
    const { cell } = createCell({ line: 7, cellIndex: 2, text: "Stable", key: "stable-key" });
    prepareEditableCells();

    expect(() => {
      loadHostMessage({ type: "table-cell-updated", line: 7, cellIndex: 2, ok: true, text: "Missing key" });
      loadHostMessage({ type: "table-cell-updated", line: "7", cellIndex: 2, ok: false });
      loadHostMessage({ type: "table-cell-updated", line: 7, cellIndex: 2 });
    }).not.toThrow();

    expect(cell.textContent).toBe("Stable");
    expect(cell.dataset.mmCellKey).toBe("stable-key");
  });

  it("never produces from a minimap clone even if editable attributes survive", () => {
    const postMessage = captureHostPosts();
    const { cell } = createCell({ minimap: true, text: "Clone draft" });
    prepareEditableCells();
    focusCell(cell);
    blurCell(cell);
    cell.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));

    expect(postMessage).not.toHaveBeenCalled();
  });

  it("turns an input-event burst followed by blur into one host post", () => {
    const postMessage = captureHostPosts();
    const { cell } = createCell({ text: "A" });
    prepareEditableCells();
    focusCell(cell);

    for (const text of ["AB", "ABC", "ABCD"]) {
      cell.textContent = text;
      cell.dispatchEvent(new InputEvent("input", { bubbles: true, data: text.slice(-1) }));
    }
    blurCell(cell);

    expect(postMessage).toHaveBeenCalledTimes(1);
    expect(postMessage.mock.calls[0]?.[0]).toMatchObject({ type: "table-cell-edit", text: "ABCD" });
  });

  it("keeps the bounded table-cell module event-only", () => {
    const source = readFileSync("RendererWeb/src/renderer.ts", "utf8");
    const moduleSource = source.match(
      /\/\/ BEGIN TABLE CELL EDIT MODULE([\s\S]*?)\/\/ END TABLE CELL EDIT MODULE/,
    )?.[1];

    expect(moduleSource).toBeDefined();
    expect(moduleSource).not.toMatch(/\b(?:setTimeout|setInterval|debounce)\b/i);
    expect(moduleSource).not.toMatch(/addEventListener\(\s*["']input["']/);
  });

  it("styles hover and focus with the existing accent under the reduced-motion policy", () => {
    const css = readFileSync("RendererWeb/assets/renderer.css", "utf8");

    expect(css).toContain(".mm-document .mm-editable-cell:hover");
    expect(css).toContain(".mm-document .mm-editable-cell:focus-visible");
    expect(css).toMatch(/\.mm-editable-cell[\s\S]*var\(--mm-accent\)/);
    expect(css).toContain("@media (prefers-reduced-motion: reduce)");
  });
});

function prepareEditableCells(): void {
  expect(typeof rendererForTesting.__testPrepareEditableTableCellsForTesting).toBe("function");
  expect(typeof rendererForTesting.__testWireTableCellEditingForTesting).toBe("function");
  rendererForTesting.__testWireTableCellEditingForTesting?.();
  rendererForTesting.__testPrepareEditableTableCellsForTesting?.();
}

function captureHostPosts() {
  const postMessage = vi.fn();
  (window as typeof window & { chrome: { webview: { postMessage: typeof postMessage } } }).chrome = {
    webview: { postMessage },
  };
  return postMessage;
}

function loadHostMessage(message: unknown): void {
  const load = (window as typeof window & { __mmRendererLoad?: HostLoad }).__mmRendererLoad;
  expect(typeof load).toBe("function");
  load?.(message);
}

// REAL focus, not a synthetic focusin. The renderer keys the rendered/source
// duality off document.activeElement, so a harness that fires the event without
// moving the caret cannot observe the raw-mode contract at all — which is
// exactly how a cancelled edit could silently commit rendered glyphs while the
// suite stayed green. happy-dom's focus()/blur() dispatch focusin and the
// capturing blur themselves, so dispatching them again would double-fire the
// handlers and overwrite the focus-time stash with already-swapped content.
function focusCell(cell: HTMLTableCellElement): void {
  cell.focus();
}

function blurCell(cell: HTMLTableCellElement): void {
  cell.blur();
}

function createCell(options: {
  support?: "plaintext-only" | "fallback";
  line?: number | string | null;
  cellIndex?: number | string | null;
  key?: string | null;
  text?: string;
  minimap?: boolean;
  // A RICH cell: `raw` is its markdown (data-mm-cell-raw) and `html` the
  // RENDERED content the host emitted for it, which is NOT its source.
  raw?: string;
  html?: string;
} = {}) {
  const {
    support = "plaintext-only",
    line = 4,
    cellIndex = 1,
    key = "cell-key",
    text = "Original",
    minimap = false,
    raw,
    html,
  } = options;
  const root = document.createElement("main");
  root.className = minimap ? "mm-minimap-content" : "mm-document";
  const table = document.createElement("table");
  const row = document.createElement("tr");
  const cell = document.createElement("td");
  cell.className = "mm-editable-cell";
  if (html === undefined) {
    cell.textContent = text;
  } else {
    cell.innerHTML = html;
  }
  if (raw !== undefined) cell.setAttribute("data-mm-cell-raw", raw);
  if (line !== null) cell.setAttribute("data-mm-cell-line", String(line));
  if (cellIndex !== null) cell.setAttribute("data-mm-cell-index", String(cellIndex));
  if (key !== null) cell.setAttribute("data-mm-cell-key", key);
  row.append(cell);
  table.append(row);
  root.append(table);

  const assignedModes: string[] = [];
  let assignedMode = "inherit";
  Object.defineProperty(cell, "contentEditable", {
    configurable: true,
    get: () => assignedMode,
    set: (value: string) => {
      assignedModes.push(value);
      assignedMode = support === "fallback" && value === "plaintext-only" ? "inherit" : value;
    },
  });
  document.body.append(root);

  return { cell, root, assignedModes };
}
