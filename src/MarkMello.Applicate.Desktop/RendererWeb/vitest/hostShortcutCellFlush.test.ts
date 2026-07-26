import { beforeAll, beforeEach, describe, expect, it } from "vitest";
import * as rendererModule from "../src/renderer";

// A forwarded host shortcut must carry the focused cell's pending text WITH it.
//
// `wireHostShortcuts` preventDefault's a host combo and forwards it, which does
// NOT move focus — so the cell stays document.activeElement holding uncommitted
// content. A cell's text reaches the host ONLY via `table-cell-edit`. For
// `ctrl+s` that is data loss: SaveCommand persists EditorSession.SourceText
// (MainWindowViewModel.cs SaveEditorAsync), and the typed text never entered it.
//
// These tests pin the ORDER, not merely the presence: the commit must be on the
// wire before the shortcut, because the host handles `table-cell-edit`
// synchronously inside HandleWebMessageBody while `host-shortcut` routes through
// Dispatcher.UIThread.Post. FIFO delivery plus that asymmetry puts the buffer
// write ahead of the save, with nothing tied to elapsed time.

type HostMessage = { type?: string; combo?: string; text?: string; raw?: boolean };

type RendererTestApi = typeof rendererModule & {
  __testPrepareEditableTableCellsForTesting?: (root?: ParentNode) => void;
};

const rendererForTesting = rendererModule as RendererTestApi;

// Every combo in `hostShortcuts` that is NOT exempt inside an editable cell.
// Parameterised so the fix stays general and is never narrowed to ctrl+s.
const LEAVING_COMBOS: ReadonlyArray<{ combo: string; key: string; shift?: boolean }> = [
  { combo: "ctrl+s", key: "s" },
  { combo: "ctrl+shift+s", key: "s", shift: true },
  { combo: "ctrl+e", key: "e" },
  { combo: "ctrl+o", key: "o" },
  { combo: "ctrl+n", key: "n" },
  { combo: "ctrl+r", key: "r" },
  { combo: "ctrl+t", key: "t" },
  { combo: "ctrl+1", key: "1" },
  { combo: "ctrl+9", key: "9" },
  { combo: "f5", key: "F5" },
];

let messages: HostMessage[] = [];

describe("host shortcut flushes a pending editable-cell edit", () => {
  beforeAll(() => {
    document.documentElement.innerHTML = `<body><main class="mm-document"></main></body>`;
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message as HostMessage) },
    };
    // Bootstrap wires BOTH wireTableCellEditing() and wireHostShortcuts().
    document.dispatchEvent(new Event("DOMContentLoaded"));
  });

  beforeEach(() => {
    document.querySelector("main")?.replaceChildren();
    // Release the ctrl+e held latch left over from a previous case.
    window.dispatchEvent(new Event("blur"));
    messages = [];
  });

  it("posts the pending cell text BEFORE the ctrl+s it forwards", () => {
    const cell = createEditableCell({ text: "Original", line: 12, cellIndex: 3, key: "deadbeef" });
    cell.focus();
    cell.textContent = "Typed but never committed";

    pressCombo(cell, { combo: "ctrl+s", key: "s" });

    // The defect at HEAD: only the shortcut goes out, so the host saves a
    // buffer that never received "Typed but never committed".
    expect(messages.map((message) => message.type)).toEqual([
      "table-cell-edit",
      "host-shortcut",
    ]);
    expect(messages[0]).toMatchObject({
      type: "table-cell-edit",
      line: 12,
      cellIndex: 3,
      text: "Typed but never committed",
      key: "deadbeef",
      raw: false,
    });
    expect(messages[1]).toEqual({ type: "host-shortcut", combo: "ctrl+s" });
  });

  it.each(LEAVING_COMBOS)(
    "posts the pending cell text before forwarding $combo",
    ({ combo, key, shift }) => {
      const cell = createEditableCell({ text: "Original" });
      cell.focus();
      cell.textContent = `Pending for ${combo}`;

      pressCombo(cell, { combo, key, ...(shift !== undefined ? { shift } : {}) });

      expect(messages.map((message) => message.type)).toEqual([
        "table-cell-edit",
        "host-shortcut",
      ]);
      expect(messages[0]).toMatchObject({ text: `Pending for ${combo}` });
      expect(messages[1]).toMatchObject({ combo });
    });

  it("flushes a rich cell's MARKDOWN, never its rendered glyphs", () => {
    const cell = createEditableCell({
      raw: "$x^2$",
      html: '<span class="math-inline" data-tex="x^2">rendered</span>',
    });
    // Focus swaps the rendered content out for the cell's source markdown.
    cell.focus();
    expect(cell.textContent).toBe("$x^2$");
    cell.textContent = "$y^2$";

    pressCombo(cell, { combo: "ctrl+s", key: "s" });

    expect(messages.map((message) => message.type)).toEqual([
      "table-cell-edit",
      "host-shortcut",
    ]);
    expect(messages[0]).toMatchObject({ text: "$y^2$", raw: true });
    expect(messages[0]?.text).not.toContain("rendered");
  });

  it("does not post twice when the flushed cell is blurred afterwards", () => {
    const cell = createEditableCell({ text: "Original" });
    cell.focus();
    cell.textContent = "Changed once";

    pressCombo(cell, { combo: "ctrl+s", key: "s" });
    cell.blur();

    const commits = messages.filter((message) => message.type === "table-cell-edit");
    expect(commits).toHaveLength(1);
    expect(commits[0]).toMatchObject({ text: "Changed once" });
  });

  it("posts only the shortcut when the focused cell is unmodified", () => {
    const cell = createEditableCell({ text: "Untouched" });
    cell.focus();

    pressCombo(cell, { combo: "ctrl+s", key: "s" });

    expect(messages).toEqual([{ type: "host-shortcut", combo: "ctrl+s" }]);
  });

  it("posts only the shortcut when focus is outside any editable cell", () => {
    createEditableCell({ text: "Original" });

    pressCombo(document.querySelector("main")!, { combo: "ctrl+s", key: "s" });

    expect(messages).toEqual([{ type: "host-shortcut", combo: "ctrl+s" }]);
  });

  // The exempt set is a CONTRACT, not a list: these combos mean something
  // INSIDE the cell (Escape reverts the edit; ctrl+z / ctrl+y drive the native
  // contenteditable undo stack with the caret still in place). They are never
  // forwarded, so they must never flush either — Escape especially, which must
  // keep reverting rather than committing.
  it.each([
    { name: "escape", key: "Escape", ctrl: false },
    { name: "ctrl+z", key: "z", ctrl: true },
    { name: "ctrl+y", key: "y", ctrl: true },
  ])("neither forwards nor flushes $name from inside a cell", ({ key, ctrl }) => {
    const cell = createEditableCell({ text: "Original" });
    cell.focus();
    cell.textContent = "Must not be committed";

    pressCombo(cell, { combo: ctrl ? "ctrl+x" : "x", key });

    expect(messages.filter((message) => message.type === "table-cell-edit")).toHaveLength(0);
    expect(messages.filter((message) => message.type === "host-shortcut")).toHaveLength(0);
  });

  it("flushes nothing while ctrl+e is held down", () => {
    const cell = createEditableCell({ text: "Original" });
    cell.focus();
    cell.textContent = "Pending";

    // First press latches the shortcut and legitimately flushes once.
    pressCombo(cell, { combo: "ctrl+e", key: "e" });
    expect(messages.filter((message) => message.type === "table-cell-edit")).toHaveLength(1);

    // Held repeats return before the forward, so they must flush nothing.
    cell.textContent = "Changed again while held";
    pressCombo(cell, { combo: "ctrl+e", key: "e", repeat: true });
    pressCombo(cell, { combo: "ctrl+e", key: "e", repeat: true });

    expect(messages.filter((message) => message.type === "table-cell-edit")).toHaveLength(1);
    expect(messages.filter((message) => message.type === "host-shortcut")).toHaveLength(1);
  });
});

function pressCombo(
  target: Element,
  options: { combo: string; key: string; shift?: boolean; repeat?: boolean },
): void {
  target.dispatchEvent(new KeyboardEvent("keydown", {
    key: options.key,
    ctrlKey: options.combo.startsWith("ctrl+"),
    shiftKey: options.shift === true,
    repeat: options.repeat === true,
    bubbles: true,
    cancelable: true,
  }));
}

// Builds a prepared editable cell in the live document. Mirrors the shape
// tableCellEdit.test.ts builds, including the contentEditable shim, so focus()
// genuinely moves the caret and the raw-mode source swap is exercised.
function createEditableCell(options: {
  line?: number;
  cellIndex?: number;
  key?: string;
  text?: string;
  raw?: string;
  html?: string;
} = {}): HTMLTableCellElement {
  const { line = 4, cellIndex = 1, key = "cell-key", text = "Original", raw, html } = options;
  const root = document.querySelector("main") ?? document.body;
  const table = document.createElement("table");
  const row = document.createElement("tr");
  const cell = document.createElement("td");
  cell.className = "mm-editable-cell";
  if (html === undefined) {
    cell.textContent = text;
  } else {
    // Test scaffolding only: a hardcoded literal standing in for host-rendered
    // markup. No external or user content reaches this assignment.
    cell.innerHTML = html;
  }
  if (raw !== undefined) cell.setAttribute("data-mm-cell-raw", raw);
  cell.setAttribute("data-mm-cell-line", String(line));
  cell.setAttribute("data-mm-cell-index", String(cellIndex));
  cell.setAttribute("data-mm-cell-key", key);
  row.append(cell);
  table.append(row);
  root.append(table);

  let assignedMode = "inherit";
  Object.defineProperty(cell, "contentEditable", {
    configurable: true,
    get: () => assignedMode,
    set: (value: string) => {
      assignedMode = value;
    },
  });

  // Discovery is MutationObserver-driven and async; drive the module-owned
  // preparation directly so the cell is editable synchronously.
  expect(typeof rendererForTesting.__testPrepareEditableTableCellsForTesting).toBe("function");
  rendererForTesting.__testPrepareEditableTableCellsForTesting?.();
  return cell;
}
