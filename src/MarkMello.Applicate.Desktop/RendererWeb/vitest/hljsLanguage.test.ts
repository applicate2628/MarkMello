import { describe, it, expect } from "vitest";
import { normalizeHljsLanguage, isHighlightable } from "../src/hljsLanguage";

const knownLanguageStub = (name: string) =>
  ["javascript", "typescript", "python", "ruby", "bash", "powershell", "rust", "csharp", "kotlin", "plaintext"].includes(name);

describe("normalizeHljsLanguage", () => {
  it("returns plaintext for empty input", () => {
    expect(normalizeHljsLanguage("")).toBe("plaintext");
    expect(normalizeHljsLanguage(undefined)).toBe("plaintext");
  });

  it("maps common aliases", () => {
    expect(normalizeHljsLanguage("js")).toBe("javascript");
    expect(normalizeHljsLanguage("ts")).toBe("typescript");
    expect(normalizeHljsLanguage("py")).toBe("python");
    expect(normalizeHljsLanguage("rb")).toBe("ruby");
    expect(normalizeHljsLanguage("sh")).toBe("bash");
    expect(normalizeHljsLanguage("ps1")).toBe("powershell");
    expect(normalizeHljsLanguage("rs")).toBe("rust");
    expect(normalizeHljsLanguage("cs")).toBe("csharp");
    expect(normalizeHljsLanguage("kt")).toBe("kotlin");
  });

  it("is case-insensitive", () => {
    expect(normalizeHljsLanguage("JS")).toBe("javascript");
    expect(normalizeHljsLanguage("Python")).toBe("python");
  });

  it("returns as-is when unknown alias", () => {
    expect(normalizeHljsLanguage("rust")).toBe("rust");
    expect(normalizeHljsLanguage("unknownlang")).toBe("unknownlang");
  });
});

describe("isHighlightable", () => {
  it("rejects unknown languages", () => {
    expect(isHighlightable("unknownlang", knownLanguageStub)).toBe(false);
  });

  it("accepts plaintext explicitly", () => {
    expect(isHighlightable("plaintext", knownLanguageStub)).toBe(true);
  });

  it("accepts known languages", () => {
    expect(isHighlightable("javascript", knownLanguageStub)).toBe(true);
    expect(isHighlightable("rust", knownLanguageStub)).toBe(true);
  });
});
