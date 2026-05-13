using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CSharpMath.Avalonia;
using MarkMello.Applicate.Desktop.Math;
using MarkMello.Applicate.Desktop.Rendering;
using SysMath = System.Math;

namespace MarkMello.Applicate.Desktop.Views;

internal sealed class ApplicateWrappingMathView : WrapPanel
{
    private const double WidthEpsilon = 0.5;

    private readonly string _tex;
    private readonly float _fontSize;
    private readonly Color _textColor;
    private double _availableContentWidth = double.NaN;

    public ApplicateWrappingMathView(string tex, float fontSize, Color textColor)
    {
        _tex = ApplicateMarkdownDocumentRenderer.NormalizeTexForRenderer(tex);
        _fontSize = fontSize;
        _textColor = textColor;

        Orientation = Orientation.Horizontal;
        ClipToBounds = true;
        UseLayoutRounding = true;
        RebuildChunks();
    }

    public double AvailableContentWidth
    {
        get => _availableContentWidth;
        set
        {
            if (SysMath.Abs(_availableContentWidth - value) <= WidthEpsilon)
            {
                return;
            }

            _availableContentWidth = value;
            if (IsUsableWidth(value))
            {
                Width = value;
                MaxWidth = value;
            }
            else
            {
                ClearValue(WidthProperty);
                ClearValue(MaxWidthProperty);
            }

            InvalidateMeasure();
        }
    }

    private void RebuildChunks()
    {
        Children.Clear();

        foreach (var chunk in ApplicateMathLineBreaker.SplitIntoChunks(_tex))
        {
            Children.Add(CreateMathView(chunk));
        }
    }

    private MathView CreateMathView(string tex)
        => new()
        {
            LaTeX = tex,
            FontSize = _fontSize,
            TextColor = _textColor,
            ErrorColor = Colors.OrangeRed,
            DisplayErrorInline = true,
            Margin = new Thickness(0, 0, 6, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };

    private static bool IsUsableWidth(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;
}
