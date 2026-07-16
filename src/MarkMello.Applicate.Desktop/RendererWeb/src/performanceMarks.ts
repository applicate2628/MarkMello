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

/**
 * Retention caps for the diagnostic histories below. These are RING buffers, not budgets: this
 * telemetry is not release-gated, shell mode keeps one renderer page alive across every document
 * swap, and the WebView hosts are singletons — so an unbounded array here grows for the whole
 * process lifetime (a single scroll publishes a retained mark). The only reset is test-only.
 * Newest entries are what diagnostics actually read, so the oldest are dropped first. Lifetime
 * COUNTERS (scrollIpcCount, mathRenderCount) are never capped — they stay exact.
 */
const MAX_RETAINED_MARKS = 500;
const MAX_RETAINED_LONG_TASKS = 200;
const MAX_RETAINED_QUEUE_SLICES = 200;

function pushBounded<T>(buffer: T[], entry: T, cap: number): void {
  buffer.push(entry);
  if (buffer.length > cap) {
    buffer.splice(0, buffer.length - cap);
  }
}

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
  pushBounded(state.marks, mark, MAX_RETAINED_MARKS);
  return mark;
}

export function emitMark(name: string, detail?: unknown): void {
  if (!hasPerformanceApi) return;
  const mark: PerfMark =
    detail !== undefined
      ? { name, startTime: performance.now(), duration: 0, detail }
      : { name, startTime: performance.now(), duration: 0 };
  pushBounded(state.marks, mark, MAX_RETAINED_MARKS);
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
  pushBounded(state.queueSlices, { name, durationMs, tasksCompleted }, MAX_RETAINED_QUEUE_SLICES);
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

export function installLongTaskObserver(): () => void {
  if (typeof PerformanceObserver === "undefined") return () => {};
  try {
    const observer = new PerformanceObserver((list) => {
      for (const entry of list.getEntries()) {
        pushBounded(state.longTasks, entry, MAX_RETAINED_LONG_TASKS);
      }
    });
    observer.observe({ entryTypes: ["longtask"] });
    return () => observer.disconnect();
  } catch {
    emitMark("mm-longtask-observer-unsupported");
    return () => {};
  }
}

export interface FpsSampler {
  start(key: string): void;
  stop(): FpsSession;
}

type SamplerState = {
  key: string;
  deltas: number[];
  lastTime: number;
  rafId: number;
  running: boolean;
};

let currentSampler: SamplerState | null = null;

export function getFpsSampler(): FpsSampler {
  return {
    start(key: string) {
      if (currentSampler?.running) currentSampler.running = false;
      currentSampler = {
        key,
        deltas: [],
        lastTime: 0,
        rafId: 0,
        running: true,
      };
      const tick = (t: number) => {
        if (!currentSampler || !currentSampler.running) return;
        if (currentSampler.lastTime > 0) {
          currentSampler.deltas.push(t - currentSampler.lastTime);
        }
        currentSampler.lastTime = t;
        currentSampler.rafId = requestAnimationFrame(tick);
      };
      currentSampler.rafId = requestAnimationFrame(tick);
    },
    stop() {
      if (!currentSampler) {
        return { minFps: 0, p50: 0, p95: 0, sampleCount: 0 };
      }
      currentSampler.running = false;
      cancelAnimationFrame(currentSampler.rafId);
      const fps = currentSampler.deltas
        .map((d) => (d > 0 ? 1000 / d : 0))
        .sort((a, b) => a - b);
      const session: FpsSession = {
        minFps: fps[0] ?? 0,
        p50: fps[Math.floor(fps.length * 0.5)] ?? 0,
        p95: fps[Math.floor(fps.length * 0.95)] ?? 0,
        sampleCount: fps.length,
      };
      state.fpsSessions[currentSampler.key] = session;
      currentSampler = null;
      return session;
    },
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
  currentSampler = null;
}
