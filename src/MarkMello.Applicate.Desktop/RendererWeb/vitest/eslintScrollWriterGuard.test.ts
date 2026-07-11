import { ESLint } from "eslint";
import { describe, expect, it } from "vitest";

const lint = new ESLint({
  cwd: process.cwd(),
  overrideConfigFile: "eslint.config.js",
});

async function lintSource(filePath: string, code: string) {
  const [result] = await lint.lintText(code, { filePath });
  expect(result).toBeDefined();
  return result!;
}

describe("renderer scroll writer lint guard", () => {
  it("rejects direct and computed root scroll writers in normal source", async () => {
    const result = await lintSource("RendererWeb/src/normalSource.ts", `
      const element = document.documentElement;
      window.scrollTo({ top: 1 });
      window["scrollBy"]({ top: 2 });
      element.scrollTop = 3;
      element["scrollTop"] += 4;
      element.scrollTop++;
      element.scrollIntoView({ block: "center" });
      element["scrollIntoView"]();
    `);

    expect(result.messages.map(message => message.ruleId)).toEqual([
      "no-restricted-syntax",
      "no-restricted-syntax",
      "no-restricted-syntax",
      "no-restricted-syntax",
      "no-restricted-syntax",
      "no-restricted-syntax",
      "no-restricted-syntax",
    ]);
  });

  it("keeps findBar.ts inside the restricted source boundary", async () => {
    const result = await lintSource("RendererWeb/src/findBar.ts", `
      const block = document.createElement("div");
      block.scrollIntoView({ block: "center" });
    `);

    expect(result.messages).toHaveLength(1);
    expect(result.messages[0]?.ruleId).toBe("no-restricted-syntax");
  });

  it("allows only the control plane and legacy facade to contain raw scroll writer syntax", async () => {
    const code = `
      const element = document.documentElement;
      window.scrollTo({ top: 1 });
      window.scrollBy({ top: 2 });
      element.scrollTop = 3;
      element.scrollIntoView({ block: "center" });
    `;

    await expect(lintSource("RendererWeb/src/scrollOwnershipControlPlane.ts", code))
      .resolves.toMatchObject({ errorCount: 0 });
    await expect(lintSource("RendererWeb/src/legacyScrollWriter.ts", code))
      .resolves.toMatchObject({ errorCount: 0 });
  });
});
