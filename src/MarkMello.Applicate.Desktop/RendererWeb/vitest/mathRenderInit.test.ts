import { afterEach, describe, expect, it, vi } from "vitest";
import { renderMath } from "../src/mathRenderInit";

class TrackingIntersectionObserver {
  static instances: TrackingIntersectionObserver[] = [];

  readonly observed = new Set<Element>();
  disconnected = false;

  constructor(
    private readonly callback: IntersectionObserverCallback,
    _options?: IntersectionObserverInit,
  ) {
    TrackingIntersectionObserver.instances.push(this);
  }

  observe(element: Element) {
    this.disconnected = false;
    this.observed.add(element);
  }

  unobserve(element: Element) {
    this.observed.delete(element);
  }

  disconnect() {
    this.disconnected = true;
    this.observed.clear();
  }

  emit(entries: Array<Pick<IntersectionObserverEntry, "target" | "isIntersecting">>) {
    this.callback(entries as IntersectionObserverEntry[], this as unknown as IntersectionObserver);
  }
}

function installIntersectionObserverStub() {
  TrackingIntersectionObserver.instances = [];
  vi.stubGlobal("IntersectionObserver", TrackingIntersectionObserver);
}

function latestObserver(): TrackingIntersectionObserver {
  const observer = TrackingIntersectionObserver.instances.at(-1);
  if (!observer) throw new Error("Expected IntersectionObserver to be constructed");
  return observer;
}

function makeRect(top: number, height = 32): DOMRect {
  return {
    top,
    bottom: top + height,
    left: 0,
    right: 800,
    width: 800,
    height,
    x: 0,
    y: top,
    toJSON: () => ({}),
  } as DOMRect;
}

function buildMathDocument(
  count: number,
  blockIndexFor: (index: number) => number,
  terminalFor?: (index: number) => string | null,
): void {
  document.body.innerHTML = `
    <main class="mm-document">
      ${Array.from({ length: count }, (_, index) => {
        const terminal = terminalFor?.(index);
        return `
          <p data-mm-block-index="${blockIndexFor(index)}" data-index="${index}">
            <span class="math-inline" data-tex="x_${index}"${terminal ? ` data-mm-math-rendered="${terminal}"` : ""}></span>
          </p>`;
      }).join("")}
    </main>`;
}

function stubInitialVisibility(topFor: (index: number) => number): void {
  for (const paragraph of Array.from(document.querySelectorAll<HTMLElement>("p[data-index]"))) {
    const index = Number.parseInt(paragraph.dataset["index"] ?? "0", 10);
    vi.spyOn(paragraph, "getBoundingClientRect").mockReturnValue(makeRect(topFor(index)));
  }
}

function observedBlockIndexes(observer = latestObserver()): number[] {
  return Array.from(observer.observed)
    .map((element) => Number.parseInt((element as HTMLElement).dataset["mmBlockIndex"] ?? "-1", 10))
    .sort((left, right) => left - right);
}

function observedDataIndexes(observer = latestObserver()): number[] {
  return Array.from(observer.observed)
    .map((element) => Number.parseInt((element as HTMLElement).dataset["index"] ?? "-1", 10))
    .sort((left, right) => left - right);
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  vi.useRealTimers();
  document.body.innerHTML = "";
});

describe("renderMath", () => {
  it("keeps lazy math observation windowed and moves it from block indexes", () => {
    installIntersectionObserverStub();
    vi.stubGlobal("innerHeight", 800);
    buildMathDocument(700, index => index);
    stubInitialVisibility(index => index === 0 ? 100 : 5000 + index * 32);
    const marks: Array<Record<string, unknown>> = [];

    const controller = renderMath({
      katex: { render: vi.fn() },
      documentRoot: document,
      initialObservationTopBlockIndex: 0,
      initialObservationBottomBlockIndex: 20,
      emitMathObserverWindowMark: (detail) => marks.push(detail),
    });
    const observer = latestObserver();

    expect(observer.observed.size).toBeLessThanOrEqual(320);
    expect(observedBlockIndexes(observer)[0]).toBe(0);
    expect(controller.initialVisibleNodes.size).toBe(1);

    controller.updateMathObservationWindow?.(520, "scroll", 540);

    expect(observer.observed.size).toBeLessThanOrEqual(320);
    expect(observedBlockIndexes(observer)).toContain(520);
    expect(observedBlockIndexes(observer)).not.toContain(0);
    expect(controller.initialVisibleNodes.size).toBe(1);
    expect(marks.at(-1)).toMatchObject({
      observedCount: observer.observed.size,
      maxObservedCount: 320,
      topBlockIndex: 520,
      bottomBlockIndex: 540,
      reason: "scroll",
    });
  });

  it("does no observer telemetry work while disabled and supports dynamic toggling", () => {
    installIntersectionObserverStub();
    buildMathDocument(2, index => index);
    stubInitialVisibility(() => 5000);
    let telemetryEnabled = false;
    const emitMark = vi.fn();
    const performanceNow = vi.spyOn(performance, "now").mockReturnValue(12.5);

    const controller = renderMath({
      katex: { render: vi.fn() },
      documentRoot: document,
      initialObservationTopBlockIndex: 0,
      initialObservationBottomBlockIndex: 1,
      isMathObserverWindowTelemetryEnabled: () => telemetryEnabled,
      emitMathObserverWindowMark: emitMark,
    });
    const observer = latestObserver();

    expect(performanceNow).not.toHaveBeenCalled();
    expect(emitMark).not.toHaveBeenCalled();
    expect(controller.updateMathObservationWindow?.(0, "scroll", 1)).toBeNull();
    observer.emit([]);
    expect(performanceNow).not.toHaveBeenCalled();
    expect(emitMark).not.toHaveBeenCalled();

    telemetryEnabled = true;
    const detail = controller.updateMathObservationWindow?.(0, "scroll", 1);
    expect(detail).toMatchObject({
      observedCount: 2,
      maxObservedCount: 320,
      updateDurationMs: 0,
      callbackDurationMs: 0,
      reason: "scroll",
    });
    expect(performanceNow).toHaveBeenCalledTimes(2);
    expect(emitMark).toHaveBeenCalledTimes(1);

    observer.emit([]);
    expect(performanceNow).toHaveBeenCalledTimes(4);
    expect(emitMark).toHaveBeenLastCalledWith(expect.objectContaining({
      observedCount: 2,
      callbackDurationMs: 0,
      reason: "callback",
    }));

    telemetryEnabled = false;
    expect(controller.updateMathObservationWindow?.(0, "scroll", 1)).toBeNull();
    observer.emit([]);
    controller.cancel();
    expect(performanceNow).toHaveBeenCalledTimes(4);
    expect(emitMark).toHaveBeenCalledTimes(2);
  });

  it("does not observe sparse far math outside the padded visible block span", () => {
    installIntersectionObserverStub();
    buildMathDocument(4, index => [100, 108, 5000, 9000][index]!);
    stubInitialVisibility(() => 5000);

    const controller = renderMath({
      katex: { render: vi.fn() },
      documentRoot: document,
    });
    controller.updateMathObservationWindow?.(100, "scroll", 110);

    expect(observedBlockIndexes()).toEqual([100, 108]);
  });

  it("keeps initial observation inside the padded visible block span", () => {
    installIntersectionObserverStub();
    buildMathDocument(4, index => [100, 108, 5000, 9000][index]!);
    stubInitialVisibility(() => 5000);

    renderMath({
      katex: { render: vi.fn() },
      documentRoot: document,
      initialObservationTopBlockIndex: 100,
      initialObservationBottomBlockIndex: 110,
    });

    expect(observedBlockIndexes()).toEqual([100, 108]);
  });

  it("does not backfill or mark when unobserved far math completes", async () => {
    installIntersectionObserverStub();
    vi.spyOn(window, "requestAnimationFrame").mockImplementation((callback: FrameRequestCallback) => {
      callback(performance.now());
      return 1;
    });
    buildMathDocument(3, index => [100, 108, 5000][index]!, index => index < 2 ? "true" : null);
    stubInitialVisibility(() => 5000);
    const marks: Array<Record<string, unknown>> = [];

    const controller = renderMath({
      katex: { render: vi.fn() },
      documentRoot: document,
      initialObservationTopBlockIndex: 100,
      initialObservationBottomBlockIndex: 110,
      emitMathObserverWindowMark: (detail) => marks.push(detail),
    });

    expect(observedDataIndexes()).toEqual([]);
    await controller.allMathRendered;

    expect(marks.filter(mark => mark["reason"] === "complete-backfill")).toEqual([]);
  });

  it("does not append unindexed buckets to an indexed viewport window", () => {
    installIntersectionObserverStub();
    document.body.innerHTML = `
      <main class="mm-document">
        <p data-mm-block-index="100" data-index="0">
          <span class="math-inline" data-tex="x_0"></span>
        </p>
        <p data-index="1">
          <span class="math-inline" data-tex="x_1"></span>
        </p>
      </main>`;
    stubInitialVisibility(() => 5000);

    renderMath({
      katex: { render: vi.fn() },
      documentRoot: document,
      initialObservationTopBlockIndex: 100,
      initialObservationBottomBlockIndex: 110,
    });

    expect(observedDataIndexes()).toEqual([0]);
  });

  it("hard-caps dense same-block math buckets", () => {
    installIntersectionObserverStub();
    vi.stubGlobal("innerHeight", 800);
    buildMathDocument(700, () => 42);
    stubInitialVisibility(() => 5000);

    const controller = renderMath({
      katex: { render: vi.fn() },
      documentRoot: document,
    });
    controller.updateMathObservationWindow?.(42, "scroll", 42);

    expect(latestObserver().observed.size).toBe(320);
  });

  it("does not observe cached terminal math and never reobserves it on window moves", () => {
    installIntersectionObserverStub();
    buildMathDocument(400, index => index, index => index === 350 ? "true" : null);
    stubInitialVisibility(() => 5000);

    const controller = renderMath({
      katex: { render: vi.fn() },
      documentRoot: document,
    });
    controller.updateMathObservationWindow?.(350, "scroll", 350);

    expect(observedDataIndexes()).not.toContain(350);
    expect(latestObserver().observed.size).toBeLessThanOrEqual(320);
  });

  it("unobserves math buckets that become terminal", async () => {
    installIntersectionObserverStub();
    vi.spyOn(window, "requestAnimationFrame").mockImplementation((callback: FrameRequestCallback) => {
      callback(performance.now());
      return 1;
    });
    buildMathDocument(1, () => 0);
    stubInitialVisibility(() => 100);
    const paragraph = document.querySelector<HTMLElement>("p")!;

    const controller = renderMath({
      katex: {
        render: vi.fn((_tex: string, node: HTMLElement) => {
          node.dataset["rendered"] = "yes";
        }),
      },
      documentRoot: document,
      initialObservationTopBlockIndex: 0,
      initialObservationBottomBlockIndex: 0,
    });

    expect(latestObserver().observed.has(paragraph)).toBe(true);
    await controller.initialVisibleReady;

    expect(paragraph.querySelector<HTMLElement>("[data-tex]")?.dataset["rendered"]).toBe("yes");
    expect(latestObserver().observed.has(paragraph)).toBe(false);
  });

  it("disconnects and clears observed math buckets when all math is already terminal", async () => {
    installIntersectionObserverStub();
    buildMathDocument(10, index => index, () => "true");
    stubInitialVisibility(() => 5000);

    const controller = renderMath({
      katex: { render: vi.fn() },
      documentRoot: document,
    });
    await controller.allMathRendered;

    expect(latestObserver().disconnected).toBe(true);
    expect(latestObserver().observed.size).toBe(0);
  });

  it("treats cached terminal math nodes as already ready", async () => {
    document.body.innerHTML = `
      <main class="mm-document">
        <span class="math-inline" data-tex="x" data-mm-math-rendered="true">
          <span class="katex">x</span>
        </span>
      </main>`;
    const katex = { render: vi.fn() };

    const controller = renderMath({ katex, documentRoot: document });
    const ready = await Promise.race([
      controller.initialVisibleReady.then(() => true),
      new Promise<boolean>(resolve => window.setTimeout(() => resolve(false), 20)),
    ]);

    expect(ready).toBe(true);
    expect(katex.render).not.toHaveBeenCalled();
  });

  it("uses a timer fallback when requestAnimationFrame is throttled before reveal", async () => {
    vi.useFakeTimers();
    installIntersectionObserverStub();
    vi.spyOn(window, "requestAnimationFrame").mockImplementation(() => 1);
    document.body.innerHTML = `
      <main class="mm-document">
        <p>
          <span class="math-inline" data-tex="x"></span>
        </p>
      </main>`;
    const paragraph = document.querySelector<HTMLElement>("p")!;
    vi.spyOn(paragraph, "getBoundingClientRect").mockReturnValue({
      top: 100,
      bottom: 140,
      left: 0,
      right: 800,
      width: 800,
      height: 40,
      x: 0,
      y: 100,
      toJSON: () => ({}),
    } as DOMRect);
    const katex = {
      render: vi.fn((_tex: string, node: HTMLElement) => {
        node.dataset["rendered"] = "yes";
      }),
    };

    const controller = renderMath({ katex, documentRoot: document });
    let ready = false;
    void controller.initialVisibleReady.then(() => { ready = true; });

    await Promise.resolve();
    expect(katex.render).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(32);
    await controller.initialVisibleReady;

    expect(ready).toBe(true);
    expect(katex.render).toHaveBeenCalledTimes(1);
    expect(document.querySelector<HTMLElement>(".math-inline")?.dataset["rendered"]).toBe("yes");
  });

  it("does not measure every off-screen formula before initial visible render", () => {
    installIntersectionObserverStub();
    vi.stubGlobal("innerHeight", 800);
    document.body.innerHTML = `
      <main class="mm-document">
        ${Array.from({ length: 24 }, (_, index) => `
          <p data-index="${index}">
            <span class="math-inline" data-tex="x_${index}"></span>
          </p>
        `).join("")}
      </main>`;
    const rectSpies = Array.from(document.querySelectorAll<HTMLElement>("p")).map((paragraph, index) =>
      vi.spyOn(paragraph, "getBoundingClientRect").mockReturnValue({
        top: index === 0 ? 100 : 5000 + index * 40,
        bottom: index === 0 ? 140 : 5040 + index * 40,
        left: 0,
        right: 800,
        width: 800,
        height: 40,
        x: 0,
        y: index === 0 ? 100 : 5000 + index * 40,
        toJSON: () => ({}),
      } as DOMRect)
    );

    const controller = renderMath({
      katex: { render: vi.fn() },
      documentRoot: document,
    });
    controller.cancel();

    const measuredCount = rectSpies.reduce((sum, spy) => sum + spy.mock.calls.length, 0);
    expect(measuredCount).toBeLessThan(document.querySelectorAll("[data-tex]").length);
    expect(measuredCount).toBe(9);
    expect(controller.initialVisibleNodes.size).toBe(1);
  });
});
