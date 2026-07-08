using System;

namespace MarkMello.Applicate.Desktop.Rendering;

internal interface IApplicateHostRevealIntents
{
    event EventHandler<ApplicateRendererFailureEvent>? RendererFailed;

    event EventHandler<ApplicateMinimapSettledEventArgs>? MinimapSettled;

    event EventHandler<ApplicateCommitCompletedEventArgs>? CommitCompleted;

    event EventHandler<ApplicateRendererSettledEventArgs>? RendererSettled;

    void SuppressOutgoingNativeRenderer(ApplicateMode displayedMode);

    void RestoreOutgoingNativeRenderer(ApplicateMode displayedMode);

    bool RevealNativeRendererForCommittedTransaction(long transactionGeneration);
}

internal sealed class SharedWebViewHostRevealIntents(IApplicateModeTransactionHost host)
    : IApplicateHostRevealIntents
{
    private readonly IApplicateModeTransactionHost _host = host ?? throw new ArgumentNullException(nameof(host));

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
}
