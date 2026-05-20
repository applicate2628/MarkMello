# Fork Notice and Attribution

This repository is an Applicate fork of MarkMello.

## Upstream Project

- Project: MarkMello
- Upstream repository: https://github.com/dartdavros/MarkMello
- Upstream authorship: MarkMello contributors
- License: GPL-3.0, see [LICENSE](LICENSE)

The upstream source remains under the copyright and license terms of the
original MarkMello project and its contributors.

## Applicate Additions

Applicate-specific additions are maintained as a separate overlay layer where
possible, primarily under `src/MarkMello.Applicate.Desktop`, `tests/MarkMello.Applicate.Tests`,
and `packaging/windows/MarkMello.Applicate.iss`.

The Applicate overlay currently targets Windows — the WebView/KaTeX renderer
pipeline is built around Microsoft Edge WebView2, and a matching native control
isn't available on macOS or Linux yet. Upstream MarkMello continues to support
its cross-platform packaging on its own terms; the fork's narrower scope is just
an Applicate-overlay choice and doesn't change anything upstream.

Copyright (C) 2026 Dmitry Denisenko (@applicate2628)

Applicate additions are distributed under GPL-3.0 as part of this fork.

## Current Applicate Functionality

- Optional WebView/KaTeX renderer for inline math and display math in Markdown
  files.
- Native renderer fallback and compatibility mode.
- Local-only WebView renderer pipeline: generated HTML and bundled KaTeX assets
  stay on the user's machine, and remote image links render as placeholders.
- Flexible reader-width resizing while preserving the upstream Narrow, Medium,
  and Wide presets.
- Native and WebView minimap support for the Applicate viewer path.
- Smooth Native/WebView renderer switching without a blank preview frame.
- Multi-document tab strip above the body with click-to-activate, close-button
  per tab, and drag-to-reorder with animated neighbor displacement.
- Open-document and active-tab persistence across launches via a JSON session
  store under `%AppData%/MarkMello/applicate-session.json`.
- File drag-and-drop into the window: reading-mode drops open as new tabs,
  edit-mode drops insert at the caret position with image files saved next
  to the document and inserted as relative-link Markdown.
- About panel points users to repository notices for copyright and attribution
  instead of duplicating personal author credits in the application UI.
- Separate Windows installer metadata so the fork installs as `MarkMello Applicate`
  and does not reuse the upstream MarkMello AppId or ProgId.

## Terms and Abbreviations

- `AppId`: Inno Setup application identifier used by Windows installers.
- `Applicate`: the fork-specific overlay maintained by Dmitry Denisenko.
- `GPL-3.0`: GNU General Public License version 3.
- `KaTeX`: local JavaScript/CSS math renderer used by the WebView backend.
- `ProgId`: Windows file-association program identifier.
- `TeX`: math notation syntax used by Markdown formula renderers.
- `upstream`: the original MarkMello repository used as the update source.
- `WebView`: embedded browser surface used only for Markdown document rendering.
