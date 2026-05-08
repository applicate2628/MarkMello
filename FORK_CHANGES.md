# Applicate MarkMello Fork

This fork keeps upstream MarkMello source files unchanged. Fork-specific behavior is added through separate overlay files and projects so upstream updates can be pulled with fewer merge conflicts.

## Current Overlay Goals

- Render technical Markdown documents with TeX formulas more comfortably.
- Keep normal Markdown rendering close to upstream behavior wherever possible.
- Add flexible reader width adjustment while preserving the existing Narrow, Medium, and Wide presets.
- Keep fork-specific code in `src/MarkMello.Applicate.Desktop`.
- Keep fork-specific packaging in additive files such as `packaging/windows/MarkMello.Applicate.iss`.

## Authorship

- Upstream MarkMello: MarkMello contributors, https://github.com/dartdavros/MarkMello.
- Applicate additions: Copyright (C) 2026 Dmitry Denisenko (@applicate2628).
- License: GPL-3.0, see [LICENSE](LICENSE) and [NOTICE.md](NOTICE.md).

## Formula Rendering Scope

- Display math blocks delimited by standalone `$$` lines are extracted before the upstream Markdown renderer runs.
- Standalone one-line formulas written as `$...$` are also promoted to display math blocks; this matches technical notes that use one formula per paragraph.
- Inline formulas written as `$...$` or `\(...\)` are protected before the upstream Markdown renderer runs, restored as overlay inline math nodes, and rendered by the overlay viewer.
- Markdown content without Applicate math is delegated back to the original `MarkdownDocumentView`, keeping normal reader behavior on the upstream rendering path unless formula rendering is needed.
- Common renderer aliases are normalized for compatibility, including `\tfrac`, `\dfrac`, `^\prime`, and `^{\prime}`.
- Unsupported visual annotations such as `\underbrace{...}_{...}` and `\overbrace{...}^{...}` are reduced to the annotated formula body so the formula remains readable instead of showing a renderer error.
- Formula font sizes are derived from the reader text font size with one shared scale for inline and display formulas, avoiding size jumps between formula styles.
- Long display formulas wrap across multiple rendered math fragments at top-level math separators instead of shrinking to an unreadable scale or overflowing the reader column.
- Overlay-rendered formulas and custom document controls rebuild on theme/resource changes so light/dark theme switching keeps the original viewer behavior.

## Reader Width Scope

- The original Narrow, Medium, and Wide presets remain available.
- The overlay viewer adds a right-edge drag handle for manual content-width adjustment without changing upstream source files.
- Presets are treated as default starting widths; manual dragging can move narrower or wider than those presets within the current viewport.
- The resize edge uses a rounded animated track that smoothly highlights on hover and while dragging so the draggable boundary is discoverable without a heavy permanent border.
- The overlay viewer preserves the document minimap behavior from the original viewer, including auto/on/off visibility policy and scroll tracking.

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
