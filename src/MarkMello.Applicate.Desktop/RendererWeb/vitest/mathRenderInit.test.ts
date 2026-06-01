import { afterEach, describe, expect, it, vi } from "vitest";
import { renderMath } from "../src/mathRenderInit";

function installIntersectionObserverStub() {
  class FakeIntersectionObserver {
    observe() {}
    unobserve() {}
    disconnect() {}
  }
  vi.stubGlobal("IntersectionObserver", FakeIntersectionObserver);
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  vi.useRealTimers();
  document.body.innerHTML = "";
});

describe("renderMath", () => {
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
