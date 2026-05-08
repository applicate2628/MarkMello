using Avalonia;
using Avalonia.Controls;
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
    private const double ScrollSyncTopInset = 24;
    private const int MaxScrollSyncAttachAttempts = 4;

    private TextBox? _editorTextBox;
    private ScrollViewer? _editorScrollViewer;
    private ScrollViewer? _previewScrollViewer;
    private MarkdownDocumentView? _previewDocumentView;
    private bool _isSynchronizingScroll;

    public EditWorkspaceView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        DataContextChanged += OnDataContextChanged;
        ApplySplitRatio();
        AttachScrollSynchronizationAsync();
        FocusEditorAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DataContextChanged -= OnDataContextChanged;
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
        _editorScrollViewer = _editorTextBox?
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();

        if (_editorScrollViewer is null || _previewScrollViewer is null || _previewDocumentView is null)
        {
            if (attempt < MaxScrollSyncAttachAttempts)
            {
                AttachScrollSynchronizationAsync(attempt + 1);
            }

            return;
        }

        _editorScrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
        _previewScrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
        _previewDocumentView.DocumentRendered += OnPreviewDocumentRendered;
        _previewDocumentView.DocumentRenderInvalidated += OnPreviewDocumentRenderInvalidated;

        SynchronizePreviewToEditor();
    }

    private void DetachScrollSynchronization()
    {
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
        _editorScrollViewer = null;
        _previewScrollViewer = null;
        _previewDocumentView = null;
        _isSynchronizingScroll = false;
    }

    private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ScrollViewer.OffsetProperty || _isSynchronizingScroll)
        {
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
        if (_editorTextBox is null
            || _editorScrollViewer is null
            || _previewScrollViewer is null
            || _previewDocumentView is null
            || !_previewDocumentView.TryGetVerticalOffsetForSourceLine(GetEditorTopSourceLine(), out var previewDocumentOffsetY))
        {
            return;
        }

        var previewDocumentOriginY = GetPreviewDocumentContentOriginY();
        SetSynchronizedVerticalOffset(
            _previewScrollViewer,
            previewDocumentOriginY + previewDocumentOffsetY - ScrollSyncTopInset);
    }

    private void SynchronizeEditorToPreview()
    {
        if (_editorTextBox is null
            || _editorScrollViewer is null
            || _previewScrollViewer is null
            || _previewDocumentView is null)
        {
            return;
        }

        var previewDocumentOffsetY = Math.Max(
            0,
            _previewScrollViewer.Offset.Y - GetPreviewDocumentContentOriginY() + ScrollSyncTopInset);

        if (!_previewDocumentView.TryGetSourceLineForVerticalOffset(previewDocumentOffsetY, out var sourceLine))
        {
            return;
        }

        sourceLine = Math.Clamp(sourceLine, 0, Math.Max(0, CountEditorSourceLines() - 1));
        SetSynchronizedVerticalOffset(
            _editorScrollViewer,
            sourceLine * ResolveEditorLineHeight(_editorTextBox));
    }

    private int GetEditorTopSourceLine()
    {
        if (_editorTextBox is null || _editorScrollViewer is null)
        {
            return 0;
        }

        var lineHeight = ResolveEditorLineHeight(_editorTextBox);
        var sourceLine = (int)Math.Floor(Math.Max(0, _editorScrollViewer.Offset.Y + ScrollSyncTopInset) / lineHeight);
        return Math.Clamp(sourceLine, 0, Math.Max(0, CountEditorSourceLines() - 1));
    }

    private int CountEditorSourceLines()
    {
        var text = _editorTextBox?.Text;
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

    private double GetPreviewDocumentContentOriginY()
    {
        if (_previewDocumentView is null || _previewScrollViewer is null)
        {
            return 0;
        }

        var origin = _previewDocumentView.TranslatePoint(new Point(0, 0), _previewScrollViewer);
        return origin is null
            ? 0
            : _previewScrollViewer.Offset.Y + origin.Value.Y;
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

    private static double ResolveEditorLineHeight(TextBox editor)
        => Math.Max(1, editor.FontSize * 1.45);

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

    private static void SetSplitterDraggingState(object? sender, bool isDragging)
    {
        if (sender is Control control)
        {
            control.Classes.Set("dragging", isDragging);
        }
    }
}
