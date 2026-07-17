# Embedded document fonts

Inlined into the WebView document CSS as base64 data-URIs (ApplicateWebAssetEmbedder), because the
document CSP is `font-src data:` — file/served fonts are blocked. Parsed once per WebView host.

- **Source Serif 4** (SourceSerif4-*.woff2): pure TTF->woff2 format conversion of the OFL faces in
  src/MarkMello.Presentation/Assets/Fonts/. OFL treats a format conversion as NOT a Modified Version,
  so the Reserved Font Name "Source" is preserved. License: SourceSerif4-OFL.md.
- **Inter Tight** (InterTight.woff2): subset (Latin + Latin-Ext + Cyrillic + punctuation) of the
  variable face; no Reserved Font Name. License: InterTight-OFL.txt.
- **JetBrains Mono** (JetBrainsMono-*.woff2): subset (same ranges) of Regular/Bold; no Reserved Font
  Name. License: JetBrainsMono-OFL.txt.

Regeneration: pyftsubset (Inter/JBM subset + woff2) / fontTools ttLib flavor='woff2' (SS4 conversion).
