import { beforeEach, describe, expect, it } from "vitest";
import * as rendererModule from "../src/renderer";

type HostLoad = (message: unknown) => void;
type RendererTestApi = typeof rendererModule & {
  __testPrepareEditableTableCellsForTesting?: (root?: ParentNode) => void;
  __testWireTableCellEditingForTesting?: () => void;
};

const rendererForTesting = rendererModule as RendererTestApi;
const documentB = [
  '<input class="mm-task-checkbox" data-task-line="10" data-task-key="b-task" type="checkbox">',
  '<table><tbody><tr><td class="mm-editable-cell" data-mm-cell-line="7" data-mm-cell-index="2" data-mm-cell-key="b-cell">B cell</td></tr></tbody></table>',
].join("");

describe("host patch render generation", () => {
  let posts: unknown[];

  beforeEach(() => {
    posts = [];
    document.documentElement.innerHTML = '<body><main class="mm-document"></main></body>';
    (window as typeof window & { chrome: { webview: { postMessage: (message: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => posts.push(message) },
    };
    loadHostMessage({
      type: "load-document",
      html: documentB,
      renderId: 2,
      skipFrameWait: true,
      hasMermaid: false,
      hasHljs: false,
      cacheKey: null,
    });
    rendererForTesting.__testWireTableCellEditingForTesting?.();
    rendererForTesting.__testPrepareEditableTableCellsForTesting?.();
  });

  it("rejects stale checkbox and table success patches while applying current patches", () => {
    const checkbox = requiredCheckbox();
    const cell = requiredCell();

    loadHostMessage({ type: "set-task-checkbox", line: 10, checked: true, renderId: 1 });
    loadHostMessage({
      type: "table-cell-updated",
      line: 7,
      cellIndex: 2,
      ok: true,
      text: "Stale A cell",
      key: "stale-a-key",
      renderId: 1,
    });

    expect(checkbox.checked).toBe(false);
    expect(cell.textContent).toBe("B cell");
    expect(cell.dataset.mmCellKey).toBe("b-cell");

    loadHostMessage({ type: "set-task-checkbox", line: 10, checked: true, renderId: 2 });
    loadHostMessage({
      type: "table-cell-updated",
      line: 7,
      cellIndex: 2,
      ok: true,
      text: "Current B cell",
      key: "current-b-key",
      renderId: 2,
    });

    expect(checkbox.checked).toBe(true);
    expect(cell.textContent).toBe("Current B cell");
    expect(cell.dataset.mmCellKey).toBe("current-b-key");
  });

  it("rejects a stale validation refusal while applying the current refusal", () => {
    const cell = requiredCell();
    cell.dispatchEvent(new FocusEvent("focusin", { bubbles: true }));
    cell.textContent = "Draft B cell";

    loadHostMessage({ type: "table-cell-updated", line: 7, cellIndex: 2, ok: false, renderId: 1 });
    expect(cell.textContent).toBe("Draft B cell");

    loadHostMessage({ type: "table-cell-updated", line: 7, cellIndex: 2, ok: false, renderId: 2 });
    expect(cell.textContent).toBe("B cell");
  });

  it("rejects a stale busy refusal while applying the current busy retry latch", () => {
    const cell = requiredCell();
    cell.dispatchEvent(new FocusEvent("focusin", { bubbles: true }));
    cell.textContent = "Draft B cell";
    cell.dispatchEvent(new FocusEvent("blur"));
    expect(tableEditPostCount()).toBe(1);

    loadHostMessage({
      type: "table-cell-updated",
      line: 7,
      cellIndex: 2,
      ok: false,
      reason: "busy",
      renderId: 1,
    });
    cell.dispatchEvent(new FocusEvent("blur"));
    expect(tableEditPostCount()).toBe(1);

    loadHostMessage({
      type: "table-cell-updated",
      line: 7,
      cellIndex: 2,
      ok: false,
      reason: "busy",
      renderId: 2,
    });
    cell.dispatchEvent(new FocusEvent("blur"));
    expect(tableEditPostCount()).toBe(2);
  });

  function tableEditPostCount(): number {
    return posts.filter(
      (message) => (message as { type?: string }).type === "table-cell-edit",
    ).length;
  }
});

function loadHostMessage(message: unknown): void {
  const load = (window as typeof window & { __mmRendererLoad?: HostLoad }).__mmRendererLoad;
  expect(typeof load).toBe("function");
  load?.(message);
}

function requiredCheckbox(): HTMLInputElement {
  const checkbox = document.querySelector<HTMLInputElement>('input.mm-task-checkbox[data-task-line="10"]');
  expect(checkbox).not.toBeNull();
  return checkbox!;
}

function requiredCell(): HTMLTableCellElement {
  const cell = document.querySelector<HTMLTableCellElement>(
    'td.mm-editable-cell[data-mm-cell-line="7"][data-mm-cell-index="2"]',
  );
  expect(cell).not.toBeNull();
  return cell!;
}
