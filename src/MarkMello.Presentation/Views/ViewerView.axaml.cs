using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MarkMello.Presentation.ViewModels;
using MarkMello.Presentation.Views.Markdown.Minimap;

namespace MarkMello.Presentation.Views;

public partial class ViewerView : UserControl
{
    private const double WheelStepMultiplier = 6.0;
    private const double MinimapMinWidth = 1100.0;
    private const double MinimapMinScrollableViewportRatio = 1.5;
    private ScrollViewer? _scroll;
    private MarkdownDocumentView? _documentView;
    private ContentControl? _minimapHost;
    private DocumentMinimapView? _minimap;
    private int _minimapBuildGeneration;
    private bool _hasRenderedDocument;

    public ViewerView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroll = this.FindControl<ScrollViewer>("DocScroll");
        if (_scroll is not null)
        {
            _scroll.ScrollChanged += OnScrollChanged;
            _scroll.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);
        }

        _minimapHost = this.FindControl<ContentControl>("MinimapHost");
        if (_minimapHost is not null)
        {
            _minimapHost.IsHitTestVisible = false;
        }

        _documentView = this.FindControl<MarkdownDocumentView>("DocumentView");
        if (_documentView is not null)
        {
            _documentView.DocumentRendered += OnDocumentRendered;
        }

        SizeChanged += OnViewerSizeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SizeChanged -= OnViewerSizeChanged;
        _minimapBuildGeneration++;
        RemoveMinimap();
        _hasRenderedDocument = false;
        _minimapHost = null;

        if (_scroll is not null)
        {
            _scroll.ScrollChanged -= OnScrollChanged;
            _scroll.RemoveHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged);
            _scroll = null;
        }

        if (_documentView is not null)
        {
            _documentView.DocumentRendered -= OnDocumentRendered;
            _documentView = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_scroll is null || Math.Abs(e.Delta.Y) <= double.Epsilon)
        {
            return;
        }

        // Preserve horizontal wheel gestures for nested controls such as
        // horizontally scrollable code blocks. We only take over primarily
        // vertical scrolling to match the faster browser-like reading feel.
        if (Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y))
        {
            return;
        }

        var maxOffset = _scroll.ScrollBarMaximum.Y;
        if (maxOffset <= 0)
        {
            return;
        }

        var baseStep = _scroll.SmallChange.Height > 0 ? _scroll.SmallChange.Height : 16.0;
        var wheelStep = baseStep * WheelStepMultiplier;
        var nextOffset = Math.Clamp(_scroll.Offset.Y - e.Delta.Y * wheelStep, 0, maxOffset);

        if (Math.Abs(nextOffset - _scroll.Offset.Y) <= double.Epsilon)
        {
            return;
        }

        _scroll.Offset = new Vector(_scroll.Offset.X, nextOffset);
        e.Handled = true;
    }

    private void OnDocumentRendered(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.MarkReadableDocumentRendered();
        }

        _hasRenderedDocument = true;
        QueueMinimapBuild();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroll is null)
        {
            return;
        }

        var max = _scroll.ScrollBarMaximum.Y;
        var current = _scroll.Offset.Y;
        if (DataContext is MainWindowViewModel vm)
        {
            vm.ReadingProgress = max > 0 ? Math.Clamp(current / max * 100.0, 0, 100) : 0;
        }

        UpdateMinimapScrollState();
        UpdateMinimapVisibility();
    }

    private void OnViewerSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_hasRenderedDocument)
        {
            return;
        }

        QueueMinimapBuild();
    }

    private void QueueMinimapBuild()
    {
        _minimapBuildGeneration++;
        var generation = _minimapBuildGeneration;

        Dispatcher.UIThread.Post(
            () => BuildMinimapIfCurrent(generation),
            DispatcherPriority.Background);
    }

    private void BuildMinimapIfCurrent(int generation)
    {
        if (generation != _minimapBuildGeneration || _documentView is null || _scroll is null || _minimapHost is null)
        {
            return;
        }

        if (!ShouldShowMinimap())
        {
            RemoveMinimap();
            return;
        }

        var snapshot = _documentView.CreateMiniatureSnapshot();
        if (snapshot.IsEmpty)
        {
            RemoveMinimap();
            return;
        }

        var minimap = EnsureMinimap();
        minimap.SetSource(_documentView, snapshot);
        UpdateMinimapScrollState();
        UpdateMinimapVisibility();
    }

    private DocumentMinimapView EnsureMinimap()
    {
        if (_minimap is not null)
        {
            return _minimap;
        }

        var minimap = new DocumentMinimapView();
        minimap.ScrollRequested += OnMinimapScrollRequested;
        _minimap = minimap;

        if (_minimapHost is not null)
        {
            _minimapHost.Content = minimap;
            _minimapHost.IsHitTestVisible = true;
        }

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

        if (_minimapHost is not null)
        {
            _minimapHost.Content = null;
            _minimapHost.IsHitTestVisible = false;
        }
    }

    private void OnMinimapScrollRequested(object? sender, DocumentMinimapScrollRequestedEventArgs e)
    {
        if (_scroll is null)
        {
            return;
        }

        var targetOffset = Math.Clamp(e.OffsetY, 0, _scroll.ScrollBarMaximum.Y);
        _scroll.Offset = new Vector(_scroll.Offset.X, targetOffset);
    }

    private void UpdateMinimapScrollState()
    {
        if (_scroll is null || _minimap is null)
        {
            return;
        }

        _minimap.ScrollOffset = _scroll.Offset.Y;
        _minimap.ScrollMaximum = _scroll.ScrollBarMaximum.Y;
        _minimap.ViewportHeight = _scroll.Viewport.Height;
    }

    private void UpdateMinimapVisibility()
    {
        if (_minimapHost is null || _minimap is null)
        {
            return;
        }

        var visible = ShouldShowMinimap();
        _minimapHost.IsVisible = visible;
        _minimapHost.IsHitTestVisible = visible;
    }

    private bool ShouldShowMinimap()
    {
        if (_scroll is null)
        {
            return false;
        }

        var viewportHeight = _scroll.Viewport.Height;
        var documentHeight = _scroll.Extent.Height;
        return Bounds.Width >= MinimapMinWidth
            && viewportHeight > 0
            && _scroll.ScrollBarMaximum.Y > 0
            && documentHeight >= viewportHeight * MinimapMinScrollableViewportRatio;
    }
}
