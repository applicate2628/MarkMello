using MarkMello.Applicate.Desktop.Activation;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateActivationArgumentsTests : IDisposable
{
    private readonly string _tempRoot;

    public ApplicateActivationArgumentsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "MarkMello.Applicate.Tests.Activation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GetSupportedFilePathsReturnsAllExistingMarkdownArguments()
    {
        var first = WriteTemp("first.md", "# First");
        var second = WriteTemp("second.txt", "Second");
        var ignored = WriteTemp("ignored.png", "not markdown");

        var paths = ApplicateActivationArguments.GetSupportedFilePaths(
            ["--flag", first, ignored, second, Path.Combine(_tempRoot, "missing.md")]);

        Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(second)], paths);
    }

    [Fact]
    public void PayloadRoundTripsSupportedFilePaths()
    {
        var first = WriteTemp("first.md", "# First");
        var second = WriteTemp("second.markdown", "# Second");
        var paths = new[] { Path.GetFullPath(first), Path.GetFullPath(second) };

        var payload = ApplicateActivationArguments.CreatePayload(paths);
        var parsed = ApplicateActivationArguments.TryParsePayload(payload, out var parsedPaths);

        Assert.True(parsed);
        Assert.Equal(paths, parsedPaths);
    }

    private string WriteTemp(string fileName, string contents)
    {
        var path = Path.Combine(_tempRoot, fileName);
        File.WriteAllText(path, contents);
        return path;
    }
}
