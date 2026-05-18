using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MarkMello.Presentation.Editing;
using MarkMello.Presentation.ViewModels;

namespace MarkMello.Presentation.Views;

public partial class EditWorkspaceView : UserControl
{
    private const double ScrollSyncViewportAnchorRatio = 0.38;
    private const double ScrollSyncMinViewportAnchorY = 24;
    private const double ScrollSyncHitTestX = 2;
    private const int MaxScrollSyncAttachAttempts = 4;
    private const int ScrollBarDragSettleDelayMs = 120;

    private TextBox? _editorTextBox;
    private TextPresenter? _editorTextPresenter;
    private ScrollViewer? _editorScrollViewer;
    private ScrollViewer? _previewScrollViewer;
    private MarkdownDocumentView? _previewDocumentView;
    private readonly List<ScrollBar> _scrollBarsWithDragHandlers = [];
    private readonly DispatcherTimer _scrollBarDragSettleTimer;
    private bool _isSynchronizingScroll;
    private ScrollViewer? _activeScrollBarDragSource;

    public EditWorkspaceView()
    {
        InitializeComponent();
        _scrollBarDragSettleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(ScrollBarDragSettleDelayMs)
        };
        _scrollBarDragSettleTimer.Tick += OnScrollBarDragSettleTimerTick;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        DataContextChanged += OnDataContextChanged;
        AddHandler(PointerPressedEvent, OnScrollBarDragPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnScrollBarDragPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnScrollBarDragPointerCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
        ApplySplitRatio();
        AttachScrollSynchronizationAsync();
        FocusEditorAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DataContextChanged -= OnDataContextChanged;
        RemoveHandler(PointerPressedEvent, OnScrollBarDragPointerPressed);
        RemoveHandler(PointerReleasedEvent, OnScrollBarDragPointerReleased);
        RemoveHandler(PointerCaptureLostEvent, OnScrollBarDragPointerCaptureLost);
        DetachScrollSynchronization();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        ApplySplitRatio();
        SynchronizePreviewToEditor();
    }

    private void AttachScrollSynchronizationAsync(int attempt = 0)
    {
        Dispatcher.UIThread.Post(() => AttachScrollSynchronization(attempt), DispatcherPriority.Background);
    }

    private void AttachScrollSynchronization(int attempt)
    {
        if (VisualRoot is null)
        {
            return;
        }

        DetachScrollSynchronization();

        _editorTextBox = this.FindControl<TextBox>("EditorTextBox");
        _previewScrollViewer = this.FindControl<ScrollViewer>("PreviewScrollViewer");
        _previewDocumentView = this.FindControl<MarkdownDocumentView>("PreviewDocumentView");
        var editorVisuals = _editorTextBox?
            .GetVisualDescendants()
            .ToArray();
        _editorScrollViewer = editorVisuals?
            .OfType<ScrollViewer>()
            .FirstOrDefault();
        _editorTextPresenter = editorVisuals?
            .OfType<TextPresenter>()
            .FirstOrDefault(static presenter => presenter.Name == "PART_TextPresenter")
            ?? editorVisuals?
                .OfType<TextPresenter>()
                .FirstOrDefault();

        if (_editorScrollViewer is null
            || _editorTextPresenter is null
            || _previewScrollViewer is null
            || _previewDocumentView is null)
        {
            if (attempt < MaxScrollSyncAttachAttempts)
            {
                AttachScrollSynchronizationAsync(attempt + 1);
            }

            return;
        }

        _editorScrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
        _previewScrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
        AttachScrollBarDragHandlers(_editorScrollViewer);
        AttachScrollBarDragHandlers(_previewScrollViewer);
        _previewDocumentView.DocumentRendered += OnPreviewDocumentRendered;
        _previewDocumentView.DocumentRenderInvalidated += OnPreviewDocumentRenderInvalidated;

        SynchronizePreviewToEditor();
    }

    private void DetachScrollSynchronization()
    {
        DetachScrollBarDragHandlers();

        if (_editorScrollViewer is not null)
        {
            _editorScrollViewer.PropertyChanged -= OnScrollViewerPropertyChanged;
        }

        if (_previewScrollViewer is not null)
        {
            _previewScrollViewer.PropertyChanged -= OnScrollViewerPropertyChanged;
        }

        if (_previewDocumentView is not null)
        {
            _previewDocumentView.DocumentRendered -= OnPreviewDocumentRendered;
            _previewDocumentView.DocumentRenderInvalidated -= OnPreviewDocumentRenderInvalidated;
        }

        _editorTextBox = null;
        _editorTextPresenter = null;
        _editorScrollViewer = null;
        _previewScrollViewer = null;
        _previewDocumentView = null;
        _isSynchronizingScroll = false;
        _activeScrollBarDragSource = null;
        _scrollBarDragSettleTimer.Stop();
    }

    private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ScrollViewer.OffsetProperty || _isSynchronizingScroll)
        {
            return;
        }

        if (_activeScrollBarDragSource is not null)
        {
            if (ReferenceEquals(sender, _activeScrollBarDragSource))
            {
                RestartScrollBarDragSettleTimer();
            }

            return;
        }

        if (ReferenceEquals(sender, _editorScrollViewer))
        {
            SynchronizePreviewToEditor();
            return;
        }

        if (ReferenceEquals(sender, _previewScrollViewer))
        {
            SynchronizeEditorToPreview();
        }
    }

    private void OnPreviewDocumentRendered(object? sender, EventArgs e)
        => SynchronizePreviewToEditor();

    private void OnPreviewDocumentRenderInvalidated(object? sender, EventArgs e)
    {
        // The preview is about to rebuild and its source-line anchors are stale.
        // The rendered event will restore synchronization after the new layout pass.
    }

    private void SynchronizePreviewToEditor()
    {
        if (_previewScrollViewer is null
            || _previewDocumentView is null
            || !TryGetEditorSourceLineAtViewportAnchor(out var sourceLine)
            || !_previewDocumentView.TryGetVerticalOffsetForSourceLine(sourceLine, out var previewDocumentOffsetY)
            || !TryGetViewportRelativeOriginY(_previewDocumentView, _previewScrollViewer, out var previewDocumentOriginY))
        {
            return;
        }

        var targetOffsetY = _previewScrollViewer.Offset.Y
            + previewDocumentOriginY
            + previewDocumentOffsetY
            - GetViewportAnchorY(_previewScrollViewer);
        SetSynchronizedVerticalOffset(_previewScrollViewer, targetOffsetY);
    }

    private void SynchronizeEditorToPreview()
    {
        if (_previewScrollViewer is null
            || _previewDocumentView is null
            || !TryGetViewportRelativeOriginY(_previewDocumentView, _previewScrollViewer, out var previewDocumentOriginY))
        {
            return;
        }

        var previewDocumentOffsetY = Math.Max(
            0,
            GetViewportAnchorY(_previewScrollViewer) - previewDocumentOriginY);

        if (!_previewDocumentView.TryGetSourceLineForVerticalOffset(previewDocumentOffsetY, out var sourceLine)
            || !TryGetEditorVerticalOffsetForSourceLine(sourceLine, out var editorOffsetY))
        {
            return;
        }

        SetSynchronizedVerticalOffset(_editorScrollViewer!, editorOffsetY);
    }

    private bool TryGetEditorSourceLineAtViewportAnchor(out int sourceLine)
    {
        sourceLine = 0;
        if (_editorTextBox is null
            || _editorTextPresenter is null
            || _editorScrollViewer is null
            || !TryGetViewportRelativeOriginY(_editorTextPresenter, _editorScrollViewer, out var presenterOriginY))
        {
            return false;
        }

        var text = _editorTextBox.Text ?? string.Empty;
        var localY = Math.Clamp(
            GetViewportAnchorY(_editorScrollViewer) - presenterOriginY,
            0,
            Math.Max(0, _editorTextPresenter.Bounds.Height - 1));
        var localX = Math.Clamp(
            ScrollSyncHitTestX,
            0,
            Math.Max(0, _editorTextPresenter.Bounds.Width - 1));

        var hit = _editorTextPresenter.TextLayout.HitTestPoint(new Point(localX, localY));
        var characterIndex = Math.Clamp(hit.TextPosition, 0, text.Length);
        sourceLine = GetSourceLineFromCharacterIndex(text, characterIndex);
        sourceLine = Math.Clamp(sourceLine, 0, Math.Max(0, CountSourceLines(text) - 1));
        return true;
    }

    private bool TryGetEditorVerticalOffsetForSourceLine(int sourceLine, out double offsetY)
    {
        offsetY = 0;
        if (_editorTextBox is null
            || _editorTextPresenter is null
            || _editorScrollViewer is null
            || !TryGetViewportRelativeOriginY(_editorTextPresenter, _editorScrollViewer, out var presenterOriginY))
        {
            return false;
        }

        var text = _editorTextBox.Text ?? string.Empty;
        var lineStartCharacterIndex = GetLineStartCharacterIndex(text, sourceLine);
        var lineBounds = _editorTextPresenter.TextLayout.HitTestTextPosition(lineStartCharacterIndex);
        offsetY = _editorScrollViewer.Offset.Y
            + presenterOriginY
            + lineBounds.Y
            - GetViewportAnchorY(_editorScrollViewer);
        return true;
    }

    private static bool TryGetViewportRelativeOriginY(Control control, Visual relativeTo, out double originY)
    {
        originY = 0;
        var origin = control.TranslatePoint(new Point(0, 0), relativeTo);
        if (origin is null)
        {
            return false;
        }

        originY = origin.Value.Y;
        return true;
    }

    private static double GetViewportAnchorY(ScrollViewer scrollViewer)
    {
        var viewportHeight = Math.Max(0, scrollViewer.Bounds.Height);
        if (viewportHeight <= 0)
        {
            return ScrollSyncMinViewportAnchorY;
        }

        if (viewportHeight <= ScrollSyncMinViewportAnchorY * 2)
        {
            return viewportHeight * 0.5;
        }

        return Math.Clamp(
            viewportHeight * ScrollSyncViewportAnchorRatio,
            ScrollSyncMinViewportAnchorY,
            viewportHeight - ScrollSyncMinViewportAnchorY);
    }

    private static int GetSourceLineFromCharacterIndex(string text, int characterIndex)
    {
        var normalizedIndex = Math.Clamp(characterIndex, 0, text.Length);
        var line = 0;
        for (var index = 0; index < normalizedIndex; index++)
        {
            if (text[index] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static int GetLineStartCharacterIndex(string text, int sourceLine)
    {
        if (string.IsNullOrEmpty(text) || sourceLine <= 0)
        {
            return 0;
        }

        var currentLine = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n')
            {
                continue;
            }

            currentLine++;
            if (currentLine >= sourceLine)
            {
                return Math.Min(text.Length, index + 1);
            }
        }

        return text.Length;
    }

    private static int CountSourceLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 1;
        }

        var count = 1;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private void SetSynchronizedVerticalOffset(ScrollViewer scrollViewer, double offsetY)
    {
        var maximumY = Math.Max(0, scrollViewer.ScrollBarMaximum.Y);
        var normalizedY = Math.Clamp(offsetY, 0, maximumY);
        if (Math.Abs(scrollViewer.Offset.Y - normalizedY) < 0.5)
        {
            return;
        }

        _isSynchronizingScroll = true;
        try
        {
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, normalizedY);
        }
        finally
        {
            _isSynchronizingScroll = false;
        }
    }

    private void OnScrollBarDragPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _activeScrollBarDragSource = TryGetOwnedScrollViewerFromScrollBarChrome(e.Source);
    }

    private void OnScrollBarDragPointerReleased(object? sender, PointerReleasedEventArgs e)
        => CompleteScrollBarDrag();

    private void OnScrollBarDragPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => CompleteScrollBarDrag();

    private void CompleteScrollBarDrag()
    {
        var source = _activeScrollBarDragSource;
        if (source is null)
        {
            return;
        }

        _activeScrollBarDragSource = null;
        _scrollBarDragSettleTimer.Stop();

        Dispatcher.UIThread.Post(() => SynchronizeFromScrollBarDragSource(source), DispatcherPriority.Background);
    }

    private void OnScrollBarDragSettleTimerTick(object? sender, EventArgs e)
    {
        _scrollBarDragSettleTimer.Stop();
        var source = _activeScrollBarDragSource;
        if (source is not null)
        {
            SynchronizeFromScrollBarDragSource(source);
        }
    }

    private void RestartScrollBarDragSettleTimer()
    {
        _scrollBarDragSettleTimer.Stop();
        _scrollBarDragSettleTimer.Start();
    }

    private void SynchronizeFromScrollBarDragSource(ScrollViewer source)
    {
        if (ReferenceEquals(source, _editorScrollViewer))
        {
            SynchronizePreviewToEditor();
            return;
        }

        if (ReferenceEquals(source, _previewScrollViewer))
        {
            SynchronizeEditorToPreview();
        }
    }

    private ScrollViewer? TryGetOwnedScrollViewerFromScrollBarChrome(object? source)
    {
        if (source is not Control control)
        {
            return null;
        }

        var scrollBar = control as ScrollBar ?? control.FindAncestorOfType<ScrollBar>();
        var scrollViewer = scrollBar?.FindAncestorOfType<ScrollViewer>();
        if (ReferenceEquals(scrollViewer, _editorScrollViewer)
            || ReferenceEquals(scrollViewer, _previewScrollViewer))
        {
            return scrollViewer;
        }

        return null;
    }

    private void AttachScrollBarDragHandlers(ScrollViewer scrollViewer)
    {
        foreach (var scrollBar in scrollViewer.GetVisualDescendants().OfType<ScrollBar>())
        {
            scrollBar.AddHandler(PointerPressedEvent, OnScrollBarDragPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            scrollBar.AddHandler(PointerReleasedEvent, OnScrollBarDragPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            scrollBar.AddHandler(PointerCaptureLostEvent, OnScrollBarDragPointerCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
            _scrollBarsWithDragHandlers.Add(scrollBar);
        }
    }

    private void DetachScrollBarDragHandlers()
    {
        foreach (var scrollBar in _scrollBarsWithDragHandlers)
        {
            scrollBar.RemoveHandler(PointerPressedEvent, OnScrollBarDragPointerPressed);
            scrollBar.RemoveHandler(PointerReleasedEvent, OnScrollBarDragPointerReleased);
            scrollBar.RemoveHandler(PointerCaptureLostEvent, OnScrollBarDragPointerCaptureLost);
        }

        _scrollBarsWithDragHandlers.Clear();
    }

    private void OnFormatButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EditorSessionViewModel session)
        {
            return;
        }

        if (sender is not Button button || button.Tag is not string rawKind)
        {
            return;
        }

        if (!Enum.TryParse<MarkdownEditorFormatKind>(rawKind, ignoreCase: true, out var kind))
        {
            return;
        }

        var editor = this.FindControl<TextBox>("EditorTextBox");
        if (editor is null)
        {
            return;
        }

        var selectionStart = Math.Min(editor.SelectionStart, editor.SelectionEnd);
        var selectionEnd = Math.Max(editor.SelectionStart, editor.SelectionEnd);
        var result = MarkdownEditorFormatter.Apply(session.SourceText, kind, selectionStart, selectionEnd);

        editor.Text = result.Text;
        editor.SelectionStart = result.SelectionStart;
        editor.SelectionEnd = result.SelectionEnd;
        editor.CaretIndex = result.SelectionEnd;
        editor.Focus();
    }

    private void OnSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        SetSplitterDraggingState(sender, isDragging: false);

        if (DataContext is not EditorSessionViewModel session)
        {
            return;
        }

        var grid = this.FindControl<Grid>("EditGrid");
        if (grid is null || grid.ColumnDefinitions.Count < 3)
        {
            return;
        }

        var leftWidth = grid.ColumnDefinitions[0].ActualWidth;
        var rightWidth = grid.ColumnDefinitions[2].ActualWidth;
        var totalWidth = leftWidth + rightWidth;
        if (totalWidth <= 0)
        {
            return;
        }

        session.SplitRatio = leftWidth / totalWidth;
    }

    private void OnSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
        => SetSplitterDraggingState(sender, isDragging: true);

    private void OnSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
        => SetSplitterDraggingState(sender, isDragging: false);

    private void OnSplitterPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => SetSplitterDraggingState(sender, isDragging: false);

    private void ApplySplitRatio()
    {
        if (DataContext is not EditorSessionViewModel session)
        {
            return;
        }

        var grid = this.FindControl<Grid>("EditGrid");
        if (grid is null || grid.ColumnDefinitions.Count < 3)
        {
            return;
        }

        var ratio = Math.Clamp(session.SplitRatio, 0.2, 0.8);
        grid.ColumnDefinitions[0].Width = new GridLength(ratio, GridUnitType.Star);
        grid.ColumnDefinitions[2].Width = new GridLength(1 - ratio, GridUnitType.Star);
    }

    private void FocusEditorAsync()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var editor = this.FindControl<TextBox>("EditorTextBox");
            editor?.Focus();
        }, DispatcherPriority.Background);
    }

    public bool TryReplacePreviewDocumentView(Control previewDocumentView)
    {
        ArgumentNullException.ThrowIfNull(previewDocumentView);

        var frame = this.FindControl<Border>("PreviewDocumentFrame");
        if (frame is null)
        {
            return false;
        }

        frame.Child = previewDocumentView;
        return true;
    }

    private static void SetSplitterDraggingState(object? sender, bool isDragging)
    {
        if (sender is Control control)
        {
            control.Classes.Set("dragging", isDragging);
        }
    }
}
