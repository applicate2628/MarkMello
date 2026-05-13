import { describe, it, expect } from "vitest";

describe("vitest setup smoke", () => {
  it("DOM is available via happy-dom", () => {
    const div = document.createElement("div");
    div.textContent = "ok";
    expect(div.textContent).toBe("ok");
  });
});
