# Applicate MarkMello Fork

This fork keeps upstream MarkMello source files unchanged. Fork-specific behavior is added through separate overlay files and projects so upstream updates can be pulled with fewer merge conflicts.

## Current Overlay Goals

- Render technical Markdown documents with TeX formulas more comfortably.
- Keep normal Markdown rendering close to upstream behavior wherever possible.
- Add flexible reader width adjustment while preserving the existing Narrow, Medium, and Wide presets.
- Add a WebView/KaTeX renderer without replacing the native Avalonia renderer.
- Keep all renderer work local; no remote document upload or network-served renderer is used.
- Keep fork-specific code in `src/MarkMello.Applicate.Desktop`.
- Keep fork-specific packaging in additive files such as `packaging/windows/MarkMello.Applicate.iss`.

## Authorship

- Upstream MarkMello: MarkMello contributors, https://github.com/dartdavros/MarkMello.
- Applicate additions: Copyright (C) 2026 Dmitry Denisenko (@applicate2628).
- License: GPL-3.0, see [LICENSE](LICENSE) and [NOTICE.md](NOTICE.md).

## Formula Rendering Scope

- Native formula rendering remains available through the Applicate native viewer path where it is useful.
- The primary technical-document path is now the optional WebView/KaTeX renderer.
- WebView renderer input is generated from the safe Markdown model, not from arbitrary raw HTML execution.
- KaTeX runtime assets are stored under `src/MarkMello.Applicate.Desktop/RendererWeb/assets/katex`.
- The generated HTML document is written to a local temporary application folder and loaded from disk by the embedded WebView.
- WebView navigation is restricted by `ApplicateWebResourcePolicy`; external navigation is not allowed as a renderer side effect.
- Remote Markdown image URLs render as placeholders in WebView mode instead of being fetched during rendering.
- The native renderer remains the fallback if WebView initialization or rendering fails.
- Renderer preferences include Native/WebView backend selection, minimap mode, width-resizer visibility, font size, line height, and content width.

## Reader Width Scope

- The original Narrow, Medium, and Wide presets remain available.
- The overlay viewer adds a right-edge drag handle for manual content-width adjustment without changing upstream source files.
- Presets are treated as default starting widths; manual dragging can move narrower or wider than those presets within the current viewport.
- The resize edge uses a rounded animated track that smoothly highlights on hover and while dragging so the draggable boundary is discoverable without a heavy permanent border.
- Native mode preserves the document minimap behavior from the original viewer, including auto/on/off visibility policy and scroll tracking.
- WebView mode has its own document minimap, scroll progress, width-resizer handle, and edit-preview rendering surface.
- Native/WebView backend switching keeps the previous renderer visible until the target renderer is ready, then crossfades without a blank frame.

## Multi-Document Tabs Scope

- A fork-only tabs strip rendered above the document body shows every open document; each tab has a close-button and the active tab is visually distinct (brighter background, semibold label).
- The strip lives in `src/MarkMello.Applicate.Desktop/Views/ApplicateTabsView.cs` as a code-only `UserControl`; it is mounted on top of `BodyPanel` at runtime by `ApplicateMainWindow` without modifying upstream XAML.
- Document state is owned by `IOpenDocumentsService` (`src/MarkMello.Applicate.Desktop/Editing/`). The service is the single source of truth for the open document list and the active document; the upstream `MainWindowViewModel.Document` is mirrored from it via a bridge.
- Tabs support click-to-activate, click-`×`-to-close (active-tab close routes through the upstream dirty prompt so cancel does not orphan the tab), and click+drag horizontal reorder with animated neighbour displacement.
- Drag reorder uses Avalonia `TransformOperationsTransition` for neighbour tabs sliding into a new slot (160ms cubic ease-out); the dragged tab follows the cursor without transition so the press point stays anchored.
- Open documents and the active tab are persisted between launches in JSON at `%AppData%/MarkMello/applicate-session.json` via `JsonApplicateSessionStore`.

## Drag-and-Drop Scope

- The Applicate WebView body and edit-preview surfaces accept file drag-and-drop without modifying upstream surfaces.
- Reading-mode drops route through `IOpenDocumentsService.OpenAsync` and open the dropped file as a new tab; the service deduplicates by file content-hash so dropping the same file twice (or dropping a file that is already open) does not create a duplicate tab.
- Edit-mode drops insert the file at the caret position. Image files are saved next to the document under `images/` with a content-hash collision suffix when a same-name image with different content already exists, and inserted as Markdown `![alt](images/name.png)` so the document remains portable.
- Cross-source dedupe in the active-document bridge also catches files opened from different paths but with identical content and display name.

## About-Panel Scope

- The About panel shows version and license information, then points to `NOTICE.md` for copyright and attribution details.
- Personal upstream author credits are not duplicated in the application UI. Repository-level notice files are the canonical attribution surface for the fork.

## Renderer Pipeline Scope

- The WebView renderer hides the document body, minimap, and width-resizer handle until the bootstrap pipeline finishes math + mermaid + code-block rendering and posts `layout-ready`. Without this gate the user briefly sees a fallback state on tab switch and fresh launch (web fonts not yet swapped, `\[ ... \]` math placeholders, raw mermaid source, width handle at a stale X coordinate). The reveal uses a 120ms CSS opacity transition shared by all three surfaces.
- The hide-rule is scoped to `body > main.mm-document` (and the minimap aside, and the width-handle div) so that the minimap's cloned `.mm-document` subtree is not affected; the clone always renders at full opacity inside the minimap container.

## Release Scope (v0.3.4-applicate)

- The Applicate tab strip now exposes overflow navigation when many documents are open: edge scroll arrows page the hidden tabs, the active tab is kept in view, and an all-tabs dropdown jumps directly to any open document.
- Tab labels reserve their semibold selected width, preventing tab widths from shifting when selection changes.
- Viewer-mode `Ctrl`/`Cmd` + wheel zoom is restored by letting Chromium handle browser zoom gestures instead of consuming them in the document wheel proxy.
- The startup reveal gate now receives the authoritative renderer id from the initial post-ready pipeline, avoiding the long fallback wait on documents that require post-ready renderer work.
- Startup now drops the already paint-gated splash cover immediately after renderer readiness, while in-session mode and tab-switch cover timing remains unchanged.
- The main-window chrome popups are hydrated lazily after first composite, preserving their bindings and overlay shells while reducing startup `InitializeComponent` work.
- The Table of Contents (TOC) panel replays active-heading scrolling after the panel becomes visible and laid out, so the highlighted heading is brought into view even when Avalonia rows were virtualized.
- Cached WebView documents that still contain unrendered TeX math now re-enter the initial render pipeline before reporting layout-ready instead of using the cached fast path too early.
- Applicate release publishes enable .NET ReadyToRun precompilation for managed assemblies to reduce cold first-launch Just-In-Time compilation cost without changing Debug builds.
- The Updates menu path now swaps between the menu and updates panel inside one stable popup, keeps update action buttons steady during busy states, and keeps the top-level update notification on the welcome shell instead of document renderer surfaces.
- The main window shell now draws a subtle one-pixel frame border, keeping the custom chrome visually bounded on light backgrounds.
- Russian settings labels and segmented controls were tightened so font options, line-height labels, slider values, and menu/settings text fit reliably, including Win10 font metrics.
- Repo-local release instructions now document the Applicate release workflow, GitHub CLI checks, PowerShell-safe tag-peel commands, release-body editing, and asset hash verification.

## Bugfix Release Scope (v0.3.3-applicate)

- Heavy WebView documents keep the previous rendered surface visible until the replacement document, mode, or tab state is ready, avoiding blank or raw intermediate frames on startup, tab switch, and reading/edit transitions.
- Heavy Markdown bodies are loaded visible-first: the initial viewport is published first, the rest of the document is appended in background chunks, and secondary WebView prewarm waits until the visible document finishes its progressive append.
- Renderer body caching and deferred post-ready work reduce repeated Markdown rendering, Mermaid/code highlighting, theme switching, tab switching, and width-resizer latency on large documents without enabling the future virtualization feature.
- The WebView minimap remains detailed when explicitly enabled; automatic heavy-document mode can still hide the minimap, but explicit `on` no longer falls back to schematic stripes.
- The detailed WebView minimap now maps through document block anchors and clone height, keeping heavy-document scrolling synchronized through downward scroll, upward scroll, and bottom-of-document positions.
- Startup reveal keeps a themed splash/shield visible until the target heavy document is ready, preventing exposed raw or partially sized document frames during the first load.
- Dark/light switches keep Avalonia and WebView theme changes covered until the renderer confirms the matching theme paint.
- The host-side Table of Contents keeps its panel and active selection stable during tab switches and tolerates virtualized Avalonia row recycling without crashing.

## Bugfix Release Scope (v0.3.2-applicate)

- The Updates entry in the application menu now opens a Settings-style overlay panel. The manual `Check now`, download, and open-release actions live inside that panel instead of expanding inline in the menu.
- Existing local links to Markdown and text files open as Applicate tabs, while links to other existing local files are passed to the operating system's default application.
- The release workflow treats Applicate suffix tags such as `v0.3.2-applicate` as stable releases so the published GitHub Release can become Latest automatically.

## Bugfix Release Scope (v0.3.1-applicate)

- Markdown links rendered by the WebView path now keep their authored target in `data-mm-href` and resolve display links against the source document directory instead of the generated temporary HTML directory.
- WebView minimap drag now maps through document/content coordinates and clamps short-document drags so the pointer stays anchored to the minimap thumb.
- Secondary process launches with supported Markdown file paths now forward those paths to the running Applicate instance and open them as tabs instead of creating another app instance.
- Edit saves now refresh the open-document cache, and the top-bar `Save` button is visible only when the edit buffer is dirty. `Save As` remains available throughout edit mode.
- WebView-rendered Markdown tables now have explicit table, header, and cell styling in the bundled renderer CSS.
- GitHub-facing repository text now uses English as the default README language, with the Russian README kept separately.
- Agent working directories (`.agents/`, `.claude/`, `.reports/`, `.plans/`, and `docs/superpowers/`) are kept out of the published repository state.

## Sibling-Mount of ViewerView and EditWorkspaceView (v0.3.0)

- `ApplicateMainWindow.InstallSiblingMountedViews` replaces the unnamed `<ContentControl Content="{Binding ActiveDocumentContent}">` in upstream `MainWindow.axaml` line 356-358 at runtime, swapping it for a `Panel` that holds both `ViewerView` and `EditWorkspaceView` as permanent siblings. Each sibling is a `ContentControl`; visibility, enabled-state, focus, tab-stop, and hit-test are driven by `ApplicateSiblingMountBridge` subscribing to `MainWindowViewModel.PropertyChanged` for `IsViewer` / `IsEditMode` / `EditorSession` / `Document`.
- The bridge keeps `editSlot.Content` sticky on the last non-null `EditorSession` ref. It is cleared only when the document is closed (`EditorSession` itself becomes null), not on every Ctrl+E. This prevents cascading `DataContext=null` events that would tear down the shared-host attachment on every mode toggle.
- The visibility gate is `isViewer && !isEdit && document is not null`. The `document is not null` clause closes a parasitic-frame hazard on the close-file path: `IsViewer` is a derived property on `State` (via `NotifyPropertyChangedFor`) and fires last in the close-file sequence (`IsEditMode=false → EditorSession=null → Document=null → State=NoDocument → IsViewer=false`). Without the document gate the viewer slot would flash visible at Tick 1 with a stale document still painted.
- Bridge is UI-thread safe: `Dispatcher.UIThread.CheckAccess()` then `Post()` if not on the UI thread. Necessary because `ApplyLoadedDocument` and similar VM mutations may run from `Task` continuations. An `Interlocked`-guarded `_reconcilePending` flag coalesces rapid PropertyChanged cascades into a single reconcile per UI tick. A `_disposed` flag makes post-close queued reconciles no-op.
- `IsHitTestVisible` is set in lockstep with `IsVisible`/`IsEnabled`/`IsTabStop`/`Focusable` on each slot. Native WebView2 HWND can receive Win32 input chain events independently of Avalonia's `IsEnabled`; hit-test is the explicit gate.
- Memory cost: two WebView2 instances live for the app lifetime instead of one being created/destroyed per mode toggle. Expect ~180-280 MB extra resident on heavy formula files compared with the v0.2.x ContentControl-swap design.
- Workarounds removed (now dead code, gone in v0.3.0):
  - `ApplicateEditPreviewView._pendingFirstAttachWithRealBounds` field and the `OnSizeChanged` latch that re-ran `AttachSession` on first real-size arrival.
  - `ApplicateSharedWebViewHost.AttachTo` warmup pre-resize block (the warmup → edit-slot reparent still happens once, but the slot's bounds are already correct at first attach under sibling-mount).
- Workarounds RETAINED for correctness:
  - `ApplicateViewerView._webRenderMask` (v0.2.5) — per-document navigation gap in the viewer's WebView is unchanged by mount strategy.
  - `ApplicateEditPreviewView.OnDataContextChanged` deferred-attach guard — still needed for tab switches within edit mode.
  - `ApplicateSharedWebViewHost BeginIntentionalReparent` — still needed for the warmup → edit-slot reparent (happens once at first edit-enter) and for `WebPreviewFailed` / `OnSharedFallbackRequested` recovery paths.
  - `ApplicateEditWorkspaceTemplate.cs:43` `parentBorder.HorizontalAlignment = Stretch` patch — retained as defensive. Load-bearing-ness under sibling-mount was not directly probed in v0.3.0 (template materialization happens once with real slot bounds; the v0.2.5-era zero-width Border bug may no longer reproduce, but a probe was deferred to v0.3.1 cleanup).
- Known pre-existing limitation NOT introduced by Phase 3 but worth recording: window-level `KeyBindings` in `MainWindow.axaml:29-44` (Ctrl+E, Ctrl+O, Ctrl+S, Ctrl+N, F5, Escape) stop responding when keyboard focus has been captured by the WebView2 native HWND (i.e., after the user clicked inside the rendered document). Clicking the tab strip or title bar restores keyboard focus to the Avalonia routing tree and accelerators resume. Resolving this requires either the `CoreWebView2.AcceleratorKeyPressed` event (currently blocked by `Avalonia.Controls.WebView 12.0.x` not exposing the managed `CoreWebView2` object) or an in-page JS keyhandler that forwards accelerators via the host message channel. Tracked for v0.3.1.

## Packaging Scope

- The upstream Windows installer remains in `packaging/windows/MarkMello.iss`.
- The Applicate fork has a separate Windows installer script, `packaging/windows/MarkMello.Applicate.iss`.
- The Applicate installer uses its own AppId, ProgId, installed app name, and executable name so it does not share installer identity with upstream MarkMello.
- Build commands for the fork-specific installer are documented in [packaging/README.md](packaging/README.md).

## Upstream-Sync Rule

Do not modify existing upstream files for fork features. Add new overlay files instead. If a future feature cannot be implemented additively, document the missing extension seam before changing upstream-owned files.

## Scroll-rendering Architecture

### Avalonia ScrollBar overlay (replaces WebKit scrollbar)

Both edit-preview and reader-mode WebView surfaces use an Avalonia `ScrollBar` overlay parented as a sibling of the WebView slot instead of the native `::-webkit-scrollbar` rendered by Chromium. The overlay runs on the Avalonia layout pass and is the visible scrollbar; Chromium retains the native scroll model (`overflow-y: auto` on the renderer body) so wheel / touch / keyboard / programmatic scrolling continues at native latency. Renderer-side `ScrollStateChanged` events (rAF-coalesced) mirror `scrollTop` / `scrollHeight` / `clientHeight` into the overlay's `Value` / `Maximum` / `ViewportSize`; thumb drag posts `ScrollToProgress` back through the bridge with a drag-gate state machine that suppresses inbound echoes during active drag plus a 200ms grace window after `EndScroll`.

Outcomes: thumb drag tracks the mouse without the Win32 → IPC → Chromium round-trip lag of the native scrollbar; no sideways release-zone artifact when the cursor drifts off the thumb mid-drag (Avalonia pointer capture follows the document body, not a native HWND child); the scrollbar stays anchored to the pane's right edge through splitter drag, tab switch, and document swap because the overlay's position is decided by Avalonia layout rather than by HWND geometry and Chromium reflow. See [WebViewHostScrollBarOverlay.cs](src/MarkMello.Applicate.Desktop/Views/WebViewHostScrollBarOverlay.cs).

## Terms and Abbreviations

- `Avalonia ScrollBar overlay`: the fork-side ScrollBar that mirrors WebView scroll state and replaces the native WebKit scrollbar as the visible thumb.
- `Chromium reflow`: HTML/CSS re-layout triggered when the WebView2 viewport resizes.
- `CSS scrollbar`: the `::-webkit-scrollbar` pseudo-element rendered by Chromium inside the WebView2 HWND — hidden in the fork via `display:none` + `width:0` + `height:0`.
- `IPC`: inter-process communication between the host process and the WebView2 renderer process.
- `NCH`: `Avalonia.Controls.NativeControlHost`; the Avalonia control wrapping a native Win32 HWND.
- `overlay`: fork-specific code added beside upstream code instead of editing upstream files.
- `ProgId`: Windows file-association program identifier.
- `ReadyToRun`: .NET publish option that precompiles managed assemblies to reduce startup Just-In-Time compilation work.
- `TeX`: math notation syntax commonly used in Markdown formula renderers.
- `TOC`: Table of Contents; the heading navigation panel shown beside the document.
- `upstream`: the original MarkMello repository used as the update source.
- `WebView2`: Microsoft Edge Chromium-based WebView control hosted via Avalonia.Controls.WebView.
