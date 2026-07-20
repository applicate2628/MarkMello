using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace MarkMello.Applicate.Desktop.Rendering;

internal sealed class ApplicateModeTransactionHostRouter :
    IApplicateModeTransactionHost,
    IApplicateTransactionRendererSettleProbeRequester,
    IDisposable
{
    private readonly IApplicateSharedWebViewHost _viewerHost;
    private readonly IApplicateSharedWebViewHost _editPreviewHost;
    // Keyed by HOST, not by generation: each host tracks at most ONE pending
    // transactional reveal (a scalar it overwrites on every transactional
    // commit) and rejects any generation other than that latest one. Keying by
    // generation instead let superseded generations accumulate for the whole
    // window session — the router outlives every transaction (built once in
    // ApplicateMainWindow.InstallSiblingMountedViews, disposed on Window.Closed),
    // so an entry abandoned by a rolled-back transaction was never removed.
    // Keying by host mirrors the hosts' own contract and is bounded at two.
    private readonly Dictionary<IApplicateSharedWebViewHost, long> _pendingRevealGenerationByHost = new();
    private bool _disposed;

    public ApplicateModeTransactionHostRouter(
        IApplicateSharedWebViewHost viewerHost,
        IApplicateSharedWebViewHost editPreviewHost)
    {
        _viewerHost = viewerHost;
        _editPreviewHost = editPreviewHost;
        Wire(_viewerHost);
        if (!ReferenceEquals(_viewerHost, _editPreviewHost))
        {
            Wire(_editPreviewHost);
        }
    }

    public event EventHandler<ApplicateRendererFailureEvent>? RendererFailed;

    public event EventHandler<ApplicateMinimapSettledEventArgs>? MinimapSettled;

    public event EventHandler<ApplicateCommitCompletedEventArgs>? CommitCompleted;

    public event EventHandler<ApplicateRendererSettledEventArgs>? RendererSettled;

    public event EventHandler<ApplicateTransactionRendererSettleProbeEventArgs>? TransactionRendererSettleProbeReady;

    public bool RevealNativeWebViewForCommittedTransaction(long transactionGeneration)
    {
        if (!TryResolvePendingHost(transactionGeneration, out var host))
        {
            return false;
        }

        var revealed = host.RevealNativeWebViewForCommittedTransaction(transactionGeneration);
        if (revealed)
        {
            _pendingRevealGenerationByHost.Remove(host);
        }

        return revealed;
    }

    public void SuppressNativeRendererForModeSwitch(ApplicateMode displayedMode)
    {
        var host = displayedMode == ApplicateMode.Viewer
            ? _viewerHost
            : _editPreviewHost;
        host.SuppressNativeRendererForModeSwitch(displayedMode);
    }

    public void RestoreNativeRendererAfterModeSwitchSuppression(ApplicateMode displayedMode)
    {
        var host = displayedMode == ApplicateMode.Viewer
            ? _viewerHost
            : _editPreviewHost;
        host.RestoreNativeRendererAfterModeSwitchSuppression(displayedMode);
    }

    public void RequestTransactionRendererSettleProbe(long transactionGeneration, bool skipFrameWait)
    {
        if (TryResolvePendingHost(transactionGeneration, out var host)
            && host is IApplicateTransactionRendererSettleProbeRequester requester)
        {
            requester.RequestTransactionRendererSettleProbe(transactionGeneration, skipFrameWait);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unwire(_viewerHost);
        if (!ReferenceEquals(_viewerHost, _editPreviewHost))
        {
            Unwire(_editPreviewHost);
        }

        _pendingRevealGenerationByHost.Clear();
    }

    private bool TryResolvePendingHost(
        long transactionGeneration,
        [MaybeNullWhen(false)] out IApplicateSharedWebViewHost host)
    {
        if (transactionGeneration > 0)
        {
            // At most two entries by construction, so the scan is cheaper than
            // maintaining a second index and cannot drift out of sync with it.
            foreach (var pending in _pendingRevealGenerationByHost)
            {
                if (pending.Value == transactionGeneration)
                {
                    host = pending.Key;
                    return true;
                }
            }
        }

        host = null;
        return false;
    }

    private void Wire(IApplicateSharedWebViewHost host)
    {
        host.RendererFailed += OnRendererFailed;
        host.MinimapSettled += OnMinimapSettled;
        host.CommitCompleted += OnCommitCompleted;
        host.RendererSettled += OnRendererSettled;
        host.TransactionRendererSettleProbeReady += OnTransactionRendererSettleProbeReady;
    }

    private void Unwire(IApplicateSharedWebViewHost host)
    {
        host.RendererFailed -= OnRendererFailed;
        host.MinimapSettled -= OnMinimapSettled;
        host.CommitCompleted -= OnCommitCompleted;
        host.RendererSettled -= OnRendererSettled;
        host.TransactionRendererSettleProbeReady -= OnTransactionRendererSettleProbeReady;
    }

    private void OnRendererFailed(object? sender, ApplicateRendererFailureEvent e)
        => RendererFailed?.Invoke(this, e);

    private void OnMinimapSettled(object? sender, ApplicateMinimapSettledEventArgs e)
        => MinimapSettled?.Invoke(this, e);

    private void OnCommitCompleted(object? sender, ApplicateCommitCompletedEventArgs e)
    {
        if (sender is IApplicateSharedWebViewHost host && e.TransactionGeneration > 0)
        {
            // Replaces rather than accumulates: this host has just overwritten
            // its own pending-reveal scalar, so every older generation on it is
            // permanently unrevealable and holding it would only ever forward a
            // call the host rejects.
            _pendingRevealGenerationByHost[host] = e.TransactionGeneration;
        }

        CommitCompleted?.Invoke(this, e);
    }

    private void OnRendererSettled(object? sender, ApplicateRendererSettledEventArgs e)
        => RendererSettled?.Invoke(this, e);

    private void OnTransactionRendererSettleProbeReady(
        object? sender,
        ApplicateTransactionRendererSettleProbeEventArgs e)
        => TransactionRendererSettleProbeReady?.Invoke(this, e);
}
