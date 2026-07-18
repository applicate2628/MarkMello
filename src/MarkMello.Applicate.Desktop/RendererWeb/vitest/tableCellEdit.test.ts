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

function focusCell(cell: HTMLTableCellElement): void {
  cell.dispatchEvent(new FocusEvent("focusin", { bubbles: true }));
}

function blurCell(cell: HTMLTableCellElement): void {
  cell.dispatchEvent(new FocusEvent("blur"));
}

function createCell(options: {
  support?: "plaintext-only" | "fallback";
  line?: number | string | null;
  cellIndex?: number | string | null;
  key?: string | null;
  text?: string;
  minimap?: boolean;
} = {}) {
  const {
    support = "plaintext-only",
    line = 4,
    cellIndex = 1,
    key = "cell-key",
    text = "Original",
    minimap = false,
  } = options;
  const root = document.createElement("main");
  root.className = minimap ? "mm-minimap-content" : "mm-document";
  const table = document.createElement("table");
  const row = document.createElement("tr");
  const cell = document.createElement("td");
  cell.className = "mm-editable-cell";
  cell.textContent = text;
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
