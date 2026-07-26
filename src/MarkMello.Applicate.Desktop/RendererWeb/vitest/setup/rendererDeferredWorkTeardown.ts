// Bench-only teardown: route every test file through renderer.ts's own document-lifecycle
// owner at the end of each test, so no deferred work outlives the file that armed it.
//
// THE FAILURE SHAPE THIS GUARDS (recorded 2026-07-18, recurred 2026-07-26)
// renderer.ts arms deferred work — debounce timers and rAF callbacks. When such work
// outlives its test file it fires after Vitest has torn the environment down (Vitest
// deletes `window` from globalThis), and the callback throws
//
//   ReferenceError: window is not defined
//    ❯ queueMinimapViewportUpdate  RendererWeb/src/renderer.ts (window.requestAnimationFrame)
//    ❯ Timeout._onTimeout          RendererWeb/src/renderer.ts (scheduleHeavyLiveUpdate)
//
// on a Node timer, OUTSIDE any test. Vitest then reports every file and every test as
// passed and exits non-zero. That combination is the whole reason this is worth a blanket
// hook: a green test count with a non-zero exit code reads as "suite passed", and that
// misreading has already happened once in this repo. Any report on this suite must quote
// the exit code, not the counts.
//
// WHY A GLOBAL HOOK RATHER THAN MORE PER-FILE CLEANUP
// `c29f545` closed the recorded recurrence by completing the cancellation set inside the
// single owner, `resetModuleGlobalsForLoadDocument` (renderer.ts:4731), which is reached
// via `clear-document` / `load-document`. That fix is correct but only reaches files that
// actually send one of those messages: 7 of the 21 test files that import renderer.ts do,
// so the other 14 still reach Vitest teardown with work potentially armed.
//
// WHY THIS INTRODUCES NO SECOND CANCELLATION PATH
// This hook does not re-implement cancellation and does not reach into module state. It
// dispatches `clear-document` through `window.__mmRendererLoad` — the test-only seam
// renderer.ts installs at renderer.ts:6039, which forwards to the same `handleHostMessage`
// dispatcher the WebView uses. The owner stays the single owner; this only guarantees it
// is reached. Production behaviour is unchanged: nothing here is imported by the bundle.
//
// SCOPE
// The lookup is deliberately a property probe, not an import. If a test file never imported
// renderer.ts, `__mmRendererLoad` is absent and this hook is a no-op — so the 22 test files
// that do not touch the renderer keep exactly the module graph and environment they had.
//
// NO TIMERS. This hook only cancels scheduled work; it never schedules any.
import { afterEach } from "vitest";

type RendererHostBridge = (message: unknown) => void;

afterEach(() => {
  const bridge = (globalThis as unknown as {
    window?: { __mmRendererLoad?: RendererHostBridge };
  }).window?.__mmRendererLoad;

  // Absent whenever the test file did not import renderer.ts.
  if (typeof bridge !== "function") {
    return;
  }

  // Deliberately unguarded by try/catch: this is a bench safety net, and a throw from the
  // document-lifecycle owner is a finding that should surface loudly rather than be
  // swallowed into a silently-degraded teardown.
  bridge({ type: "clear-document" });
});
