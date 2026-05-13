import { describe, it, expect } from "vitest";
import { runInitialRenderPipeline } from "../src/initialRenderPipeline";

describe("runInitialRenderPipeline", () => {
  it("calls dependencies in the documented order", async () => {
    const order: string[] = [];
    await runInitialRenderPipeline({
      getCurrentTheme: () => "dark",
      applyTheme: (t) => { order.push(`apply:${t}`); },
      initMermaidWithTheme: (t) => { order.push(`init:${t}`); },
      renderMath: () => { order.push("math"); },
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
      renderMath: () => {},
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
      renderMath: () => {},
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
      renderMath: () => { events.push("math"); },
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
