import { afterEach, describe, expect, it } from "vitest";
import contractJson from "../contract/ipc-contract.json";
import {
  collectWireShapeViolations,
  HOST_MESSAGE_SHAPES,
  RENDERER_MESSAGE_SHAPES,
  type IpcShapeDescriptor,
} from "../src/ipcContract";
import {
  __testEmitHeadingsUpdatedForTesting,
  __testEmitLayoutReadyForTesting,
  __testEmitPerfMarkForTesting,
  __testEmitScrollForTesting,
  __testPrepareEditableTableCellsForTesting,
  __testWireTableCellEditingForTesting,
} from "../src/renderer";

type CapturedMessage = { type?: unknown } & Record<string, unknown>;

// Route postHostMessage through the invokeCSharpAction fallback (happy-dom has
// no chrome.webview), capturing the exact serialized wire object.
function capture(run: () => void): CapturedMessage[] {
  const captured: CapturedMessage[] = [];
  const rendererWindow = window as typeof window & {
    invokeCSharpAction?: (message: string) => void;
    chrome?: unknown;
  };
  const previous = rendererWindow.invokeCSharpAction;
  const previousChrome = rendererWindow.chrome;
  rendererWindow.chrome = undefined;
  rendererWindow.invokeCSharpAction = (message: string) => {
    captured.push(JSON.parse(message) as CapturedMessage);
  };
  try {
    run();
  } finally {
    rendererWindow.invokeCSharpAction = previous;
    rendererWindow.chrome = previousChrome;
  }
  return captured;
}

const rendererShapes = RENDERER_MESSAGE_SHAPES as Record<string, IpcShapeDescriptor>;

afterEach(() => {
  document.body.replaceChildren();
});

describe("ipc-contract.json single-source", () => {
  it("matches the in-code descriptor registries (regenerate with npm run gen:ipc-contract)", () => {
    const json = contractJson as {
      hostMessageShapes: Record<string, unknown>;
      rendererMessageShapes: Record<string, unknown>;
    };

    expect(json.hostMessageShapes).toEqual(HOST_MESSAGE_SHAPES);
    expect(json.rendererMessageShapes).toEqual(RENDERER_MESSAGE_SHAPES);
  });

  it("every message descriptor declares a `type` string field", () => {
    for (const [messageType, shape] of Object.entries(HOST_MESSAGE_SHAPES)) {
      expect((shape as IpcShapeDescriptor).type?.kind, `${messageType}`).toBe("string");
    }
    for (const [messageType, shape] of Object.entries(RENDERER_MESSAGE_SHAPES)) {
      expect((shape as IpcShapeDescriptor).type?.kind, `${messageType}`).toBe("string");
    }
  });
});

describe("recursive wire-shape validator", () => {
  it("accepts a well-formed nested headings-updated payload", () => {
    const valid = {
      type: "headings-updated",
      headings: [
        { id: "h1", level: 1, text: "Intro", segments: [{ kind: "text", text: "Intro" }] },
        { id: "h2", level: 2, text: "x", segments: [{ kind: "math", text: "x^2" }] },
      ],
    };
    expect(collectWireShapeViolations(valid, rendererShapes["headings-updated"]!)).toEqual([]);
  });

  it("flags a stray key nested inside a heading segment (recursion, not just top-level)", () => {
    const drifted = {
      type: "headings-updated",
      headings: [{ id: "h1", level: 1, text: "Intro", segments: [{ kind: "text", text: "Intro", stray: 1 }] }],
    };
    const violations = collectWireShapeViolations(drifted, rendererShapes["headings-updated"]!);
    expect(violations.some((v) => v.includes("stray") && v.includes("undeclared"))).toBe(true);
  });

  it("flags a wrong nested kind and an out-of-variant literal", () => {
    const drifted = {
      type: "headings-updated",
      headings: [{ id: "h1", level: "two", text: "Intro", segments: [{ kind: "emoji", text: "x" }] }],
    };
    const violations = collectWireShapeViolations(drifted, rendererShapes["headings-updated"]!);
    expect(violations.some((v) => v.includes("expected number"))).toBe(true);
    expect(violations.some((v) => v.includes("not in variants"))).toBe(true);
  });
});

describe("renderer->host producer capture", () => {
  function assertNoViolations(messages: CapturedMessage[]): void {
    expect(messages.length).toBeGreaterThan(0);
    for (const message of messages) {
      const messageType = message.type;
      expect(typeof messageType, "captured message must have a string type").toBe("string");
      const shape = rendererShapes[messageType as string];
      expect(shape, `unregistered renderer->host type: ${String(messageType)}`).toBeTruthy();
      expect(
        collectWireShapeViolations(message, shape!),
        `${String(messageType)} wire-shape violations`,
      ).toEqual([]);
    }
  }

  it("scroll producer emits only declared fields (no topBlockOffsetPx spread leak)", () => {
    const messages = capture(() => __testEmitScrollForTesting());
    const scroll = messages.find((m) => m.type === "scroll");
    expect(scroll, "postScroll should emit a scroll message").toBeTruthy();
    expect(Object.keys(scroll as CapturedMessage).sort()).toEqual(
      ["clientHeight", "scrollHeight", "scrollTop", "topBlockIndex", "type"],
    );
    assertNoViolations(messages);
  });

  it("layout-ready producer emits only declared fields", () => {
    assertNoViolations(capture(() => __testEmitLayoutReadyForTesting(7)));
  });

  it("headings-updated producer emits only declared fields", () => {
    assertNoViolations(capture(() => __testEmitHeadingsUpdatedForTesting()));
  });

  it("headings-updated producer emits a valid NONEMPTY nested payload", () => {
    // terra revision 3: exercise real headings[].segments[] so nested-value drift
    // is caught empirically, not only via the top-level empty-array path.
    document.body.innerHTML =
      '<main class="mm-document"><h1 id="h1">Intro</h1><h2 id="h2">Sub</h2></main>';
    const messages = capture(() => __testEmitHeadingsUpdatedForTesting());
    const headings = messages.find((m) => m.type === "headings-updated");
    expect(headings, "should emit a headings-updated message").toBeTruthy();
    const list = (headings as CapturedMessage).headings as unknown[];
    expect(list.length, "headings should be nonempty").toBeGreaterThan(0);
    const first = list[0] as { segments?: unknown[] };
    expect(first.segments?.length ?? 0, "heading should carry nested segments").toBeGreaterThan(0);
    assertNoViolations(messages);
  });

  it("perf-mark producer emits only declared fields", () => {
    assertNoViolations(capture(() => __testEmitPerfMarkForTesting("mm-test", { a: 1 })));
  });

  it("table-cell-edit producer emits exactly the registered fields", () => {
    document.body.innerHTML = `
      <main class="mm-document"><table><tbody><tr>
        <td class="mm-editable-cell" data-mm-cell-line="12"
            data-mm-cell-index="3" data-mm-cell-key="abc123">Changed</td>
      </tr></tbody></table></main>`;
    __testWireTableCellEditingForTesting();
    __testPrepareEditableTableCellsForTesting();
    const cell = document.querySelector<HTMLTableCellElement>("td.mm-editable-cell")!;

    const messages = capture(() => {
      cell.dispatchEvent(new FocusEvent("focusin", { bubbles: true }));
      // Modify the text so the (post-fix) no-op-blur suppression does not stop
      // the producer from emitting — an unmodified blur now posts nothing.
      cell.textContent = "Edited";
      cell.dispatchEvent(new FocusEvent("blur"));
    });
    const edit = messages.find((message) => message.type === "table-cell-edit");

    expect(Object.keys(edit as CapturedMessage).sort()).toEqual(
      ["cellIndex", "key", "line", "renderId", "text", "type"],
    );
    assertNoViolations(messages);
  });

  it("task-toggle producer stamps the current render generation", () => {
    const messages = capture(() => {
      document.dispatchEvent(new Event("DOMContentLoaded"));
      const load = (window as typeof window & {
        __mmRendererLoad?: (message: unknown) => void;
      }).__mmRendererLoad;
      expect(typeof load).toBe("function");
      load?.({ type: "load-document", html: "", renderId: 73 });

      document.body.innerHTML =
        '<main class="mm-document"><input class="mm-task-checkbox" data-task-line="12" data-task-key="abc123" type="checkbox"></main>';
      const checkbox = document.querySelector<HTMLInputElement>(".mm-task-checkbox")!;
      checkbox.checked = true;
      checkbox.dispatchEvent(new Event("change", { bubbles: true }));
    });

    const toggle = messages.find((message) => message.type === "task-toggle");
    expect(toggle).toEqual({
      type: "task-toggle",
      line: 12,
      checked: true,
      key: "abc123",
      renderId: 73,
    });
    assertNoViolations(messages);
  });
});
