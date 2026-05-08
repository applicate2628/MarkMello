using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CSharpMath.Avalonia;
using MarkMello.Application.Abstractions;
using MarkMello.Applicate.Desktop.Math;
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

    static ApplicateMarkdownDocumentView()
    {
        DocumentProperty.Changed.AddClassHandler<ApplicateMarkdownDocumentView>((view, _) => view.Rebuild());
        DocumentPaddingProperty.Changed.AddClassHandler<ApplicateMarkdownDocumentView>((view, _) => view.ApplyDocumentPadding());
        ReadingPreferencesProperty.Changed.AddClassHandler<ApplicateMarkdownDocumentView>((view, _) => view.Rebuild());
        ImageSourceResolverProperty.Changed.AddClassHandler<ApplicateMarkdownDocumentView>((view, _) => view.Rebuild());
    }

    public ApplicateMarkdownDocumentView()
    {
        UseLayoutRounding = true;
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

    public event EventHandler? DocumentRendered;

    private void ApplyDocumentPadding()
    {
        _viewport.Padding = DocumentPadding;
    }

    private void Rebuild()
    {
        _root.Children.Clear();

        var document = Document;
        if (document is null || document.Blocks.Count == 0)
        {
            QueueRenderedNotification();
            return;
        }

        for (var index = 0; index < document.Blocks.Count; index++)
        {
            _root.Children.Add(BuildBlock(document.Blocks[index], nested: false));
        }

        QueueRenderedNotification();
    }

    private void QueueRenderedNotification()
    {
        Dispatcher.UIThread.Post(() => DocumentRendered?.Invoke(this, EventArgs.Empty), DispatcherPriority.Background);
    }

    private Control BuildBlock(MarkdownBlock block, bool nested)
        => block switch
        {
            ApplicateMathBlock math => BuildMathBlock(math, nested),
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
            BorderBrush = Brush("MmRuleBrush", Brushes.LightGray),
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
            Background = Brush("MmRuleBrush", Brushes.LightGray),
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
            Padding = new Thickness(0, 4),
            Margin = new Thickness(0, nested ? 4 : 8, 0, nested ? 8 : 16),
            Child = new MathView
            {
                LaTeX = block.Tex,
                FontSize = MathFontSize(),
                TextColor = TextColor(),
                ErrorColor = Colors.OrangeRed,
                DisplayErrorInline = true,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

    private Control BuildInlineMath(string tex)
        => new MathView
        {
            LaTeX = ApplicateMarkdownDocumentRenderer.NormalizeTexForRenderer(tex),
            FontSize = MathFontSize(),
            TextColor = TextColor(),
            ErrorColor = Colors.OrangeRed,
            DisplayErrorInline = true,
            Margin = new Thickness(3, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

    private float MathFontSize()
        => (float)SysMath.Round(ReadingPreferences.FontSize * MathFontScale, 1, MidpointRounding.AwayFromZero);

    private Control BuildNativeBlock(MarkdownBlock block)
        => new MarkdownDocumentView
        {
            Document = new RenderedMarkdownDocument([block], Document?.BaseDirectory),
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
            Background = Brush("MmInlineCodeBackgroundBrush", new SolidColorBrush(Color.FromRgb(246, 243, 239))),
            BorderBrush = Brush("MmInlineCodeBorderBrush", Brushes.LightGray),
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
