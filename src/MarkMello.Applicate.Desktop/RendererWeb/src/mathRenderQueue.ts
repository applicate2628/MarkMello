import { emitMark, recordQueueSlice } from "./performanceMarks";

export type MathPriority = "high" | "low";

export type MathRenderTask = {
  node: HTMLElement;
  tex: string;
  displayMode: boolean;
};

type KatexLike = {
  render(
    tex: string,
    node: HTMLElement,
    opts: {
      throwOnError?: boolean;
      displayMode?: boolean;
      strict?: "warn" | "error" | "ignore";
      trust?: boolean;
    },
  ): void;
};

export type MathRenderQueueDeps = {
  katex: KatexLike;
  timeBudgetMs?: number;
  now: () => number;
  yield: () => Promise<void>;
};

export function isTerminalMathState(
  state: string | undefined,
): state is "true" | "failed" {
  return state === "true" || state === "failed";
}

type Entry = { task: MathRenderTask; priority: MathPriority };

export class MathRenderQueue {
  private high: Entry[] = [];
  private low: Entry[] = [];
  private inQueue = new Map<HTMLElement, Entry>();
  private taskListeners = new Set<(node: HTMLElement) => void>();
  private cancelled = false;
  private processing = false;
  private idlePromise: Promise<void> | null = null;
  private idleResolver: (() => void) | null = null;
  private sliceCounter = 0;

  constructor(private deps: MathRenderQueueDeps) {}

  enqueue(task: MathRenderTask, priority: MathPriority): void {
    if (this.cancelled) return;
    if (isTerminalMathState(task.node.dataset["mmMathRendered"])) return;
    const existing = this.inQueue.get(task.node);
    if (existing) {
      if (priority === "high" && existing.priority === "low") {
        const idx = this.low.indexOf(existing);
        if (idx >= 0) this.low.splice(idx, 1);
        existing.priority = "high";
        this.high.push(existing);
      }
      return;
    }
    const entry: Entry = { task, priority };
    this.inQueue.set(task.node, entry);
    if (priority === "high") this.high.push(entry);
    else this.low.push(entry);
    this.kick();
  }

  start(): Promise<void> {
    if (!this.idlePromise) {
      this.idlePromise = new Promise((resolve) => {
        this.idleResolver = resolve;
      });
    }
    const promise = this.idlePromise;
    if (this.high.length + this.low.length === 0 && !this.processing) {
      this.resolveIdle();
      return promise;
    }
    this.kick();
    return promise;
  }

  private kick(): void {
    if (this.processing || this.cancelled) return;
    if (this.high.length + this.low.length === 0) return;
    this.processing = true;
    void this.processLoop();
  }

  private async processLoop(): Promise<void> {
    try {
      // Yield first so that any synchronously-batched enqueues in the same
      // microtask turn (e.g. enqueue(low), enqueue(high)) all land before we
      // begin draining. Otherwise the first enqueue would drain the queue
      // immediately and starve later same-tick enqueues of priority ordering.
      await this.deps.yield();
      while (!this.cancelled && this.high.length + this.low.length > 0) {
        const frameStart = this.deps.now();
        const budget = this.deps.timeBudgetMs ?? 7;
        let tasksCompleted = 0;
        while (!this.cancelled && this.high.length + this.low.length > 0) {
          const entry = (this.high.length > 0
            ? this.high.shift()
            : this.low.shift()) as Entry;
          this.inQueue.delete(entry.task.node);
          if (isTerminalMathState(entry.task.node.dataset["mmMathRendered"])) {
            continue;
          }
          try {
            this.deps.katex.render(entry.task.tex, entry.task.node, {
              throwOnError: false,
              displayMode: entry.task.displayMode,
              strict: "warn",
              trust: false,
            });
            entry.task.node.dataset["mmMathRendered"] = "true";
          } catch (e) {
            entry.task.node.dataset["mmMathRendered"] = "failed";
            emitMark("mm-render-math-fail", { tex: entry.task.tex, error: String(e) });
          } finally {
            tasksCompleted++;
            for (const listener of this.taskListeners) listener(entry.task.node);
          }
          if (this.deps.now() - frameStart > budget) break;
        }
        const sliceName = `mm-queue-slice-${this.sliceCounter++}`;
        const sliceDurationMs = this.deps.now() - frameStart;
        emitMark(sliceName, { tasksCompleted, durationMs: sliceDurationMs });
        recordQueueSlice(sliceName, sliceDurationMs, tasksCompleted);
        if (!this.cancelled && this.high.length + this.low.length > 0) {
          await this.deps.yield();
        }
      }
    } finally {
      this.processing = false;
      this.resolveIdle();
    }
  }

  private resolveIdle(): void {
    if (this.idleResolver) {
      const r = this.idleResolver;
      this.idleResolver = null;
      this.idlePromise = null;
      r();
    }
  }

  cancel(): void {
    this.cancelled = true;
    this.high.length = 0;
    this.low.length = 0;
    this.inQueue.clear();
    if (!this.processing) this.resolveIdle();
  }

  isProcessing(): boolean {
    return this.processing;
  }

  size(): { high: number; low: number } {
    return { high: this.high.length, low: this.low.length };
  }

  onTaskComplete(listener: (node: HTMLElement) => void): () => void {
    this.taskListeners.add(listener);
    return () => {
      this.taskListeners.delete(listener);
    };
  }
}
