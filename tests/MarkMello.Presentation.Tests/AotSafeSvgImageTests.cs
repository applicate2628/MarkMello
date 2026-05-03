using System.Text;
using MarkMello.Presentation.Views.Markdown;

namespace MarkMello.Presentation.Tests;

public sealed class AotSafeSvgImageTests
{
    [Fact]
    public void TryLoadSupportsBasicStaticSvgSubset()
    {
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="120" height="80" viewBox="0 0 120 80">
              <rect x="4" y="6" width="30" height="20" rx="2" fill="#db7558" />
              <circle cx="60" cy="20" r="10" style="fill: white; stroke: black; stroke-width: 2" />
              <ellipse cx="90" cy="20" rx="12" ry="8" fill="rgb(10, 20, 30)" />
              <line x1="0" y1="50" x2="120" y2="50" stroke="#000" stroke-width="1" />
              <polyline points="10,70 30,60 50,70" fill="none" stroke="blue" />
              <polygon points="70,70 85,55 100,70" fill="green" />
              <path d="M 105 60 L 115 70 L 105 70 Z" fill="red" />
            </svg>
            """;

        var loaded = AotSafeSvgImage.TryLoad(Encoding.UTF8.GetBytes(svg), out var image);

        Assert.True(loaded);
        Assert.Equal(120, image.Size.Width);
        Assert.Equal(80, image.Size.Height);
    }

    [Fact]
    public void TryLoadRejectsUnsupportedEmptySvg()
    {
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="120" height="80" viewBox="0 0 120 80">
              <defs>
                <rect id="shape" width="10" height="10" />
              </defs>
            </svg>
            """;

        var loaded = AotSafeSvgImage.TryLoad(Encoding.UTF8.GetBytes(svg), out _);

        Assert.False(loaded);
    }
}
