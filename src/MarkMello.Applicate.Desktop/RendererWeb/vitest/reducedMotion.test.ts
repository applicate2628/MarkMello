import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { readFileSync } from "node:fs";

type HostBridge = (msg: unknown) => void;

const CSS_PATH = "RendererWeb/assets/renderer.css";

/**
 * D12 reduced-motion policy guard.
 *
 * A reader who asked the OS to reduce motion frequently did so for a vestibular
 * disorder, so this is a correctness contract, not polish. The policy has two
 * halves and BOTH are asserted here, because each is invisible to the other's
 * tests:
 *
 *   CSS half  -- a `@media (prefers-reduced-motion: reduce)` block that
 *                neutralises every declarative transition/animation.
 *   JS half   -- the one `scrollIntoView({ behavior: "smooth" })` call site
 *                (TOC click) reads matchMedia and downgrades to "instant".
 *                An explicit `behavior` argument overrides CSS scroll-behavior,
 *                so the CSS half CANNOT cover it.
 *
 * The CSS block is written with the universal selector on purpose: the
 * mode-reveal choreography sets `element.style.transition` INLINE from
 * renderer.ts, and per CSS Cascade 4 sorting order an important author
 * declaration (rank 4) outranks a normal author declaration in a style
 * attribute (rank 6). A non-universal or non-important rule would silently stop
 * covering the reveal translate.
 */
describe("D12 reduced-motion policy (CSS contract)", () => {
  const css = readFileSync(CSS_PATH, "utf8");

  function reducedMotionBlock(): string {
    const start = css.indexOf("@media (prefers-reduced-motion: reduce)");
    expect(start, "reduced-motion media query is missing from renderer.css").toBeGreaterThan(-1);
    const open = css.indexOf("{", start);
    let depth = 0;
    for (let index = open; index < css.length; index++) {
      if (css[index] === "{") depth++;
      if (css[index] === "}") {
        depth--;
        if (depth === 0) return css.slice(start, index + 1);
      }
    }
    throw new Error("reduced-motion media query is unbalanced");
  }

  it("declares the reduced-motion media query", () => {
    expect(css).toContain("@media (prefers-reduced-motion: reduce)");
  });

  it("neutralises motion through the universal selector so inline transitions are covered", () => {
    const block = reducedMotionBlock();

    // Universal reach is what lets the block override the mode-reveal shield's
    // inline `style.transition` set in renderer.ts (startModeReveal).
    expect(block).toMatch(/(^|\s)\*\s*,/);
    expect(block).toContain("*::before");
    expect(block).toContain("*::after");
  });

  it("forces near-instant durations rather than removing them outright", () => {
    const block = reducedMotionBlock();

    // 0.01ms, NOT 0/none: a zero-length transition never fires `transitionend`,
    // which would strand any current or future listener waiting on it.
    expect(block).toMatch(/animation-duration:\s*0\.01ms\s*!important/);
    expect(block).toMatch(/transition-duration:\s*0\.01ms\s*!important/);
    expect(block).toMatch(/animation-iteration-count:\s*1\s*!important/);
  });

  it("zeroes motion delays so a delayed effect cannot outlive the policy", () => {
    const block = reducedMotionBlock();

    expect(block).toMatch(/animation-delay:\s*0\.01ms\s*!important/);
    expect(block).toMatch(/transition-delay:\s*0\.01ms\s*!important/);
  });

  it("forces instant scrolling for CSS-driven scrolls", () => {
    expect(reducedMotionBlock()).toMatch(/scroll-behavior:\s*auto\s*!important/);
  });

  it("never declares smooth scrolling outside the policy", () => {
    // A `scroll-behavior: smooth` anywhere else would be exactly the motion a
    // vestibular reader needs off, and would also change the scroll-ownership
    // control plane's timing for everyone.
    expect(css).not.toMatch(/scroll-behavior:\s*smooth/);
  });

  it("keeps the known document-surface motions declared (inventory lock)", () => {
    // Inventory as of this commit: these are the only two declarative motions on
    // the document surface. Both are covered by the universal rule above; the
    // assertions exist so the inventory in the policy comment stays honest.
    expect(css).toMatch(/\.mm-editable-cell[\s\S]{0,400}transition:\s*background-color 120ms/);
    expect(css).toMatch(/\.mm-width-handle-track[\s\S]{0,400}transition:/);
  });

  it("does not disturb the print path", () => {
    // @media print carries print-fit rules; the reduced-motion block must be a
    // sibling media query, never nested into or merged with it.
    const block = reducedMotionBlock();
    expect(block).not.toContain("@media print");
    expect(block).not.toContain("--mm-print-fit-scale");
  });
});

describe("D12 reduced-motion policy (JS smooth-scroll gate)", () => {
  let rafCallbacks: FrameRequestCallback[];
  let scrollIntoView: ReturnType<typeof vi.fn>;

  function stubReducedMotion(matches: boolean): void {
    vi.stubGlobal("matchMedia", (query: string) => ({
      matches: query.includes("prefers-reduced-motion") ? matches : false,
      media: query,
      onchange: null,
      addListener: () => {},
      removeListener: () => {},
      addEventListener: () => {},
      removeEventListener: () => {},
      dispatchEvent: () => false,
    }));
  }

  beforeEach(async () => {
    vi.resetModules();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();

    document.documentElement.innerHTML = `
      <body>
        <main class="mm-document">
          <h2 id="target-heading" data-mm-block-index="0">Target</h2>
          <p data-mm-block-index="1">Body</p>
        </main>
      </body>`;

    Object.defineProperty(document, "scrollingElement", {
      configurable: true,
      get: () => document.documentElement,
    });
    Object.defineProperty(document.documentElement, "scrollTop", {
      configurable: true,
      writable: true,
      value: 0,
    });
    Object.defineProperty(document.documentElement, "clientHeight", { configurable: true, value: 600 });
    Object.defineProperty(document.documentElement, "scrollHeight", { configurable: true, value: 2000 });
    Object.defineProperty(window, "scrollY", { configurable: true, writable: true, value: 0 });

    scrollIntoView = vi.fn();
    Object.defineProperty(Element.prototype, "scrollIntoView", {
      configurable: true,
      writable: true,
      value: scrollIntoView,
    });

    rafCallbacks = [];
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
      rafCallbacks.push(callback);
      return rafCallbacks.length;
    });

    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: () => {} },
    };

    await import("../src/renderer");
  });

  afterEach(() => {
    (window as unknown as { __mmRendererLoad?: HostBridge }).__mmRendererLoad?.({ type: "clear-document" });
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    document.body.innerHTML = "";
  });

  function load(message: unknown): void {
    (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad(message);
  }

  it("scrolls instantly to a TOC heading when the reader asked for reduced motion", () => {
    stubReducedMotion(true);

    load({ type: "scroll-to-heading", id: "target-heading" });

    expect(scrollIntoView).toHaveBeenCalledTimes(1);
    expect(scrollIntoView.mock.calls[0][0]).toMatchObject({ behavior: "instant", block: "start" });
  });

  it("keeps the smooth TOC scroll byte-identical for everyone else", () => {
    stubReducedMotion(false);

    load({ type: "scroll-to-heading", id: "target-heading" });

    expect(scrollIntoView).toHaveBeenCalledTimes(1);
    expect(scrollIntoView.mock.calls[0][0]).toMatchObject({ behavior: "smooth", block: "start" });
  });
});
