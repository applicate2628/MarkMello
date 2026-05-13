import { describe, it, expect, vi, afterEach } from "vitest";

function installIntersectionObserverStub() {
  type Obs = { cb: IntersectionObserverCallback; elements: Set<Element>; opts?: IntersectionObserverInit };
  const observers: Obs[] = [];
  const FakeIO = class {
    private elements = new Set<Element>();
    constructor(private cb: IntersectionObserverCallback, public opts?: IntersectionObserverInit) {
      observers.push({ cb, elements: this.elements, opts });
    }
    observe(el: Element) { this.elements.add(el); }
    unobserve(el: Element) { this.elements.delete(el); }
    disconnect() { this.elements.clear(); }
  };
  vi.stubGlobal("IntersectionObserver", FakeIO as unknown as typeof IntersectionObserver);
  return {
    fire(target: HTMLElement, isIntersecting: boolean) {
      for (const obs of observers) {
        if (obs.elements.has(target)) {
          obs.cb([{ target, isIntersecting } as unknown as IntersectionObserverEntry], obs as unknown as IntersectionObserver);
        }
      }
    },
  };
}

function makeFakeKatex() {
  return {
    render: vi.fn((tex: string, node: HTMLElement, _opts: unknown) => {
      node.textContent = `[${tex}]`;
    }),
  };
}

function mockRect(el: HTMLElement, top: number, bottom: number) {
  vi.spyOn(el, "getBoundingClientRect").mockReturnValue({
    top, bottom, left: 0, right: 800, width: 800, height: bottom - top, x: 0, y: top, toJSON: () => ({}),
  } as DOMRect);
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  document.body.innerHTML = "";
  delete (window as unknown as { katex?: unknown }).katex;
});

describe("renderMath integration", () => {
  it("frozen-set invariant: initialVisibleReady resolves on initial-visible set; IO-promoted off-screen node does NOT extend it", async () => {
    document.body.innerHTML = "";
    const doc = document.createElement("div");
    doc.className = "mm-document";
    // 3 visible inline (parents in viewport) + 3 off-screen display
    for (let i = 0; i < 3; i++) {
      const p = document.createElement("p");
      const s = document.createElement("span");
      s.className = "math-inline";
      s.dataset["tex"] = `inline${i}`;
      p.appendChild(s);
      doc.appendChild(p);
    }
    for (let i = 0; i < 3; i++) {
      const d = document.createElement("div");
      d.className = "math-display";
      d.dataset["tex"] = `display${i}`;
      doc.appendChild(d);
    }
    document.body.appendChild(doc);

    const visiblePs = Array.from(doc.querySelectorAll("p"));
    const offscreenDivs = Array.from(doc.querySelectorAll<HTMLElement>(".math-display"));
    visiblePs.forEach(p => mockRect(p, 100, 200));
    offscreenDivs.forEach(d => mockRect(d, 5000, 5100));
    vi.stubGlobal("innerHeight", 800);

    installIntersectionObserverStub();
    (window as unknown as { katex: ReturnType<typeof makeFakeKatex> }).katex = makeFakeKatex();

    const { renderMath } = await import("../src/mathRenderInit");
    const controller = renderMath({
      katex: (window as unknown as { katex: ReturnType<typeof makeFakeKatex> }).katex,
      documentRoot: document,
    });

    await controller.initialVisibleReady;
    const visibleSpans = Array.from(doc.querySelectorAll<HTMLElement>(".math-inline"));
    for (const s of visibleSpans) {
      expect(s.dataset["mmMathRendered"]).toBe("true");
    }

    await controller.allMathRendered;
    for (const n of [...visibleSpans, ...offscreenDivs]) {
      expect(n.dataset["mmMathRendered"]).toBe("true");
    }
  });

  it("inline-math classification via parent rect (0-size span inside visible paragraph)", async () => {
    document.body.innerHTML = '<div class="mm-document"><p><span class="math-inline" data-tex="a"></span></p></div>';
    const para = document.querySelector("p")!;
    const span = document.querySelector<HTMLElement>(".math-inline")!;
    mockRect(para, 100, 200);
    mockRect(span, 0, 0);  // 0-size before render
    vi.stubGlobal("innerHeight", 800);
    installIntersectionObserverStub();
    (window as unknown as { katex: ReturnType<typeof makeFakeKatex> }).katex = makeFakeKatex();

    const { renderMath } = await import("../src/mathRenderInit");
    const controller = renderMath({
      katex: (window as unknown as { katex: ReturnType<typeof makeFakeKatex> }).katex,
      documentRoot: document,
    });
    await controller.initialVisibleReady;

    expect(span.dataset["mmMathRendered"]).toBe("true");
  });

  it("failure handling: initialVisibleReady still resolves when katex.render throws (terminal state failed)", async () => {
    document.body.innerHTML = '<div class="mm-document"><p><span class="math-inline" data-tex="bad"></span></p></div>';
    const para = document.querySelector("p")!;
    mockRect(para, 100, 200);
    vi.stubGlobal("innerHeight", 800);
    installIntersectionObserverStub();
    (window as unknown as { katex: { render: ReturnType<typeof vi.fn> } }).katex = {
      render: vi.fn(() => { throw new Error("bad latex"); }),
    };

    const { renderMath } = await import("../src/mathRenderInit");
    const controller = renderMath({
      katex: (window as unknown as { katex: { render: ReturnType<typeof vi.fn> } }).katex,
      documentRoot: document,
    });

    await controller.initialVisibleReady;

    const span = document.querySelector<HTMLElement>(".math-inline")!;
    expect(span.dataset["mmMathRendered"]).toBe("failed");
  });
});
