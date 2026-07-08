using System;
using System.Collections.Generic;
using Avalonia.Controls;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Domain;
using MarkMello.Presentation.ViewModels;

namespace MarkMello.Applicate.Desktop.Rendering;

internal sealed partial class ApplicateAirspaceCompositor
{
    public IDisposable RegisterStartupSession(
        Window window,
        IApplicateSharedWebViewHost? host,
        MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        return RegisterStartupSession(
            new WindowStartupRevealShell(window),
            host is null ? null : new SharedWebViewStartupRevealSignals(host),
            new MainWindowStartupRevealState(viewModel));
    }

    public IDisposable RegisterDocumentSession(
        IApplicateSharedWebViewHost host,
        ApplicateMode mode,
        Func<bool> isActiveSurface,
        bool clearHeadingsOnRendererFailure = true,
        bool skipInitialCoverSession = false,
        bool suppressSamePathReloadCover = false)
    {
        ArgumentNullException.ThrowIfNull(host);

        return RegisterDocumentSession(
            new SharedWebViewDocumentRevealSignals(host),
            mode,
            isActiveSurface,
            clearHeadingsOnRendererFailure,
            skipInitialCoverSession,
            suppressSamePathReloadCover);
    }

    public IDisposable RegisterThemeSession(
        IApplicateSharedWebViewHost host,
        Func<bool> isActiveSurface)
    {
        ArgumentNullException.ThrowIfNull(host);

        return RegisterThemeSession(
            new SharedWebViewThemeRevealSignals(host),
            isActiveSurface);
    }

    private sealed class SharedWebViewDocumentRevealSignals(IApplicateSharedWebViewHost host)
        : IApplicateDocumentRevealSignals
    {
        private readonly IApplicateSharedWebViewHost _host = host ?? throw new ArgumentNullException(nameof(host));

        public event EventHandler<ApplicateCommitCompletedEventArgs>? CommitCompleted
        {
            add => _host.CommitCompleted += value;
            remove => _host.CommitCompleted -= value;
        }

        public event EventHandler<ApplicateRendererFailureEvent>? RendererFailed
        {
            add => _host.RendererFailed += value;
            remove => _host.RendererFailed -= value;
        }

        public event EventHandler? DocumentRevealReady
        {
            add => _host.View.DocumentRevealReady += value;
            remove => _host.View.DocumentRevealReady -= value;
        }
    }

    private sealed class SharedWebViewStartupRevealSignals(IApplicateSharedWebViewHost host)
        : IApplicateStartupRevealSignals
    {
        private readonly IApplicateSharedWebViewHost _host = host ?? throw new ArgumentNullException(nameof(host));

        public TimeSpan RendererSettleFallbackTimeout => ApplicateSharedWebViewHost.RendererSettleFallbackTimeout;

        public event EventHandler? DocumentRevealReady
        {
            add => _host.View.DocumentRevealReady += value;
            remove => _host.View.DocumentRevealReady -= value;
        }

        public event EventHandler<IReadOnlyList<DocumentHeading>>? HeadingsChanged
        {
            add => _host.View.HeadingsChanged += value;
            remove => _host.View.HeadingsChanged -= value;
        }

        public event EventHandler<ApplicateRendererFailureEvent>? RendererFailed
        {
            add => _host.RendererFailed += value;
            remove => _host.RendererFailed -= value;
        }

        public event EventHandler? RendererSettled
        {
            add => _host.View.ModeToggleSettled += value;
            remove => _host.View.ModeToggleSettled -= value;
        }

        public bool ShouldSkipRendererFrameWait(MarkdownSource? source, long transactionGeneration)
            => ApplicateSharedWebViewHost.ShouldSkipRendererFrameWait(source, transactionGeneration);

        public void RequestRendererSettleProbe()
            => _host.View.RequestModeToggleSettleProbe();
    }

    private sealed class SharedWebViewThemeRevealSignals(IApplicateSharedWebViewHost host)
        : IApplicateThemeRevealSignals
    {
        private readonly IApplicateSharedWebViewHost _host = host ?? throw new ArgumentNullException(nameof(host));

        public event EventHandler<ApplicateRendererFailureEvent>? RendererFailed
        {
            add => _host.RendererFailed += value;
            remove => _host.RendererFailed -= value;
        }

        public event EventHandler<ApplicateWebThemeChangeSentEventArgs>? ThemeChangeSent
        {
            add => _host.View.ThemeChangeSent += value;
            remove => _host.View.ThemeChangeSent -= value;
        }

        public event EventHandler<ApplicateWebThemeAppliedEventArgs>? ThemeApplied
        {
            add => _host.View.ThemeApplied += value;
            remove => _host.View.ThemeApplied -= value;
        }

        public bool HasLoadedDocumentForSource(MarkdownSource? source)
            => _host.View.HasLoadedDocumentForSource(source);
    }
}
