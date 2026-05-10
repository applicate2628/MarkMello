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

## Packaging Scope

- The upstream Windows installer remains in `packaging/windows/MarkMello.iss`.
- The Applicate fork has a separate Windows installer script, `packaging/windows/MarkMello.Applicate.iss`.
- The Applicate installer uses its own AppId, ProgId, installed app name, and executable name so it does not share installer identity with upstream MarkMello.
- Build commands for the fork-specific installer are documented in [packaging/README.md](packaging/README.md).

## Upstream-Sync Rule

Do not modify existing upstream files for fork features. Add new overlay files instead. If a future feature cannot be implemented additively, document the missing extension seam before changing upstream-owned files.

## Terms and Abbreviations

- `overlay`: fork-specific code added beside upstream code instead of editing upstream files.
- `ProgId`: Windows file-association program identifier.
- `TeX`: math notation syntax commonly used in Markdown formula renderers.
- `upstream`: the original MarkMello repository used as the update source.
