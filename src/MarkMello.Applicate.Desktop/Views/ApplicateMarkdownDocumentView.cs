using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSharpMath.Avalonia;
using MarkMello.Application.Abstractions;
using MarkMello.Applicate.Desktop.Math;
using MarkMello.Applicate.Desktop.Views.Minimap;
using MarkMello.Domain;
using MarkMello.Presentation.Views;
using SysMath = System.Math;

namespace MarkMello.Applicate.Desktop.Views;

public sealed partial class ApplicateMarkdownDocumentView : UserControl
{
    private const double MathFontScale = 1.05;

    public static readonly StyledProperty<RenderedMarkdownDocument?> DocumentProperty =
        AvaloniaProperty.Register<ApplicateMarkdownDocumentView, RenderedMarkdownDocument?>(nameof(Document));

    public static readonly StyledProperty<Thickness> DocumentPaddingProperty =
        AvaloniaProperty.Register<ApplicateMarkdownDocumentView, Thickness>(
            nameof(DocumentPadding),
            new Thickness(0));

    public static readonly StyledProperty<ReadingPreferences> ReadingPreferencesProperty =
        AvaloniaProperty.Register<ApplicateMarkdownDocumentView, ReadingPreferences>(
            nameof(ReadingPreferences),
            ReadingPreferences.Default);

    public static readonly StyledProperty<IImageSourceResolver?> ImageSourceResolverProperty =
        AvaloniaProperty.Register<ApplicateMarkdownDocumentView, IImageSourceResolver?>(nameof(ImageSourceResolver));

    public static readonly StyledProperty<double> AvailableContentWidthProperty =
        AvaloniaProperty.Register<ApplicateMarkdownDocumentView, double>(nameof(AvailableContentWidth), double.NaN);

    private static readonly Regex InlineMathPattern = new(
        @"(?<!\\)(\$(?!\$)(.+?)(?<!\\)\$|\\\((.+?)\\\))",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly StackPanel _root = new()
    {
        Orientation = Orientation.Vertical,
        Spacing = 0,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        UseLayoutRounding = true
    };

    private readonly Border _viewport = new()
    {
        Background = Brushes.Transparent,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private long _renderGeneration;
    private bool _hasPendingRenderedNotification;

    static ApplicateMarkdownDocumentView()
    {
        DocumentProperty.Changed.AddClassHandler<ApplicateMarkdownDocumentView>((view, _) => view.Rebuild());
        DocumentPaddingProperty.Changed.AddClassHandler<ApplicateMarkdownDocumentView>((view, _) => view.ApplyDocumentPadding());
        ReadingPreferencesProperty.Changed.AddClassHandler<ApplicateMarkdownDocumentView>((view, _) => view.Rebuild());
        ImageSourceResolverProperty.Changed.AddClassHandler<ApplicateMarkdownDocumentView>((view, _) => view.Rebuild());
        AvailableContentWidthProperty.Changed.AddClassHandler<ApplicateMarkdownDocumentView>((view, _) => view.ApplyAvailableContentWidth());
    }

    public ApplicateMarkdownDocumentView()
    {
        UseLayoutRounding = true;
        ActualThemeVariantChanged += OnAppearanceChanged;
        ResourcesChanged += OnResourcesChanged;
        _viewport.Child = _root;
        ApplyDocumentPadding();
        Content = _viewport;
    }

    public RenderedMarkdownDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public ReadingPreferences ReadingPreferences
    {
        get => GetValue(ReadingPreferencesProperty);
        set => SetValue(ReadingPreferencesProperty, value);
    }

    public Thickness DocumentPadding
    {
        get => GetValue(DocumentPaddingProperty);
        set => SetValue(DocumentPaddingProperty, value);
    }

    public IImageSourceResolver? ImageSourceResolver
    {
        get => GetValue(ImageSourceResolverProperty);
        set => SetValue(ImageSourceResolverProperty, value);
    }

    public double AvailableContentWidth
    {
        get => GetValue(AvailableContentWidthProperty);
        set => SetValue(AvailableContentWidthProperty, value);
    }

    public event EventHandler? DocumentRendered;

    public event EventHandler? DocumentRenderInvalidated;

    internal ApplicateDocumentMiniatureSnapshot CreateMiniatureSnapshot()
    {
        if (Document is null || Bounds.Height <= 0 || Bounds.Width <= 0)
        {
            return ApplicateDocumentMiniatureSnapshot.Empty;
        }

        return new ApplicateDocumentMiniatureSnapshot(
            totalWidth: SysMath.Max(1, Bounds.Width),
            totalHeight: SysMath.Max(1, Bounds.Height));
    }

    internal void RenderMiniature(DrawingContext context, Rect targetBounds)
    {
        var snapshot = CreateMiniatureSnapshot();
        if (snapshot.IsEmpty || targetBounds.Width <= 0 || targetBounds.Height <= 0)
        {
            return;
        }

        var scaleX = targetBounds.Width / snapshot.TotalWidth;
        var scaleY = targetBounds.Height / snapshot.TotalHeight;

        using (context.PushClip(targetBounds))
        {
            foreach (var border in _root.GetVisualDescendants().OfType<Border>().Where(IsMiniatureVisibleBorder))
            {
                DrawBorderMiniature(context, border, targetBounds, scaleX, scaleY);
            }

            foreach (var control in _root.GetVisualDescendants().OfType<Control>().Where(IsMiniatureRenderableControl))
            {
                DrawControlMiniature(context, control, targetBounds, scaleX, scaleY);
            }
        }
    }

    private void ApplyDocumentPadding()
    {
        _viewport.Padding = DocumentPadding;
    }

    private void ApplyAvailableContentWidth()
    {
        foreach (var mathView in _root.GetVisualDescendants().OfType<ApplicateWrappingMathView>())
        {
            mathView.AvailableContentWidth = AvailableContentWidth;
        }
    }

    private void Rebuild()
    {
        DocumentRenderInvalidated?.Invoke(this, EventArgs.Empty);
        _root.Children.Clear();
        LayoutUpdated -= OnLayoutUpdatedAfterDocumentRebuild;
        _hasPendingRenderedNotification = false;

        var document = Document;
        var generation = ++_renderGeneration;
        if (document is null || document.Blocks.Count == 0)
        {
            QueueRenderedNotification(generation);
            return;
        }

        if (!ContainsApplicateMath(document))
        {
            _root.Children.Add(BuildNativeDocument(document.Blocks));
            QueueRenderedNotification(generation);
            return;
        }

        for (var index = 0; index < document.Blocks.Count; index++)
        {
            _root.Children.Add(BuildBlock(document.Blocks[index], nested: false));
        }

        QueueRenderedNotification(generation);
    }

    private void OnAppearanceChanged(object? sender, EventArgs e)
        => Rebuild();

    private void OnResourcesChanged(object? sender, ResourcesChangedEventArgs e)
        => Rebuild();

    private void DrawBorderMiniature(
        DrawingContext context,
        Border border,
        Rect targetBounds,
        double scaleX,
        double scaleY)
    {
        var bounds = TranslateControlBounds(border);
        if (bounds is null)
        {
            return;
        }

        var target = MapMiniatureRect(bounds.Value, targetBounds, scaleX, scaleY);
        if (target.Width <= 0 || target.Height <= 0)
        {
            return;
        }

        var background = border.Background;
        var borderBrush = border.BorderBrush;
        var pen = borderBrush is null || IsEmptyThickness(border.BorderThickness)
            ? null
            : new Pen(borderBrush, 1);
        if (background is null && pen is null)
        {
            return;
        }

        using (context.PushOpacity(0.7))
        {
            context.DrawRectangle(background, pen, target, 1.5, 1.5);
        }
    }

    private void DrawControlMiniature(
        DrawingContext context,
        Control control,
        Rect targetBounds,
        double scaleX,
        double scaleY)
    {
        var bounds = TranslateControlBounds(control);
        if (bounds is null)
        {
            return;
        }

        var matrix = new Matrix(
            scaleX,
            0,
            0,
            scaleY,
            targetBounds.X + bounds.Value.X * scaleX,
            targetBounds.Y + bounds.Value.Y * scaleY);

        using (context.PushTransform(matrix))
        {
            control.Render(context);
        }
    }

    private Rect? TranslateControlBounds(Control control)
    {
        if (control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
        {
            return null;
        }

        var origin = control.TranslatePoint(new Point(0, 0), this);
        return origin is null
            ? null
            : new Rect(origin.Value, control.Bounds.Size);
    }

    private static Rect MapMiniatureRect(Rect sourceBounds, Rect targetBounds, double scaleX, double scaleY)
        => new(
            targetBounds.X + sourceBounds.X * scaleX,
            targetBounds.Y + sourceBounds.Y * scaleY,
            sourceBounds.Width * scaleX,
            SysMath.Max(1, sourceBounds.Height * scaleY));

    private static bool IsMiniatureVisibleBorder(Border border)
        => border.Background is not null
            || border.BorderBrush is not null && !IsEmptyThickness(border.BorderThickness);

    private static bool IsMiniatureRenderableControl(Control control)
    {
        if (!control.IsVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
        {
            return false;
        }

        if (control is Border or Panel or ContentControl)
        {
            return false;
        }

        if (control is TextBlock or SelectableTextBlock or MathView)
        {
            return true;
        }

        var typeName = control.GetType().Name;
        if (typeName.Contains("TextFragment", StringComparison.Ordinal)
            || typeName.Contains("ImageView", StringComparison.Ordinal))
        {
            return true;
        }

        return !control.GetVisualChildren().OfType<Control>().Any();
    }

    private static bool IsEmptyThickness(Thickness thickness)
        => thickness.Left <= 0 && thickness.Top <= 0 && thickness.Right <= 0 && thickness.Bottom <= 0;

    private void QueueRenderedNotification(long generation)
    {
        _hasPendingRenderedNotification = true;
        LayoutUpdated -= OnLayoutUpdatedAfterDocumentRebuild;
        LayoutUpdated += OnLayoutUpdatedAfterDocumentRebuild;

        Dispatcher.UIThread.Post(
            () => CompleteDocumentRenderedNotification(generation),
            DispatcherPriority.Render);
    }

    private void OnLayoutUpdatedAfterDocumentRebuild(object? sender, EventArgs e)
        => CompleteDocumentRenderedNotification(_renderGeneration);

    private void CompleteDocumentRenderedNotification(long generation)
    {
        if (!_hasPendingRenderedNotification || generation != _renderGeneration)
        {
            return;
        }

        _hasPendingRenderedNotification = false;
        LayoutUpdated -= OnLayoutUpdatedAfterDocumentRebuild;
        DocumentRendered?.Invoke(this, EventArgs.Empty);
    }

    private Control BuildBlock(MarkdownBlock block, bool nested)
        => block switch
        {
            ApplicateMathBlock math => BuildMathBlock(math, nested),
            _ when !ContainsApplicateMath(block) => BuildNativeBlock(block),
            MarkdownHeadingBlock heading => BuildHeading(heading),
            MarkdownParagraphBlock paragraph => BuildParagraph(paragraph.Inlines, nested),
            MarkdownQuoteBlock quote => BuildQuote(quote),
            MarkdownListBlock list => BuildList(list),
            MarkdownHorizontalRuleBlock => BuildHorizontalRule(),
            MarkdownCodeBlock code => BuildCodeBlock(code),
            MarkdownTableBlock or MarkdownImageBlock => BuildNativeBlock(block),
            _ => BuildNativeBlock(block)
        };

    private Control BuildHeading(MarkdownHeadingBlock block)
    {
        var text = FlattenInlines(block.Inlines);
        return new TextBlock
        {
            Text = text,
            FontFamily = ResolveBodyFontFamily(),
            FontSize = block.Level switch
            {
                1 => ReadingPreferences.FontSize * 1.9,
                2 => ReadingPreferences.FontSize * 1.55,
                3 => ReadingPreferences.FontSize * 1.28,
                _ => ReadingPreferences.FontSize * 1.08
            },
            FontWeight = FontWeight.Bold,
            Foreground = Brush("MmTextBrush", Brushes.Black),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, block.Level <= 2 ? 24 : 18, 0, 10)
        };
    }

    private Control BuildParagraph(IReadOnlyList<MarkdownInline> inlines, bool nested)
    {
        var pieces = new List<InlinePiece>();
        AddInlinePieces(inlines, pieces, InlineStyle.Default);

        var panel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, nested ? 8 : 14),
            UseLayoutRounding = true
        };

        foreach (var piece in pieces)
        {
            if (piece.Math is not null)
            {
                panel.Children.Add(BuildInlineMath(piece.Math));
            }
            else if (piece.Text is not null)
            {
                AddTextRuns(panel, piece.Text, piece.Style);
            }
        }

        return panel;
    }

    private Control BuildQuote(MarkdownQuoteBlock block)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 0
        };

        foreach (var child in block.Blocks)
        {
            stack.Children.Add(BuildBlock(child, nested: true));
        }

        return new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = Brush("MmQuoteBarBrush", Brushes.LightGray),
            Padding = new Thickness(18, 4, 0, 4),
            Margin = new Thickness(0, 10, 0, 16),
            Child = stack
        };
    }

    private Control BuildList(MarkdownListBlock block)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Margin = new Thickness(0, 0, 0, 14)
        };

        for (var index = 0; index < block.Items.Count; index++)
        {
            var marker = block.IsOrdered ? $"{index + 1}." : "-";
            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(36, GridUnitType.Pixel),
                    new ColumnDefinition(1, GridUnitType.Star)
                }
            };
            row.Children.Add(new TextBlock
            {
                Text = marker,
                FontFamily = ResolveBodyFontFamily(),
                FontSize = ReadingPreferences.FontSize,
                Foreground = Brush("MmTextSoftBrush", Brushes.Gray),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 12, 0)
            });

            var itemStack = new StackPanel { Orientation = Orientation.Vertical };
            foreach (var child in block.Items[index].Blocks)
            {
                itemStack.Children.Add(BuildBlock(child, nested: true));
            }
            Grid.SetColumn(itemStack, 1);
            row.Children.Add(itemStack);
            stack.Children.Add(row);
        }

        return stack;
    }

    private Control BuildHorizontalRule()
        => new Border
        {
            Height = 1,
            Background = Brush("MmBorderBrush", Brushes.LightGray),
            Margin = new Thickness(0, 20, 0, 20)
        };

    private Control BuildCodeBlock(MarkdownCodeBlock block)
        => new Border
        {
            Background = Brush("MmCodeBackgroundBrush", new SolidColorBrush(Color.FromRgb(244, 241, 237))),
            BorderBrush = Brush("MmCodeBorderBrush", Brushes.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 14),
            Margin = new Thickness(0, 8, 0, 18),
            Child = new SelectableTextBlock
            {
                Text = block.Code,
                FontFamily = FontFamily.Parse("Cascadia Mono, Consolas"),
                FontSize = SysMath.Max(13, ReadingPreferences.FontSize - 1),
                Foreground = Brush("MmTextBrush", Brushes.Black),
                TextWrapping = TextWrapping.NoWrap
            }
        };

    private Control BuildMathBlock(ApplicateMathBlock block, bool nested)
        => new Border
        {
            Background = Brushes.Transparent,
            ClipToBounds = true,
            Padding = new Thickness(0, 4),
            Margin = new Thickness(0, nested ? 4 : 8, 0, nested ? 8 : 16),
            Child = new ApplicateWrappingMathView(
                block.Tex,
                MathFontSize(),
                TextColor())
            {
                AvailableContentWidth = AvailableContentWidth
            }
        };

    private Control BuildInlineMath(string tex)
        => ApplicateMathPresenter.CreateOrFallback(
            ApplicateMarkdownDocumentRenderer.NormalizeTexForRenderer(tex),
            MathFontSize(),
            TextColor(),
            new Thickness(3, 0, 3, 0));

    private float MathFontSize()
        => (float)SysMath.Round(ReadingPreferences.FontSize * MathFontScale, 1, MidpointRounding.AwayFromZero);

    private Control BuildNativeBlock(MarkdownBlock block)
        => BuildNativeDocument([block]);

    private Control BuildNativeDocument(IReadOnlyList<MarkdownBlock> blocks)
        => new MarkdownDocumentView
        {
            Document = new RenderedMarkdownDocument(blocks, Document?.BaseDirectory),
            DocumentPadding = new Thickness(0),
            ReadingPreferences = ReadingPreferences,
            ImageSourceResolver = ImageSourceResolver,
            UseLayoutRounding = true
        };

    private void AddTextRuns(WrapPanel panel, string text, InlineStyle style)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (style.IsCode)
        {
            panel.Children.Add(BuildInlineCode(text));
            return;
        }

        foreach (Match match in Regex.Matches(text, @"\s+|\S+"))
        {
            var value = match.Value;
            if (value.Length == 0)
            {
                continue;
            }

            panel.Children.Add(BuildInlineText(value, style));
        }
    }

    private Control BuildInlineText(string text, InlineStyle style)
        => new TextBlock
        {
            Text = text,
            FontFamily = ResolveBodyFontFamily(),
            FontSize = ReadingPreferences.FontSize,
            FontWeight = style.IsBold ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = style.IsItalic ? FontStyle.Italic : FontStyle.Normal,
            TextDecorations = style.IsLink ? TextDecorations.Underline : null,
            Foreground = Brush(style.IsLink ? "MmAccentBrush" : "MmTextBrush", Brushes.Black),
            LineHeight = SysMath.Max(ReadingPreferences.FontSize * ReadingPreferences.LineHeight, ReadingPreferences.FontSize + 4)
        };

    private Control BuildInlineCode(string text)
        => new Border
        {
            Background = Brush("MmCodeBackgroundBrush", new SolidColorBrush(Color.FromRgb(246, 243, 239))),
            BorderBrush = Brush("MmCodeBorderBrush", Brushes.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 1),
            Margin = new Thickness(1, 0, 1, 0),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = FontFamily.Parse("Cascadia Mono, Consolas"),
                FontSize = SysMath.Max(12, ReadingPreferences.FontSize - 2),
                Foreground = Brush("MmTextBrush", Brushes.Black)
            }
        };

    private void AddInlinePieces(IReadOnlyList<MarkdownInline> inlines, List<InlinePiece> target, InlineStyle style)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MarkdownTextInline text:
                    AddTextAndMathPieces(text.Text, target, style);
                    break;

                case ApplicateMathInline math:
                    target.Add(new InlinePiece(Text: null, math.Tex, style));
                    break;

                case MarkdownStrongInline strong:
                    AddInlinePieces(strong.Inlines, target, style with { IsBold = true });
                    break;

                case MarkdownEmphasisInline emphasis:
                    AddInlinePieces(emphasis.Inlines, target, style with { IsItalic = true });
                    break;

                case MarkdownCodeInline code:
                    target.Add(new InlinePiece(code.Code, Math: null, style with { IsCode = true }));
                    break;

                case MarkdownLinkInline link:
                    if (link.Inlines.Count > 0)
                    {
                        AddInlinePieces(link.Inlines, target, style with { IsLink = true });
                    }
                    else if (!string.IsNullOrWhiteSpace(link.Url))
                    {
                        target.Add(new InlinePiece(link.Url, Math: null, style with { IsLink = true }));
                    }
                    break;

                case MarkdownImageInline image:
                    target.Add(new InlinePiece(GetImageInlineText(image), Math: null, style));
                    break;

                case MarkdownLineBreakInline:
                    target.Add(new InlinePiece(" ", Math: null, style));
                    break;
            }
        }
    }

    private static void AddTextAndMathPieces(string text, List<InlinePiece> target, InlineStyle style)
    {
        var last = 0;
        foreach (Match match in InlineMathPattern.Matches(text))
        {
            if (match.Index > last)
            {
                target.Add(new InlinePiece(text[last..match.Index], Math: null, style));
            }

            var tex = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            target.Add(new InlinePiece(Text: null, tex, style));
            last = match.Index + match.Length;
        }

        if (last < text.Length)
        {
            target.Add(new InlinePiece(text[last..], Math: null, style));
        }
    }

    private static string FlattenInlines(IReadOnlyList<MarkdownInline> inlines)
    {
        var result = new StringBuilder();
        AppendFlattened(inlines, result);
        return result.ToString();
    }

    private static void AppendFlattened(IReadOnlyList<MarkdownInline> inlines, StringBuilder result)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MarkdownTextInline text:
                    result.Append(text.Text);
                    break;
                case MarkdownStrongInline strong:
                    AppendFlattened(strong.Inlines, result);
                    break;
                case MarkdownEmphasisInline emphasis:
                    AppendFlattened(emphasis.Inlines, result);
                    break;
                case MarkdownCodeInline code:
                    result.Append(code.Code);
                    break;
                case MarkdownLinkInline link:
                    AppendFlattened(link.Inlines, result);
                    break;
                case MarkdownImageInline image:
                    result.Append(GetImageInlineText(image));
                    break;
                case MarkdownLineBreakInline:
                    result.Append(' ');
                    break;
            }
        }
    }

    private static string GetImageInlineText(MarkdownImageInline image)
        => !string.IsNullOrWhiteSpace(image.AltText)
            ? image.AltText
            : !string.IsNullOrWhiteSpace(image.Title)
                ? image.Title
                : string.IsNullOrWhiteSpace(image.Url) ? "image" : image.Url;

    private static bool ContainsApplicateMath(RenderedMarkdownDocument document)
        => document.Blocks.Any(ContainsApplicateMath);

    private static bool ContainsApplicateMath(MarkdownBlock block)
        => block switch
        {
            ApplicateMathBlock => true,
            MarkdownHeadingBlock heading => ContainsApplicateMath(heading.Inlines),
            MarkdownParagraphBlock paragraph => ContainsApplicateMath(paragraph.Inlines),
            MarkdownQuoteBlock quote => quote.Blocks.Any(ContainsApplicateMath),
            MarkdownListBlock list => list.Items.Any(item => item.Blocks.Any(ContainsApplicateMath)),
            MarkdownTableBlock table => table.Header.Any(ContainsApplicateMath)
                || table.Rows.Any(row => row.Any(ContainsApplicateMath)),
            _ => false
        };

    private static bool ContainsApplicateMath(MarkdownTableCell cell)
        => ContainsApplicateMath(cell.Inlines);

    private static bool ContainsApplicateMath(IReadOnlyList<MarkdownInline> inlines)
        => inlines.Any(inline => inline switch
        {
            ApplicateMathInline => true,
            MarkdownStrongInline strong => ContainsApplicateMath(strong.Inlines),
            MarkdownEmphasisInline emphasis => ContainsApplicateMath(emphasis.Inlines),
            MarkdownLinkInline link => ContainsApplicateMath(link.Inlines),
            _ => false
        });

    private FontFamily ResolveBodyFontFamily()
        => ReadingPreferences.FontFamily switch
        {
            FontFamilyMode.Sans => FontFamily.Parse("Segoe UI, Inter, Arial"),
            FontFamilyMode.Mono => FontFamily.Parse("Cascadia Mono, Consolas"),
            _ => FontFamily.Parse("Georgia, Times New Roman")
        };

    private IBrush Brush(string key, IBrush fallback)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var resource) && resource is IBrush brush)
        {
            return brush;
        }

        return fallback;
    }

    private Color TextColor()
        => Brush("MmTextBrush", Brushes.Black) is ISolidColorBrush solid
            ? solid.Color
            : Colors.Black;

    private readonly record struct InlinePiece(string? Text, string? Math, InlineStyle Style);

    private readonly record struct InlineStyle(bool IsBold, bool IsItalic, bool IsCode, bool IsLink)
    {
        public static InlineStyle Default { get; } = new(false, false, false, false);
    }
}
