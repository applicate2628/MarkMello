using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MarkMello.Applicate.Desktop.Diagnostics;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Applicate.Desktop.Views.Minimap;
using MarkMello.Domain;
using MarkMello.Presentation.ViewModels;
using SysMath = System.Math;

namespace MarkMello.Applicate.Desktop.Views;

public sealed class ApplicateViewerView : UserControl, IDisposable
{
    private const double WheelStepMultiplier = 6.0;
    internal const double MinManualContentWidth = 320.0;
    private const double ViewportHorizontalGutter = 32.0;
    private const double MinimapColumnGap = 24.0;
    private const double WidthHandleHitArea = 24.0;
    private const double WidthHandleIdleTrackWidth = 2.0;
    private const double WidthHandleHoverTrackWidth = 5.0;
    private const double WidthHandleDraggingTrackWidth = 7.0;

    private readonly ScrollViewer _scroll;
    private readonly Border _scrollContentFrame;
    private readonly Border _column;
    private readonly Grid _documentShell;
    private readonly Grid _documentLayer;
    private readonly Border _webRenderMask;
    private readonly ApplicateMarkdownDocumentView _documentView;
    private readonly IApplicateHtmlMarkdownRenderer? _htmlRenderer;
    private readonly IApplicateShellAssetBundleFactory? _shellAssetFactory;
    private readonly Border _widthHandle;
    private readonly Border _widthHandleTrack;
    private readonly ContentControl _minimapHost;
    private ApplicateWebMarkdownDocumentView? _webDocumentView;
    private WebViewHostScrollBarOverlay? _webDocumentScrollBarOverlay;
    private MainWindowViewModel? _viewModel;
    private bool _isDraggingWidth;
    private bool _isWidthHandleHovering;
    private ApplicateDocumentMinimapView? _minimap;
    private int _minimapBuildGeneration;
    private bool _isMinimapBuildQueued;
    private bool _hasRenderedDocument;
    private Point _dragStart;
    private double _dragStartWidth;
    private double? _manualContentWidth;
    private double _lastViewModelContentWidth;
    private double _lastReadingProgress;
    private double _documentHorizontalPadding = 144.0;
    private double _webMinimapReservedWidth;
    private Size _lastMinimapExtent;
    private Size _lastMinimapViewport;
    private MarkdownSource? _lastDocumentSource;
    private MarkdownRendererBackend _lastRequestedRendererBackend = MarkdownRendererBackend.Native;
    // WebView is the primary renderer; the native Avalonia surface is kept
    // only as a fallback when the WebView pipeline fails. Starting the state
    // machine at WebView prevents the native renderer from painting its
    // progressive layout in the body of the viewer while the WebView is
    // still loading — that painted-then-replaced flicker was visible as
    // ~1.5s of incremental Avalonia content before the WebView took over.
    private ApplicateRendererSurfaceKind _activeRendererSurface = ApplicateRendererSurfaceKind.WebView;
    private ApplicateRendererSurfaceKind? _pendingRendererSurface;
    private bool _pendingRendererReady;
    private long _rendererSwitchGeneration;
    private bool _webRendererFailedForCurrentDocument;
    private ApplicateWidthHandleVisualState? _lastWidthHandleVisualState;

    public ApplicateViewerView(
        IApplicateHtmlMarkdownRenderer? htmlRenderer = null,
        IApplicateShellAssetBundleFactory? shellAssetFactory = null)
    {
        _htmlRenderer = htmlRenderer;
        _shellAssetFactory = shellAssetFactory;
        _documentView = new ApplicateMarkdownDocumentView
        {
            DocumentPadding = new Thickness(72, 96, 72, 160),
            UseLayoutRounding = true
        };
        _documentView.DocumentRendered += OnDocumentRendered;
        _documentView.DocumentRenderInvalidated += OnDocumentRenderInvalidated;
        ApplicateRendererSurfaceTransition.EnsureOpacityTransition(_documentView);

        _widthHandleTrack = new Border
        {
            Width = WidthHandleIdleTrackWidth,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 42, 6, 42),
            CornerRadius = new CornerRadius(99),
            Background = Brush("MmTextFaintBrush", new SolidColorBrush(Color.FromArgb(70, 120, 120, 120))),
            Opacity = 0,
            IsHitTestVisible = false,
            Transitions =
            [
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = ApplicateMotion.Standard,
                    Easing = ApplicateMotion.Easing
                },
                new DoubleTransition
                {
                    Property = Layoutable.WidthProperty,
                    Duration = ApplicateMotion.Standard,
                    Easing = ApplicateMotion.Easing
                }
            ]
        };

        _widthHandle = new Border
        {
            Width = WidthHandleHitArea,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            Child = _widthHandleTrack
        };
        _widthHandle.PointerEntered += OnWidthHandlePointerEntered;
        _widthHandle.PointerExited += OnWidthHandlePointerExited;
        _widthHandle.PointerPressed += OnWidthHandlePointerPressed;
        _widthHandle.PointerMoved += OnWidthHandlePointerMoved;
        _widthHandle.PointerReleased += OnWidthHandlePointerReleased;
        _widthHandle.PointerCaptureLost += OnWidthHandlePointerCaptureLost;

        _documentLayer = new Grid { UseLayoutRounding = true };
        _documentLayer.Children.Add(_documentView);

        // Theme-matching mask overlay shown between DocumentRenderInvalidated
        // and DocumentRendered. Native WebView2 HWND ignores Avalonia Opacity,
        // so on tab switch the old document's content stays painted while
        // RenderAsync builds the new HTML — observed as ~200-400ms of stale
        // content under the new tab's title. The mask sits z-above the
        // WebView, opaque, and is toggled by render events so the user sees
        // a clean theme-colored tile during the render gap.
        // Mirrors ApplicateEditPreviewView's _webRenderMask pattern (proven
        // for edit-preview transitions since v0.2.0).
        _webRenderMask = new Border
        {
            Background = Avalonia.Application.Current?.TryGetResource(
                "MmBackgroundBrush",
                Avalonia.Application.Current.ActualThemeVariant,
                out var bg) == true && bg is IBrush bgBrush
                ? bgBrush
                : new SolidColorBrush(Colors.White),
            IsHitTestVisible = false,
            IsVisible = false
        };
        _documentLayer.Children.Add(_webRenderMask);

        _documentShell = new Grid { UseLayoutRounding = true };
        _documentShell.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        _documentShell.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(WidthHandleHitArea)));
        Grid.SetColumn(_documentLayer, 0);
        Grid.SetColumn(_widthHandle, 1);
        _documentShell.Children.Add(_documentLayer);
        _documentShell.Children.Add(_widthHandle);

        _column = new Border
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            UseLayoutRounding = true,
            Child = _documentShell
        };

        _scrollContentFrame = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            UseLayoutRounding = true,
            Child = _column
        };

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            UseLayoutRounding = true,
            Content = new Grid
            {
                UseLayoutRounding = true,
                Children =
                {
                    _scrollContentFrame
                }
            }
        };
        _scroll.ScrollChanged += OnScrollChanged;
        _scroll.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);

        _minimapHost = new ContentControl
        {
            Width = 136,
            Margin = new Thickness(0, 64, 16, 64),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            UseLayoutRounding = true
        };
        _minimapHost.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);

        Content = new Grid
        {
            UseLayoutRounding = true,
            Children =
            {
                _scroll,
                _minimapHost
            }
        };

        ActualThemeVariantChanged += OnViewerAppearanceChanged;
        ResourcesChanged += OnViewerResourcesChanged;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        AttachViewModel(DataContext as MainWindowViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnViewerAppearanceChanged;
        ResourcesChanged -= OnViewerResourcesChanged;
        DisposeWebDocumentView();

        AttachViewModel(null);
        _minimapBuildGeneration++;
        _isMinimapBuildQueued = false;
        RemoveMinimap();
        _hasRenderedDocument = false;
        _lastMinimapExtent = default;
        _lastMinimapViewport = default;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyColumnWidth();
        if (_hasRenderedDocument)
        {
            QueueMinimapBuild();
        }
    }

    private void AttachViewModel(MainWindowViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        SyncFromViewModel();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.RenderedDocument)
            or nameof(MainWindowViewModel.DocumentReadingPreferences)
            or nameof(MainWindowViewModel.DocumentColumnMaxWidth)
            or nameof(MainWindowViewModel.ReadingPreferences))
        {
            SyncFromViewModel();
            if (_hasRenderedDocument)
            {
                if (!ShouldShowMinimap())
                {
                    RemoveMinimap();
                    return;
                }

                QueueMinimapBuild();
            }
        }
    }

    private void SyncFromViewModel()
    {
        if (_viewModel is null)
        {
            _documentView.Document = RenderedMarkdownDocument.Empty;
            _documentView.ReadingPreferences = ReadingPreferences.Default;
            _documentView.ImageSourceResolver = null;
            _documentView.AvailableContentWidth = double.NaN;
            if (_webDocumentView is not null)
            {
                _webDocumentView.UpdateInputs(
                    source: null,
                    readingPreferences: ReadingPreferences.Default,
                    imageSourceResolver: null,
                    availableContentWidth: double.NaN,
                    viewerChromeEnabled: true);
            }

            _activeRendererSurface = ApplicateRendererSurfaceKind.Native;
            _pendingRendererSurface = null;
            _pendingRendererReady = false;
            _rendererSwitchGeneration++;
            ApplyRendererSurfaceVisuals();
            _manualContentWidth = null;
            _lastViewModelContentWidth = 0;
            _lastReadingProgress = 0;
            _webMinimapReservedWidth = 0;
            _lastDocumentSource = null;
            _webRendererFailedForCurrentDocument = false;
            _column.MaxWidth = double.PositiveInfinity;
            return;
        }

        var documentChanged = !ReferenceEquals(_lastDocumentSource, _viewModel.Document);
        if (documentChanged)
        {
            _lastDocumentSource = _viewModel.Document;
            _webRendererFailedForCurrentDocument = false;
            _webMinimapReservedWidth = 0;
            _lastReadingProgress = 0;
        }

        var requestedRendererBackend = _viewModel.DocumentReadingPreferences.RendererBackend;
        if (_lastRequestedRendererBackend != requestedRendererBackend)
        {
            _lastRequestedRendererBackend = requestedRendererBackend;
            if (requestedRendererBackend == MarkdownRendererBackend.WebView)
            {
                _webRendererFailedForCurrentDocument = false;
            }
        }

        var requestedRendererSurface = ResolveRequestedRendererSurface();
        if (requestedRendererSurface == ApplicateRendererSurfaceKind.WebView)
        {
            if (EnsureWebDocumentView() is null)
            {
                requestedRendererSurface = ApplicateRendererSurfaceKind.Native;
            }
        }

        if (ShouldUpdateNativeSurface(
            requestedRendererSurface,
            _activeRendererSurface,
            _hasRenderedDocument,
            documentChanged))
        {
            _documentView.Document = _viewModel.RenderedDocument;
            _documentView.ReadingPreferences = _viewModel.DocumentReadingPreferences;
            _documentView.ImageSourceResolver = _viewModel.ImageSourceResolver;
        }

        var viewModelContentWidth = _viewModel.ContentWidthSetting;
        _documentHorizontalPadding = SysMath.Max(0, _viewModel.DocumentColumnMaxWidth - viewModelContentWidth);
        if (_manualContentWidth is null ||
            (!_isDraggingWidth && SysMath.Abs(viewModelContentWidth - _lastViewModelContentWidth) > double.Epsilon))
        {
            _manualContentWidth = viewModelContentWidth;
        }

        _lastViewModelContentWidth = viewModelContentWidth;

        if (_webDocumentView is not null
            && (requestedRendererSurface == ApplicateRendererSurfaceKind.WebView || IsWebRendererVisibleOrTargeted()))
        {
            var webAlreadyRenderedCurrentDocument =
                requestedRendererSurface == ApplicateRendererSurfaceKind.WebView
                && _webDocumentView.HasLoadedDocumentForSource(_viewModel.Document);
            _webDocumentView.UpdateInputs(
                source: _viewModel.Document,
                readingPreferences: CreateWebDocumentReadingPreferences(
                    _viewModel.DocumentReadingPreferences,
                    _viewModel.ReadingPreferences),
                imageSourceResolver: _viewModel.ImageSourceResolver,
                availableContentWidth: CalculateDocumentColumnWidthForSurface(ApplicateRendererSurfaceKind.WebView),
                viewerChromeEnabled: true);
            StageRendererSurface(requestedRendererSurface);
            if (webAlreadyRenderedCurrentDocument)
            {
                CommitPendingRendererSurface(ApplicateRendererSurfaceKind.WebView);
            }
        }
        else
        {
            StageRendererSurface(requestedRendererSurface);
        }

        ApplyColumnWidth();
    }

    private void OnDocumentRendered(object? sender, EventArgs e)
    {
        var senderKind = ReferenceEquals(sender, _webDocumentView) ? "web" : ReferenceEquals(sender, _documentView) ? "native" : "?";
        ApplicateTrace.ModeToggle($"Viewer.OnDocumentRendered sender={senderKind} pending={_pendingRendererSurface}");
        // Hide mask conditions:
        //   (1) WebView Rendered — the normal happy path, WebView committed new content
        //   (2) Native Rendered AND pending=Native — WebView gave up via fallback
        //       and Native committed. WebView surface is no longer the active path,
        //       so the mask's purpose (hide stale WebView) is moot.
        // Native renderer's parallel Rendered (no fallback) runs ~70ms after
        // Invalidated, faster than WebView. If we react to that we'd unmask
        // while WebView is still loading and re-expose stale content.
        var isWebRendered = ReferenceEquals(sender, _webDocumentView);
        var isNativeFallbackCommit = ReferenceEquals(sender, _documentView)
            && _pendingRendererSurface == ApplicateRendererSurfaceKind.Native;
        if (isWebRendered || isNativeFallbackCommit)
        {
            _webRenderMask.IsVisible = false;
        }

        if (ReferenceEquals(sender, _webDocumentView)
            && _pendingRendererSurface == ApplicateRendererSurfaceKind.WebView)
        {
            CommitPendingRendererSurface(ApplicateRendererSurfaceKind.WebView);
            MarkCurrentDocumentRendered();
            return;
        }

        if (ReferenceEquals(sender, _documentView)
            && _pendingRendererSurface == ApplicateRendererSurfaceKind.Native)
        {
            CommitPendingRendererSurface(ApplicateRendererSurfaceKind.Native);
            MarkCurrentDocumentRendered();
            return;
        }

        if (!IsRenderedSurfaceActive(sender))
        {
            return;
        }

        MarkCurrentDocumentRendered();
    }

    private void OnDocumentRenderInvalidated(object? sender, EventArgs e)
    {
        var senderKind = ReferenceEquals(sender, _webDocumentView) ? "web" : ReferenceEquals(sender, _documentView) ? "native" : "?";
        ApplicateTrace.ModeToggle($"Viewer.OnDocumentRenderInvalidated sender={senderKind}");
        if (!IsRenderedSurfaceActive(sender))
        {
            return;
        }

        _webRenderMask.IsVisible = true;

        _hasRenderedDocument = false;
        _lastMinimapExtent = default;
        _lastMinimapViewport = default;
        _webMinimapReservedWidth = 0;
        _minimapBuildGeneration++;
        RemoveMinimap();
    }

    private void MarkCurrentDocumentRendered()
    {
        _viewModel?.MarkReadableDocumentRendered();
        _hasRenderedDocument = true;
        QueueMinimapBuild();
        Dispatcher.UIThread.Post(QueueMinimapBuild, DispatcherPriority.Loaded);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var max = _scroll.ScrollBarMaximum.Y;
        var current = _scroll.Offset.Y;
        if (!IsWebRendererActive())
        {
            _lastReadingProgress = max > 0 ? SysMath.Clamp(current / max * 100.0, 0, 100) : 0;
            _viewModel.ReadingProgress = _lastReadingProgress;
        }

        if (_hasRenderedDocument && HasMinimapLayoutMetricsChanged())
        {
            QueueMinimapBuild();
        }

        UpdateMinimapScrollState();
        UpdateMinimapVisibility();
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (SysMath.Abs(e.Delta.Y) <= double.Epsilon || SysMath.Abs(e.Delta.X) > SysMath.Abs(e.Delta.Y))
        {
            return;
        }

        var baseStep = _scroll.SmallChange.Height > 0 ? _scroll.SmallChange.Height : 16.0;
        if (ScrollByWheelDelta(-e.Delta.Y * baseStep * WheelStepMultiplier))
        {
            e.Handled = true;
        }
    }

    private bool ScrollByWheelDelta(double deltaY)
    {
        var maxOffset = _scroll.ScrollBarMaximum.Y;
        if (maxOffset <= 0)
        {
            return false;
        }

        var nextOffset = SysMath.Clamp(_scroll.Offset.Y + deltaY, 0, maxOffset);
        if (SysMath.Abs(nextOffset - _scroll.Offset.Y) <= double.Epsilon)
        {
            return false;
        }

        _scroll.Offset = new Vector(_scroll.Offset.X, nextOffset);
        return true;
    }

    internal static double NormalizeWebWheelDeltaForTesting(double deltaY, int deltaMode, double smallChangeHeight, double viewportHeight)
        => NormalizeWebWheelDelta(deltaY, deltaMode, smallChangeHeight, viewportHeight);

    private static double NormalizeWebWheelDelta(double deltaY, int deltaMode, double smallChangeHeight, double viewportHeight)
    {
        var baseStep = smallChangeHeight > 0 ? smallChangeHeight : 16.0;
        return deltaMode switch
        {
            1 => deltaY * baseStep * 3.0,
            2 => deltaY * SysMath.Max(baseStep, viewportHeight * 0.85),
            _ => deltaY
        };
    }

    private void OnWidthHandlePointerEntered(object? sender, PointerEventArgs e)
    {
        _isWidthHandleHovering = true;
        UpdateWidthHandleVisual();
    }

    private void OnWidthHandlePointerExited(object? sender, PointerEventArgs e)
    {
        _isWidthHandleHovering = false;
        UpdateWidthHandleVisual();
    }

    private void OnWidthHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null || !e.GetCurrentPoint(_widthHandle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isDraggingWidth = true;
        _dragStart = e.GetPosition(this);
        _dragStartWidth = _manualContentWidth ?? _viewModel.ContentWidthSetting;
        UpdateWidthHandleVisual();
        e.Pointer.Capture(_widthHandle);
        SetWebDocumentHitTestingForWidthDrag(false);
        e.Handled = true;
    }

    private void OnWidthHandlePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingWidth || _viewModel is null)
        {
            return;
        }

        var delta = e.GetPosition(this).X - _dragStart.X;
        ApplyWidthDragDelta(delta);
        e.Handled = true;
    }

    private void OnWidthHandlePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingWidth)
        {
            return;
        }

        FinishWidthHandleDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnWidthHandlePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        FinishWidthHandleDrag();
    }

    private void FinishWidthHandleDrag()
    {
        _isDraggingWidth = false;
        UpdateWidthHandleVisual();
        SetWebDocumentHitTestingForWidthDrag(true);
    }

    private void ApplyWidthDragDelta(double deltaX)
    {
        // The column is center-aligned, so dragging the right handle by N pixels
        // grows the readable document by 2N pixels and keeps the centerline stable.
        _manualContentWidth = ClampManualContentWidth(CalculateWidthDragContentWidth(_dragStartWidth, deltaX));
        ApplyColumnWidth();
    }

    private void SetWebDocumentHitTestingForWidthDrag(bool enabled)
    {
        if (_webDocumentView is not null)
        {
            _webDocumentView.IsHitTestVisible = enabled;
        }
    }

    private void OnViewerAppearanceChanged(object? sender, EventArgs e)
    {
        if (_hasRenderedDocument)
        {
            QueueMinimapBuild();
        }
    }

    private void OnViewerResourcesChanged(object? sender, ResourcesChangedEventArgs e)
    {
        _lastWidthHandleVisualState = null;
        UpdateWidthHandleVisual();
        if (_hasRenderedDocument)
        {
            QueueMinimapBuild();
        }
    }

    private void ApplyColumnWidth()
    {
        if (_viewModel is null)
        {
            return;
        }

        var desiredContentWidth = _manualContentWidth ?? _viewModel.ContentWidthSetting;
        var visibleContentWidth = ClampManualContentWidth(desiredContentWidth);
        var documentColumnWidth = visibleContentWidth + _documentHorizontalPadding;
        _documentView.AvailableContentWidth = visibleContentWidth;
        var layoutSurface = GetLayoutRendererSurface();
        var layoutUsesWebRenderer = layoutSurface == ApplicateRendererSurfaceKind.WebView;
        _scroll.VerticalScrollBarVisibility = layoutUsesWebRenderer
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        var nativeWidthHandleSlotWidth = layoutUsesWebRenderer ? 0 : WidthHandleHitArea;
        _documentShell.ColumnDefinitions[1].Width = new GridLength(nativeWidthHandleSlotWidth);
        _widthHandle.IsVisible = !layoutUsesWebRenderer;
        _widthHandle.IsHitTestVisible = !layoutUsesWebRenderer;
        if (_webDocumentView is not null)
        {
            // The WebView document uses CSS border-box padding, so it needs the
            // full document column width to keep the readable text width aligned
            // with the native renderer. The WebView surface itself may be wider:
            // its DOM minimap is viewport-fixed and should anchor to the window
            // edge, not to the readable text column.
            _webDocumentView.AvailableContentWidth = IsWebRendererVisibleOrTargeted()
                ? CalculateDocumentColumnWidthForSurface(ApplicateRendererSurfaceKind.WebView)
                : documentColumnWidth;
            _webDocumentView.MinHeight = SysMath.Max(480, Bounds.Height);
        }

        var documentLayerWidth = CalculateDocumentLayerWidth(documentColumnWidth, Bounds.Width, layoutUsesWebRenderer);
        var shellWidth = documentLayerWidth + nativeWidthHandleSlotWidth;

        _documentLayer.Width = documentLayerWidth;
        _column.Width = shellWidth;
        _column.MaxWidth = shellWidth;
        UpdateWidthHandleVisual();
    }

    internal static ReadingPreferences CreateWebDocumentReadingPreferences(
        ReadingPreferences documentPreferences,
        ReadingPreferences shellPreferences)
        => documentPreferences with { DocumentMinimapMode = shellPreferences.DocumentMinimapMode };

    internal static bool ShouldUpdateNativeSurfaceForTesting(
        ApplicateRendererSurfaceKind requestedSurface,
        ApplicateRendererSurfaceKind activeSurface,
        bool hasRenderedDocument,
        bool documentChanged)
        => ShouldUpdateNativeSurface(requestedSurface, activeSurface, hasRenderedDocument, documentChanged);

    private static bool ShouldUpdateNativeSurface(
        ApplicateRendererSurfaceKind requestedSurface,
        ApplicateRendererSurfaceKind activeSurface,
        bool hasRenderedDocument,
        bool documentChanged)
        // Only feed the native renderer when it is the renderer the user is
        // about to see — either the request explicitly targets Native, or
        // Native is currently active (fallback after a WebView failure).
        // Otherwise we skip the Document/prefs assignments to spare the CPU
        // work of building Avalonia-side layout (math, code blocks, images)
        // for a surface that will never paint.
        => requestedSurface == ApplicateRendererSurfaceKind.Native
           || activeSurface == ApplicateRendererSurfaceKind.Native;

    internal static double CalculateDocumentLayerWidth(double documentColumnWidth, double hostWidth, bool useWebRenderer)
        => useWebRenderer
            ? SysMath.Max(documentColumnWidth, hostWidth)
            : documentColumnWidth;

    internal static double CalculateWidthDragContentWidth(double dragStartWidth, double deltaX)
        => dragStartWidth + deltaX * 2.0;

    private double ClampManualContentWidth(double contentWidth)
        => ClampManualContentWidth(contentWidth, GetLayoutRendererSurface());

    private double CalculateDocumentColumnWidthForSurface(ApplicateRendererSurfaceKind surface)
    {
        if (_viewModel is null)
        {
            return double.NaN;
        }

        var desiredContentWidth = _manualContentWidth ?? _viewModel.ContentWidthSetting;
        return ClampManualContentWidth(desiredContentWidth, surface) + _documentHorizontalPadding;
    }

    private double ClampManualContentWidth(double contentWidth, ApplicateRendererSurfaceKind layoutSurface)
    {
        var availableWidth = CalculateAvailableContentWidth(
            Bounds.Width,
            GetResizeReservedWidth(layoutSurface),
            _documentHorizontalPadding,
            layoutSurface == ApplicateRendererSurfaceKind.WebView);
        return SysMath.Clamp(contentWidth, MinManualContentWidth, availableWidth);
    }

    private void ApplyMinimapReservation()
    {
        var reservedWidth = GetNativeMinimapReservedWidth();
        _scrollContentFrame.Padding = new Thickness(0, 0, reservedWidth, 0);
    }

    private double GetResizeReservedWidth()
        => GetResizeReservedWidth(GetLayoutRendererSurface());

    private double GetResizeReservedWidth(ApplicateRendererSurfaceKind layoutSurface)
        => layoutSurface == ApplicateRendererSurfaceKind.WebView
            ? _webMinimapReservedWidth
            : GetNativeMinimapReservedWidth();

    internal static double CalculateAvailableContentWidth(
        double boundsWidth,
        double resizeReservedWidth,
        double documentHorizontalPadding,
        bool useWebRenderer)
        => SysMath.Max(
            MinManualContentWidth,
            boundsWidth
                - resizeReservedWidth
                - documentHorizontalPadding
                - (useWebRenderer ? 0 : WidthHandleHitArea)
                - ViewportHorizontalGutter);

    private double GetNativeMinimapReservedWidth()
    {
        if (_minimap is null || !_minimapHost.IsVisible)
        {
            return 0;
        }

        var minimapWidth = _minimapHost.Bounds.Width > 0 ? _minimapHost.Bounds.Width : _minimapHost.Width;
        if (double.IsNaN(minimapWidth) || double.IsInfinity(minimapWidth) || minimapWidth <= 0)
        {
            minimapWidth = 136;
        }

        return minimapWidth + _minimapHost.Margin.Left + _minimapHost.Margin.Right + MinimapColumnGap;
    }

    private void UpdateWidthHandleVisual()
    {
        var visibility = _viewModel?.ReadingPreferences.WidthResizerVisibility
            ?? ReadingPreferences.Default.WidthResizerVisibility;
        var state = CalculateWidthHandleVisualState(visibility, _isWidthHandleHovering, _isDraggingWidth);
        if (_lastWidthHandleVisualState == state)
        {
            return;
        }

        _lastWidthHandleVisualState = state;

        _widthHandleTrack.Width = state.Width;
        _widthHandleTrack.Opacity = state.Opacity;
        _widthHandleTrack.Background = state.UseAccentBrush
            ? Brush("MmAccentBrush", Brushes.OrangeRed)
            : Brush("MmTextFaintBrush", new SolidColorBrush(Color.FromArgb(70, 120, 120, 120)));
    }

    internal static ApplicateWidthHandleVisualState CalculateWidthHandleVisualState(
        WidthResizerVisibility visibility,
        bool isHovering,
        bool isDragging)
    {
        if (isDragging)
        {
            return new ApplicateWidthHandleVisualState(WidthHandleDraggingTrackWidth, 0.9, UseAccentBrush: true);
        }

        if (isHovering)
        {
            return new ApplicateWidthHandleVisualState(WidthHandleHoverTrackWidth, 0.72, UseAccentBrush: true);
        }

        var idleOpacity = visibility == WidthResizerVisibility.Always ? 0.18 : 0;
        return new ApplicateWidthHandleVisualState(WidthHandleIdleTrackWidth, idleOpacity, UseAccentBrush: false);
    }

    private IBrush Brush(string key, IBrush fallback)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var resource) && resource is IBrush brush)
        {
            return brush;
        }

        return fallback;
    }

    private void QueueMinimapBuild()
    {
        _minimapBuildGeneration++;
        if (_isMinimapBuildQueued)
        {
            return;
        }

        _isMinimapBuildQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _isMinimapBuildQueued = false;
                BuildMinimapIfCurrent(_minimapBuildGeneration);
            },
            DispatcherPriority.Background);
    }

    private void BuildMinimapIfCurrent(int generation)
    {
        if (generation != _minimapBuildGeneration || !_hasRenderedDocument)
        {
            return;
        }

        _lastMinimapExtent = _scroll.Extent;
        _lastMinimapViewport = _scroll.Viewport;

        if (!ShouldShowMinimap())
        {
            RemoveMinimap();
            return;
        }

        var snapshot = _documentView.CreateMiniatureSnapshot();
        if (!ApplicateDocumentMinimapBuildPolicy.AllowsDetailedMiniature(snapshot))
        {
            RemoveMinimap();
            return;
        }

        var minimap = EnsureMinimap();
        minimap.SetSource(_documentView, snapshot);
        UpdateMinimapScrollState();
        UpdateMinimapVisibility();
    }

    private ApplicateDocumentMinimapView EnsureMinimap()
    {
        if (_minimap is not null)
        {
            return _minimap;
        }

        var minimap = new ApplicateDocumentMinimapView();
        minimap.ScrollRequested += OnMinimapScrollRequested;
        _minimap = minimap;
        _minimapHost.Content = minimap;
        _minimapHost.IsHitTestVisible = true;
        return minimap;
    }

    private void RemoveMinimap()
    {
        if (_minimap is not null)
        {
            _minimap.ScrollRequested -= OnMinimapScrollRequested;
            _minimap.ClearSource();
            _minimap = null;
        }

        _minimapHost.Content = null;
        _minimapHost.IsHitTestVisible = false;
        ApplyMinimapReservation();
        ApplyColumnWidth();
    }

    private void OnMinimapScrollRequested(object? sender, ApplicateDocumentMinimapScrollRequestedEventArgs e)
    {
        var targetOffset = SysMath.Clamp(e.OffsetY, 0, _scroll.ScrollBarMaximum.Y);
        _scroll.Offset = new Vector(_scroll.Offset.X, targetOffset);
    }

    private void OnWebScrollStateChanged(object? sender, ApplicateWebDocumentScrollEventArgs e)
    {
        if (_viewModel is not null && IsWebRendererActive())
        {
            _lastReadingProgress = e.ProgressPercent;
            _viewModel.ReadingProgress = _lastReadingProgress;
        }
    }

    private void OnWebMinimapStateChanged(object? sender, ApplicateWebMinimapStateEventArgs e)
    {
        if (!IsWebRendererVisibleOrTargeted())
        {
            return;
        }

        var nextReservedWidth = e.Visible ? e.ReservedWidth : 0;
        if (SysMath.Abs(_webMinimapReservedWidth - nextReservedWidth) < 0.5)
        {
            return;
        }

        _webMinimapReservedWidth = nextReservedWidth;
        ApplyColumnWidth();
    }

    private void OnWebWidthDragRequested(object? sender, ApplicateWebWidthDragEventArgs e)
    {
        if (_viewModel is null || !IsWebRendererActive())
        {
            return;
        }

        if (e.Phase == ApplicateWebWidthDragPhase.Start)
        {
            _isDraggingWidth = true;
            _dragStartWidth = _manualContentWidth ?? _viewModel.ContentWidthSetting;
            UpdateWidthHandleVisual();
            return;
        }

        if (!_isDraggingWidth)
        {
            return;
        }

        ApplyWidthDragDelta(e.DeltaX);
        if (e.Phase == ApplicateWebWidthDragPhase.End)
        {
            FinishWidthHandleDrag();
        }
    }

    private void OnWebWheelRequested(object? sender, ApplicateWebWheelEventArgs e)
    {
        if (!IsWebRendererActive())
        {
            return;
        }

        var deltaY = NormalizeWebWheelDelta(
            e.DeltaY,
            e.DeltaMode,
            _scroll.SmallChange.Height,
            _scroll.Viewport.Height);
        ScrollByWheelDelta(deltaY);
    }

    private void OnWebFallbackRequested(object? sender, EventArgs e)
    {
        _webRendererFailedForCurrentDocument = true;
        if (_pendingRendererSurface == ApplicateRendererSurfaceKind.WebView)
        {
            CancelPendingRendererSurface();
        }

        if (_activeRendererSurface == ApplicateRendererSurfaceKind.WebView)
        {
            StageRendererSurface(ApplicateRendererSurfaceKind.Native);
        }

        SyncFromViewModel();
    }

    private void OnWebViewerInteractionRequested(object? sender, EventArgs e)
    {
        if (_viewModel?.HasOpenOverlay == true)
        {
            _viewModel.CloseOverlayCommand.Execute(null);
        }
    }

    private ApplicateWebMarkdownDocumentView? EnsureWebDocumentView()
    {
        if (_webDocumentView is not null)
        {
            return _webDocumentView;
        }

        if (_htmlRenderer is null)
        {
            return null;
        }

        ApplicateWebMarkdownDocumentView view;
        try
        {
            view = new ApplicateWebMarkdownDocumentView(_htmlRenderer, _shellAssetFactory)
            {
                IsVisible = false,
                MinHeight = 1,
                UseLayoutRounding = true
            };
            ApplicateRendererSurfaceTransition.EnsureOpacityTransition(view);
        }
        catch
        {
            _webRendererFailedForCurrentDocument = true;
            return null;
        }

        view.DocumentRendered += OnDocumentRendered;
        view.DocumentRenderInvalidated += OnDocumentRenderInvalidated;
        view.ScrollStateChanged += OnWebScrollStateChanged;
        view.MinimapStateChanged += OnWebMinimapStateChanged;
        view.WidthDragRequested += OnWebWidthDragRequested;
        view.WheelRequested += OnWebWheelRequested;
        view.ViewerInteractionRequested += OnWebViewerInteractionRequested;
        view.FallbackRequested += OnWebFallbackRequested;
        _webDocumentView = view;

        // Reserve a 12px right strip for the Avalonia ScrollBar overlay so
        // the WebView2 HWND doesn't paint into the scrollbar's Avalonia
        // airspace via Win32 z-order. Symmetric with edit-preview overlay
        // setup in ApplicateEditPreviewView._webSlot.Margin.
        view.Margin = new Thickness(0, 0, 12, 0);
        _documentLayer.Children.Add(view);

        // Avalonia ScrollBar overlay — see WebViewHostScrollBarOverlay class
        // doc and the consultant blueprint at .scratch/codex-prompts/option-
        // a-avalonia-scrollbar-overlay-blueprint.md. Replaces WebKit
        // ::-webkit-scrollbar so drag tracks mouse perfectly (Avalonia
        // pointer capture, no IPC lag, no sideways release-zone).
        _webDocumentScrollBarOverlay = new WebViewHostScrollBarOverlay(view);
        _documentLayer.Children.Add(_webDocumentScrollBarOverlay.Control);

        return view;
    }

    private void DisposeWebDocumentView()
    {
        if (_webDocumentView is null)
        {
            return;
        }

        _webDocumentView.DocumentRendered -= OnDocumentRendered;
        _webDocumentView.DocumentRenderInvalidated -= OnDocumentRenderInvalidated;
        _webDocumentView.ScrollStateChanged -= OnWebScrollStateChanged;
        _webDocumentView.MinimapStateChanged -= OnWebMinimapStateChanged;
        _webDocumentView.WidthDragRequested -= OnWebWidthDragRequested;
        _webDocumentView.WheelRequested -= OnWebWheelRequested;
        _webDocumentView.ViewerInteractionRequested -= OnWebViewerInteractionRequested;
        _webDocumentView.FallbackRequested -= OnWebFallbackRequested;
        if (_webDocumentScrollBarOverlay is not null)
        {
            _documentLayer.Children.Remove(_webDocumentScrollBarOverlay.Control);
            _webDocumentScrollBarOverlay.Dispose();
            _webDocumentScrollBarOverlay = null;
        }
        _documentLayer.Children.Remove(_webDocumentView);
        _webDocumentView.Dispose();
        _webDocumentView = null;
    }

    private void UpdateMinimapScrollState()
    {
        if (_minimap is null)
        {
            return;
        }

        _minimap.ScrollOffset = _scroll.Offset.Y;
        _minimap.ScrollMaximum = _scroll.ScrollBarMaximum.Y;
        _minimap.ViewportHeight = _scroll.Viewport.Height;
    }

    private void UpdateMinimapVisibility()
    {
        if (_minimap is null)
        {
            return;
        }

        var visible = ShouldShowMinimap();
        _minimapHost.IsVisible = visible;
        _minimapHost.IsHitTestVisible = visible;
        ApplyMinimapReservation();
        ApplyColumnWidth();
    }

    private bool HasMinimapLayoutMetricsChanged()
        => ApplicateDocumentMinimapBuildPolicy.HasLayoutMetricsChanged(
            _lastMinimapExtent,
            _lastMinimapViewport,
            _scroll.Extent,
            _scroll.Viewport);

    private bool ShouldShowMinimap()
    {
        var mode = _viewModel?.ReadingPreferences.DocumentMinimapMode ?? DocumentMinimapMode.Auto;
        if (GetLayoutRendererSurface() == ApplicateRendererSurfaceKind.WebView)
        {
            return false;
        }

        return ApplicateDocumentMinimapBuildPolicy.ShouldShow(
            mode,
            Bounds.Width,
            _scroll.Extent,
            _scroll.Viewport,
            _scroll.ScrollBarMaximum.Y);
    }

    private bool IsWebRendererActive()
        => _activeRendererSurface == ApplicateRendererSurfaceKind.WebView
           && _webDocumentView is not null;

    private bool IsWebRendererVisibleOrTargeted()
        => IsWebRendererActive()
           || _pendingRendererSurface == ApplicateRendererSurfaceKind.WebView;

    private bool IsRenderedSurfaceActive(object? sender)
        => ReferenceEquals(sender, _webDocumentView)
            ? IsWebRendererActive()
            : ReferenceEquals(sender, _documentView)
              && _activeRendererSurface == ApplicateRendererSurfaceKind.Native;

    private ApplicateRendererSurfaceKind ResolveRequestedRendererSurface()
        => ShouldRequestWebRenderer()
            ? ApplicateRendererSurfaceKind.WebView
            : ApplicateRendererSurfaceKind.Native;

    private ApplicateRendererSurfaceKind GetLayoutRendererSurface()
        => _pendingRendererSurface is { } pending && _pendingRendererReady
            ? pending
            : _activeRendererSurface;

    private void StageRendererSurface(ApplicateRendererSurfaceKind requestedSurface)
    {
        if (_pendingRendererSurface == requestedSurface)
        {
            ApplyRendererSurfaceVisuals();
            return;
        }

        if (_pendingRendererSurface is not null && requestedSurface == _activeRendererSurface)
        {
            CancelPendingRendererSurface();
            return;
        }

        if (_activeRendererSurface == requestedSurface)
        {
            _pendingRendererSurface = null;
            _pendingRendererReady = false;
            ApplyRendererSurfaceVisuals();
            return;
        }

        _pendingRendererSurface = requestedSurface;
        _pendingRendererReady = requestedSurface == ApplicateRendererSurfaceKind.Native;
        _rendererSwitchGeneration++;
        ApplyRendererSurfaceVisuals();

        if (_pendingRendererReady)
        {
            CommitPendingRendererSurface(requestedSurface);
        }
    }

    private void CommitPendingRendererSurface(ApplicateRendererSurfaceKind surface)
    {
        if (_pendingRendererSurface != surface)
        {
            return;
        }

        _pendingRendererReady = true;
        var generation = ++_rendererSwitchGeneration;
        ApplyColumnWidth();
        ApplyRendererSurfaceVisuals();
        RestoreReadingProgressForSurface(surface);
        _ = CompleteRendererSwitchAfterDelayAsync(surface, generation);
    }

    private async Task CompleteRendererSwitchAfterDelayAsync(
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
            ApplyColumnWidth();
            ApplyRendererSurfaceVisuals();
            RestoreReadingProgressForSurface(surface);
        });
    }

    private void RestoreReadingProgressForSurface(ApplicateRendererSurfaceKind surface)
    {
        if (surface == ApplicateRendererSurfaceKind.WebView)
        {
            _webDocumentView?.ScrollToProgress(_lastReadingProgress);
            return;
        }

        RestoreNativeReadingProgress();
        Dispatcher.UIThread.Post(RestoreNativeReadingProgress, DispatcherPriority.Loaded);
    }

    private void RestoreNativeReadingProgress()
    {
        var maxOffset = _scroll.ScrollBarMaximum.Y;
        if (maxOffset <= 0)
        {
            return;
        }

        var targetOffset = SysMath.Clamp(_lastReadingProgress / 100.0 * maxOffset, 0, maxOffset);
        _scroll.Offset = new Vector(_scroll.Offset.X, targetOffset);
    }

    private void CancelPendingRendererSurface()
    {
        _pendingRendererSurface = null;
        _pendingRendererReady = false;
        _rendererSwitchGeneration++;
        ApplyColumnWidth();
        ApplyRendererSurfaceVisuals();
    }

    private void ApplyRendererSurfaceVisuals()
    {
        ApplicateRendererSurfaceTransition.ApplyVisualState(
            _documentView,
            ApplicateRendererSurfaceTransition.CalculateVisualState(
                ApplicateRendererSurfaceKind.Native,
                _activeRendererSurface,
                _pendingRendererSurface,
                _pendingRendererReady));

        if (_webDocumentView is not null)
        {
            ApplicateRendererSurfaceTransition.ApplyVisualState(
                _webDocumentView,
                ApplicateRendererSurfaceTransition.CalculateVisualState(
                    ApplicateRendererSurfaceKind.WebView,
                    _activeRendererSurface,
                    _pendingRendererSurface,
                    _pendingRendererReady));
        }
    }

    private bool ShouldRequestWebRenderer()
        => _viewModel?.DocumentReadingPreferences.RendererBackend == MarkdownRendererBackend.WebView
           && _htmlRenderer is not null
           && !_webRendererFailedForCurrentDocument;

    internal bool IsWidthHandleOutsideDocumentLayerForTesting
        => _widthHandle.Parent is not null
           && !ReferenceEquals(_widthHandle.Parent, _documentLayer);

    public void Dispose()
    {
        DisposeWebDocumentView();
        GC.SuppressFinalize(this);
    }
}

internal readonly record struct ApplicateWidthHandleVisualState(
    double Width,
    double Opacity,
    bool UseAccentBrush);
