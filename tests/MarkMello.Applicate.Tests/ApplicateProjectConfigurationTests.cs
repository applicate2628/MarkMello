using System.Xml.Linq;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateProjectConfigurationTests
{
    [Fact]
    public void ApplicateDebugBuildDoesNotOpenConsoleByDefault()
    {
        var project = LoadApplicateProject();
        var outputTypes = project.Descendants("OutputType")
            .Select(element => new
            {
                Value = element.Value,
                Condition = element.Parent?.Attribute("Condition")?.Value ?? string.Empty
            })
            .ToArray();

        Assert.Contains(outputTypes, entry =>
            entry.Value == "WinExe" &&
            string.IsNullOrEmpty(entry.Condition));
        Assert.DoesNotContain(outputTypes, entry =>
            entry.Value == "Exe" &&
            !entry.Condition.Contains("MarkMelloDebugConsole", StringComparison.Ordinal));
    }

    private static XDocument LoadApplicateProject()
        => XDocument.Load(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "MarkMello.Applicate.Desktop.csproj"));
}
