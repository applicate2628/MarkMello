using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Domain;
using MarkMello.Presentation.ViewModels;
using SysMath = System.Math;

namespace MarkMello.Applicate.Desktop.Views;

internal sealed class ApplicateEditPreviewView : UserControl, IDisposable
{
    private static readonly Thickness PreviewDocumentPadding = new(72, 96, 72, 160);
    private static readonly TimeSpan WebPreviewDebounce = TimeSpan.FromMilliseconds(180);

    private readonly IApplicateHtmlMarkdownRenderer? _htmlRenderer;
    private readonly Grid _root = new() { UseLayoutRounding = true };
    private readonly ApplicateMarkdownDocumentView _nativePreview;
    private readonly DispatcherTimer _webRenderTimer;
    private ApplicateWebMarkdownDocumentView? _webPreview;
    private EditorSessionViewModel? _session;
    private ScrollViewer? _hostScrollViewer;
    private ScrollBarVisibility? _hostScrollViewerVerticalMode;
    private MarkdownRendererBackend _lastRequestedRendererBackend = MarkdownRendererBackend.Native;
    private ApplicateRendererSurfaceKind _activeRendererSurface = ApplicateRendererSurfaceKind.Native;
    private ApplicateRendererSurfaceKind? _pendingRendererSurface;
    private bool _pendingRendererReady;
    private long _rendererSwitchGeneration;
    private bool _webPreviewFailed;

    public ApplicateEditPreviewView(IApplicateHtmlMarkdownRenderer? htmlRenderer)
    {
        _htmlRenderer = htmlRenderer;
        _nativePreview = new ApplicateMarkdownDocumentView
        {
            DocumentPadding = PreviewDocumentPadding,
            UseLayoutRounding = true
        };
        ApplicateRendererSurfaceTransition.EnsureOpacityTransition(_nativePreview);

        _root.Children.Add(_nativePreview);
        Content = _root;
        UseLayoutRounding = true;

        _webRenderTimer = new DispatcherTimer { Interval = WebPreviewDebounce };
        _webRenderTimer.Tick += OnWebRenderTimerTick;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        AttachSession(DataContext as EditorSessionViewModel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachSession(DataContext as EditorSessionViewModel);
        UpdateHostScrollMode();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyAvailableWidth();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        RestoreHostScrollMode();
        AttachSession(null);
        _webRenderTimer.Stop();
        DisposeWebPreview();

        base.OnDetachedFromVisualTree(e);
    }

    private void AttachSession(EditorSessionViewModel? session)
    {
        if (ReferenceEquals(_session, session))
        {
            return;
        }

        if (_session is not null)
        {
            _session.PropertyChanged -= OnSessionPropertyChanged;
        }

        _session = session;
        _webPreviewFailed = false;
        _lastRequestedRendererBackend = _session?.ReadingPreferences.RendererBackend ?? MarkdownRendererBackend.Native;

        if (_session is not null)
        {
            _session.PropertyChanged += OnSessionPropertyChanged;
        }

        ApplySession();
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorSessionViewModel.ReadingPreferences))
        {
            var requestedRendererBackend = _session?.ReadingPreferences.RendererBackend ?? MarkdownRendererBackend.Native;
            if (_lastRequestedRendererBackend != requestedRendererBackend)
            {
                _lastRequestedRendererBackend = requestedRendererBackend;
                if (requestedRendererBackend == MarkdownRendererBackend.WebView)
                {
                    _webPreviewFailed = false;
                }
            }

            ApplySession();
            return;
        }

        if (e.PropertyName is nameof(EditorSessionViewModel.RenderedPreview))
        {
            ApplyNativePreview();
            return;
        }

        if (e.PropertyName is nameof(EditorSessionViewModel.SourceText)
            or nameof(EditorSessionViewModel.CurrentPath)
            or nameof(EditorSessionViewModel.FileName))
        {
            QueueWebPreviewRender(immediate: false);
        }
    }

    private void ApplySession()
    {
        ApplyNativePreview();
        ApplyRendererMode();
        ApplyAvailableWidth();
    }

    private void ApplyNativePreview()
    {
        if (_session is null)
        {
            _nativePreview.Document = RenderedMarkdownDocument.Empty;
            _nativePreview.ImageSourceResolver = null;
            _nativePreview.ReadingPreferences = ReadingPreferences.Default;
            return;
        }

        _nativePreview.Document = _session.RenderedPreview;
        _nativePreview.ImageSourceResolver = _session.ImageSourceResolver;
        _nativePreview.ReadingPreferences = _session.ReadingPreferences;
    }

    private void ApplyRendererMode()
    {
        var requestedSurface = ResolveRequestedPreviewSurface();

        if (requestedSurface == ApplicateRendererSurfaceKind.WebView)
        {
            var webPreview = EnsureWebPreview();
            if (webPreview is null)
            {
                requestedSurface = ApplicateRendererSurfaceKind.Native;
            }
        }

        StagePreviewSurface(requestedSurface);
        if (requestedSurface == ApplicateRendererSurfaceKind.WebView)
        {
            QueueWebPreviewRender(immediate: true);
        }

        UpdateHostScrollMode();
    }

    private ApplicateWebMarkdownDocumentView? EnsureWebPreview()
    {
        if (_webPreview is not null)
        {
            return _webPreview;
        }

        if (_htmlRenderer is null)
        {
            return null;
        }

        var webPreview = new ApplicateWebMarkdownDocumentView(_htmlRenderer)
        {
            IsVisible = false,
            MinHeight = 1,
            UseLayoutRounding = true,
            ViewerChromeEnabled = false
        };
        ApplicateRendererSurfaceTransition.EnsureOpacityTransition(webPreview);
        webPreview.DocumentRendered += OnWebPreviewDocumentRendered;
        webPreview.FallbackRequested += OnWebPreviewFallbackRequested;
        webPreview.ViewerInteractionRequested += OnWebPreviewViewerInteractionRequested;
        _webPreview = webPreview;
        _root.Children.Add(webPreview);
        return webPreview;
    }

    private void OnWebPreviewDocumentRendered(object? sender, EventArgs e)
    {
        if (_pendingRendererSurface == ApplicateRendererSurfaceKind.WebView)
        {
            CommitPendingPreviewSurface(ApplicateRendererSurfaceKind.WebView);
        }
    }

    private void OnWebPreviewFallbackRequested(object? sender, EventArgs e)
    {
        _webPreviewFailed = true;
        if (_pendingRendererSurface == ApplicateRendererSurfaceKind.WebView)
        {
            CancelPendingPreviewSurface();
        }

        if (_activeRendererSurface == ApplicateRendererSurfaceKind.WebView)
        {
            StagePreviewSurface(ApplicateRendererSurfaceKind.Native);
        }

        ApplyRendererMode();
    }

    private void OnWebPreviewViewerInteractionRequested(object? sender, EventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel { HasOpenOverlay: true } viewModel)
        {
            viewModel.CloseOverlayCommand.Execute(null);
        }
    }

    private bool ShouldRequestWebPreview()
        => _session?.ReadingPreferences.RendererBackend == MarkdownRendererBackend.WebView
           && _htmlRenderer is not null
           && !_webPreviewFailed;

    private bool IsWebPreviewVisibleOrTargeted()
        => _activeRendererSurface == ApplicateRendererSurfaceKind.WebView
           || _pendingRendererSurface == ApplicateRendererSurfaceKind.WebView;

    private ApplicateRendererSurfaceKind ResolveRequestedPreviewSurface()
        => ShouldRequestWebPreview()
            ? ApplicateRendererSurfaceKind.WebView
            : ApplicateRendererSurfaceKind.Native;

    private void QueueWebPreviewRender(bool immediate)
    {
        if (!ShouldRequestWebPreview() || _webPreview is null)
        {
            _webRenderTimer.Stop();
            return;
        }

        if (immediate)
        {
            _webRenderTimer.Stop();
            ApplyWebPreviewSource();
            return;
        }

        _webRenderTimer.Stop();
        _webRenderTimer.Start();
    }

    private void OnWebRenderTimerTick(object? sender, EventArgs e)
    {
        _webRenderTimer.Stop();
        ApplyWebPreviewSource();
    }

    private void ApplyWebPreviewSource()
    {
        if (_session is null || _webPreview is null || !ShouldRequestWebPreview())
        {
            return;
        }

        var source = new MarkdownSource(
            _session.CurrentPath ?? string.Empty,
            _session.FileName,
            _session.SourceText);
        var webAlreadyRenderedCurrentDocument = _webPreview.HasLoadedDocumentForSource(source);
        var widths = CalculatePreviewWidths(Bounds.Width, _session.ReadingPreferences, PreviewDocumentPadding);

        _webPreview.UpdateInputs(
            source,
            CreateWebPreviewPreferences(_session.ReadingPreferences),
            _session.ImageSourceResolver,
            widths.WebColumnWidth,
            viewerChromeEnabled: false);
        if (webAlreadyRenderedCurrentDocument)
        {
            CommitPendingPreviewSurface(ApplicateRendererSurfaceKind.WebView);
        }
    }

    private void StagePreviewSurface(ApplicateRendererSurfaceKind requestedSurface)
    {
        if (_pendingRendererSurface == requestedSurface)
        {
            ApplyPreviewSurfaceVisuals();
            return;
        }

        if (_pendingRendererSurface is not null && requestedSurface == _activeRendererSurface)
        {
            CancelPendingPreviewSurface();
            return;
        }

        if (_activeRendererSurface == requestedSurface)
        {
            _pendingRendererSurface = null;
            _pendingRendererReady = false;
            ApplyPreviewSurfaceVisuals();
            return;
        }

        _pendingRendererSurface = requestedSurface;
        _pendingRendererReady = requestedSurface == ApplicateRendererSurfaceKind.Native;
        _rendererSwitchGeneration++;
        ApplyPreviewSurfaceVisuals();

        if (_pendingRendererReady)
        {
            CommitPendingPreviewSurface(requestedSurface);
        }
    }

    private void CommitPendingPreviewSurface(ApplicateRendererSurfaceKind surface)
    {
        if (_pendingRendererSurface != surface)
        {
            return;
        }

        _pendingRendererReady = true;
        var generation = ++_rendererSwitchGeneration;
        ApplyPreviewSurfaceVisuals();
        UpdateHostScrollMode();
        _ = CompletePreviewSwitchAfterDelayAsync(surface, generation);
    }

    private async Task CompletePreviewSwitchAfterDelayAsync(
        ApplicateRendererSurfaceKind surface,
        long generation)
    {
        await Task.Delay(ApplicateRendererSurfaceTransition.FadeDuration).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _rendererSwitchGeneration || _pendingRendererSurface != surface || !_pendingRendererReady)
            {
                return;
            }

            _activeRendererSurface = surface;
            _pendingRendererSurface = null;
            _pendingRendererReady = false;
            ApplyPreviewSurfaceVisuals();
            UpdateHostScrollMode();
        });
    }

    private void CancelPendingPreviewSurface()
    {
        _pendingRendererSurface = null;
        _pendingRendererReady = false;
        _rendererSwitchGeneration++;
        ApplyPreviewSurfaceVisuals();
        UpdateHostScrollMode();
    }

    private void ApplyPreviewSurfaceVisuals()
    {
        ApplicateRendererSurfaceTransition.ApplyVisualState(
            _nativePreview,
            ApplicateRendererSurfaceTransition.CalculateVisualState(
                ApplicateRendererSurfaceKind.Native,
                _activeRendererSurface,
                _pendingRendererSurface,
                _pendingRendererReady));

        if (_webPreview is not null)
        {
            ApplicateRendererSurfaceTransition.ApplyVisualState(
                _webPreview,
                ApplicateRendererSurfaceTransition.CalculateVisualState(
                    ApplicateRendererSurfaceKind.WebView,
                    _activeRendererSurface,
                    _pendingRendererSurface,
                    _pendingRendererReady));
        }
    }

    private void ApplyAvailableWidth()
    {
        var preferences = _session?.ReadingPreferences ?? ReadingPreferences.Default;
        var widths = CalculatePreviewWidths(Bounds.Width, preferences, PreviewDocumentPadding);
        _nativePreview.AvailableContentWidth = widths.NativeContentWidth;
        if (_webPreview is not null)
        {
            _webPreview.AvailableContentWidth = widths.WebColumnWidth;
            _webPreview.MinHeight = CalculateWebPreviewMinHeight(Bounds.Height);
        }
    }

    internal static ApplicateEditPreviewWidths CalculatePreviewWidths(
        double hostWidth,
        ReadingPreferences preferences,
        Thickness documentPadding)
    {
        var normalized = ReadingPreferences.Normalize(preferences);
        var preferredColumnWidth = normalized.ContentWidth + documentPadding.Left + documentPadding.Right;
        if (!double.IsFinite(hostWidth) || hostWidth <= 0)
        {
            return new ApplicateEditPreviewWidths(normalized.ContentWidth, preferredColumnWidth);
        }

        var columnWidth = SysMath.Max(1, SysMath.Min(preferredColumnWidth, hostWidth));
        var contentWidth = SysMath.Max(1, columnWidth - documentPadding.Left - documentPadding.Right);
        return new ApplicateEditPreviewWidths(contentWidth, columnWidth);
    }

    internal static double CalculateWebPreviewMinHeight(double hostHeight)
        => double.IsFinite(hostHeight) && hostHeight > 0
            ? SysMath.Max(480, hostHeight)
            : 1;

    private static ReadingPreferences CreateWebPreviewPreferences(ReadingPreferences preferences)
        => ReadingPreferences.Normalize(preferences) with { DocumentMinimapMode = DocumentMinimapMode.Off };

    private void UpdateHostScrollMode()
    {
        var scrollViewer = FindHostScrollViewer();
        if (!ReferenceEquals(_hostScrollViewer, scrollViewer))
        {
            RestoreHostScrollMode();
            _hostScrollViewer = scrollViewer;
            _hostScrollViewerVerticalMode = scrollViewer?.VerticalScrollBarVisibility;
        }

        if (_hostScrollViewer is null)
        {
            return;
        }

        _hostScrollViewer.VerticalScrollBarVisibility = CalculateHostVerticalScrollMode(
            IsWebPreviewVisibleOrTargeted(),
            _hostScrollViewerVerticalMode ?? ScrollBarVisibility.Auto);
    }

    internal static ScrollBarVisibility CalculateHostVerticalScrollMode(
        bool useWebPreview,
        ScrollBarVisibility originalMode)
        => useWebPreview ? ScrollBarVisibility.Disabled : originalMode;

    private void RestoreHostScrollMode()
    {
        if (_hostScrollViewer is not null && _hostScrollViewerVerticalMode is { } mode)
        {
            _hostScrollViewer.VerticalScrollBarVisibility = mode;
        }

        _hostScrollViewer = null;
        _hostScrollViewerVerticalMode = null;
    }

    private ScrollViewer? FindHostScrollViewer()
    {
        for (var parent = this.GetVisualParent(); parent is not null; parent = parent.GetVisualParent())
        {
            if (parent is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }
        }

        return null;
    }

    private void DisposeWebPreview()
    {
        if (_webPreview is null)
        {
            return;
        }

        _webPreview.FallbackRequested -= OnWebPreviewFallbackRequested;
        _webPreview.ViewerInteractionRequested -= OnWebPreviewViewerInteractionRequested;
        _webPreview.DocumentRendered -= OnWebPreviewDocumentRendered;
        _webPreview.Dispose();
        _webPreview = null;
    }

    public void Dispose()
    {
        _webRenderTimer.Stop();
        RestoreHostScrollMode();
        AttachSession(null);
        DisposeWebPreview();
        _webRenderTimer.Tick -= OnWebRenderTimerTick;
    }
}

internal readonly record struct ApplicateEditPreviewWidths(double NativeContentWidth, double WebColumnWidth);
