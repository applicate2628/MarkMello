import { describe, it, expect, vi } from "vitest";
import { runInitialRenderPipeline, type MathReadinessController } from "../src/initialRenderPipeline";

function readyController(): MathReadinessController {
  return {
    initialVisibleReady: Promise.resolve(),
    allMathRendered: Promise.resolve(),
    cancel: () => {},
    initialVisibleNodes: new Set<HTMLElement>(),
    totalMathCount: 0,
    isCancelled: () => false,
  };
}

describe("runInitialRenderPipeline", () => {
  it("calls dependencies in the documented order", async () => {
    const order: string[] = [];
    await runInitialRenderPipeline({
      getCurrentTheme: () => "dark",
      applyTheme: (t) => { order.push(`apply:${t}`); },
      initMermaidWithTheme: (t) => { order.push(`init:${t}`); },
      renderMath: () => { order.push("math"); return readyController(); },
      renderMermaid: async () => { order.push("mermaid"); },
      renderCodeBlocks: () => { order.push("code"); },
      scheduleLayoutReady: () => { order.push("schedule"); }
    });
    expect(order).toEqual(["apply:dark", "init:dark", "math", "mermaid", "code", "schedule"]);
  });

  it("propagates current theme from getCurrentTheme", async () => {
    const applied: string[] = [];
    await runInitialRenderPipeline({
      getCurrentTheme: () => "light",
      applyTheme: (t) => { applied.push(t); },
      initMermaidWithTheme: () => {},
      renderMath: () => readyController(),
      renderMermaid: async () => {},
      renderCodeBlocks: () => {},
      scheduleLayoutReady: () => {}
    });
    expect(applied).toEqual(["light"]);
  });

  it("still calls scheduleLayoutReady if renderMermaid rejects", async () => {
    let scheduled = false;
    await runInitialRenderPipeline({
      getCurrentTheme: () => "light",
      applyTheme: () => {},
      initMermaidWithTheme: () => {},
      renderMath: () => readyController(),
      renderMermaid: async () => { throw new Error("boom"); },
      renderCodeBlocks: () => {},
      scheduleLayoutReady: () => { scheduled = true; }
    });
    expect(scheduled).toBe(true);
  });

  it("does not call scheduleLayoutReady before renderMermaid resolves", async () => {
    const events: string[] = [];
    let resolveMermaid!: () => void;
    const mermaidPromise = new Promise<void>(resolve => { resolveMermaid = resolve; });
    const pipelinePromise = runInitialRenderPipeline({
      getCurrentTheme: () => "light",
      applyTheme: () => {},
      initMermaidWithTheme: () => {},
      renderMath: () => { events.push("math"); return readyController(); },
      renderMermaid: async () => { events.push("mermaid:start"); await mermaidPromise; events.push("mermaid:end"); },
      renderCodeBlocks: () => { events.push("code"); },
      scheduleLayoutReady: () => { events.push("schedule"); }
    });
    await Promise.resolve();
    await Promise.resolve();
    expect(events).toEqual(["math", "mermaid:start"]);
    resolveMermaid();
    await pipelinePromise;
    expect(events).toEqual(["math", "mermaid:start", "mermaid:end", "code", "schedule"]);
  });
});

describe("runInitialRenderPipeline (MathReadinessController contract)", () => {
  it("scheduleLayoutReady awaits initialVisibleReady", async () => {
    let resolveInitial: () => void = () => {};
    const initialPromise = new Promise<void>(r => { resolveInitial = r; });
    const controller: MathReadinessController = {
      initialVisibleReady: initialPromise,
      allMathRendered: Promise.resolve(),
      cancel: () => {},
      initialVisibleNodes: new Set<HTMLElement>(),
      totalMathCount: 0,
      isCancelled: () => false,
    };
    const scheduleLayoutReady = vi.fn();
    const pipePromise = runInitialRenderPipeline({
      getCurrentTheme: () => "light",
      applyTheme: vi.fn(),
      initMermaidWithTheme: vi.fn(),
      renderMath: () => controller,
      renderMermaid: () => Promise.resolve(),
      renderCodeBlocks: vi.fn(),
      scheduleLayoutReady,
    });
    await new Promise(r => setTimeout(r, 10));
    expect(scheduleLayoutReady).not.toHaveBeenCalled();
    resolveInitial();
    await pipePromise;
    expect(scheduleLayoutReady).toHaveBeenCalled();
  });

  it("scheduleLayoutReady still called even if initialVisibleReady resolves immediately (failure-tolerant)", async () => {
    const controller: MathReadinessController = {
      initialVisibleReady: Promise.resolve(),
      allMathRendered: Promise.resolve(),
      cancel: () => {},
      initialVisibleNodes: new Set<HTMLElement>(),
      totalMathCount: 0,
      isCancelled: () => false,
    };
    const scheduleLayoutReady = vi.fn();
    await runInitialRenderPipeline({
      getCurrentTheme: () => "light",
      applyTheme: vi.fn(),
      initMermaidWithTheme: vi.fn(),
      renderMath: () => controller,
      renderMermaid: () => Promise.resolve(),
      renderCodeBlocks: vi.fn(),
      scheduleLayoutReady,
    });
    expect(scheduleLayoutReady).toHaveBeenCalled();
  });
});
