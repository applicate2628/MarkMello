import { describe, it, expect, vi } from "vitest";
import {
  MathRenderQueue,
  isTerminalMathState,
  type MathRenderTask,
} from "../src/mathRenderQueue";

function makeNode(tex: string): HTMLElement {
  const el = document.createElement("span");
  el.dataset["tex"] = tex;
  return el;
}

function makeQueue(opts?: { timeBudgetMs?: number }) {
  const katex = { render: vi.fn() };
  let nowVal = 0;
  const advance = (ms: number) => {
    nowVal += ms;
  };
  const yieldCalls: number[] = [];
  const queue = new MathRenderQueue({
    katex,
    timeBudgetMs: opts?.timeBudgetMs ?? 7,
    now: () => nowVal,
    yield: () => {
      yieldCalls.push(nowVal);
      return Promise.resolve();
    },
  });
  return { queue, katex, advance, yieldCalls };
}

function task(node: HTMLElement, tex: string): MathRenderTask {
  return { node, tex, displayMode: false };
}

describe("MathRenderQueue", () => {
  it("processes high-priority before low-priority", async () => {
    const { queue, katex } = makeQueue();
    const a = makeNode("a");
    const b = makeNode("b");
    const c = makeNode("c");
    queue.enqueue(task(a, "a"), "low");
    queue.enqueue(task(b, "b"), "high");
    queue.enqueue(task(c, "c"), "low");
    await queue.start();
    expect(katex.render.mock.calls.map((c) => c[0]!)).toEqual(["b", "a", "c"]);
  });

  it("dedupes by node identity; promotion upgrades low to high", async () => {
    const { queue, katex } = makeQueue();
    const a = makeNode("a");
    const b = makeNode("b");
    queue.enqueue(task(a, "a"), "low");
    queue.enqueue(task(b, "b"), "low");
    queue.enqueue(task(a, "a"), "high");
    await queue.start();
    expect(katex.render.mock.calls.map((c) => c[0]!)).toEqual(["a", "b"]);
  });

  it("does NOT demote high to low", async () => {
    const { queue, katex } = makeQueue();
    const a = makeNode("a");
    const b = makeNode("b");
    queue.enqueue(task(a, "a"), "high");
    queue.enqueue(task(b, "b"), "low");
    queue.enqueue(task(a, "a"), "low");
    await queue.start();
    expect(katex.render.mock.calls.map((c) => c[0]!)).toEqual(["a", "b"]);
  });

  it("skips nodes with terminal state on enqueue", async () => {
    const { queue, katex } = makeQueue();
    const a = makeNode("a");
    a.dataset["mmMathRendered"] = "true";
    queue.enqueue(task(a, "a"), "high");
    await queue.start();
    expect(katex.render).not.toHaveBeenCalled();
  });

  it("sets dataset.mmMathRendered=true after successful render", async () => {
    const { queue } = makeQueue();
    const a = makeNode("a");
    queue.enqueue(task(a, "a"), "high");
    await queue.start();
    expect(a.dataset["mmMathRendered"]).toBe("true");
  });

  it("sets dataset.mmMathRendered=failed on render error; fires onTaskComplete in finally", async () => {
    const { queue, katex } = makeQueue();
    const a = makeNode("a");
    katex.render.mockImplementationOnce(() => {
      throw new Error("boom");
    });
    let completed: HTMLElement | null = null;
    queue.onTaskComplete((n) => {
      completed = n;
    });
    queue.enqueue(task(a, "a"), "high");
    await queue.start();
    expect(a.dataset["mmMathRendered"]).toBe("failed");
    expect(completed).toBe(a);
  });

  it("isTerminalMathState helper", () => {
    expect(isTerminalMathState("true")).toBe(true);
    expect(isTerminalMathState("failed")).toBe(true);
    expect(isTerminalMathState(undefined)).toBe(false);
    expect(isTerminalMathState("pending")).toBe(false);
  });

  it("time budget triggers yield between tasks", async () => {
    const { queue, advance, yieldCalls, katex } = makeQueue({
      timeBudgetMs: 5,
    });
    katex.render.mockImplementation(() => advance(3));
    for (let i = 0; i < 5; i++) {
      queue.enqueue(task(makeNode(`t${i}`), `t${i}`), "high");
    }
    await queue.start();
    expect(yieldCalls.length).toBeGreaterThan(0);
  });

  it("empty queue + start resolves immediately", async () => {
    const { queue } = makeQueue();
    await expect(queue.start()).resolves.toBeUndefined();
  });

  it("enqueue after start auto-kicks idle processor", async () => {
    const { queue, katex } = makeQueue();
    await queue.start();
    const a = makeNode("a");
    queue.enqueue(task(a, "a"), "high");
    await new Promise((r) => setTimeout(r, 0));
    expect(katex.render).toHaveBeenCalledTimes(1);
  });

  it("cancel halts queue; allMathRendered resolves", async () => {
    const { queue, katex, advance } = makeQueue({ timeBudgetMs: 1 });
    katex.render.mockImplementation(() => advance(2));
    for (let i = 0; i < 100; i++) {
      queue.enqueue(task(makeNode(`t${i}`), `t${i}`), "high");
    }
    const startPromise = queue.start();
    queue.cancel();
    await expect(startPromise).resolves.toBeUndefined();
    expect(katex.render.mock.calls.length).toBeLessThan(100);
  });
});
