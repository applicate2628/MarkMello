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
  it("schedules first readable layout before heavy rich enhancements", async () => {
    const order: string[] = [];
    let deferred: (() => void) | undefined;
    await runInitialRenderPipeline({
      getCurrentTheme: () => "dark",
      applyTheme: (t) => { order.push(`apply:${t}`); },
      initMermaidWithTheme: (t) => { order.push(`init:${t}`); },
      renderMath: () => { order.push("math"); return readyController(); },
      renderMermaid: async () => { order.push("mermaid"); },
      renderCodeBlocks: () => { order.push("code"); },
      scheduleLayoutReady: () => { order.push("schedule"); },
      deferPostReadyWork: (work) => { order.push("defer"); deferred = work; },
    });
    expect(order).toEqual(["apply:dark", "init:dark", "math", "schedule", "defer"]);

    deferred?.();
    await Promise.resolve();
    await Promise.resolve();
    expect(order).toEqual(["apply:dark", "init:dark", "math", "schedule", "defer", "mermaid", "code"]);
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
      scheduleLayoutReady: () => {},
      deferPostReadyWork: () => {},
    });
    expect(applied).toEqual(["light"]);
  });

  it("still schedules layout before a rejected deferred mermaid render", async () => {
    let scheduled = false;
    let highlighted = false;
    let deferred: (() => void) | undefined;
    await runInitialRenderPipeline({
      getCurrentTheme: () => "light",
      applyTheme: () => {},
      initMermaidWithTheme: () => {},
      renderMath: () => readyController(),
      renderMermaid: async () => { throw new Error("boom"); },
      renderCodeBlocks: () => { highlighted = true; },
      scheduleLayoutReady: () => { scheduled = true; },
      deferPostReadyWork: (work) => { deferred = work; },
    });
    expect(scheduled).toBe(true);
    expect(highlighted).toBe(false);

    deferred?.();
    await Promise.resolve();
    await Promise.resolve();
    expect(highlighted).toBe(true);
  });

  it("does not wait for deferred mermaid before scheduleLayoutReady", async () => {
    const events: string[] = [];
    let resolveMermaid!: () => void;
    let deferred: (() => void) | undefined;
    const mermaidPromise = new Promise<void>(resolve => { resolveMermaid = resolve; });
    await runInitialRenderPipeline({
      getCurrentTheme: () => "light",
      applyTheme: () => {},
      initMermaidWithTheme: () => {},
      renderMath: () => { events.push("math"); return readyController(); },
      renderMermaid: async () => { events.push("mermaid:start"); await mermaidPromise; events.push("mermaid:end"); },
      renderCodeBlocks: () => { events.push("code"); },
      scheduleLayoutReady: () => { events.push("schedule"); },
      deferPostReadyWork: (work) => { deferred = work; },
    });
    expect(events).toEqual(["math", "schedule"]);

    deferred?.();
    await Promise.resolve();
    expect(events).toEqual(["math", "schedule", "mermaid:start"]);
    resolveMermaid();
    await Promise.resolve();
    await Promise.resolve();
    expect(events).toEqual(["math", "schedule", "mermaid:start", "mermaid:end", "code"]);
  });

  it("notifies post-ready completion after rich enhancements without waiting for visual settle", async () => {
    const events: string[] = [];
    let resolveMermaid!: () => void;
    let resolveVisualSettle!: () => void;
    let deferred: (() => void) | undefined;
    const mermaidPromise = new Promise<void>(resolve => { resolveMermaid = resolve; });
    const visualSettlePromise = new Promise<void>(resolve => { resolveVisualSettle = resolve; });

    await runInitialRenderPipeline({
      getCurrentTheme: () => "dark",
      applyTheme: () => {},
      initMermaidWithTheme: () => {},
      renderMath: () => ({
        ...readyController(),
        initialVisualSettleReady: visualSettlePromise,
      }),
      renderMermaid: async () => { events.push("mermaid:start"); await mermaidPromise; events.push("mermaid:end"); },
      renderCodeBlocks: () => { events.push("code"); },
      scheduleLayoutReady: () => { events.push("layout"); },
      deferPostReadyWork: (work) => { deferred = work; },
      notifyPostReadyEnhancementsComplete: () => { events.push("complete"); },
    });

    expect(events).toEqual(["layout"]);
    deferred?.();
    await Promise.resolve();
    expect(events).toEqual(["layout", "mermaid:start"]);
    resolveMermaid();
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
    expect(events).toEqual(["layout", "mermaid:start", "mermaid:end", "code", "complete"]);
    resolveVisualSettle();
    await new Promise(r => setTimeout(r, 0));
    expect(events).toEqual(["layout", "mermaid:start", "mermaid:end", "code", "complete"]);
  });

  it("keeps visual settle timeout diagnostics after post-ready completion", async () => {
    vi.useFakeTimers();
    try {
      const events: string[] = [];
      const marks: string[] = [];
      let deferred: (() => void) | undefined;

      await runInitialRenderPipeline({
        getCurrentTheme: () => "dark",
        applyTheme: () => {},
        initMermaidWithTheme: () => {},
        renderMath: () => ({
          ...readyController(),
          totalMathCount: 12,
          initialVisualSettleReady: new Promise<void>(() => {}),
        }),
        renderMermaid: async () => { events.push("mermaid"); },
        renderCodeBlocks: () => { events.push("code"); },
        scheduleLayoutReady: () => { events.push("layout"); },
        deferPostReadyWork: (work) => { deferred = work; },
        notifyPostReadyEnhancementsComplete: () => { events.push("complete"); },
        postPerfMark: (name) => { marks.push(name); },
      });

      deferred?.();
      await Promise.resolve();
      await Promise.resolve();
      expect(events).toEqual(["layout", "mermaid", "code", "complete"]);
      expect(marks).not.toContain("initial-visual-settle-timeout");

      await vi.advanceTimersByTimeAsync(1800);
      await Promise.resolve();

      expect(events).toEqual(["layout", "mermaid", "code", "complete"]);
      expect(marks).toContain("initial-visual-settle-timeout");
    } finally {
      vi.useRealTimers();
    }
  });

  it("suppresses stale pipeline work after document reset", async () => {
    let current = true;
    let resolveInitial!: () => void;
    const initialVisibleReady = new Promise<void>(resolve => { resolveInitial = resolve; });
    const scheduleLayoutReady = vi.fn();
    const deferPostReadyWork = vi.fn();

    const pipeline = runInitialRenderPipeline({
      getCurrentTheme: () => "light",
      applyTheme: vi.fn(),
      initMermaidWithTheme: vi.fn(),
      renderMath: () => ({
        ...readyController(),
        initialVisibleReady,
      }),
      renderMermaid: vi.fn(),
      renderCodeBlocks: vi.fn(),
      scheduleLayoutReady,
      deferPostReadyWork,
      isCurrent: () => current,
    });

    await Promise.resolve();
    current = false;
    resolveInitial();
    await pipeline;

    expect(scheduleLayoutReady).not.toHaveBeenCalled();
    expect(deferPostReadyWork).not.toHaveBeenCalled();
  });

  it("suppresses stale deferred post-ready completion", async () => {
    let current = true;
    const events: string[] = [];
    let deferred: (() => void) | undefined;

    await runInitialRenderPipeline({
      getCurrentTheme: () => "light",
      applyTheme: vi.fn(),
      initMermaidWithTheme: vi.fn(),
      renderMath: () => readyController(),
      renderMermaid: async () => { events.push("mermaid"); },
      renderCodeBlocks: () => { events.push("code"); },
      scheduleLayoutReady: () => { events.push("layout"); },
      deferPostReadyWork: (work) => { deferred = work; },
      notifyPostReadyEnhancementsComplete: () => { events.push("complete"); },
      isCurrent: () => current,
    });

    current = false;
    deferred?.();
    await Promise.resolve();
    await Promise.resolve();

    expect(events).toEqual(["layout"]);
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
      deferPostReadyWork: () => {},
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
      deferPostReadyWork: () => {},
    });
    expect(scheduleLayoutReady).toHaveBeenCalled();
  });

  it("scheduleLayoutReady falls back when initialVisibleReady stalls", async () => {
    vi.useFakeTimers();
    try {
      const marks: string[] = [];
      const controller: MathReadinessController = {
        initialVisibleReady: new Promise<void>(() => {}),
        allMathRendered: Promise.resolve(),
        cancel: () => {},
        initialVisibleNodes: new Set<HTMLElement>([document.createElement("span")]),
        totalMathCount: 3,
        isCancelled: () => false,
      };
      const scheduleLayoutReady = vi.fn();
      const pipeline = runInitialRenderPipeline({
        getCurrentTheme: () => "dark",
        applyTheme: vi.fn(),
        initMermaidWithTheme: vi.fn(),
        renderMath: () => controller,
        renderMermaid: () => Promise.resolve(),
        renderCodeBlocks: vi.fn(),
        scheduleLayoutReady,
        deferPostReadyWork: () => {},
        initialVisibleReadyTimeoutMs: 25,
        postPerfMark: (name) => { marks.push(name); },
      });

      await Promise.resolve();
      expect(scheduleLayoutReady).not.toHaveBeenCalled();

      await vi.advanceTimersByTimeAsync(25);
      await pipeline;

      expect(scheduleLayoutReady).toHaveBeenCalledTimes(1);
      expect(marks).toContain("initial-visible-ready-timeout");
    } finally {
      vi.useRealTimers();
    }
  });
});
