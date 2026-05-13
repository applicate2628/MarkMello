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

    private readonly IApplicateSharedWebViewHost? _sharedHost;
    private readonly Grid _root = new() { UseLayoutRounding = true };
    private readonly ApplicateMarkdownDocumentView _nativePreview;
    private readonly Panel _webSlot = new() { UseLayoutRounding = true };
    private readonly DispatcherTimer _webRenderTimer;
    private EditorSessionViewModel? _session;
    private ScrollViewer? _hostScrollViewer;
    private ScrollBarVisibility? _hostScrollViewerVerticalMode;
    private bool _isAttachedToHost;
    private bool _webPreviewFailed;
    private bool _hostEventsWired;

    public ApplicateEditPreviewView(IApplicateSharedWebViewHost? sharedHost)
    {
        _sharedHost = sharedHost;
        _nativePreview = new ApplicateMarkdownDocumentView
        {
            DocumentPadding = PreviewDocumentPadding,
            UseLayoutRounding = true
        };

        _root.Children.Add(_nativePreview);
        _root.Children.Add(_webSlot);
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
        ReleaseSharedHost();

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
            if (_session?.ReadingPreferences.RendererBackend == MarkdownRendererBackend.WebView)
            {
                // Re-arm fallback on explicit user-pref switch back to WebView.
                _webPreviewFailed = false;
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
        if (ShouldUseWebPreview())
        {
            WireSharedHostEvents();
            QueueWebPreviewRender(immediate: true);
        }
        else
        {
            ReleaseSharedHost();
        }

        ApplyVisuals();
        UpdateHostScrollMode();
    }

    private bool ShouldUseWebPreview()
        => _session?.ReadingPreferences.RendererBackend == MarkdownRendererBackend.WebView
           && _sharedHost is not null
           && !_webPreviewFailed;

    private bool IsWebPreviewActiveOrTargeted()
        => _isAttachedToHost || (ShouldUseWebPreview() && _hostEventsWired);

    private void ReleaseSharedHost()
    {
        if (_sharedHost is null)
        {
            UnwireSharedHostEvents();
            return;
        }

        if (_isAttachedToHost)
        {
            // DetachFrom returns the view to the warmup parent so it stays
            // warm for the next consumer; we just stop showing it.
            _sharedHost.DetachFrom(_webSlot);
            _isAttachedToHost = false;
        }

        UnwireSharedHostEvents();
    }

    private void WireSharedHostEvents()
    {
        if (_sharedHost is null || _hostEventsWired)
        {
            return;
        }

        _sharedHost.View.DocumentRendered += OnSharedDocumentRendered;
        _sharedHost.View.DocumentRenderInvalidated += OnSharedDocumentInvalidated;
        _sharedHost.View.FallbackRequested += OnSharedFallbackRequested;
        _sharedHost.View.ViewerInteractionRequested += OnSharedViewerInteractionRequested;
        _hostEventsWired = true;
    }

    private void UnwireSharedHostEvents()
    {
        if (_sharedHost is null || !_hostEventsWired)
        {
            return;
        }

        _sharedHost.View.DocumentRendered -= OnSharedDocumentRendered;
        _sharedHost.View.DocumentRenderInvalidated -= OnSharedDocumentInvalidated;
        _sharedHost.View.FallbackRequested -= OnSharedFallbackRequested;
        _sharedHost.View.ViewerInteractionRequested -= OnSharedViewerInteractionRequested;
        _hostEventsWired = false;
    }

    private void OnSharedDocumentRendered(object? sender, EventArgs e)
    {
        ApplyVisuals();
    }

    private void OnSharedDocumentInvalidated(object? sender, EventArgs e)
    {
        // New render is starting; the WebView's current paint is no longer for
        // our source. Reveal native as placeholder until DocumentRendered fires.
        ApplyVisuals();
    }

    private void OnSharedFallbackRequested(object? sender, EventArgs e)
    {
        _webPreviewFailed = true;
        ReleaseSharedHost();
        ApplyVisuals();
    }

    private void OnSharedViewerInteractionRequested(object? sender, EventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel { HasOpenOverlay: true } viewModel)
        {
            viewModel.CloseOverlayCommand.Execute(null);
        }
    }

    private void QueueWebPreviewRender(bool immediate)
    {
        if (!ShouldUseWebPreview() || _sharedHost is null)
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
        if (_session is null || _sharedHost is null || !ShouldUseWebPreview())
        {
            return;
        }

        var source = new MarkdownSource(
            _session.CurrentPath ?? string.Empty,
            _session.FileName,
            _session.SourceText);
        var widths = CalculatePreviewWidths(Bounds.Width, _session.ReadingPreferences, PreviewDocumentPadding);

        _sharedHost.View.UpdateInputs(
            source,
            CreateWebPreviewPreferences(_session.ReadingPreferences),
            _session.ImageSourceResolver,
            widths.WebColumnWidth,
            viewerChromeEnabled: false);

        ApplyVisuals();
    }

    // Single canonical visibility decision:
    //   showWebView == true  →  reparent shared view into _webSlot, show WebView,
    //                            hide native preview
    //   showWebView == false →  detach shared view back to warmup parent, show
    //                            native preview as placeholder
    //
    // showWebView is true only when the user requested WebView, the host has
    // already rendered the current source, and we have not been told to fall
    // back. Until then native is shown — the WebView keeps loading offscreen
    // in the warmup panel so the user never sees a partial/loading paint.
    private void ApplyVisuals()
    {
        var source = BuildCurrentSource();
        var showWebView = ShouldUseWebPreview()
                          && _sharedHost is not null
                          && source is not null
                          && _sharedHost.View.HasLoadedDocumentForSource(source);

        if (showWebView && _sharedHost is not null)
        {
            if (!_isAttachedToHost)
            {
                _sharedHost.AttachTo(_webSlot);
                _isAttachedToHost = true;
            }
            _webSlot.IsVisible = true;
            _nativePreview.IsVisible = false;
        }
        else
        {
            if (_isAttachedToHost && _sharedHost is not null)
            {
                _sharedHost.DetachFrom(_webSlot);
                _isAttachedToHost = false;
            }
            _webSlot.IsVisible = false;
            _nativePreview.IsVisible = true;
        }
    }

    private MarkdownSource? BuildCurrentSource()
    {
        if (_session is null)
        {
            return null;
        }

        return new MarkdownSource(
            _session.CurrentPath ?? string.Empty,
            _session.FileName,
            _session.SourceText);
    }

    private void ApplyAvailableWidth()
    {
        var preferences = _session?.ReadingPreferences ?? ReadingPreferences.Default;
        var widths = CalculatePreviewWidths(Bounds.Width, preferences, PreviewDocumentPadding);
        _nativePreview.AvailableContentWidth = widths.NativeContentWidth;
        if (_sharedHost is not null && _isAttachedToHost)
        {
            _sharedHost.View.AvailableContentWidth = widths.WebColumnWidth;
            _sharedHost.View.MinHeight = CalculateWebPreviewMinHeight(Bounds.Height);
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
            IsWebPreviewActiveOrTargeted(),
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

    public void Dispose()
    {
        _webRenderTimer.Stop();
        RestoreHostScrollMode();
        AttachSession(null);
        ReleaseSharedHost();
        _webRenderTimer.Tick -= OnWebRenderTimerTick;
    }
}

internal readonly record struct ApplicateEditPreviewWidths(double NativeContentWidth, double WebColumnWidth);
