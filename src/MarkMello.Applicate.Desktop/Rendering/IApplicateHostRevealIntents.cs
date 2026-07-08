using System;
using Avalonia.Controls;
using MarkMello.Domain;

namespace MarkMello.Applicate.Desktop.Rendering;

internal interface IApplicateHostRevealIntents
{
    TimeSpan RendererSettleFallbackTimeout { get; }

    event EventHandler<ApplicateHostAttachStartingEventArgs>? AttachStarting;

    event EventHandler<ApplicateHostAttachCompletedEventArgs>? AttachCompleted;

    event EventHandler<ApplicateHostRenderStartingEventArgs>? RenderStarting;

    event EventHandler<ApplicateHostCommitPreparingEventArgs>? CommitPreparing;

    event EventHandler? DocumentRenderVisualReady;

    event EventHandler? RendererRevealSettled;

    event EventHandler<ApplicateTransactionRendererSettleProbeEventArgs>? TransactionRendererSettleProbeReady;

    event EventHandler<ApplicateRendererFailureEvent>? RendererFailed;

    event EventHandler<ApplicateMinimapSettledEventArgs>? MinimapSettled;

    event EventHandler<ApplicateCommitCompletedEventArgs>? CommitCompleted;

    event EventHandler<ApplicateRendererSettledEventArgs>? RendererSettled;

    void SuppressOutgoingNativeRenderer(ApplicateMode displayedMode);

    void RestoreOutgoingNativeRenderer(ApplicateMode displayedMode);

    bool RevealNativeRendererForCommittedTransaction(long transactionGeneration);

    void ParkNativeWebViewForReparent();

    void SetNativeWebViewVisibility(bool isVisible);

    void PrepareNativeWebViewHiddenPaint();

    void CompleteNativeWebViewHiddenPaint();

    void PrepareModeRendererReveal(TimeSpan duration);

    void StartModeRendererReveal(TimeSpan duration);

    void PrepareDocumentRendererReveal(TimeSpan duration);

    void StartDocumentRendererReveal(TimeSpan duration);

    void RequestRendererSettleProbe();

    void RequestTransactionRendererSettleProbe(long transactionGeneration, bool skipFrameWait);
}

internal sealed class SharedWebViewHostRevealIntents(IApplicateModeTransactionHost host)
    : IApplicateHostRevealIntents
{
    private readonly IApplicateModeTransactionHost _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly IApplicateHostRevealEndpoint? _endpoint = host as IApplicateHostRevealEndpoint;

    public TimeSpan RendererSettleFallbackTimeout =>
        _endpoint?.RendererSettleFallbackTimeout ?? ApplicateAirspaceCompositor.HostRendererSettleFallbackTimeout;

    public event EventHandler<ApplicateHostAttachStartingEventArgs>? AttachStarting
    {
        add
        {
            if (_endpoint is not null)
            {
                _endpoint.AttachStarting += value;
            }
        }
        remove
        {
            if (_endpoint is not null)
            {
                _endpoint.AttachStarting -= value;
            }
        }
    }

    public event EventHandler<ApplicateHostAttachCompletedEventArgs>? AttachCompleted
    {
        add
        {
            if (_endpoint is not null)
            {
                _endpoint.AttachCompleted += value;
            }
        }
        remove
        {
            if (_endpoint is not null)
            {
                _endpoint.AttachCompleted -= value;
            }
        }
    }

    public event EventHandler<ApplicateHostRenderStartingEventArgs>? RenderStarting
    {
        add
        {
            if (_endpoint is not null)
            {
                _endpoint.RenderStarting += value;
            }
        }
        remove
        {
            if (_endpoint is not null)
            {
                _endpoint.RenderStarting -= value;
            }
        }
    }

    public event EventHandler<ApplicateHostCommitPreparingEventArgs>? CommitPreparing
    {
        add
        {
            if (_endpoint is not null)
            {
                _endpoint.CommitPreparing += value;
            }
        }
        remove
        {
            if (_endpoint is not null)
            {
                _endpoint.CommitPreparing -= value;
            }
        }
    }

    public event EventHandler? DocumentRenderVisualReady
    {
        add
        {
            if (_endpoint is not null)
            {
                _endpoint.DocumentRenderVisualReady += value;
            }
        }
        remove
        {
            if (_endpoint is not null)
            {
                _endpoint.DocumentRenderVisualReady -= value;
            }
        }
    }

    public event EventHandler? RendererRevealSettled
    {
        add
        {
            if (_endpoint is not null)
            {
                _endpoint.RendererRevealSettled += value;
            }
        }
        remove
        {
            if (_endpoint is not null)
            {
                _endpoint.RendererRevealSettled -= value;
            }
        }
    }

    public event EventHandler<ApplicateTransactionRendererSettleProbeEventArgs>? TransactionRendererSettleProbeReady
    {
        add => _host.TransactionRendererSettleProbeReady += value;
        remove => _host.TransactionRendererSettleProbeReady -= value;
    }

    public event EventHandler<ApplicateRendererFailureEvent>? RendererFailed
    {
        add => _host.RendererFailed += value;
        remove => _host.RendererFailed -= value;
    }

    public event EventHandler<ApplicateMinimapSettledEventArgs>? MinimapSettled
    {
        add => _host.MinimapSettled += value;
        remove => _host.MinimapSettled -= value;
    }

    public event EventHandler<ApplicateCommitCompletedEventArgs>? CommitCompleted
    {
        add => _host.CommitCompleted += value;
        remove => _host.CommitCompleted -= value;
    }

    public event EventHandler<ApplicateRendererSettledEventArgs>? RendererSettled
    {
        add => _host.RendererSettled += value;
        remove => _host.RendererSettled -= value;
    }

    public void SuppressOutgoingNativeRenderer(ApplicateMode displayedMode)
        => _host.SuppressNativeRendererForModeSwitch(displayedMode);

    public void RestoreOutgoingNativeRenderer(ApplicateMode displayedMode)
        => _host.RestoreNativeRendererAfterModeSwitchSuppression(displayedMode);

    public bool RevealNativeRendererForCommittedTransaction(long transactionGeneration)
        => _host.RevealNativeWebViewForCommittedTransaction(transactionGeneration);

    public void ParkNativeWebViewForReparent()
        => RequireEndpoint().ParkNativeWebViewForReparent();

    public void SetNativeWebViewVisibility(bool isVisible)
        => RequireEndpoint().SetNativeWebViewVisibility(isVisible);

    public void PrepareNativeWebViewHiddenPaint()
        => RequireEndpoint().PrepareNativeWebViewHiddenPaint();

    public void CompleteNativeWebViewHiddenPaint()
        => RequireEndpoint().CompleteNativeWebViewHiddenPaint();

    public void PrepareModeRendererReveal(TimeSpan duration)
        => RequireEndpoint().PostRendererRevealMessage(new
        {
            type = "mode-reveal-prepare",
            durationMs = ToRendererDurationMs(duration)
        });

    public void StartModeRendererReveal(TimeSpan duration)
        => RequireEndpoint().PostRendererRevealMessage(new
        {
            type = "mode-reveal-start",
            durationMs = ToRendererDurationMs(duration)
        });

    public void PrepareDocumentRendererReveal(TimeSpan duration)
        => RequireEndpoint().PostRendererRevealMessage(new
        {
            type = "document-reveal-prepare",
            durationMs = ToRendererDurationMs(duration),
            theme = RequireEndpoint().RendererThemeName
        });

    public void StartDocumentRendererReveal(TimeSpan duration)
        => RequireEndpoint().PostRendererRevealMessage(new
        {
            type = "document-reveal-start",
            durationMs = ToRendererDurationMs(duration)
        });

    public void RequestRendererSettleProbe()
        => RequireEndpoint().RequestRendererSettleProbe();

    public void RequestTransactionRendererSettleProbe(long transactionGeneration, bool skipFrameWait)
    {
        if (_host is IApplicateTransactionRendererSettleProbeRequester requester)
        {
            requester.RequestTransactionRendererSettleProbe(transactionGeneration, skipFrameWait);
            return;
        }

        RequireEndpoint().RequestTransactionRendererSettleProbe(transactionGeneration, skipFrameWait);
    }

    private IApplicateHostRevealEndpoint RequireEndpoint()
        => _endpoint
           ?? throw new InvalidOperationException(
               "Host reveal primitives require an individual shared WebView host endpoint.");

    private static int ToRendererDurationMs(TimeSpan duration)
        => (int)global::System.Math.Clamp(
            global::System.Math.Round(duration.TotalMilliseconds, MidpointRounding.AwayFromZero),
            ReadingPreferences.MinModeSwitchSmoothDurationMs,
            ReadingPreferences.MaxModeSwitchSmoothDurationMs);
}

internal interface IApplicateTransactionRendererSettleProbeRequester
{
    void RequestTransactionRendererSettleProbe(long transactionGeneration, bool skipFrameWait);
}

internal interface IApplicateHostRevealEndpoint
{
    TimeSpan RendererSettleFallbackTimeout { get; }

    string RendererThemeName { get; }

    event EventHandler<ApplicateHostAttachStartingEventArgs>? AttachStarting;

    event EventHandler<ApplicateHostAttachCompletedEventArgs>? AttachCompleted;

    event EventHandler<ApplicateHostRenderStartingEventArgs>? RenderStarting;

    event EventHandler<ApplicateHostCommitPreparingEventArgs>? CommitPreparing;

    event EventHandler? DocumentRenderVisualReady;

    event EventHandler? RendererRevealSettled;

    void ParkNativeWebViewForReparent();

    void SetNativeWebViewVisibility(bool isVisible);

    void PrepareNativeWebViewHiddenPaint();

    void CompleteNativeWebViewHiddenPaint();

    void PostRendererRevealMessage(object message);

    void RequestRendererSettleProbe();

    void RequestTransactionRendererSettleProbe(long transactionGeneration, bool skipFrameWait);
}

internal sealed class ApplicateHostAttachStartingEventArgs(
    Panel target,
    bool isTransactional) : EventArgs
{
    public Panel Target { get; } = target;

    public bool IsTransactional { get; } = isTransactional;
}

internal sealed class ApplicateHostAttachCompletedEventArgs(
    Panel target,
    bool isTransactional,
    bool hasEverCommitted) : EventArgs
{
    public Panel Target { get; } = target;

    public bool IsTransactional { get; } = isTransactional;

    public bool HasEverCommitted { get; } = hasEverCommitted;
}

internal sealed class ApplicateHostRenderStartingEventArgs(
    Panel? currentParent,
    Panel? warmupParent,
    MarkdownSource? currentSource,
    MarkdownSource? nextSource,
    bool hasLoadedDocument,
    bool isTransactional,
    bool keepColdParentVisibleForInactivePrime,
    bool hasEverCommitted) : EventArgs
{
    public Panel? CurrentParent { get; } = currentParent;

    public Panel? WarmupParent { get; } = warmupParent;

    public MarkdownSource? CurrentSource { get; } = currentSource;

    public MarkdownSource? NextSource { get; } = nextSource;

    public bool HasLoadedDocument { get; } = hasLoadedDocument;

    public bool IsTransactional { get; } = isTransactional;

    public bool KeepColdParentVisibleForInactivePrime { get; } = keepColdParentVisibleForInactivePrime;

    public bool HasEverCommitted { get; } = hasEverCommitted;
}

internal sealed class ApplicateHostCommitPreparingEventArgs(
    Panel? currentParent,
    Panel? warmupParent,
    bool isTransactional,
    long transactionGeneration,
    bool hasEverCommitted,
    TimeSpan revealDuration) : EventArgs
{
    public Panel? CurrentParent { get; } = currentParent;

    public Panel? WarmupParent { get; } = warmupParent;

    public bool IsTransactional { get; } = isTransactional;

    public long TransactionGeneration { get; } = transactionGeneration;

    public bool HasEverCommitted { get; } = hasEverCommitted;

    public TimeSpan RevealDuration { get; } = revealDuration;
}
