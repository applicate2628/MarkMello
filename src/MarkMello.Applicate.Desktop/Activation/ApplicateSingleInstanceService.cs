using System.IO.Pipes;
using System.Text;

namespace MarkMello.Applicate.Desktop.Activation;

public sealed class ApplicateSingleInstanceService : IDisposable
{
    private const string MutexName = "MarkMello.Applicate.SingleInstance";
    private const string PipeName = "MarkMello.Applicate.SingleInstance";

    private readonly Mutex _mutex;
    private readonly object _gate = new();
    private readonly Queue<IReadOnlyList<string>> _pendingActivations = new();
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

            IReadOnlyList<IReadOnlyList<string>> pending;
            lock (_gate)
            {
                _activationRequested += value;
                pending = _pendingActivations.ToArray();
                _pendingActivations.Clear();
            }

            foreach (var filePaths in pending)
            {
                value(this, new ApplicateActivationRequestedEventArgs(filePaths));
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
    {
        var filePaths = ApplicateActivationArguments.GetSupportedFilePaths(args);
        var payload = ApplicateActivationArguments.CreatePayload(filePaths);

        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            pipe.Connect(timeout: 2500);

            using var writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: false);
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
                if (ApplicateActivationArguments.TryParsePayload(payload, out var filePaths))
                {
                    Dispatch(filePaths);
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

    private void Dispatch(IReadOnlyList<string> filePaths)
    {
        EventHandler<ApplicateActivationRequestedEventArgs>? handler;
        lock (_gate)
        {
            handler = _activationRequested;
            if (handler is null)
            {
                _pendingActivations.Enqueue(filePaths);
                return;
            }
        }

        handler(this, new ApplicateActivationRequestedEventArgs(filePaths));
    }
}
