import { describe, expect, it, vi } from "vitest";
import { DocumentWindowModel, type SectionModelEntry } from "../src/documentWindow";
import { createVirtualizedDocumentWindowController } from "../src/virtualizedDocumentWindow";
import { renderWindowTargetThenAct } from "../src/windowTargetResolver";

function entry(sectionIndex: number, blockIndex: number, estimatedHeight: number, html?: string): SectionModelEntry {
  return {
    blockIndex,
    cumulativeTop: 0,
    estimatedHeight,
    headingLevel: 0,
    html: html ?? `<section data-mm-block-index="${blockIndex}" data-mm-block-kind="paragraph">Block ${blockIndex}</section>`,
    kind: "paragraph",
    measuredHeight: undefined,
    sectionIndex,
  };
}

function setScrollRoot(scrollTop: number, scrollHeight: number, clientHeight: number): HTMLElement {
  let mutableScrollTop = scrollTop;
  const root = document.documentElement;
  Object.defineProperty(document, "scrollingElement", {
    configurable: true,
    get: () => root,
  });
  Object.defineProperty(root, "scrollTop", {
    configurable: true,
    get: () => mutableScrollTop,
    set: value => {
      mutableScrollTop = value;
    },
  });
  Object.defineProperty(root, "scrollHeight", {
    configurable: true,
    get: () => scrollHeight,
  });
  Object.defineProperty(root, "clientHeight", {
    configurable: true,
    get: () => clientHeight,
  });
  return root;
}

describe("window target resolver", () => {
  it("delegates to legacy live-DOM behavior and does not call the controller when the flag is off", async () => {
    const model = new DocumentWindowModel([entry(0, 10, 100)]);
    const controller = {
      ensureSectionRendered: vi.fn(),
      ensureSectionRangeRendered: vi.fn(),
      getCurrentRange: vi.fn(),
      isSectionRendered: vi.fn(),
    };
    const legacyAction = vi.fn(() => "legacy");
    const action = vi.fn(() => "virtualized");

    const result = await renderWindowTargetThenAct({
      action,
      actionKind: "navigate",
      controller,
      descriptor: { blockIndex: 10, kind: "block" },
      legacyAction,
      main: document.body,
      model,
      ownerWindow: window,
      root: document.documentElement,
      virtualizationEnabled: false,
    });

    expect(result).toBe("legacy");
    expect(legacyAction).toHaveBeenCalledTimes(1);
    expect(controller.ensureSectionRendered).not.toHaveBeenCalled();
    expect(action).not.toHaveBeenCalled();
  });

  it("resolves, renders, waits a layout tick, acts on the live target, and restores query anchors", async () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'></main></body>";
    const frames: FrameRequestCallback[] = [];
    let tickCount = 0;
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
      frames.push(time => {
        tickCount++;
        callback(time);
      });
      return frames.length;
    });
    const root = setScrollRoot(15, 300, 40);
    const main = document.querySelector<HTMLElement>("main.mm-document")!;
    const prepareSnapshots: number[][] = [];
    const model = new DocumentWindowModel([
      entry(0, 20, 100),
      entry(1, 21, 100),
      entry(2, 22, 100, '<section data-mm-block-index="22" data-mm-block-kind="quote"><blockquote data-mm-block-index="222">Nested target</blockquote></section>'),
    ]);
    const controller = createVirtualizedDocumentWindowController({
      main,
      model,
      ownerWindow: window,
      prepareInsertedContent: rootNode => {
        prepareSnapshots.push(Array.from(rootNode.querySelectorAll<HTMLElement>("[data-mm-block-index]")).map(node =>
          Number(node.dataset.mmBlockIndex)));
      },
      renderAhead: {
        aboveViewports: 0,
        belowViewports: 0,
        minAbovePx: 0,
        minBelowPx: 0,
      },
      root,
    });
    const operation = {
      documentEpoch: 1,
      operationEpoch: 1,
      isCurrent: () => true,
      requestScrollTop: (target: number) => {
        root.scrollTop = target;
      },
      scheduleFrameTransaction: (work: () => void) => {
        frames.push(time => {
          tickCount++;
          work();
        });
        return true;
      },
    };
    controller.updateWindowForScroll({ operation });
    const originalElement = main.querySelector<HTMLElement>('[data-mm-block-index="20"]')!;
    vi.spyOn(originalElement, "getBoundingClientRect").mockReturnValue({
      bottom: 85,
      height: 100,
      left: 0,
      right: 0,
      top: -15,
      width: 0,
      x: 0,
      y: -15,
      toJSON() { return this; },
    } as DOMRect);

    const action = vi.fn(({ targetElement }) => {
      expect(tickCount).toBe(1);
      expect(targetElement?.textContent).toBe("Nested target");
      return "done";
    });

    const pending = renderWindowTargetThenAct({
      action,
      actionKind: "query",
      controller,
      descriptor: { blockIndex: 222, kind: "block" },
      legacyAction: () => "legacy",
      main,
      model,
      operation,
      ownerWindow: window,
      root,
      virtualizationEnabled: true,
    });

    expect(action).not.toHaveBeenCalled();
    expect(controller.getCurrentRange()).toEqual({ start: 0, end: 0 });
    frames.shift()?.(0);
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
    expect(action).toHaveBeenCalledTimes(1);
    expect(controller.getCurrentRange()).toEqual({ start: 2, end: 2 });
    expect(frames).toHaveLength(1);
    frames.shift()?.(0);
    await expect(pending).resolves.toBe("done");

    expect(controller.getCurrentRange()).toEqual({ start: 0, end: 0 });
    expect(root.scrollTop).toBe(15);
    expect(prepareSnapshots).toEqual([
      [20],
      [22, 222],
      [20],
    ]);
  });

  it("drops a queued target action when its document epoch is invalidated before delivery", async () => {
    const model = new DocumentWindowModel([entry(0, 30, 100)]);
    const action = vi.fn(() => "stale");
    const legacyAction = vi.fn(() => "legacy");
    const frames: Array<() => void> = [];
    let current = true;
    const operation = {
      documentEpoch: 7,
      operationEpoch: 11,
      isCurrent: () => current,
      requestScrollTop: vi.fn(),
      scheduleFrameTransaction: (work: () => void) => {
        frames.push(work);
        return true;
      },
    };
    const controller = {
      ensureSectionRangeRendered: vi.fn(() => false),
      ensureSectionRendered: vi.fn(() => false),
      getCurrentRange: vi.fn(() => ({ start: 0, end: 0 })),
      isSectionRendered: vi.fn(() => true),
    };

    const pending = renderWindowTargetThenAct({
      action,
      actionKind: "navigate",
      controller,
      descriptor: { blockIndex: 30, kind: "block" },
      legacyAction,
      main: document.body,
      model,
      operation,
      ownerWindow: window,
      root: document.documentElement,
      virtualizationEnabled: true,
    });

    expect(action).not.toHaveBeenCalled();
    expect(frames).toHaveLength(1);
    current = false;
    frames.shift()?.();
    await expect(pending).resolves.toBeUndefined();
    expect(action).not.toHaveBeenCalled();
    expect(legacyAction).not.toHaveBeenCalled();
    expect(operation.requestScrollTop).not.toHaveBeenCalled();
  });

  it("does not restore a query anchor after the operation is superseded during its action", async () => {
    const model = new DocumentWindowModel([entry(0, 40, 100)]);
    const frames: Array<() => void> = [];
    let current = true;
    let resolveAction!: (value: string) => void;
    const actionResult = new Promise<string>(resolve => { resolveAction = resolve; });
    const action = vi.fn(() => actionResult);
    const operation = {
      documentEpoch: 9,
      operationEpoch: 12,
      isCurrent: () => current,
      requestScrollTop: vi.fn(),
      scheduleFrameTransaction: (work: () => void) => {
        frames.push(work);
        return true;
      },
    };
    const controller = {
      ensureSectionRangeRendered: vi.fn(() => true),
      ensureSectionRendered: vi.fn(() => true),
      getCurrentRange: vi.fn(() => ({ start: 0, end: 0 })),
      isSectionRendered: vi.fn(() => false),
    };

    const pending = renderWindowTargetThenAct({
      action,
      actionKind: "query",
      controller,
      descriptor: { blockIndex: 40, kind: "block" },
      legacyAction: () => "legacy",
      main: document.body,
      model,
      operation,
      ownerWindow: window,
      root: document.documentElement,
      virtualizationEnabled: true,
    });

    frames.shift()?.();
    await Promise.resolve();
    expect(action).toHaveBeenCalledTimes(1);
    current = false;
    resolveAction("done");
    await expect(pending).resolves.toBeUndefined();
    expect(frames).toEqual([]);
    expect(operation.requestScrollTop).toHaveBeenCalledTimes(1);
    expect(operation.requestScrollTop).toHaveBeenCalledWith(0, "query-anchor-preserve");
  });
});
