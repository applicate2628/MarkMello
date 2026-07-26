import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["RendererWeb/vitest/**/*.test.ts"],
    // This config declared no setupFiles until 2026-07-26; the entry below is the first,
    // and is added deliberately rather than incidentally. It exists because renderer.ts
    // can leave deferred work armed past the end of a test file, which then fires after
    // Vitest deletes `window` and makes the run exit non-zero AFTER reporting every test
    // passed. The setup file routes each test file through renderer.ts's own
    // document-lifecycle owner and is a no-op for files that never import the renderer.
    // Full rationale, including why it is global rather than per-file, lives in its header.
    setupFiles: ["./RendererWeb/vitest/setup/rendererDeferredWorkTeardown.ts"],
    environment: "happy-dom"
  }
});
