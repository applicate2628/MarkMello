using MarkMello.Applicate.Desktop.Rendering;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateRendererShellModeTests
{
    [Fact]
    public void IsEnabledDefaultsToFalseWhenEnvVarMissing()
    {
        Assert.False(ApplicateRendererShellMode.ReadFromEnvironment(envValue: null));
        Assert.False(ApplicateRendererShellMode.ReadFromEnvironment(envValue: ""));
        Assert.False(ApplicateRendererShellMode.ReadFromEnvironment(envValue: "  "));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("off", false)]
    [InlineData("no", false)]
    [InlineData("garbage", false)]
    public void IsEnabledParsesCommonBooleanStrings(string envValue, bool expected)
    {
        Assert.Equal(expected, ApplicateRendererShellMode.ReadFromEnvironment(envValue));
    }
}
