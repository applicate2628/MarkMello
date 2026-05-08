using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MarkMello.Applicate.Desktop.Views.Minimap;
using MarkMello.Domain;
using MarkMello.Presentation.ViewModels;
using SysMath = System.Math;

namespace MarkMello.Applicate.Desktop.Views;

public sealed class ApplicateViewerView : UserControl
{
    private const double WheelStepMultiplier = 6.0;
    private const double MinManualContentWidth = 320.0;
    private const double ViewportHorizontalGutter = 32.0;
    private const double MinimapColumnGap = 24.0;
    private const double WidthHandleHitArea = 24.0;
    private const double WidthHandleIdleTrackWidth = 2.0;
    private const double WidthHandleHoverTrackWidth = 5.0;
    private const double WidthHandleDraggingTrackWidth = 7.0;

    private readonly ScrollViewer _scroll;
    private readonly Border _scrollContentFrame;
    private readonly Border _column;
    private readonly ApplicateMarkdownDocumentView _documentView;
    private readonly Border _widthHandle;
    private readonly Border _widthHandleTrack;
    private readonly ContentControl _minimapHost;
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
    private double _documentHorizontalPadding = 144.0;
    private Size _lastMinimapExtent;
    private Size _lastMinimapViewport;

    public ApplicateViewerView()
    {
        _documentView = new ApplicateMarkdownDocumentView
        {
            DocumentPadding = new Thickness(72, 96, 72, 160),
            UseLayoutRounding = true
        };
        _documentView.DocumentRendered += OnDocumentRendered;
        _documentView.DocumentRenderInvalidated += OnDocumentRenderInvalidated;

        _widthHandleTrack = new Border
        {
            Width = WidthHandleIdleTrackWidth,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 42, 6, 42),
            CornerRadius = new CornerRadius(99),
            Background = Brush("MmTextFaintBrush", new SolidColorBrush(Color.FromArgb(70, 120, 120, 120))),
            Opacity = 0.18,
            IsHitTestVisible = false,
            Transitions =
            [
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(160)
                },
                new DoubleTransition
                {
                    Property = Layoutable.WidthProperty,
                    Duration = TimeSpan.FromMilliseconds(160)
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

        var documentLayer = new Grid { UseLayoutRounding = true };
        documentLayer.Children.Add(_documentView);
        documentLayer.Children.Add(_widthHandle);

        _column = new Border
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            UseLayoutRounding = true,
            Child = documentLayer
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
            _manualContentWidth = null;
            _lastViewModelContentWidth = 0;
            _column.MaxWidth = double.PositiveInfinity;
            return;
        }

        _documentView.Document = _viewModel.RenderedDocument;
        _documentView.ReadingPreferences = _viewModel.DocumentReadingPreferences;
        _documentView.ImageSourceResolver = _viewModel.ImageSourceResolver;

        var viewModelContentWidth = _viewModel.ContentWidthSetting;
        _documentHorizontalPadding = SysMath.Max(0, _viewModel.DocumentColumnMaxWidth - viewModelContentWidth);
        if (_manualContentWidth is null ||
            (!_isDraggingWidth && SysMath.Abs(viewModelContentWidth - _lastViewModelContentWidth) > double.Epsilon))
        {
            _manualContentWidth = viewModelContentWidth;
        }

        _lastViewModelContentWidth = viewModelContentWidth;
        ApplyColumnWidth();
    }

    private void OnDocumentRendered(object? sender, EventArgs e)
    {
        _viewModel?.MarkReadableDocumentRendered();
        _hasRenderedDocument = true;
        QueueMinimapBuild();
        Dispatcher.UIThread.Post(QueueMinimapBuild, DispatcherPriority.Loaded);
    }

    private void OnDocumentRenderInvalidated(object? sender, EventArgs e)
    {
        _hasRenderedDocument = false;
        _lastMinimapExtent = default;
        _lastMinimapViewport = default;
        _minimapBuildGeneration++;
        RemoveMinimap();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var max = _scroll.ScrollBarMaximum.Y;
        var current = _scroll.Offset.Y;
        _viewModel.ReadingProgress = max > 0 ? SysMath.Clamp(current / max * 100.0, 0, 100) : 0;

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

        var maxOffset = _scroll.ScrollBarMaximum.Y;
        if (maxOffset <= 0)
        {
            return;
        }

        var baseStep = _scroll.SmallChange.Height > 0 ? _scroll.SmallChange.Height : 16.0;
        var nextOffset = SysMath.Clamp(_scroll.Offset.Y - e.Delta.Y * baseStep * WheelStepMultiplier, 0, maxOffset);
        if (SysMath.Abs(nextOffset - _scroll.Offset.Y) <= double.Epsilon)
        {
            return;
        }

        _scroll.Offset = new Vector(_scroll.Offset.X, nextOffset);
        e.Handled = true;
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
        e.Handled = true;
    }

    private void OnWidthHandlePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingWidth || _viewModel is null)
        {
            return;
        }

        var delta = e.GetPosition(this).X - _dragStart.X;
        _manualContentWidth = ClampManualContentWidth(_dragStartWidth + delta * 2.0);
        ApplyColumnWidth();
        e.Handled = true;
    }

    private void OnWidthHandlePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingWidth)
        {
            return;
        }

        _isDraggingWidth = false;
        UpdateWidthHandleVisual();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnWidthHandlePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isDraggingWidth = false;
        UpdateWidthHandleVisual();
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
        _documentView.AvailableContentWidth = visibleContentWidth;
        _column.MaxWidth = visibleContentWidth + _documentHorizontalPadding;
    }

    private double ClampManualContentWidth(double contentWidth)
    {
        var availableWidth = SysMath.Max(
            MinManualContentWidth,
            Bounds.Width - GetMinimapReservedWidth() - _documentHorizontalPadding - ViewportHorizontalGutter);
        return SysMath.Clamp(contentWidth, MinManualContentWidth, availableWidth);
    }

    private void ApplyMinimapReservation()
    {
        var reservedWidth = GetMinimapReservedWidth();
        _scrollContentFrame.Padding = new Thickness(0, 0, reservedWidth, 0);
    }

    private double GetMinimapReservedWidth()
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
        if (_isDraggingWidth)
        {
            _widthHandleTrack.Width = WidthHandleDraggingTrackWidth;
            _widthHandleTrack.Opacity = 0.9;
            _widthHandleTrack.Background = Brush("MmAccentBrush", Brushes.OrangeRed);
            return;
        }

        if (_isWidthHandleHovering)
        {
            _widthHandleTrack.Width = WidthHandleHoverTrackWidth;
            _widthHandleTrack.Opacity = 0.72;
            _widthHandleTrack.Background = Brush("MmAccentBrush", Brushes.OrangeRed);
            return;
        }

        _widthHandleTrack.Width = WidthHandleIdleTrackWidth;
        _widthHandleTrack.Opacity = 0.18;
        _widthHandleTrack.Background = Brush("MmTextFaintBrush", new SolidColorBrush(Color.FromArgb(70, 120, 120, 120)));
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
        return ApplicateDocumentMinimapBuildPolicy.ShouldShow(
            mode,
            Bounds.Width,
            _scroll.Extent,
            _scroll.Viewport,
            _scroll.ScrollBarMaximum.Y);
    }
}
