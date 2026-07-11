# Task 2 Report: Recoverable Rendered-Find State

Status: PASS
Date: 2026-07-11
Branch: feat/heavy-perf-v2
Commit subject: `:bug: fix(find-ipc): recover rendered-find transfers without losing last-good data`
Commit SHA: reported in the final handoff after commit creation. The SHA cannot be embedded in the same committed report because the commit hash depends on this file content.

## Scope

Implemented Task 2 from `.superpowers/sdd/task-2-brief.md`: recoverable rendered-find state for MAJOR 1 and M5. The change stays within the approved files for the rendered-find transfer protocol, domain state, WPF host bridge, renderer projection, renderer retry handling, and their focused tests.

## Verified Roots

- Same-render replacement starts cleared the committed rendered index, so a corrupt replacement made prior rev-1 data unavailable. Root anchor before fix: `src/MarkMello.Applicate.Desktop/Rendering/ApplicateRenderedFindDomainState.cs:134-166`.
- Transfer rejection cleared committed state and reported no truthful retry floor. Root anchor before fix: `src/MarkMello.Applicate.Desktop/Rendering/ApplicateRenderedFindDomainState.cs:196-226` and `src/MarkMello.Applicate.Desktop/Rendering/ApplicateRenderedFindTextProtocol.cs:973-1032`.
- The protocol had no terminal `rendered-find-unavailable` message and no host NACK route, so uncancelled projection failure paths could not converge on a terminal rendered-find state. Root anchor before fix: `src/MarkMello.Applicate.Desktop/Rendering/ApplicateRenderedFindTextProtocol.cs:667-671`, `src/MarkMello.Applicate.Desktop/Views/ApplicateWebMarkdownDocumentView.cs:1808-1811`, and `src/MarkMello.Applicate.Desktop/RendererWeb/src/renderer.ts:1423-1484`.
- Transfer revision-floor semantics were inconsistent with an inclusive minimum retry revision. Root anchor before fix: `src/MarkMello.Applicate.Desktop/Rendering/ApplicateRenderedFindTextProtocol.cs:785-813` and `src/MarkMello.Applicate.Desktop/Rendering/ApplicateRenderedFindTextProtocol.cs:885-975`.

## RED Evidence

Command:

```powershell
dotnet test tests\MarkMello.Applicate.Tests\MarkMello.Applicate.Tests.csproj --no-restore --filter "FullyQualifiedName~ApplicateRenderedFindDomainStateTests|FullyQualifiedName~ApplicateRenderedFindTextProtocolTests"
```

Observed RED: 64 total, 53 passed, 11 failed. Failures covered committed-index loss on same-render replacement, missing `MinimumProjectionRevision`, stale replacement failure becoming rejection, missing terminal unavailable state, missing parser/classifier support for `rendered-find-unavailable`, and exclusive floor behavior.

Command:

```powershell
npm --prefix src\MarkMello.Applicate.Desktop run test:renderer -- RendererWeb/vitest/renderedFindProjection.test.ts RendererWeb/vitest/rendererScrollFamilyVirtualization.test.ts -t "rendered find|unavailable|rejected|retry"
```

Observed RED: 108 total, 104 passed, 4 failed. Failures covered missing `createRenderedFindUnavailableMessage`, readiness unavailable returning `cancelled`, no retry transfer after host NACK, and projection throws producing no `rendered-find-unavailable`.

## Implementation Anchors

- Domain state now owns render identity, last-good committed data, and terminal same-render unavailability. Same-render replacement failures retain committed data; new renders invalidate it. Current anchors: `src/MarkMello.Applicate.Desktop/Rendering/ApplicateRenderedFindDomainState.cs:19`, `src/MarkMello.Applicate.Desktop/Rendering/ApplicateRenderedFindDomainState.cs:32`, `src/MarkMello.Applicate.Desktop/Rendering/ApplicateRenderedFindDomainState.cs:149`, `src/MarkMello.Applicate.Desktop/Rendering/ApplicateRenderedFindDomainState.cs:219`, and `src/MarkMello.Applicate.Desktop/Rendering/ApplicateRenderedFindDomainState.cs:237`.
- Transfer state is the single staging and inclusive revision-floor owner. Rejections now carry `MinimumProjectionRevision` and `RenderId`; retry starts are accepted at the inclusive floor. Current anchors: `src/MarkMello.Applicate.Desktop/Rendering/ApplicateRenderedFindTextProtocol.cs:441`, `src/MarkMello.Applicate.Desktop/Rendering/ApplicateRenderedFindTextProtocol.cs:614`, `src/MarkMello.Applicate.Desktop/Rendering/ApplicateRenderedFindTextProtocol.cs:757`, `src/MarkMello.Applicate.Desktop/Rendering/ApplicateRenderedFindTextProtocol.cs:803`, `src/MarkMello.Applicate.Desktop/Rendering/ApplicateRenderedFindTextProtocol.cs:981`, and `src/MarkMello.Applicate.Desktop/Rendering/ApplicateRenderedFindTextProtocol.cs:1065`.
- The WPF host bridge emits bounded NACKs for rejected rendered transfers before posting the latest query state. Current anchors: `src/MarkMello.Applicate.Desktop/Views/ApplicateWebMarkdownDocumentView.cs:1810` and `src/MarkMello.Applicate.Desktop/Views/ApplicateWebMarkdownDocumentView.cs:2723`.
- Renderer projection now has a terminal unavailable envelope, treats uncancelled readiness failure as unavailable, retries current host NACKs twice, ignores stale or superseded NACKs, and posts terminal unavailable for retry exhaustion, null lease, readiness unavailable, and projection failure. Current anchors: `src/MarkMello.Applicate.Desktop/RendererWeb/src/renderedFindProjection.ts:78`, `src/MarkMello.Applicate.Desktop/RendererWeb/src/renderedFindProjection.ts:131`, `src/MarkMello.Applicate.Desktop/RendererWeb/src/renderedFindProjection.ts:232`, `src/MarkMello.Applicate.Desktop/RendererWeb/src/renderer.ts:1438`, `src/MarkMello.Applicate.Desktop/RendererWeb/src/renderer.ts:1524`, `src/MarkMello.Applicate.Desktop/RendererWeb/src/renderer.ts:1535`, and `src/MarkMello.Applicate.Desktop/RendererWeb/src/renderer.ts:7209`.

## Regression Coverage

- Domain retained-data and terminal-state coverage: `tests/MarkMello.Applicate.Tests/ApplicateRenderedFindDomainStateTests.cs:116`, `tests/MarkMello.Applicate.Tests/ApplicateRenderedFindDomainStateTests.cs:133`, `tests/MarkMello.Applicate.Tests/ApplicateRenderedFindDomainStateTests.cs:168`, `tests/MarkMello.Applicate.Tests/ApplicateRenderedFindDomainStateTests.cs:193`, and `tests/MarkMello.Applicate.Tests/ApplicateRenderedFindDomainStateTests.cs:211`.
- Protocol floor and rejection coverage: `tests/MarkMello.Applicate.Tests/ApplicateRenderedFindTextProtocolTests.cs:381` and `tests/MarkMello.Applicate.Tests/ApplicateRenderedFindTextProtocolTests.cs:401`.
- Renderer projection terminal-unavailable coverage: `src/MarkMello.Applicate.Desktop/RendererWeb/vitest/renderedFindProjection.test.ts:333` and `src/MarkMello.Applicate.Desktop/RendererWeb/vitest/renderedFindProjection.test.ts:352`.
- Renderer NACK, supersession, null-lease terminal, and projection-failure coverage: `src/MarkMello.Applicate.Desktop/RendererWeb/vitest/rendererScrollFamilyVirtualization.test.ts:2856`, `src/MarkMello.Applicate.Desktop/RendererWeb/vitest/rendererScrollFamilyVirtualization.test.ts:2936`, `src/MarkMello.Applicate.Desktop/RendererWeb/vitest/rendererScrollFamilyVirtualization.test.ts:2980`, and `src/MarkMello.Applicate.Desktop/RendererWeb/vitest/rendererScrollFamilyVirtualization.test.ts:3014`.

## GREEN Evidence

Focused .NET:

```powershell
dotnet test tests\MarkMello.Applicate.Tests\MarkMello.Applicate.Tests.csproj --no-restore --filter "FullyQualifiedName~ApplicateRenderedFindDomainStateTests|FullyQualifiedName~ApplicateRenderedFindTextProtocolTests"
```

Result: PASS, 64 passed, 0 failed.

Focused renderer:

```powershell
npm --prefix src\MarkMello.Applicate.Desktop run test:renderer -- RendererWeb/vitest/renderedFindProjection.test.ts RendererWeb/vitest/rendererScrollFamilyVirtualization.test.ts -t "rendered find|unavailable|rejected|retry"
```

Result: PASS, both target files passed, 108 tests passed. Note: the npm/Vitest argument path ran the full two target files rather than only the title filter.

Renderer type and lint check:

```powershell
npm --prefix src\MarkMello.Applicate.Desktop run check:renderer
```

Result: PASS.

Solution build:

```powershell
dotnet build MarkMello.sln --no-restore
```

Result: PASS, build succeeded with 0 warnings and 0 errors.

Full .NET:

```powershell
dotnet test MarkMello.sln --no-restore --no-build -m:1 -- xunit.parallelizeTestCollections=false
```

Result: PASS. Domain.Tests 63 passed; Presentation.Tests 204 passed; Applicate.Tests 552 passed.

Full renderer:

```powershell
npm --prefix src\MarkMello.Applicate.Desktop run test:renderer
```

Result: PASS, 37 files passed, 483 tests passed. Vitest printed repeated Node localstorage-file path warnings; they did not fail the run.

Whitespace:

```powershell
git diff --check
```

Result: PASS.

## QA Revision Evidence

Independent QA found that the brief-required null-lease terminal path had production behavior but no direct regression. I added `posts terminal rendered-find unavailable without transfer when the current projection lease is unavailable` at `src/MarkMello.Applicate.Desktop/RendererWeb/vitest/rendererScrollFamilyVirtualization.test.ts:2980`. The test uses the existing renderer harness and a test-only `scrollOwnershipControlPlane` mock to make the rendered-find lease acquisition see a stale document epoch after the projection precheck. It asserts exactly one `rendered-find-unavailable` with `reason: "lease-unavailable"` and no `find-text-index-*` transfer messages.

Falsifying RED:

```powershell
npm --prefix src\MarkMello.Applicate.Desktop run test:renderer -- RendererWeb/vitest/rendererScrollFamilyVirtualization.test.ts -t "posts terminal rendered-find unavailable without transfer when the current projection lease is unavailable"
```

Temporary mutation: removed `postCurrentRenderedFindUnavailable(renderId, "lease-unavailable")` at `src/MarkMello.Applicate.Desktop/RendererWeb/src/renderer.ts:1479`. Result: FAIL, 1 failed, 91 passed; the failure showed only `find-domain-begin` and no `rendered-find-unavailable`. Production was restored immediately; no production defect was found.

Focused GREEN after restoration:

```powershell
npm --prefix src\MarkMello.Applicate.Desktop run test:renderer -- RendererWeb/vitest/rendererScrollFamilyVirtualization.test.ts -t "posts terminal rendered-find unavailable without transfer when the current projection lease is unavailable"
```

Result: PASS, 92 passed, 0 failed.

Required focused renderer files after revision:

```powershell
npm --prefix src\MarkMello.Applicate.Desktop run test:renderer -- RendererWeb/vitest/renderedFindProjection.test.ts RendererWeb/vitest/rendererScrollFamilyVirtualization.test.ts
```

Result: PASS, 2 files passed, 109 tests passed.

Renderer type and lint check after revision:

```powershell
npm --prefix src\MarkMello.Applicate.Desktop run check:renderer
```

Result: PASS.

## Claims

- Same-render corrupt replacement preserves the last committed rendered-find index and continues serving ready query results when committed data exists.
- A new render invalidates retained committed rendered-find data and stale transfer failures cannot mutate the new render state.
- Transfer state owns staging and revision-floor progression; rejection metadata reports the next inclusive `minimumProjectionRevision`.
- The host emits `rendered-find-rejected` NACKs only for rejected rendered transfers with a concrete render id.
- The renderer retries current NACKs at the host floor up to the initial attempt plus two retries, ignores stale or superseded NACKs, and posts terminal `rendered-find-unavailable` on retry exhaustion or uncancelled terminal projection paths.
- The current-generation null-lease path posts one terminal `rendered-find-unavailable` with `reason: "lease-unavailable"` and performs no rendered-find transfer.

## Residual Risk

- `npm --prefix src\MarkMello.Applicate.Desktop run build:renderer` was intentionally not run because Task 2 scoped out bundle rebuilds. `check:renderer` and full renderer tests passed.
- Manual visual verification was not run because this change is protocol/state/retry behavior and does not intentionally change visible renderer layout.
- Independent `$qa-engineer` review remains parent-owned if the overall SDD lane requires a separate role gate after this implementation commit.

## Terms and Abbreviations

- NACK: Negative acknowledgement; a host message that rejects the current renderer transfer and asks for a newer projection revision.
- RED: The expected failing test run before implementation.
- GREEN: The passing verification run after implementation.
- IPC: Inter-process communication between the renderer and host.
- SDD: Spec-driven development.
- WPF: Windows Presentation Foundation, the desktop host UI framework.
