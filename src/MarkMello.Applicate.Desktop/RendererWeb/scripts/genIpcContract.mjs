// Regenerate RendererWeb/contract/ipc-contract.json from src/ipcContract.ts.
//
// The TS `HOST_MESSAGE_SHAPES` / `RENDERER_MESSAGE_SHAPES` recursive descriptors
// are the single source of truth (design H2, Option B+). This script bundles
// ipcContract.ts with the already-present esbuild dependency (no new toolchain),
// evaluates the descriptors, and serializes them to the checked-in JSON that both
// the vitest deep-equal test and the C# IpcContractTests read.
// Run:  npm run gen:ipc-contract
//
// The JSON lives in contract/ (NOT assets/) so the csproj `RendererWeb\assets\**`
// copy glob does not ship it as dead runtime content. A stale JSON is caught by
// the vitest deep-equal test — this script is the fix.

import esbuild from "esbuild";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const entry = resolve(here, "..", "src", "ipcContract.ts");
const outPath = resolve(here, "..", "contract", "ipc-contract.json");

const result = await esbuild.build({
  entryPoints: [entry],
  bundle: true,
  format: "esm",
  target: "es2020",
  write: false,
  legalComments: "none",
});

const code = result.outputFiles[0].text;
const moduleUrl = "data:text/javascript;base64," + Buffer.from(code).toString("base64");
const contract = await import(moduleUrl);

const payload = {
  hostMessageShapes: contract.HOST_MESSAGE_SHAPES,
  rendererMessageShapes: contract.RENDERER_MESSAGE_SHAPES,
};

mkdirSync(dirname(outPath), { recursive: true });
writeFileSync(outPath, JSON.stringify(payload, null, 2) + "\n", "utf8");
console.log(`wrote ${outPath}`);
