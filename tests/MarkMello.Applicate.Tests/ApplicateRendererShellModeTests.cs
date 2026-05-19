using MarkMello.Applicate.Desktop.Rendering;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateRendererShellModeTests
{
    [Fact]
    public void IsEnabledDefaultsToTrueWhenEnvVarMissing()
    {
        // Post-Phase 4 default is shell-mode ON (no per-document Navigate).
        // Legacy mode is opt-in via MARKMELLO_RENDERER_SHELL_MODE=0.
        Assert.True(ApplicateRendererShellMode.ReadFromEnvironment(envValue: null));
        Assert.True(ApplicateRendererShellMode.ReadFromEnvironment(envValue: ""));
        Assert.True(ApplicateRendererShellMode.ReadFromEnvironment(envValue: "  "));
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
    // Unknown values fail-open to the new default (shell-mode).
    [InlineData("garbage", true)]
    public void IsEnabledParsesCommonBooleanStrings(string envValue, bool expected)
    {
        Assert.Equal(expected, ApplicateRendererShellMode.ReadFromEnvironment(envValue));
    }
}
