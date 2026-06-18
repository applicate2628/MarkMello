using MarkMello.Domain.Diagnostics;

namespace MarkMello.Domain.Tests;

public sealed class MarkdownMathHealthAnalyzerTests
{
    [Fact]
    public void CleanDocumentHasNoDefects()
    {
        const string text = "Intro $a + b = c$ done.\n\nNext $x^2$ line.\n";

        var result = MarkdownMathHealthAnalyzer.Analyze(text);

        Assert.False(result.HasDefects);
        Assert.Equal(0, result.RepairableDefectCount);
        Assert.Equal(text, result.RepairedText);
    }

    [Fact]
    public void WrappedInlineMathIsDetectedAndJoined()
    {
        // A single $…$ span hard-wrapped across two source lines.
        const string text = "Notation: $W_{ij} \\equiv\n\\lambda_{i} - \\lambda_{j}$ (the Whitney edge)\n";

        var result = MarkdownMathHealthAnalyzer.Analyze(text);

        Assert.True(result.HasRepairableDefects);
        Assert.Equal(1, result.RepairableDefectCount);
        Assert.Equal(0, result.UnrepairableDefectCount);
        var defect = Assert.Single(result.Defects);
        Assert.Equal(MarkdownMathDefectKind.WrappedInlineMath, defect.Kind);
        Assert.Equal(1, defect.LineNumber);
        Assert.True(defect.Repaired);
        Assert.Equal(
            "Notation: $W_{ij} \\equiv \\lambda_{i} - \\lambda_{j}$ (the Whitney edge)\n",
            result.RepairedText);
    }

    [Fact]
    public void RepairedTextIsCleanIdempotent()
    {
        const string text = "$W_{ij} \\equiv\n\\lambda_{i}$ tail\nplain line\n$a =\nb$ end\n";

        var first = MarkdownMathHealthAnalyzer.Analyze(text);
        Assert.True(first.HasRepairableDefects);

        var second = MarkdownMathHealthAnalyzer.Analyze(first.RepairedText);
        Assert.False(second.HasDefects);
        Assert.Equal(first.RepairedText, second.RepairedText);
    }

    [Fact]
    public void CrlfLineEndingsArePreservedOnUnchangedAndJoinedLines()
    {
        const string text = "plain\r\n$a +\r\nb$ tail\r\nmore\r\n";

        var result = MarkdownMathHealthAnalyzer.Analyze(text);

        Assert.Equal(1, result.RepairableDefectCount);
        // unchanged lines keep CRLF; the join collapses the wrap-newline to a space
        Assert.Equal("plain\r\n$a + b$ tail\r\nmore\r\n", result.RepairedText);
    }

    [Fact]
    public void DollarInsideInlineCodeIsIgnored()
    {
        const string text = "Use `price = $5` here and `$x$` literal.\n";

        var result = MarkdownMathHealthAnalyzer.Analyze(text);

        Assert.False(result.HasDefects);
        Assert.Equal(text, result.RepairedText);
    }

    [Fact]
    public void DollarInsideFencedCodeBlockIsIgnored()
    {
        const string text = "```bash\necho $HOME\nx=$((1+2))\n```\n$a + b$ ok\n";

        var result = MarkdownMathHealthAnalyzer.Analyze(text);

        Assert.False(result.HasDefects);
        Assert.Equal(text, result.RepairedText);
    }

    [Fact]
    public void EscapedDollarIsLiteralNotADelimiter()
    {
        const string text = "Costs \\$5 and \\$10 today.\n";

        var result = MarkdownMathHealthAnalyzer.Analyze(text);

        Assert.False(result.HasDefects);
        Assert.Equal(text, result.RepairedText);
    }

    [Fact]
    public void DisplayMathBlockIsNotTreatedAsInline()
    {
        const string text = "before\n$$\na + b = c\n$$\nafter $x$ ok\n";

        var result = MarkdownMathHealthAnalyzer.Analyze(text);

        Assert.False(result.HasDefects);
        Assert.Equal(text, result.RepairedText);
    }

    [Fact]
    public void WholeLineDisplayMathIsNotTreatedAsInline()
    {
        const string text = "intro\n$$a + b = c$$\noutro\n";

        var result = MarkdownMathHealthAnalyzer.Analyze(text);

        Assert.False(result.HasDefects);
        Assert.Equal(text, result.RepairedText);
    }

    [Fact]
    public void JoinDoesNotCrossBlankLineLeavesResidueUnrepairable()
    {
        // Open span, then a blank line before any close → genuine missing $.
        const string text = "$a +\n\nb plain\n";

        var result = MarkdownMathHealthAnalyzer.Analyze(text);

        Assert.False(result.HasRepairableDefects);
        Assert.Equal(1, result.UnrepairableDefectCount);
        // text is unchanged (no safe join possible)
        Assert.Equal(text, result.RepairedText);
    }

    [Fact]
    public void MultiLineWrapJoinsAllSegments()
    {
        const string text = "$a +\nb +\nc$ done\n";

        var result = MarkdownMathHealthAnalyzer.Analyze(text);

        Assert.Equal(1, result.RepairableDefectCount);
        Assert.Equal(0, result.UnrepairableDefectCount);
        Assert.Equal(3, result.Defects[0].JoinedLineCount);
        Assert.Equal("$a + b + c$ done\n", result.RepairedText);
    }

    [Fact]
    public void TwoSeparateWrapsBothRepaired()
    {
        const string text = "$a =\nb$ first\nplain middle\n$c =\nd$ second\n";

        var result = MarkdownMathHealthAnalyzer.Analyze(text);

        Assert.Equal(2, result.RepairableDefectCount);
        Assert.Equal(0, result.UnrepairableDefectCount);
        Assert.Equal("$a = b$ first\nplain middle\n$c = d$ second\n", result.RepairedText);
    }

    [Fact]
    public void BridgingLineClosesOneSpanAndOpensNextFoldsToOneLine()
    {
        // The middle source line closes the first span AND opens the second, so
        // both interior newlines are inside a span and must both be removed —
        // the minimal fix folds all three lines into one (one fold session).
        const string text = "$a =\nb$ mid text $c =\nd$ end\n";

        var result = MarkdownMathHealthAnalyzer.Analyze(text);

        Assert.Equal(1, result.RepairableDefectCount);
        Assert.Equal(0, result.UnrepairableDefectCount);
        Assert.Equal("$a = b$ mid text $c = d$ end\n", result.RepairedText);
    }

    [Fact]
    public void NullOrEmptyIsClean()
    {
        Assert.False(MarkdownMathHealthAnalyzer.Analyze("").HasDefects);
        Assert.False(MarkdownMathHealthAnalyzer.Analyze(null!).HasDefects);
    }

    [Fact]
    public void MultiLineLatexDisplayIsConvertedToDollarDisplay()
    {
        const string text = "Maxwell:\n\n\\[\n\\nabla \\times \\mathbf E = -j\\omega \\mathbf B,\n\\]\n\ntail\n";

        var result = MarkdownMathHealthAnalyzer.Analyze(text);

        Assert.True(result.HasRepairableDefects);
        Assert.Equal(1, result.RepairableDefectCount);
        Assert.Equal(0, result.UnrepairableDefectCount);
        var defect = Assert.Single(result.Defects);
        Assert.Equal(MarkdownMathDefectKind.LatexDisplayMath, defect.Kind);
        Assert.Equal(3, defect.LineNumber); // 1-based line of the "\[" open
        Assert.Equal(3, defect.JoinedLineCount); // \[ + body + \]
        Assert.True(defect.Repaired);
        Assert.Equal(
            "Maxwell:\n\n$$\n\\nabla \\times \\mathbf E = -j\\omega \\mathbf B,\n$$\n\ntail\n",
            result.RepairedText);
    }

    [Fact]
    public void SingleLineLatexDisplayIsConvertedToDollarDisplay()
    {
        const string text = "before\n\\[ E = mc^2 \\]\nafter\n";

        var result = MarkdownMathHealthAnalyzer.Analyze(text);

        Assert.Equal(1, result.RepairableDefectCount);
        var defect = Assert.Single(result.Defects);
        Assert.Equal(MarkdownMathDefectKind.LatexDisplayMath, defect.Kind);
        Assert.Equal(1, defect.JoinedLineCount);
        Assert.Equal("before\n$$ E = mc^2 $$\nafter\n", result.RepairedText);
    }

    [Fact]
    public void InlineLatexDelimitersAreLeftUntouched()
    {
        // The renderer already accepts \( … \); it is not a defect.
        const string text = "With \\(e^{j\\omega t}\\) and \\(j^2=-1\\) inline.\n";

        var result = MarkdownMathHealthAnalyzer.Analyze(text);

        Assert.False(result.HasDefects);
        Assert.Equal(text, result.RepairedText);
    }

    [Fact]
    public void LatexDisplayConversionPreservesCrlf()
    {
        const string text = "a\r\n\\[\r\nx = y\r\n\\]\r\nb\r\n";

        var result = MarkdownMathHealthAnalyzer.Analyze(text);

        Assert.Equal(1, result.RepairableDefectCount);
        Assert.Equal("a\r\n$$\r\nx = y\r\n$$\r\nb\r\n", result.RepairedText);
    }

    [Fact]
    public void LatexDisplayInsideFencedCodeBlockIsIgnored()
    {
        const string text = "```text\n\\[\nnot math\n\\]\n```\nplain\n";

        var result = MarkdownMathHealthAnalyzer.Analyze(text);

        Assert.False(result.HasDefects);
        Assert.Equal(text, result.RepairedText);
    }

    [Fact]
    public void MultipleLatexDisplayBlocksAreAllConverted()
    {
        const string text = "\\[\na = b\n\\]\n\n\\[\nc = d\n\\]\n";

        var result = MarkdownMathHealthAnalyzer.Analyze(text);

        Assert.Equal(2, result.RepairableDefectCount);
        Assert.Equal("$$\na = b\n$$\n\n$$\nc = d\n$$\n", result.RepairedText);
    }

    [Fact]
    public void UnterminatedLatexDisplayOpenIsReportedUnrepairable()
    {
        // "\[" with no matching "\]" before the blank-line boundary.
        const string text = "\\[\na = b\n\nplain paragraph\n";

        var result = MarkdownMathHealthAnalyzer.Analyze(text);

        Assert.False(result.HasRepairableDefects);
        Assert.Equal(1, result.UnrepairableDefectCount);
        var defect = Assert.Single(result.Defects);
        Assert.Equal(MarkdownMathDefectKind.LatexDisplayMath, defect.Kind);
        Assert.False(defect.Repaired);
        Assert.Equal(text, result.RepairedText); // unchanged — no guess
    }

    [Fact]
    public void LatexDisplayAndWrappedInlineAreBothDetectedAndOrdered()
    {
        // A \[ … \] display block followed by a hard-wrapped $…$ inline span.
        const string text = "\\[\na = b\n\\]\nNote $x +\ny$ tail\n";

        var result = MarkdownMathHealthAnalyzer.Analyze(text);

        Assert.Equal(2, result.RepairableDefectCount);
        Assert.Equal(0, result.UnrepairableDefectCount);
        Assert.Equal(MarkdownMathDefectKind.LatexDisplayMath, result.Defects[0].Kind);
        Assert.Equal(MarkdownMathDefectKind.WrappedInlineMath, result.Defects[1].Kind);
        Assert.True(result.Defects[0].LineNumber < result.Defects[1].LineNumber);
        Assert.Equal("$$\na = b\n$$\nNote $x + y$ tail\n", result.RepairedText);
    }

    [Fact]
    public void LatexDisplayRepairIsIdempotent()
    {
        const string text = "\\[\n\\nabla \\cdot \\mathbf D = \\rho\n\\]\n";

        var first = MarkdownMathHealthAnalyzer.Analyze(text);
        Assert.True(first.HasRepairableDefects);

        var second = MarkdownMathHealthAnalyzer.Analyze(first.RepairedText);
        Assert.False(second.HasDefects);
        Assert.Equal(first.RepairedText, second.RepairedText);
    }
}
