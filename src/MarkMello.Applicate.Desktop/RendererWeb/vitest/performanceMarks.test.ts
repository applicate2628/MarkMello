import { describe, it, expect, beforeEach, vi } from "vitest";
import {
  markStart,
  markEnd,
  emitMark,
  getReport,
  recordScrollIpc,
  installLongTaskObserver,
  getFpsSampler,
  _resetForTests,
} from "../src/performanceMarks";

describe("performanceMarks", () => {
  beforeEach(() => {
    _resetForTests();
  });

  it("markStart + markEnd produces PerfMark with positive duration", async () => {
    markStart("test-op", { foo: 1 });
    await new Promise((r) => setTimeout(r, 5));
    const mark = markEnd("test-op", { result: "ok" });
    expect(mark).not.toBeNull();
    expect(mark!.name).toBe("test-op");
    expect(mark!.duration).toBeGreaterThan(0);
    expect(mark!.detail).toMatchObject({
      start: { foo: 1 },
      end: { result: "ok" },
    });
  });

  it("getReport contains collected marks", () => {
    markStart("a");
    markEnd("a");
    markStart("b");
    markEnd("b");
    const report = getReport();
    expect(report.marks.map((m) => m.name)).toEqual(["a", "b"]);
  });

  it("emitMark creates single-point mark (duration=0)", () => {
    emitMark("snapshot", { tag: 1 });
    const report = getReport();
    expect(report.marks).toHaveLength(1);
    expect(report.marks[0]!.name).toBe("snapshot");
    expect(report.marks[0]!.duration).toBe(0);
    expect(report.marks[0]!.detail).toEqual({ tag: 1 });
  });

  it("recordScrollIpc increments scrollIpcCount AND emits mm-scroll-ipc mark", () => {
    recordScrollIpc();
    recordScrollIpc();
    recordScrollIpc();
    const report = getReport();
    expect(report.scrollIpcCount).toBe(3);
    expect(report.marks.filter((m) => m.name === "mm-scroll-ipc")).toHaveLength(
      3,
    );
  });

  it("markStart for same name twice keeps latest start (single-flight contract)", async () => {
    markStart("op", { attempt: 1 });
    await new Promise((r) => setTimeout(r, 2));
    markStart("op", { attempt: 2 });
    await new Promise((r) => setTimeout(r, 2));
    const mark = markEnd("op", { ok: true });
    expect(mark).not.toBeNull();
    expect(mark!.detail).toMatchObject({
      start: { attempt: 2 },
      end: { ok: true },
    });
  });

  it("methods are no-op when performance.now is undefined", async () => {
    vi.resetModules();
    // happy-dom always provides a `performance` object; stub `now` to undefined
    // so the module-load capability check (`typeof performance.now === "function"`)
    // evaluates to false in the freshly re-imported module instance.
    vi.stubGlobal("performance", { ...performance, now: undefined });
    try {
      const mod = await import("../src/performanceMarks");
      mod._resetForTests();
      mod.markStart("x");
      const result = mod.markEnd("x");
      expect(result).toBeNull();
      expect(mod.getReport().marks).toHaveLength(0);
      mod.emitMark("y");
      expect(mod.getReport().marks).toHaveLength(0);
    } finally {
      vi.unstubAllGlobals();
      vi.resetModules();
    }
  });

  it("installLongTaskObserver returns disposer; entries collected from observer callback", () => {
    let lastObserverCallback:
      | ((list: { getEntries: () => PerformanceEntry[] }) => void)
      | null = null;
    const observeArgs: PerformanceObserverInit[] = [];
    const disconnectMock = vi.fn();
    const FakeObserver = vi
      .fn()
      .mockImplementation(
        (cb: (list: { getEntries: () => PerformanceEntry[] }) => void) => {
          lastObserverCallback = cb;
          return {
            observe: (opts: PerformanceObserverInit) => observeArgs.push(opts),
            disconnect: disconnectMock,
          };
        },
      );
    (FakeObserver as unknown as { supportedEntryTypes: string[] }).supportedEntryTypes = [
      "longtask",
    ];
    vi.stubGlobal("PerformanceObserver", FakeObserver);

    const dispose = installLongTaskObserver();
    expect(observeArgs).toEqual([{ entryTypes: ["longtask"] }]);

    // Simulate longtask entry
    lastObserverCallback!({
      getEntries: () => [
        { name: "longTask1", duration: 60 } as unknown as PerformanceEntry,
      ],
    });
    const report = getReport();
    expect(report.longTasks.map((t) => t.name)).toEqual(["longTask1"]);

    dispose();
    expect(disconnectMock).toHaveBeenCalled();

    vi.unstubAllGlobals();
  });

  it("installLongTaskObserver: silent fallback emits mm-longtask-observer-unsupported on observe throw", () => {
    const FakeObserver = vi.fn().mockImplementation(() => ({
      observe: () => {
        throw new Error("longtask not supported");
      },
      disconnect: vi.fn(),
    }));
    vi.stubGlobal("PerformanceObserver", FakeObserver);

    const dispose = installLongTaskObserver();
    expect(typeof dispose).toBe("function");
    const report = getReport();
    expect(
      report.marks.some((m) => m.name === "mm-longtask-observer-unsupported"),
    ).toBe(true);

    vi.unstubAllGlobals();
  });

  it("FpsSampler samples rAF deltas and returns p50/p95/min stats", () => {
    let nextRafCallback: FrameRequestCallback | null = null;
    vi.stubGlobal("requestAnimationFrame", (cb: FrameRequestCallback) => {
      nextRafCallback = cb;
      return 1;
    });
    vi.stubGlobal("cancelAnimationFrame", () => {});

    const sampler = getFpsSampler();
    sampler.start("test");
    // Simulate 10 frames with constant 16.67ms delta (~60 fps)
    for (let i = 1; i <= 10; i++) {
      nextRafCallback?.(i * 16.67);
    }
    const session = sampler.stop();
    expect(session.sampleCount).toBeGreaterThanOrEqual(8);
    expect(session.p50).toBeCloseTo(60, 0);
    expect(session.minFps).toBeGreaterThan(50);

    // Stored on report by key
    expect(getReport().fpsSessions["test"]).toBeDefined();

    vi.unstubAllGlobals();
  });
});
