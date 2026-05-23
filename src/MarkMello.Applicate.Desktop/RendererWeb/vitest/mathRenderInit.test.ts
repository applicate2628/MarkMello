import { describe, expect, it, vi } from "vitest";
import { renderMath } from "../src/mathRenderInit";

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
});
