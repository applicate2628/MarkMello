import { defineConfig } from "vitest/config";

export default defineConfig({
  define: {
    __MM_RENDERER_DEV_DIAGNOSTICS__: "true",
  },
  test: {
    include: ["RendererWeb/vitest/**/*.test.ts"],
    environment: "happy-dom",
    setupFiles: ["RendererWeb/vitest/setupRendererEnvironment.ts"]
  }
});
