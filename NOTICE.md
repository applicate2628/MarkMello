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

Copyright (C) 2026 Dmitry Denisenko (@applicate2628)

Applicate additions are distributed under GPL-3.0 as part of this fork.

## Current Applicate Functionality

- TeX formula rendering for inline math and display math in Markdown files.
- Compatibility normalization for common TeX aliases unsupported by the current
  renderer path.
- Long display formula wrapping across top-level math separators.
- Flexible reader-width resizing while preserving the upstream Narrow, Medium,
  and Wide presets.
- Overlay minimap support for the Applicate viewer path.
- Separate Windows installer metadata so the fork installs as `MarkMello Applicate`
  and does not reuse the upstream MarkMello AppId or ProgId.

## Terms and Abbreviations

- `AppId`: Inno Setup application identifier used by Windows installers.
- `Applicate`: the fork-specific overlay maintained by Dmitry Denisenko.
- `GPL-3.0`: GNU General Public License version 3.
- `ProgId`: Windows file-association program identifier.
- `TeX`: math notation syntax used by Markdown formula renderers.
- `upstream`: the original MarkMello repository used as the update source.
