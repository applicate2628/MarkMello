using MarkMello.Applicate.Desktop.Activation;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateSingleInstanceServiceTests : IDisposable
{
    [Fact]
    public void ProgramStartsListeningBeforeBuildingServices()
    {
        // TryCreatePrimary takes the mutex, and from that instant a second launch resolves this
        // process as the primary and forwards its activation. If the pipe only comes up after
        // ConfigureServices, anything launched in that window hits the forwarder's timeout and the
        // activation is silently LOST. Starting early is safe: the service queues requests and
        // flushes them to the first ActivationRequested subscriber.
        var programSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "MarkMello.Applicate.Desktop", "Program.cs"));

        var startListening = programSource.IndexOf("singleInstance!.StartListening();", StringComparison.Ordinal);
        var configureServices = programSource.IndexOf("ConfigureServices(metrics, args, singleInstance)", StringComparison.Ordinal);

        Assert.True(startListening >= 0, "StartListening call not found in Program.cs");
        Assert.True(configureServices >= 0, "ConfigureServices call not found in Program.cs");
        Assert.True(
            startListening < configureServices,
            "StartListening must precede ConfigureServices or early activations are dropped.");
    }

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
        Assert.True(ApplicateActivationArguments.TryParsePayload(forwarder.Payload, out var request));
        Assert.False(request.ShutdownRequested);
        Assert.Equal([Path.GetFullPath(path)], request.FilePaths);
    }

    [Fact]
    public void ForwardActivationSendsShutdownRequestWithoutOpeningPaths()
    {
        var calls = new List<string>();
        var foreground = new RecordingForegroundActivationPermission(calls);
        var forwarder = new RecordingActivationForwarder(calls);
        var path = WriteTemp("open.md", "# Open");

        var forwarded = ApplicateSingleInstanceService.ForwardActivation(
            ["--shutdown", path],
            forwarder,
            foreground);

        Assert.True(forwarded);
        Assert.Equal(["foreground", "forward"], calls);
        Assert.True(ApplicateActivationArguments.TryParsePayload(forwarder.Payload, out var request));
        Assert.True(request.ShutdownRequested);
        Assert.Empty(request.FilePaths);
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
