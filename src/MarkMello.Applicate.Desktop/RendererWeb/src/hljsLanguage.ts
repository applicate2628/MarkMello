const ALIASES: Record<string, string> = {
  js: "javascript",
  ts: "typescript",
  py: "python",
  rb: "ruby",
  sh: "bash",
  ps1: "powershell",
  rs: "rust",
  cs: "csharp",
  kt: "kotlin"
};

export function normalizeHljsLanguage(name: string | undefined): string {
  if (!name) return "plaintext";
  const lower = name.toLowerCase();
  return ALIASES[lower] ?? lower;
}

export function isHighlightable(name: string, hasLanguage: (name: string) => boolean): boolean {
  return hasLanguage(name);
}
