export type PerfMark = {
  name: string;
  startTime: number;
  duration: number;
  detail?: unknown;
};

export type FpsSession = {
  minFps: number;
  p50: number;
  p95: number;
  sampleCount: number;
};

export type QueueSlice = {
  name: string;
  durationMs: number;
  tasksCompleted: number;
};

export type PerfReport = {
  marks: PerfMark[];
  longTasks: PerformanceEntry[];
  scrollIpcCount: number;
  mathRenderCount: number;
  queueSlices: ReadonlyArray<QueueSlice>;
  fpsSessions: Record<string, FpsSession>;
};

type PendingStart = {
  startTime: number;
  startDetail?: unknown;
};

type State = {
  marks: PerfMark[];
  pendingStarts: Map<string, PendingStart>;
  longTasks: PerformanceEntry[];
  scrollIpcCount: number;
  mathRenderCount: number;
  queueSlices: QueueSlice[];
  fpsSessions: Record<string, FpsSession>;
};

const state: State = {
  marks: [],
  pendingStarts: new Map(),
  longTasks: [],
  scrollIpcCount: 0,
  mathRenderCount: 0,
  queueSlices: [],
  fpsSessions: {},
};

const hasPerformanceApi =
  typeof performance !== "undefined" &&
  typeof performance.now === "function";

/**
 * Records a start timestamp under `name`. If `markStart(name)` is called twice
 * without an intervening `markEnd(name)`, the second call replaces the pending
 * entry (single-flight per name). Callers that need overlapping scopes should
 * use distinct names.
 */
export function markStart(name: string, detail?: unknown): void {
  if (!hasPerformanceApi) return;
  const entry: PendingStart =
    detail !== undefined
      ? { startTime: performance.now(), startDetail: detail }
      : { startTime: performance.now() };
  state.pendingStarts.set(name, entry);
}

export function markEnd(name: string, detail?: unknown): PerfMark | null {
  if (!hasPerformanceApi) return null;
  const start = state.pendingStarts.get(name);
  if (!start) return null;
  state.pendingStarts.delete(name);
  const endTime = performance.now();
  const hasDetail = start.startDetail !== undefined || detail !== undefined;
  const mark: PerfMark = hasDetail
    ? {
        name,
        startTime: start.startTime,
        duration: endTime - start.startTime,
        detail: { start: start.startDetail, end: detail },
      }
    : {
        name,
        startTime: start.startTime,
        duration: endTime - start.startTime,
      };
  state.marks.push(mark);
  return mark;
}

export function emitMark(name: string, detail?: unknown): void {
  if (!hasPerformanceApi) return;
  const mark: PerfMark =
    detail !== undefined
      ? { name, startTime: performance.now(), duration: 0, detail }
      : { name, startTime: performance.now(), duration: 0 };
  state.marks.push(mark);
}

export function recordScrollIpc(): void {
  state.scrollIpcCount++;
  emitMark("mm-scroll-ipc");
}

export function incrementMathRenderCount(): void {
  state.mathRenderCount++;
}

export function recordQueueSlice(
  name: string,
  durationMs: number,
  tasksCompleted: number,
): void {
  state.queueSlices.push({ name, durationMs, tasksCompleted });
}

export function getReport(): PerfReport {
  return {
    marks: [...state.marks],
    longTasks: [...state.longTasks],
    scrollIpcCount: state.scrollIpcCount,
    mathRenderCount: state.mathRenderCount,
    queueSlices: [...state.queueSlices],
    fpsSessions: { ...state.fpsSessions },
  };
}

// Stub for Task 3 (FpsSampler). Real body will replace; signature is stable
// so callers and tests against the public surface compile today.
export function installLongTaskObserver(): () => void {
  if (typeof PerformanceObserver === "undefined") return () => {};
  try {
    const observer = new PerformanceObserver((list) => {
      for (const entry of list.getEntries()) {
        state.longTasks.push(entry);
      }
    });
    observer.observe({ entryTypes: ["longtask"] });
    return () => observer.disconnect();
  } catch {
    return () => {};
  }
}

export interface FpsSampler {
  start(key: string): void;
  stop(): FpsSession;
}

export function getFpsSampler(): FpsSampler {
  return {
    start: () => {},
    stop: () => ({ minFps: 0, p50: 0, p95: 0, sampleCount: 0 }),
  };
}

/** Test-only. NOT exported via barrel; tests import directly. */
export function _resetForTests(): void {
  state.marks.length = 0;
  state.pendingStarts.clear();
  state.longTasks.length = 0;
  state.scrollIpcCount = 0;
  state.mathRenderCount = 0;
  state.queueSlices.length = 0;
  state.fpsSessions = {};
}
