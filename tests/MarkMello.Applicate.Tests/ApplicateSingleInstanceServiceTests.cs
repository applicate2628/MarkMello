using MarkMello.Applicate.Desktop.Activation;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateSingleInstanceServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public ApplicateSingleInstanceServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "MarkMello.Applicate.Tests.SingleInstance", Guid.NewGuid().ToString("N"));
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
    public void ForwardActivationPermitsPrimaryForegroundBeforeSendingPayload()
    {
        var calls = new List<string>();
        var foreground = new RecordingForegroundActivationPermission(calls);
        var forwarder = new RecordingActivationForwarder(calls);
        var path = WriteTemp("open.md", "# Open");

        var forwarded = ApplicateSingleInstanceService.ForwardActivation(
            [path],
            forwarder,
            foreground);

        Assert.True(forwarded);
        Assert.Equal(["foreground", "forward"], calls);
        Assert.True(ApplicateActivationArguments.TryParsePayload(forwarder.Payload, out var filePaths));
        Assert.Equal([Path.GetFullPath(path)], filePaths);
    }

    private string WriteTemp(string fileName, string contents)
    {
        var path = Path.Combine(_tempRoot, fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    private sealed class RecordingActivationForwarder(List<string> calls) : IApplicateActivationForwarder
    {
        public string Payload { get; private set; } = string.Empty;

        public bool Forward(string payload)
        {
            calls.Add("forward");
            Payload = payload;
            return true;
        }
    }

    private sealed class RecordingForegroundActivationPermission(List<string> calls) : IApplicateForegroundActivationPermission
    {
        public void PermitPrimaryForegroundActivation()
        {
            calls.Add("foreground");
        }
    }
}
