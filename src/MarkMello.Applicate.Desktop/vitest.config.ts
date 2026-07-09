import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["RendererWeb/vitest/**/*.test.ts"],
    environment: "happy-dom",
    setupFiles: ["RendererWeb/vitest/setupRendererEnvironment.ts"]
  }
});
