import { describe, it, expect, beforeEach } from "vitest";
import {
  markStart,
  markEnd,
  emitMark,
  getReport,
  recordScrollIpc,
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

  it("methods are no-op when performance.now is undefined", () => {
    expect(() => emitMark("x")).not.toThrow();
  });
});
