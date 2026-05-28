using System;
using System.Collections.Generic;

namespace MarkMello.Applicate.Desktop.Rendering;

internal sealed class ApplicateModeTransactionHostRouter : IApplicateModeTransactionHost, IDisposable
{
    private readonly IApplicateSharedWebViewHost _viewerHost;
    private readonly IApplicateSharedWebViewHost _editPreviewHost;
    private readonly Dictionary<long, IApplicateSharedWebViewHost> _hostsByGeneration = new();
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

    public bool RevealNativeWebViewForCommittedTransaction(long transactionGeneration)
    {
        if (!_hostsByGeneration.TryGetValue(transactionGeneration, out var host))
        {
            return false;
        }

        var revealed = host.RevealNativeWebViewForCommittedTransaction(transactionGeneration);
        if (revealed)
        {
            _hostsByGeneration.Remove(transactionGeneration);
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

        _hostsByGeneration.Clear();
    }

    private void Wire(IApplicateSharedWebViewHost host)
    {
        host.RendererFailed += OnRendererFailed;
        host.MinimapSettled += OnMinimapSettled;
        host.CommitCompleted += OnCommitCompleted;
        host.RendererSettled += OnRendererSettled;
    }

    private void Unwire(IApplicateSharedWebViewHost host)
    {
        host.RendererFailed -= OnRendererFailed;
        host.MinimapSettled -= OnMinimapSettled;
        host.CommitCompleted -= OnCommitCompleted;
        host.RendererSettled -= OnRendererSettled;
    }

    private void OnRendererFailed(object? sender, ApplicateRendererFailureEvent e)
        => RendererFailed?.Invoke(this, e);

    private void OnMinimapSettled(object? sender, ApplicateMinimapSettledEventArgs e)
        => MinimapSettled?.Invoke(this, e);

    private void OnCommitCompleted(object? sender, ApplicateCommitCompletedEventArgs e)
    {
        if (sender is IApplicateSharedWebViewHost host && e.TransactionGeneration > 0)
        {
            _hostsByGeneration[e.TransactionGeneration] = host;
        }

        CommitCompleted?.Invoke(this, e);
    }

    private void OnRendererSettled(object? sender, ApplicateRendererSettledEventArgs e)
        => RendererSettled?.Invoke(this, e);
}
