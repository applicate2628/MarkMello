using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace MarkMello.Applicate.Desktop.Activation;

public sealed class ApplicateSingleInstanceService : IDisposable
{
    private const string MutexName = "MarkMello.Applicate.SingleInstance";
    private const string PipeName = "MarkMello.Applicate.SingleInstance";

    private readonly Mutex _mutex;
    private readonly object _gate = new();
    private readonly Queue<ApplicateActivationRequest> _pendingActivations = new();
    private CancellationTokenSource? _listenCancellation;
    private Task? _listenTask;
    private EventHandler<ApplicateActivationRequestedEventArgs>? _activationRequested;

    private ApplicateSingleInstanceService(Mutex mutex)
    {
        _mutex = mutex;
    }

    public event EventHandler<ApplicateActivationRequestedEventArgs>? ActivationRequested
    {
        add
        {
            if (value is null)
            {
                return;
            }

            IReadOnlyList<ApplicateActivationRequest> pending;
            lock (_gate)
            {
                _activationRequested += value;
                pending = _pendingActivations.ToArray();
                _pendingActivations.Clear();
            }

            foreach (var request in pending)
            {
                value(this, CreateEventArgs(request));
            }
        }
        remove
        {
            lock (_gate)
            {
                _activationRequested -= value;
            }
        }
    }

    public static bool TryCreatePrimary(out ApplicateSingleInstanceService? service)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            service = null;
            return false;
        }

        service = new ApplicateSingleInstanceService(mutex);
        return true;
    }

    public static bool ForwardActivation(string[] args)
        => ForwardActivation(
            args,
            new NamedPipeApplicateActivationForwarder(PipeName),
            ApplicateForegroundActivationPermission.Instance);

    internal static bool ForwardActivation(
        string[] args,
        IApplicateActivationForwarder forwarder,
        IApplicateForegroundActivationPermission foregroundPermission)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(forwarder);
        ArgumentNullException.ThrowIfNull(foregroundPermission);

        var request = ApplicateActivationArguments.CreateRequest(args);
        var payload = ApplicateActivationArguments.CreatePayload(request);

        foregroundPermission.PermitPrimaryForegroundActivation();
        return forwarder.Forward(payload);
    }

    public void StartListening()
    {
        if (_listenTask is not null)
        {
            return;
        }

        _listenCancellation = new CancellationTokenSource();
        _listenTask = Task.Run(() => ListenLoopAsync(_listenCancellation.Token));
    }

    public void Dispose()
    {
        _listenCancellation?.Cancel();
        try
        {
            _listenTask?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (AggregateException)
        {
            // Shutdown path only; a canceled pipe wait should not affect app exit.
        }
        finally
        {
            _listenCancellation?.Dispose();
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var payload = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                if (ApplicateActivationArguments.TryParsePayload(payload, out var request))
                {
                    Dispatch(request);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                // The sender may exit mid-write. Keep the primary listener alive.
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void Dispatch(ApplicateActivationRequest request)
    {
        EventHandler<ApplicateActivationRequestedEventArgs>? handler;
        lock (_gate)
        {
            handler = _activationRequested;
            if (handler is null)
            {
                _pendingActivations.Enqueue(request);
                return;
            }
        }

        handler(this, CreateEventArgs(request));
    }

    private static ApplicateActivationRequestedEventArgs CreateEventArgs(ApplicateActivationRequest request)
        => new(request.FilePaths, request.ShutdownRequested);

    private sealed class NamedPipeApplicateActivationForwarder(string pipeName) : IApplicateActivationForwarder
    {
        public bool Forward(string payload)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);
                pipe.Connect(timeout: 2500);

                using var writer = new StreamWriter(
                    pipe,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    leaveOpen: false);
                writer.Write(payload);
                writer.Flush();
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}

internal interface IApplicateActivationForwarder
{
    bool Forward(string payload);
}

internal interface IApplicateForegroundActivationPermission
{
    void PermitPrimaryForegroundActivation();
}

internal sealed class ApplicateForegroundActivationPermission : IApplicateForegroundActivationPermission
{
    public static readonly ApplicateForegroundActivationPermission Instance = new();

    private ApplicateForegroundActivationPermission()
    {
    }

    public void PermitPrimaryForegroundActivation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _ = NativeMethods.AllowSetForegroundWindow(NativeMethods.AsfwAny);
    }

    private static class NativeMethods
    {
        public const int AsfwAny = -1;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AllowSetForegroundWindow(int processId);
    }
}
