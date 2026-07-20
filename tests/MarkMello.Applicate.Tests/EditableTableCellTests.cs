using System.Text.RegularExpressions;
using MarkMello.Application.Abstractions;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Domain;
using MarkMello.Infrastructure.Markdown;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// P1 (internal-only span capture + emit) gate for editable tables. These tests
/// verify the dormant write-back handles the HTML carries — no user-visible editing
/// yet. The four emit cases plus the emit/write-back key-agreement and round-trip
/// gates come from the design's P1 RULE#0 gate.
/// </summary>
public sealed class EditableTableCellTests
{
    // --- TableCellIdentity: FNV-1a-32 over trimmed raw text -----------------

    [Fact]
    public void ComputeKeyIsFnv1a32OverTrimmedText()
    {
        // Independently computed FNV-1a-32 (Python reference) of the TRIMMED text.
        Assert.Equal("c40bf6cc", TableCellIdentity.ComputeKey("A"));
        Assert.Equal("811c9dc5", TableCellIdentity.ComputeKey(string.Empty));

        // Trimming makes the padded raw span (emit sees ' A ') and the re-derived
        // padded raw span (write-back re-parses to ' A ') hash identically.
        Assert.Equal(TableCellIdentity.ComputeKey("A"), TableCellIdentity.ComputeKey(" A "));
        Assert.Equal(TableCellIdentity.ComputeKey("a\\|b"), TableCellIdentity.ComputeKey(" a\\|b "));

        // The raw '\|' bytes and the rendered 'a|b' bytes hash DIFFERENTLY — the
        // reason the key must come from raw source, not the rendered inline text.
        Assert.Equal("34d00dd2", TableCellIdentity.ComputeKey("a\\|b"));
        Assert.Equal("294c7dd6", TableCellIdentity.ComputeKey("a|b"));
        Assert.NotEqual(TableCellIdentity.ComputeKey("a\\|b"), TableCellIdentity.ComputeKey("a|b"));

        Assert.Matches("^[0-9a-f]{8}$", TableCellIdentity.ComputeKey("anything"));
    }

    // --- (a) simple table ---------------------------------------------------

    [Fact]
    public async Task SimpleTableEmitsCellHandlesForEveryPlainCell()
    {
        // Probe table: | A | B | C | (line 0) / delimiter (line 1) / | 11 | 22 | 33 | (line 2).
        var html = await RenderHtmlAsync("| A | B | C |\n|---|---|---|\n| 11 | 22 | 33 |\n");

        Assert.Contains("<th class=\"mm-editable-cell\"", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"mm-editable-cell\"", html, StringComparison.Ordinal);

        // Header cells: document line 0, ordinals 0/1/2, key over each raw letter.
        AssertCell(html, "th", line: 0, index: 0, key: TableCellIdentity.ComputeKey("A"));
        AssertCell(html, "th", line: 0, index: 1, key: TableCellIdentity.ComputeKey("B"));
        AssertCell(html, "th", line: 0, index: 2, key: TableCellIdentity.ComputeKey("C"));

        // Data cells: document line 2, ordinals 0/1/2.
        AssertCell(html, "td", line: 2, index: 0, key: TableCellIdentity.ComputeKey("11"));
        AssertCell(html, "td", line: 2, index: 1, key: TableCellIdentity.ComputeKey("22"));
        AssertCell(html, "td", line: 2, index: 2, key: TableCellIdentity.ComputeKey("33"));

        // The dropped dead-weight attribute is never emitted (HTML-size gate).
        Assert.DoesNotContain("data-mm-cell-raw", html, StringComparison.Ordinal);
    }

    // --- (b) escaped-pipe cell: key over RAW '\|' bytes, not rendered 'a|b' --

    [Fact]
    public async Task EscapedPipeCellKeyHashesRawBackslashPipeNotRenderedText()
    {
        // | Col | Val | / delimiter / | a\|b | x |  — the '\|' stays one cell.
        var html = await RenderHtmlAsync("| Col | Val |\n|---|---|\n| a\\|b | x |\n");

        // The escaped-pipe cell renders 'a|b' but its key MUST hash the raw 'a\|b'.
        Assert.Contains(
            $"data-mm-cell-key=\"{TableCellIdentity.ComputeKey("a\\|b")}\"",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"data-mm-cell-key=\"{TableCellIdentity.ComputeKey("a|b")}\"",
            html,
            StringComparison.Ordinal);

        // It is a plain (all-text) cell, so it is editable and sits at ordinal 0.
        AssertCell(html, "td", line: 2, index: 0, key: TableCellIdentity.ComputeKey("a\\|b"));
    }

    // --- (c) table AFTER a $$…$$ segment: cell lines document-absolute ------

    [Fact]
    public async Task TableAfterDisplayMathHasDocumentAbsoluteCellLines()
    {
        // line0 $$ / 1 a=b / 2 $$ / 3 blank / 4 header / 5 delimiter / 6 data.
        const string md = "$$\na=b\n$$\n\n| A | B |\n| - | - |\n| 1 | 2 |\n";
        var html = await RenderHtmlAsync(md);

        // The OffsetSourceSpan table arm makes these DOCUMENT-absolute…
        Assert.Contains("data-mm-cell-line=\"4\"", html, StringComparison.Ordinal); // header row
        Assert.Contains("data-mm-cell-line=\"6\"", html, StringComparison.Ordinal); // data row

        // …not the segment-relative 1/3 the bug would emit.
        Assert.DoesNotContain("data-mm-cell-line=\"1\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-mm-cell-line=\"3\"", html, StringComparison.Ordinal);

        // And write-back agrees: a fresh RAW parse of the whole doc locates the
        // 'A' header cell at that same document-absolute line, same key.
        var span = RawTableCellLocator.Locate(md, line: 4, cellIndex: 0);
        Assert.NotNull(span);
        var raw = md.Substring(span!.Value.Start, span.Value.Length);
        Assert.Equal(" A ", raw);
        Assert.Contains(
            $"data-mm-cell-key=\"{TableCellIdentity.ComputeKey(raw)}\"",
            html,
            StringComparison.Ordinal);
    }

    // --- (d) $x$ math cell BEFORE a plain cell: plain key still correct -----

    [Fact]
    public async Task InlineMathCellBeforePlainCellKeepsPlainCellKey()
    {
        // header | $x$ | plain | / delimiter / data | $y$ | keep |.
        const string md = "| $x$ | plain |\n| --- | ----- |\n| $y$ | keep |\n";
        var html = await RenderHtmlAsync(md);

        // The math token in cell 0 shifts cell 1's char offsets, but cell 1's own
        // bytes are unchanged, so its key is exactly the visible plain text's key.
        Assert.Contains(
            $"data-mm-cell-key=\"{TableCellIdentity.ComputeKey("plain")}\"",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            $"data-mm-cell-key=\"{TableCellIdentity.ComputeKey("keep")}\"",
            html,
            StringComparison.Ordinal);

        // All FOUR cells are editable. The two math cells are RICH, so they also
        // carry data-mm-cell-raw: their DOM is KaTeX markup, and only the raw
        // markdown can be handed to the caret and committed back.
        Assert.Equal(4, Regex.Count(html, "mm-editable-cell"));
        Assert.Contains("data-mm-cell-index=\"1\"", html, StringComparison.Ordinal);
        Assert.Contains("data-mm-cell-index=\"0\"", html, StringComparison.Ordinal);
        Assert.Contains("math-inline", html, StringComparison.Ordinal);

        // Exactly the two rich cells carry the raw source; the plain ones do not.
        Assert.Equal(2, Regex.Count(html, "data-mm-cell-raw"));
        Assert.Contains("data-mm-cell-raw=\"$x$\"", html, StringComparison.Ordinal);
        Assert.Contains("data-mm-cell-raw=\"$y$\"", html, StringComparison.Ordinal);

        // Write-back agreement for the plain cell despite the math token.
        var span = RawTableCellLocator.Locate(md, line: 0, cellIndex: 1);
        Assert.NotNull(span);
        var raw = md.Substring(span!.Value.Start, span.Value.Length);
        Assert.Equal(" plain ", raw);
        Assert.Equal(TableCellIdentity.ComputeKey("plain"), TableCellIdentity.ComputeKey(raw));
    }

    // --- emit / write-back key agreement (the trickiest: escaped pipe) ------

    [Fact]
    public async Task EmitKeyMatchesWriteBackLocatorKeyForEscapedPipeCell()
    {
        const string original = "| Col | Val |\n|---|---|\n| a\\|b | x |\n";
        var html = await RenderHtmlAsync(original);

        var span = RawTableCellLocator.Locate(original, line: 2, cellIndex: 0);
        Assert.NotNull(span);
        var writeBackRaw = original.Substring(span!.Value.Start, span.Value.Length);
        Assert.Equal(" a\\|b ", writeBackRaw);

        var writeBackKey = TableCellIdentity.ComputeKey(writeBackRaw);
        Assert.Contains(
            $"data-mm-cell-key=\"{writeBackKey}\"",
            html,
            StringComparison.Ordinal);
    }

    // --- RawTableCellLocator round-trips the probe tables -------------------

    [Fact]
    public void RawTableCellLocatorRoundTripsProbeSpans()
    {
        const string simple = "| A | B | C |\n|---|---|---|\n| 11 | 22 | 33 |\n";
        AssertRoundTrip(simple, line: 0, index: 0, expectedRaw: " A ", expectedStart: 1, expectedEnd: 3);
        AssertRoundTrip(simple, line: 0, index: 1, expectedRaw: " B ", expectedStart: 5, expectedEnd: 7);
        AssertRoundTrip(simple, line: 0, index: 2, expectedRaw: " C ", expectedStart: 9, expectedEnd: 11);
        AssertRoundTrip(simple, line: 2, index: 0, expectedRaw: " 11 ", expectedStart: 29, expectedEnd: 32);
        AssertRoundTrip(simple, line: 2, index: 1, expectedRaw: " 22 ", expectedStart: 34, expectedEnd: 37);
        AssertRoundTrip(simple, line: 2, index: 2, expectedRaw: " 33 ", expectedStart: 39, expectedEnd: 42);

        const string escaped = "| Col | Val |\n|---|---|\n| a\\|b | x |\n";
        AssertRoundTrip(escaped, line: 2, index: 0, expectedRaw: " a\\|b ", expectedStart: 25, expectedEnd: 30);

        // Out-of-range coordinate returns null (fail-closed).
        Assert.Null(RawTableCellLocator.Locate(simple, line: 99, cellIndex: 0));
        Assert.Null(RawTableCellLocator.Locate(simple, line: 0, cellIndex: 99));
    }

    // --- HTML-size delta on a representative table --------------------------

    [Fact]
    public async Task EditableCellHandlesAddBoundedHtmlOverhead()
    {
        // 3 header + 9 body = 12 plain cells, each carrying the 3 handles + class.
        const string md =
            "| Name | Qty | Note |\n"
            + "| - | - | - |\n"
            + "| Apple | 3 | fresh |\n"
            + "| Pear | 5 | ripe |\n"
            + "| Plum | 2 | soft |\n";
        var html = await RenderHtmlAsync(md);

        var stripped = Regex.Replace(
            html,
            " class=\"mm-editable-cell\" data-mm-cell-line=\"\\d+\" data-mm-cell-index=\"\\d+\" data-mm-cell-key=\"[0-9a-f]{8}\"",
            string.Empty);
        var delta = html.Length - stripped.Length;

        Assert.Equal(12, Regex.Count(html, "mm-editable-cell"));
        Assert.DoesNotContain("data-mm-cell-raw", html, StringComparison.Ordinal);
        // Exact measured overhead: 1176 bytes for 12 plain cells (~98 B/cell) —
        // 3 short attrs + class per cell, no raw-text attr. Pins the per-cell cost.
        Assert.Equal(1176, delta);
    }

    // --- rich cells: raw source emission + fragment rendering ---------------

    [Fact]
    public async Task RichCellsEmitTheirRawMarkdownAlongsideTheEditHandles()
    {
        // One cell per rich flavour the old gate excluded outright.
        const string md =
            "| Plain | $x^2$ | **bold** | [t](u) | `code` |\n"
            + "| - | - | - | - | - |\n"
            + "| a | b | c | d | e |\n";
        var html = await RenderHtmlAsync(md);

        // Every cell is editable now — 5 header + 5 body.
        Assert.Equal(10, Regex.Count(html, "mm-editable-cell"));

        // The four rich header cells carry their markdown verbatim (HTML-escaped),
        // so the renderer can hand the SOURCE to the caret.
        Assert.Contains("data-mm-cell-raw=\"$x^2$\"", html, StringComparison.Ordinal);
        Assert.Contains("data-mm-cell-raw=\"**bold**\"", html, StringComparison.Ordinal);
        Assert.Contains("data-mm-cell-raw=\"[t](u)\"", html, StringComparison.Ordinal);
        Assert.Contains("data-mm-cell-raw=\"`code`\"", html, StringComparison.Ordinal);

        // Only those four: the six plain cells stay on the literal contract and
        // pay no raw-text bytes (the size rationale the plain path was built on).
        Assert.Equal(4, Regex.Count(html, "data-mm-cell-raw"));
    }

    [Fact]
    public async Task EmitKeyMatchesWriteBackLocatorKeyForMathCell()
    {
        // THE gate for rich-cell editing. The emit side captures a cell's RawText
        // from the math-PROTECTED source, so without placeholder restoration a math
        // cell's key would hash '@@APPLICATE_MATH_0@@' while the write-back hashes
        // the real '$x^2$' bytes — and every rich-cell edit would be refused.
        const string original = "| A | B |\n|---|---|\n| $x^2$ | right |\n";
        var html = await RenderHtmlAsync(original);

        var span = RawTableCellLocator.Locate(original, line: 2, cellIndex: 0);
        Assert.NotNull(span);
        var writeBackRaw = original.Substring(span!.Value.Start, span.Value.Length);
        Assert.Equal("$x^2$", writeBackRaw.Trim());

        // The emitted key and the raw attribute both come from the TRUE file bytes.
        Assert.Contains(
            $"data-mm-cell-key=\"{TableCellIdentity.ComputeKey(writeBackRaw)}\"",
            html,
            StringComparison.Ordinal);
        Assert.Contains("data-mm-cell-raw=\"$x^2$\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("APPLICATE_MATH", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmitKeyMatchesWriteBackLocatorKeyForMathCellAfterDisplayMath()
    {
        // Same agreement across a display-math segment boundary, where the cell's
        // line is segment-relative before OffsetSourceSpans makes it absolute.
        const string original = "$$\na=b\n$$\n\n| A | B |\n| - | - |\n| $y_1$ | 2 |\n";
        var html = await RenderHtmlAsync(original);

        var span = RawTableCellLocator.Locate(original, line: 6, cellIndex: 0);
        Assert.NotNull(span);
        var writeBackRaw = original.Substring(span!.Value.Start, span.Value.Length);
        Assert.Equal("$y_1$", writeBackRaw.Trim());

        Assert.Contains(
            $"data-mm-cell-key=\"{TableCellIdentity.ComputeKey(writeBackRaw)}\"",
            html,
            StringComparison.Ordinal);
        Assert.Contains("data-mm-cell-raw=\"$y_1$\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RichCellRawAttributeIsHtmlEscaped()
    {
        // A quote or angle bracket in the source must not break out of the
        // attribute — the emit side escapes, the renderer reads it back decoded.
        const string md = "| a <br> \"q\" | b |\n| - | - |\n| 1 | 2 |\n";
        var html = await RenderHtmlAsync(md);

        Assert.Contains("data-mm-cell-raw=\"a &lt;br&gt; &quot;q&quot;\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-mm-cell-raw=\"a <br>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderTableCellHtmlRendersMarkdownThroughTheDocumentPipeline()
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();

        // Math reaches KaTeX through the same data-tex contract the document pass
        // emits — this is what lets a committed raw edit settle RE-RENDERED.
        var math = await renderer.RenderTableCellHtmlAsync("$x^3$", null, CancellationToken.None);
        Assert.Contains("data-tex=\"x^3\"", math, StringComparison.Ordinal);
        Assert.Contains("math-inline", math, StringComparison.Ordinal);

        var bold = await renderer.RenderTableCellHtmlAsync("**b**", null, CancellationToken.None);
        Assert.Contains("<strong>b</strong>", bold, StringComparison.Ordinal);

        // Plain text renders as escaped text, not markup.
        var plain = await renderer.RenderTableCellHtmlAsync("a & b", null, CancellationToken.None);
        Assert.Contains("a &amp; b", plain, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderTableCellHtmlDegradesToEscapedTextWhenTheFragmentIsNotOneCell()
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();

        // A bare pipe would split the probe into two cells. The write-back path
        // refuses such text before it ever gets here; if it somehow arrives, the
        // fragment must degrade to VISIBLE TEXT, never to injected markup.
        var html = await renderer.RenderTableCellHtmlAsync("a | b | c", null, CancellationToken.None);
        Assert.DoesNotContain("<td", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", html, StringComparison.Ordinal);
    }

    // --- helpers ------------------------------------------------------------

    private static async Task<string> RenderHtmlAsync(string markdown)
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();
        var source = new MarkdownSource("table.md", "table.md", markdown);
        var document = await renderer.RenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            CancellationToken.None);
        return document.Html;
    }

    private static void AssertCell(string html, string tag, int line, int index, string key)
    {
        Assert.Contains(
            $"<{tag} class=\"mm-editable-cell\" data-mm-cell-line=\"{line}\" data-mm-cell-index=\"{index}\" data-mm-cell-key=\"{key}\">",
            html,
            StringComparison.Ordinal);
    }

    private static void AssertRoundTrip(string source, int line, int index, string expectedRaw, int expectedStart, int expectedEnd)
    {
        var span = RawTableCellLocator.Locate(source, line, index);
        Assert.NotNull(span);
        Assert.Equal(expectedStart, span!.Value.Start);
        Assert.Equal(expectedEnd, span.Value.End);
        Assert.Equal(expectedRaw, source.Substring(span.Value.Start, span.Value.Length));
    }
}
