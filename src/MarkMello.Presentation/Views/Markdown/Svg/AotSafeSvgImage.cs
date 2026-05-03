using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;

namespace MarkMello.Presentation.Views.Markdown;

/// <summary>
/// Native AOT-safe SVG subset renderer for Markdown images.
///
/// It intentionally avoids runtime SVG discovery/animation stacks. The supported
/// subset covers the static vector primitives commonly used in README assets,
/// badges, simple icons and diagrams: svg/g/a, path, rect, circle, ellipse, line,
/// polyline and polygon with inline presentation attributes or style attributes.
/// Unsupported SVG features are ignored instead of activating a reflection-heavy
/// runtime renderer in the viewer path.
/// </summary>
internal sealed class AotSafeSvgImage : IImage
{
    private const double DefaultWidth = 300;
    private const double DefaultHeight = 150;
    private static readonly SvgStyle DefaultStyle = new(
        Fill: Colors.Black,
        Stroke: null,
        StrokeWidth: 1,
        Opacity: 1);

    private readonly IReadOnlyList<SvgDrawable> _drawables;
    private readonly Rect _viewBox;

    private AotSafeSvgImage(Size size, Rect viewBox, IReadOnlyList<SvgDrawable> drawables)
    {
        Size = size;
        _viewBox = viewBox;
        _drawables = drawables;
    }

    public Size Size { get; }

    public static bool TryLoad(byte[] bytes, out AotSafeSvgImage image)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        image = null!;
        if (bytes.Length == 0)
        {
            return false;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                XmlResolver = null,
            };

            using var reader = XmlReader.Create(stream, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            var root = document.Root;
            if (root is null || !IsElement(root, "svg"))
            {
                return false;
            }

            var viewBox = ParseViewBox(root.Attribute("viewBox")?.Value);
            var width = ParseLength(root.Attribute("width")?.Value);
            var height = ParseLength(root.Attribute("height")?.Value);

            if (viewBox is null)
            {
                var fallbackWidth = NormalizeSize(width ?? DefaultWidth, DefaultWidth);
                var fallbackHeight = NormalizeSize(height ?? DefaultHeight, DefaultHeight);
                viewBox = new Rect(0, 0, fallbackWidth, fallbackHeight);
            }

            var imageWidth = NormalizeSize(width ?? viewBox.Value.Width, DefaultWidth);
            var imageHeight = NormalizeSize(height ?? viewBox.Value.Height, DefaultHeight);
            var drawables = new List<SvgDrawable>();
            ParseChildren(root, DefaultStyle, drawables);

            if (drawables.Count == 0)
            {
                return false;
            }

            image = new AotSafeSvgImage(new Size(imageWidth, imageHeight), viewBox.Value, drawables);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_drawables.Count == 0 || destRect.Width <= 0 || destRect.Height <= 0 || _viewBox.Width <= 0 || _viewBox.Height <= 0)
        {
            return;
        }

        var source = sourceRect.Width > 0 && sourceRect.Height > 0
            ? sourceRect
            : new Rect(Size);
        var scaleX = destRect.Width / source.Width;
        var scaleY = destRect.Height / source.Height;
        var viewScaleX = source.Width / _viewBox.Width;
        var viewScaleY = source.Height / _viewBox.Height;
        var transform = new SvgRenderTransform(
            ScaleX: scaleX * viewScaleX,
            ScaleY: scaleY * viewScaleY,
            OffsetX: destRect.X - source.X * scaleX - _viewBox.X * scaleX * viewScaleX,
            OffsetY: destRect.Y - source.Y * scaleY - _viewBox.Y * scaleY * viewScaleY);

        foreach (var drawable in _drawables)
        {
            drawable.Draw(context, transform);
        }
    }

    private static void ParseChildren(XElement parent, SvgStyle inheritedStyle, List<SvgDrawable> drawables)
    {
        foreach (var child in parent.Elements())
        {
            var name = child.Name.LocalName;
            if (IsIgnoredContainer(name))
            {
                continue;
            }

            var style = ParseStyle(child, inheritedStyle);
            switch (name)
            {
                case "svg":
                case "g":
                case "a":
                    ParseChildren(child, style, drawables);
                    break;
                case "rect":
                    TryAddRect(child, style, drawables);
                    break;
                case "circle":
                    TryAddCircle(child, style, drawables);
                    break;
                case "ellipse":
                    TryAddEllipse(child, style, drawables);
                    break;
                case "line":
                    TryAddLine(child, style, drawables);
                    break;
                case "polyline":
                    TryAddPoly(child, style, close: false, drawables);
                    break;
                case "polygon":
                    TryAddPoly(child, style, close: true, drawables);
                    break;
                case "path":
                    TryAddPath(child, style, drawables);
                    break;
            }
        }
    }

    private static SvgStyle ParseStyle(XElement element, SvgStyle inherited)
    {
        var styleValues = ParseStyleAttribute(element.Attribute("style")?.Value);
        var fillText = GetStyleValue(element, styleValues, "fill");
        var strokeText = GetStyleValue(element, styleValues, "stroke");
        var strokeWidthText = GetStyleValue(element, styleValues, "stroke-width");
        var opacityText = GetStyleValue(element, styleValues, "opacity");
        var fillOpacityText = GetStyleValue(element, styleValues, "fill-opacity");
        var strokeOpacityText = GetStyleValue(element, styleValues, "stroke-opacity");

        var opacity = Math.Clamp(ParseOpacity(opacityText, inherited.Opacity), 0, 1);
        var fill = ParseOptionalColor(fillText, inherited.Fill, opacity * ParseOpacity(fillOpacityText, 1));
        var stroke = ParseOptionalColor(strokeText, inherited.Stroke, opacity * ParseOpacity(strokeOpacityText, 1));
        var strokeWidth = ParseLength(strokeWidthText) ?? inherited.StrokeWidth;

        return new SvgStyle(fill, stroke, NormalizeStrokeWidth(strokeWidth), opacity);
    }

    private static Dictionary<string, string> ParseStyleAttribute(string? style)
    {
        if (string.IsNullOrWhiteSpace(style))
        {
            return new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = declaration.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 || separator >= declaration.Length - 1)
            {
                continue;
            }

            values[declaration[..separator].Trim()] = declaration[(separator + 1)..].Trim();
        }

        return values;
    }

    private static string? GetStyleValue(XElement element, Dictionary<string, string> styleValues, string name)
        => element.Attribute(name)?.Value is { } attributeValue
            ? attributeValue
            : styleValues.TryGetValue(name, out var styleValue) ? styleValue : null;

    private static void TryAddRect(XElement element, SvgStyle style, List<SvgDrawable> drawables)
    {
        var x = ParseLength(element.Attribute("x")?.Value) ?? 0;
        var y = ParseLength(element.Attribute("y")?.Value) ?? 0;
        var width = ParseLength(element.Attribute("width")?.Value) ?? 0;
        var height = ParseLength(element.Attribute("height")?.Value) ?? 0;
        if (width <= 0 || height <= 0 || !style.HasVisiblePaint)
        {
            return;
        }

        var rx = ParseLength(element.Attribute("rx")?.Value) ?? 0;
        var ry = ParseLength(element.Attribute("ry")?.Value) ?? rx;
        drawables.Add(new SvgRectDrawable(new Rect(x, y, width, height), Math.Max(0, rx), Math.Max(0, ry), style));
    }

    private static void TryAddCircle(XElement element, SvgStyle style, List<SvgDrawable> drawables)
    {
        var radius = ParseLength(element.Attribute("r")?.Value) ?? 0;
        if (radius <= 0 || !style.HasVisiblePaint)
        {
            return;
        }

        var center = new Point(
            ParseLength(element.Attribute("cx")?.Value) ?? 0,
            ParseLength(element.Attribute("cy")?.Value) ?? 0);
        drawables.Add(new SvgEllipseDrawable(center, radius, radius, style));
    }

    private static void TryAddEllipse(XElement element, SvgStyle style, List<SvgDrawable> drawables)
    {
        var rx = ParseLength(element.Attribute("rx")?.Value) ?? 0;
        var ry = ParseLength(element.Attribute("ry")?.Value) ?? 0;
        if (rx <= 0 || ry <= 0 || !style.HasVisiblePaint)
        {
            return;
        }

        var center = new Point(
            ParseLength(element.Attribute("cx")?.Value) ?? 0,
            ParseLength(element.Attribute("cy")?.Value) ?? 0);
        drawables.Add(new SvgEllipseDrawable(center, rx, ry, style));
    }

    private static void TryAddLine(XElement element, SvgStyle style, List<SvgDrawable> drawables)
    {
        if (style.Stroke is null || style.StrokeWidth <= 0 || style.Opacity <= 0)
        {
            return;
        }

        var start = new Point(
            ParseLength(element.Attribute("x1")?.Value) ?? 0,
            ParseLength(element.Attribute("y1")?.Value) ?? 0);
        var end = new Point(
            ParseLength(element.Attribute("x2")?.Value) ?? 0,
            ParseLength(element.Attribute("y2")?.Value) ?? 0);
        drawables.Add(new SvgLineDrawable(start, end, style));
    }

    private static void TryAddPoly(XElement element, SvgStyle style, bool close, List<SvgDrawable> drawables)
    {
        if (!style.HasVisiblePaint)
        {
            return;
        }

        var points = ParsePoints(element.Attribute("points")?.Value);
        if (points.Count < 2)
        {
            return;
        }

        drawables.Add(new SvgPolyDrawable(points, close, style));
    }

    private static void TryAddPath(XElement element, SvgStyle style, List<SvgDrawable> drawables)
    {
        if (!style.HasVisiblePaint)
        {
            return;
        }

        var pathData = element.Attribute("d")?.Value;
        if (string.IsNullOrWhiteSpace(pathData))
        {
            return;
        }

        try
        {
            var geometry = StreamGeometry.Parse(pathData);
            drawables.Add(new SvgGeometryDrawable(geometry, style));
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            // Unsupported path data must degrade to the normal image fallback,
            // not break the document render path.
        }
    }

    private static List<Point> ParsePoints(string? value)
    {
        var points = new List<Point>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return points;
        }

        var numbers = ParseNumberList(value);
        for (var index = 0; index + 1 < numbers.Count; index += 2)
        {
            points.Add(new Point(numbers[index], numbers[index + 1]));
        }

        return points;
    }

    private static Rect? ParseViewBox(string? value)
    {
        var numbers = ParseNumberList(value);
        if (numbers.Count != 4 || numbers[2] <= 0 || numbers[3] <= 0)
        {
            return null;
        }

        return new Rect(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    private static List<double> ParseNumberList(string? value)
    {
        var result = new List<double>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        var span = value.AsSpan();
        var start = -1;
        for (var index = 0; index <= span.Length; index++)
        {
            var isSeparator = index == span.Length || char.IsWhiteSpace(span[index]) || span[index] == ',';
            if (!isSeparator && start < 0)
            {
                start = index;
            }

            if (!isSeparator || start < 0)
            {
                continue;
            }

            var token = span[start..index].Trim();
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                result.Add(number);
            }

            start = -1;
        }

        return result;
    }

    private static double? ParseLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.EndsWith('%'))
        {
            return null;
        }

        var span = trimmed.AsSpan();
        var end = 0;
        while (end < span.Length && IsLengthCharacter(span[end]))
        {
            end++;
        }

        if (end == 0)
        {
            return null;
        }

        return double.TryParse(span[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    private static bool IsLengthCharacter(char value)
        => char.IsDigit(value) || value is '+' or '-' or '.' or 'e' or 'E';

    private static Color? ParseOptionalColor(string? value, Color? inherited, double opacity)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("inherit", StringComparison.OrdinalIgnoreCase))
        {
            return ApplyOpacity(inherited, opacity);
        }

        if (value.Equals("none", StringComparison.OrdinalIgnoreCase) || value.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return TryParseColor(value, out var color)
            ? ApplyOpacity(color, opacity)
            : ApplyOpacity(inherited, opacity);
    }

    private static bool TryParseColor(string value, out Color color)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('#'))
        {
            return TryParseHexColor(trimmed, out color);
        }

        if (trimmed.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith(')'))
        {
            return TryParseRgbColor(trimmed, out color);
        }

        if (NamedColors.TryGetValue(trimmed, out color))
        {
            return true;
        }

        color = default;
        return false;
    }

    private static bool TryParseHexColor(string value, out Color color)
    {
        color = default;
        var hex = value.AsSpan(1);
        if (hex.Length == 3)
        {
            var r = ParseHexNibble(hex[0]);
            var g = ParseHexNibble(hex[1]);
            var b = ParseHexNibble(hex[2]);
            if (r < 0 || g < 0 || b < 0)
            {
                return false;
            }

            color = Color.FromArgb(255, ExpandNibble(r), ExpandNibble(g), ExpandNibble(b));
            return true;
        }

        if (hex.Length is not (6 or 8))
        {
            return false;
        }

        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
        {
            return false;
        }

        color = hex.Length == 6
            ? Color.FromArgb(255, (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw)
            : Color.FromArgb((byte)raw, (byte)(raw >> 24), (byte)(raw >> 16), (byte)(raw >> 8));
        return true;
    }

    private static bool TryParseRgbColor(string value, out Color color)
    {
        color = default;
        var numbers = ParseNumberList(value[4..^1]);
        if (numbers.Count < 3)
        {
            return false;
        }

        color = Color.FromArgb(
            255,
            ClampColorByte(numbers[0]),
            ClampColorByte(numbers[1]),
            ClampColorByte(numbers[2]));
        return true;
    }

    private static byte ClampColorByte(double value)
        => (byte)Math.Clamp(Math.Round(value), 0, 255);

    private static int ParseHexNibble(char value)
        => value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            >= 'A' and <= 'F' => value - 'A' + 10,
            _ => -1,
        };

    private static byte ExpandNibble(int value)
        => (byte)((value << 4) | value);

    private static Color? ApplyOpacity(Color? color, double opacity)
    {
        if (color is not { } source)
        {
            return null;
        }

        var alpha = (byte)Math.Clamp(Math.Round(source.A * Math.Clamp(opacity, 0, 1)), 0, 255);
        return Color.FromArgb(alpha, source.R, source.G, source.B);
    }

    private static double ParseOpacity(string? value, double fallback)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity)
            ? Math.Clamp(opacity, 0, 1)
            : fallback;

    private static double NormalizeSize(double value, double fallback)
        => double.IsFinite(value) && value > 0 ? value : fallback;

    private static double NormalizeStrokeWidth(double value)
        => double.IsFinite(value) && value > 0 ? value : 1;

    private static bool IsElement(XElement element, string name)
        => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static bool IsIgnoredContainer(string name)
        => name is "defs" or "style" or "script" or "metadata" or "title" or "desc" or "symbol" or "mask" or "clipPath";

    private static readonly Dictionary<string, Color> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = Colors.Black,
        ["white"] = Colors.White,
        ["red"] = Colors.Red,
        ["green"] = Colors.Green,
        ["blue"] = Colors.Blue,
        ["gray"] = Colors.Gray,
        ["grey"] = Colors.Gray,
        ["silver"] = Colors.Silver,
        ["yellow"] = Colors.Yellow,
        ["orange"] = Colors.Orange,
        ["purple"] = Colors.Purple,
        ["brown"] = Colors.Brown,
    };

    private abstract record SvgDrawable(SvgStyle Style)
    {
        protected IBrush? FillBrush => Style.Fill is { } fill ? new SolidColorBrush(fill) : null;
        protected Pen? StrokePen(SvgRenderTransform transform)
            => Style.Stroke is { } stroke && Style.StrokeWidth > 0
                ? new Pen(new SolidColorBrush(stroke), Math.Max(0.5, Style.StrokeWidth * transform.StrokeScale))
                : null;

        public abstract void Draw(DrawingContext context, SvgRenderTransform transform);
    }

    private sealed record SvgRectDrawable(Rect Rect, double RadiusX, double RadiusY, SvgStyle Style) : SvgDrawable(Style)
    {
        public override void Draw(DrawingContext context, SvgRenderTransform transform)
        {
            var rect = transform.Transform(Rect);
            context.DrawRectangle(
                FillBrush,
                StrokePen(transform),
                rect,
                RadiusX * transform.ScaleX,
                RadiusY * transform.ScaleY);
        }
    }

    private sealed record SvgEllipseDrawable(Point Center, double RadiusX, double RadiusY, SvgStyle Style) : SvgDrawable(Style)
    {
        public override void Draw(DrawingContext context, SvgRenderTransform transform)
        {
            context.DrawEllipse(
                FillBrush,
                StrokePen(transform),
                transform.Transform(Center),
                RadiusX * transform.ScaleX,
                RadiusY * transform.ScaleY);
        }
    }

    private sealed record SvgLineDrawable(Point Start, Point End, SvgStyle Style) : SvgDrawable(Style)
    {
        public override void Draw(DrawingContext context, SvgRenderTransform transform)
        {
            if (StrokePen(transform) is { } pen)
            {
                context.DrawLine(pen, transform.Transform(Start), transform.Transform(End));
            }
        }
    }

    private sealed record SvgPolyDrawable(IReadOnlyList<Point> Points, bool Close, SvgStyle Style) : SvgDrawable(Style)
    {
        public override void Draw(DrawingContext context, SvgRenderTransform transform)
        {
            if (Points.Count < 2)
            {
                return;
            }

            var pathData = BuildPathData(Points, Close);
            try
            {
                var geometry = StreamGeometry.Parse(pathData);
                using (context.PushTransform(transform.Matrix))
                {
                    context.DrawGeometry(FillBrush, StrokePen(transform), geometry);
                }
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                DrawAsLines(context, transform);
            }
        }

        private void DrawAsLines(DrawingContext context, SvgRenderTransform transform)
        {
            var pen = StrokePen(transform);
            if (pen is null)
            {
                return;
            }

            for (var index = 1; index < Points.Count; index++)
            {
                context.DrawLine(pen, transform.Transform(Points[index - 1]), transform.Transform(Points[index]));
            }

            if (Close)
            {
                context.DrawLine(pen, transform.Transform(Points[^1]), transform.Transform(Points[0]));
            }
        }

        private static string BuildPathData(IReadOnlyList<Point> points, bool close)
        {
            var builder = new StringBuilder();
            builder.Append(CultureInfo.InvariantCulture, $"M {points[0].X} {points[0].Y}");
            for (var index = 1; index < points.Count; index++)
            {
                builder.Append(CultureInfo.InvariantCulture, $" L {points[index].X} {points[index].Y}");
            }

            if (close)
            {
                builder.Append(" Z");
            }

            return builder.ToString();
        }
    }

    private sealed record SvgGeometryDrawable(StreamGeometry Geometry, SvgStyle Style) : SvgDrawable(Style)
    {
        public override void Draw(DrawingContext context, SvgRenderTransform transform)
        {
            using (context.PushTransform(transform.Matrix))
            {
                context.DrawGeometry(FillBrush, StrokePen(transform), Geometry);
            }
        }
    }

    private readonly record struct SvgStyle(Color? Fill, Color? Stroke, double StrokeWidth, double Opacity)
    {
        public bool HasVisiblePaint => Opacity > 0 && (Fill is not null || Stroke is not null && StrokeWidth > 0);
    }

    private readonly record struct SvgRenderTransform(double ScaleX, double ScaleY, double OffsetX, double OffsetY)
    {
        public double StrokeScale => Math.Min(Math.Abs(ScaleX), Math.Abs(ScaleY));
        public Matrix Matrix => new(ScaleX, 0, 0, ScaleY, OffsetX, OffsetY);

        public Point Transform(Point point)
            => new(point.X * ScaleX + OffsetX, point.Y * ScaleY + OffsetY);

        public Rect Transform(Rect rect)
            => new(Transform(new Point(rect.X, rect.Y)), new Size(rect.Width * ScaleX, rect.Height * ScaleY));
    }
}
