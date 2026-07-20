namespace MarkMello.Domain;

/// <summary>
/// Результат markdown parse/render pipeline для native viewer M3.
/// Содержит устойчивую block/inline модель, независимую от UI framework.
/// </summary>
/// <param name="Blocks">Плоский список блоков документа.</param>
/// <param name="BaseDirectory">
/// Директория исходного .md-файла. Используется для разрешения относительных
/// путей ресурсов (изображений). Null когда источник не имеет файловой
/// локации (например, при рендере plain-text fallback или в тестах).
/// </param>
public sealed record RenderedMarkdownDocument(
    IReadOnlyList<MarkdownBlock> Blocks,
    string? BaseDirectory = null)
{
    public static RenderedMarkdownDocument Empty { get; } = new(Array.Empty<MarkdownBlock>());

    public static RenderedMarkdownDocument PlainText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Empty;
        }

        return new RenderedMarkdownDocument(
        [
            new MarkdownParagraphBlock(
            [
                new MarkdownTextInline(text)
            ])
        ]);
    }
}

/// <summary>
/// Zero-based source line span for a rendered markdown block.
/// Used by edit-mode scroll synchronization to map source lines to preview blocks.
/// </summary>
public readonly record struct MarkdownSourceSpan
{
    public MarkdownSourceSpan(int startLine, int endLine)
    {
        StartLine = Math.Max(0, startLine);
        EndLine = Math.Max(StartLine, endLine);
    }

    public MarkdownSourceSpan(int line)
        : this(line, line)
    {
    }

    public int StartLine { get; }

    public int EndLine { get; }
}

public abstract record MarkdownBlock
{
    public MarkdownSourceSpan? SourceSpan { get; init; }
}

public sealed record MarkdownHeadingBlock(int Level, IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

public sealed record MarkdownParagraphBlock(IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

public sealed record MarkdownQuoteBlock(IReadOnlyList<MarkdownBlock> Blocks) : MarkdownBlock;

public sealed record MarkdownListBlock(bool IsOrdered, IReadOnlyList<MarkdownListItem> Items) : MarkdownBlock;

/// <param name="Blocks">The item's rendered content.</param>
/// <param name="TaskChecked">
/// GFM task-list state: <c>null</c> when the item is a plain list item, otherwise
/// <c>true</c>/<c>false</c> for a checked/unchecked <c>- [x]</c> / <c>- [ ]</c> checkbox.
/// </param>
/// <param name="TaskSourceLine">
/// 0-based document source line of a task item's marker (indexes
/// <c>Content.Split('\n')</c> directly), used to toggle <c>[ ]</c>/<c>[x]</c>
/// in the file on click. <c>null</c> for non-task items.
/// </param>
public sealed record MarkdownListItem(
    IReadOnlyList<MarkdownBlock> Blocks,
    bool? TaskChecked = null,
    int? TaskSourceLine = null);

public sealed record MarkdownHorizontalRuleBlock() : MarkdownBlock;

public sealed record MarkdownCodeBlock(string? Info, string Code) : MarkdownBlock;

public sealed record MarkdownTableBlock(
    IReadOnlyList<MarkdownTableCell> Header,
    IReadOnlyList<IReadOnlyList<MarkdownTableCell>> Rows) : MarkdownBlock;

/// <param name="Inlines">The cell's rendered content.</param>
/// <param name="Source">
/// Optional write-back coordinate for an editable table cell, captured during
/// parse. <c>null</c> when the cell was produced without span capture (e.g. the
/// plain-text fallback or a hand-built test document).
/// </param>
public sealed record MarkdownTableCell(
    IReadOnlyList<MarkdownInline> Inlines,
    MarkdownTableCellSource? Source = null);

/// <summary>
/// Write-back coordinate of a table cell, captured at parse time.
/// </summary>
/// <param name="SourceLine">
/// 0-based DOCUMENT-absolute source line of the cell's row. Captured
/// segment-relative by the parser and made document-absolute by
/// <c>ApplicateMarkdownDocumentRenderer.OffsetSourceSpan</c> (mirrors
/// <see cref="MarkdownListItem.TaskSourceLine"/>).
/// </param>
/// <param name="CellIndex">0-based ordinal of the cell within its row.</param>
/// <param name="RawText">
/// The cell's OWN raw source bytes — the padded <c>cell.Span</c> substring, e.g.
/// <c>' A '</c> or <c>' a\|b '</c>. For a PLAIN cell this equals the original file
/// bytes even when a sibling cell's inline-math token shifted the row, so
/// <c>TableCellIdentity.ComputeKey(RawText)</c> at emit matches the key the
/// write-back re-derives from a fresh raw parse of the same cell. A PLAIN cell
/// emits line/index/key only, keeping tables and the minimap clone small. A RICH
/// cell (math, emphasis, link, code, &lt;br&gt;) additionally emits
/// <c>RawText.Trim()</c> as <c>data-mm-cell-raw</c>, because its rendered DOM is
/// NOT its source and the renderer must hand the caret the markdown to edit.
/// </param>
public sealed record MarkdownTableCellSource(int SourceLine, int CellIndex, string RawText);

/// <summary>
/// Block-level image. Emitted when a markdown source paragraph contains
/// exactly one image node (e.g. a standalone ![alt](url) line) or a block
/// of HTML whose sole meaningful content is a &lt;img&gt; tag. Rendered as
/// an own non-selectable visual, outside the document text flow and text
/// map. Alt text is shown as a caption below the image, or as a
/// placeholder when the image cannot be loaded.
/// </summary>
public sealed record MarkdownImageBlock(
    string Url,
    string? AltText,
    string? Title,
    double? Width = null,
    double? Height = null) : MarkdownBlock;

public abstract record MarkdownInline;

public sealed record MarkdownTextInline(string Text) : MarkdownInline;

public sealed record MarkdownStrongInline(IReadOnlyList<MarkdownInline> Inlines) : MarkdownInline;

public sealed record MarkdownEmphasisInline(IReadOnlyList<MarkdownInline> Inlines) : MarkdownInline;

public sealed record MarkdownCodeInline(string Code) : MarkdownInline;

public sealed record MarkdownImageInline(string Url, string? AltText, string? Title) : MarkdownInline;

public sealed record MarkdownLinkInline(IReadOnlyList<MarkdownInline> Inlines, string Url, string? Title) : MarkdownInline;

public sealed record MarkdownLineBreakInline() : MarkdownInline;
