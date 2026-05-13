using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
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

    // Window after a programmatic scroll during which the OPPOSITE side's
    // scroll events are ignored, suppressing the editor↔preview ping-pong
    // loop. 200ms covers a typical Avalonia scroll animation tick + the
    // round-trip into WebView2's renderer thread.
    private static readonly TimeSpan SyncOriginGuard = TimeSpan.FromMilliseconds(200);

    private readonly IApplicateSharedWebViewHost? _sharedHost;
    private readonly Grid _root = new() { UseLayoutRounding = true };
    private readonly Grid _surface = new() { UseLayoutRounding = true };
    private readonly ApplicateMarkdownDocumentView _nativePreview;
    private readonly ScrollViewer _nativeScroll;
    private readonly Panel _webSlot = new() { UseLayoutRounding = true };
    private readonly ToggleButton _syncToggle;
    private readonly DispatcherTimer _webRenderTimer;
    private EditorSessionViewModel? _session;
    private ScrollViewer? _hostScrollViewer;
    private ScrollBarVisibility? _hostScrollViewerVerticalMode;
    private TextBox? _editorTextBox;
    private ScrollViewer? _editorScrollViewer;
    private bool _isAttachedToHost;
    private bool _webPreviewFailed;
    private bool _hostEventsWired;
    private bool _syncEnabled;
    private DateTime _ignoreEditorScrollUntil;
    private DateTime _ignorePreviewScrollUntil;

    public ApplicateEditPreviewView(IApplicateSharedWebViewHost? sharedHost)
    {
        _sharedHost = sharedHost;
        _nativePreview = new ApplicateMarkdownDocumentView
        {
            DocumentPadding = PreviewDocumentPadding,
            UseLayoutRounding = true
        };

        // Wrap native preview in its own ScrollViewer so the surface row
        // itself does not need to scroll. This keeps the toolbar (Row 0)
        // fixed when the outer host scroll viewer is disabled, and gives us
        // a single scroll source to sync against in native mode.
        _nativeScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            UseLayoutRounding = true,
            Content = _nativePreview
        };
        _nativeScroll.ScrollChanged += OnNativeScrollChanged;

        _surface.Children.Add(_nativeScroll);
        _surface.Children.Add(_webSlot);

        _syncToggle = BuildSyncToggle();
        var toolbar = BuildPreviewToolbar(_syncToggle);

        _root.RowDefinitions = new RowDefinitions("Auto,*");
        Grid.SetRow(toolbar, 0);
        Grid.SetRow(_surface, 1);
        _root.Children.Add(toolbar);
        _root.Children.Add(_surface);

        Content = _root;
        UseLayoutRounding = true;

        _webRenderTimer = new DispatcherTimer { Interval = WebPreviewDebounce };
        _webRenderTimer.Tick += OnWebRenderTimerTick;
    }

    private static Border BuildPreviewToolbar(ToggleButton syncToggle)
    {
        var label = new TextBlock
        {
            Text = "PREVIEW",
            VerticalAlignment = VerticalAlignment.Center
        };
        label.Classes.Add("mm-editor-toolbar-label");

        var leftGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { label }
        };

        var rightGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { syncToggle }
        };

        var grid = new Grid();
        grid.ColumnDefinitions = new ColumnDefinitions("*,Auto");
        Grid.SetColumn(leftGroup, 0);
        Grid.SetColumn(rightGroup, 1);
        grid.Children.Add(leftGroup);
        grid.Children.Add(rightGroup);

        var toolbar = new Border { Child = grid };
        toolbar.Classes.Add("mm-editor-toolbar");
        return toolbar;
    }

    private ToggleButton BuildSyncToggle()
    {
        var toggle = new ToggleButton
        {
            Width = 28,
            Height = 24,
            MinWidth = 28,
            MinHeight = 24,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Content = new TextBlock
            {
                Text = "⇅",
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            IsChecked = false,
            IsThreeState = false
        };
        ToolTip.SetTip(toggle, "Editor ↔ preview scroll sync");
        toggle.IsCheckedChanged += OnSyncToggleChanged;
        return toggle;
    }

    private void OnSyncToggleChanged(object? sender, RoutedEventArgs e)
    {
        _syncEnabled = _syncToggle.IsChecked == true;
        if (_syncEnabled)
        {
            EnsureEditorWiring();
            // On enable, snap the preview to the editor's current position so
            // the two surfaces start aligned.
            ForwardEditorScrollToPreview();
        }
    }

    private void EnsureEditorWiring()
    {
        if (_editorTextBox is not null && _editorScrollViewer is not null)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        // Upstream EditWorkspaceView.axaml names the editor TextBox "EditorTextBox".
        // It lives in the same TopLevel as this preview (left pane of the split).
        var textBox = topLevel.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(static tb => string.Equals(tb.Name, "EditorTextBox", StringComparison.Ordinal));
        if (textBox is null)
        {
            return;
        }

        var scrollViewer = textBox.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
        if (scrollViewer is null)
        {
            return;
        }

        _editorTextBox = textBox;
        _editorScrollViewer = scrollViewer;
        _editorScrollViewer.ScrollChanged += OnEditorScrollChanged;
    }

    private void TeardownEditorWiring()
    {
        if (_editorScrollViewer is not null)
        {
            _editorScrollViewer.ScrollChanged -= OnEditorScrollChanged;
        }
        _editorScrollViewer = null;
        _editorTextBox = null;
    }

    private void OnEditorScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (!_syncEnabled)
        {
            return;
        }

        if (DateTime.UtcNow < _ignoreEditorScrollUntil)
        {
            // Preview-origin scroll just propagated through editor; suppress
            // this echo to break the ping-pong loop.
            return;
        }

        // ForwardEditorScrollToPreview branches between WebView (IPC) and
        // native (_nativeScroll.Offset) internally, so this handler does
        // not need to know which surface is currently active.
        ForwardEditorScrollToPreview();
    }

    private void ForwardEditorScrollToPreview()
    {
        if (_editorScrollViewer is null)
        {
            return;
        }

        var maximum = _editorScrollViewer.Extent.Height - _editorScrollViewer.Viewport.Height;
        if (maximum <= 0)
        {
            return;
        }

        var percent = SysMath.Clamp(_editorScrollViewer.Offset.Y / maximum * 100.0, 0, 100);

        if (_isAttachedToHost && _sharedHost is not null)
        {
            // WebView preview active: forward percent through the IPC.
            _ignorePreviewScrollUntil = DateTime.UtcNow + SyncOriginGuard;
            _sharedHost.View.ScrollToProgress(percent);
            return;
        }

        // Native preview active: drive _nativeScroll directly.
        var nativeMaximum = _nativeScroll.Extent.Height - _nativeScroll.Viewport.Height;
        if (nativeMaximum <= 0)
        {
            return;
        }
        _ignorePreviewScrollUntil = DateTime.UtcNow + SyncOriginGuard;
        _nativeScroll.Offset = _nativeScroll.Offset.WithY(percent / 100.0 * nativeMaximum);
    }

    private void ForwardPreviewScrollToEditor(double previewProgressPercent)
    {
        if (_editorScrollViewer is null)
        {
            return;
        }

        var maximum = _editorScrollViewer.Extent.Height - _editorScrollViewer.Viewport.Height;
        if (maximum <= 0)
        {
            return;
        }

        var targetOffset = SysMath.Clamp(previewProgressPercent / 100.0, 0, 1) * maximum;
        _ignoreEditorScrollUntil = DateTime.UtcNow + SyncOriginGuard;
        _editorScrollViewer.Offset = _editorScrollViewer.Offset.WithY(targetOffset);
    }

    private void OnNativeScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (!_syncEnabled || _isAttachedToHost)
        {
            // Sync disabled OR WebView preview is active (its own
            // ScrollStateChanged drives editor sync). Don't double-drive.
            return;
        }

        if (DateTime.UtcNow < _ignorePreviewScrollUntil)
        {
            // Editor-origin scroll just propagated to native; suppress this
            // echo to break the ping-pong loop.
            return;
        }

        var maximum = _nativeScroll.Extent.Height - _nativeScroll.Viewport.Height;
        if (maximum <= 0)
        {
            return;
        }

        var percent = SysMath.Clamp(_nativeScroll.Offset.Y / maximum * 100.0, 0, 100);
        ForwardPreviewScrollToEditor(percent);
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
        TeardownEditorWiring();
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
        _sharedHost.View.ScrollStateChanged += OnSharedScrollStateChanged;
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
        _sharedHost.View.ScrollStateChanged -= OnSharedScrollStateChanged;
        _hostEventsWired = false;
    }

    private void OnSharedScrollStateChanged(object? sender, ApplicateWebDocumentScrollEventArgs e)
    {
        if (!_syncEnabled)
        {
            return;
        }

        if (DateTime.UtcNow < _ignorePreviewScrollUntil)
        {
            // Editor-origin scroll just propagated to preview; suppress this
            // echo to break the ping-pong loop.
            return;
        }

        ForwardPreviewScrollToEditor(e.ProgressPercent);
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
            viewerChromeEnabled: false,
            documentScrollEnabled: true,
            wheelProxyEnabled: false);

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
            _nativeScroll.IsVisible = false;
        }
        else
        {
            if (_isAttachedToHost && _sharedHost is not null)
            {
                _sharedHost.DetachFrom(_webSlot);
                _isAttachedToHost = false;
            }
            _webSlot.IsVisible = false;
            _nativeScroll.IsVisible = true;
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
            // Use the surface (Row 1) height, not the whole preview pane.
            // Bounds.Height includes the toolbar (Row 0) above _surface; if we
            // size the WebView2 wrapper to the full pane height its native HWND
            // overflows into the toolbar area (Avalonia layout positions the
            // wrapper inside _surface, but the HWND respects MinHeight first).
            var surfaceHeight = _surface.Bounds.Height > 0 ? _surface.Bounds.Height : Bounds.Height;
            _sharedHost.View.MinHeight = CalculateWebPreviewMinHeight(surfaceHeight);
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

        // Always disable the outer host ScrollViewer: scroll lives inside the
        // preview surface (WebView's own scroll or _nativeScroll). Otherwise
        // the outer scroll would lift the toolbar (Row 0) along with the
        // content when it scrolls in native mode.
        _hostScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }

    /// <remarks>
    /// Kept for backward compatibility with existing tests that exercise the
    /// previous mode-dependent behaviour. The runtime path now always returns
    /// <see cref="ScrollBarVisibility.Disabled"/> regardless of mode because
    /// the surface owns its own scroll source (WebView internal scroll or
    /// <c>_nativeScroll</c>).
    /// </remarks>
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
