using MarkMello.Applicate.Desktop.Rendering;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateVirtualizationModeTests
{
    [Fact]
    public void IsEnabledDefaultsToFalseWhenEnvVarMissingOrBlank()
    {
        Assert.False(ApplicateVirtualizationMode.ReadFromEnvironment(envValue: null));
        Assert.False(ApplicateVirtualizationMode.ReadFromEnvironment(envValue: ""));
        Assert.False(ApplicateVirtualizationMode.ReadFromEnvironment(envValue: "  "));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("on", true)]
    [InlineData("ON", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("off", false)]
    [InlineData("OFF", false)]
    [InlineData("garbage", false)]
    public void IsEnabledParsesExplicitOptInOnly(string envValue, bool expected)
    {
        Assert.Equal(expected, ApplicateVirtualizationMode.ReadFromEnvironment(envValue));
    }
}
