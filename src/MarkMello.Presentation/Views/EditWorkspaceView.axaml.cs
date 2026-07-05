using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using MarkMello.Presentation.Diagnostics;
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
    private static readonly TimeSpan ScrollSyncFeedbackGuard = TimeSpan.FromMilliseconds(160);

    // SPIKE: source pane was a TextBox; now an AvaloniaEdit.TextEditor.
    // Scroll-sync and format-button helpers are stubbed for this spike.
    private TextEditor? _editorTextEditor;
    private ScrollViewer? _editorScrollViewer;
    private ScrollViewer? _previewScrollViewer;
    private MarkdownDocumentView? _previewDocumentView;
    private ISourceLineScrollSyncPreview? _previewSourceLineSync;
    private readonly List<ScrollBar> _scrollBarsWithDragHandlers = [];
    private readonly DispatcherTimer _scrollBarDragSettleTimer;
    private bool _isSynchronizingScroll;
    private ScrollViewer? _activeScrollBarDragSource;
    private EditorSessionViewModel? _boundSession;
    private bool _firstVisualLinesLogged;
    private DateTime _ignoreEditorScrollUntil;
    private DateTime _ignorePreviewSourceLineUntil;
    // Bidirectional source sync. The AvaloniaEdit source-pane spike (f1d18a9)
    // wired only session -> editor (ApplySourceTextToEditor); the reverse was
    // never ported from the old TextBox TwoWay binding. Without it, typing never
    // reached EditorSession.SourceText, so IsDirty stayed false: no dirty star,
    // no Save button, and Save persisted stale text. _writeBackEditor is the
    // TextEditor we subscribed to; _suppressEditorWriteBack guards the
    // session -> editor Document rebuild so it does not echo back.
    private TextEditor? _writeBackEditor;
    private bool _suppressEditorWriteBack;

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
        if (_writeBackEditor is not null)
        {
            _writeBackEditor.TextChanged -= OnEditorTextChanged;
            _writeBackEditor = null;
        }
        if (_boundSession is not null)
        {
            _boundSession.PropertyChanged -= OnBoundSessionPropertyChanged;
            _boundSession = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private bool _paneSeqFirstVisibleLayoutLogged;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        StartupDiag.DiagMs(
            "pane-seq",
            "editview-datacontext-changed",
            $"hasSession={(DataContext is not null)} bounds={Bounds.Width:F0}x{Bounds.Height:F0} isVisible={IsVisible} isEffectivelyVisible={IsEffectivelyVisible}");
        // One-shot probe: log the FIRST LayoutUpdated after DataContext attach
        // where the EditWorkspaceView has effective-visible bounds matching
        // the edit pane (>600 width). This is "source pane is actually visible
        // to the user" — distinct from "host-hwnd-shown" which is the WebView2
        // preview pane visibility. Gap between the two = pane-sequencing bug.
        if (DataContext is not null && !_paneSeqFirstVisibleLayoutLogged)
        {
            EventHandler? handler = null;
            handler = (_, _) =>
            {
                if (_paneSeqFirstVisibleLayoutLogged)
                {
                    LayoutUpdated -= handler!;
                    return;
                }
                if (IsEffectivelyVisible && Bounds.Width > 600)
                {
                    _paneSeqFirstVisibleLayoutLogged = true;
                    LayoutUpdated -= handler!;
                    StartupDiag.DiagMs(
                        "pane-seq",
                        "editview-first-visible-layout",
                        $"bounds={Bounds.Width:F0}x{Bounds.Height:F0}");
                }
            };
            LayoutUpdated += handler;
        }
        BindSourceTextToEditor();
        ApplySplitRatio();
        SynchronizePreviewToEditor();
    }

    private void BindSourceTextToEditor()
    {
        if (_boundSession is not null)
        {
            _boundSession.PropertyChanged -= OnBoundSessionPropertyChanged;
            _boundSession = null;
        }

        if (DataContext is not EditorSessionViewModel session)
        {
            return;
        }

        _boundSession = session;
        session.PropertyChanged += OnBoundSessionPropertyChanged;

        ApplySourceTextToEditor(session.SourceText);
    }

    private void OnBoundSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EditorSessionViewModel.SourceText) || _boundSession is null)
        {
            return;
        }

        ApplySourceTextToEditor(_boundSession.SourceText);
    }

    private void ApplySourceTextToEditor(string? text)
    {
        var editor = _editorTextEditor ?? this.FindControl<TextEditor>("EditorTextEditor");
        if (editor is null)
        {
            return;
        }

        EnsureEditorWriteBack(editor);

        var newText = text ?? string.Empty;
        var currentText = editor.Document?.Text;
        // No-op when already in sync. This is the common case when the change
        // echoes a write-back that originated in the editor itself; rebuilding
        // the Document would reset the caret to the start on every keystroke.
        if (string.Equals(currentText, newText, StringComparison.Ordinal))
        {
            return;
        }

        // Surgical single-char delta (in-place task-toggle channel, edit mode):
        // an external buffer change that flips exactly one char (TryFlipMarker's
        // shape) is applied as a 1-char Document.Replace. Preserves caret,
        // scroll, selection, and undo — the editor's ScrollViewer offset never
        // moves, so the always-on preview scroll-sync never drags the preview
        // to line 0. The invariant is general: a minimal external buffer delta
        // must not destroy editor view/undo state, whoever produced it.
        if (currentText is not null
            && TryGetSingleCharDelta(currentText, newText, out var deltaOffset))
        {
            _suppressEditorWriteBack = true;
            try
            {
                editor.Document!.Replace(deltaOffset, 1, newText[deltaOffset].ToString());
            }
            finally
            {
                _suppressEditorWriteBack = false;
            }

            return;
        }

        // Replace whole Document — fallback for genuine document swaps (load,
        // tab switch, discard, external multi-char change). Preserves
        // AvaloniaEdit virtualization semantics and avoids partial-text-change
        // events. Suppress the write-back so this session -> editor push does
        // not bounce back.
        _suppressEditorWriteBack = true;
        try
        {
            editor.Document = new TextDocument(newText);
        }
        finally
        {
            _suppressEditorWriteBack = false;
        }

        AttachFirstVisualLinesProbe(editor);
    }

    /// <summary>
    /// True when <paramref name="newText"/> differs from <paramref name="oldText"/>
    /// by exactly one char at one offset (equal lengths) — the shape the
    /// task-toggle channel produces. Wider or length-changing edits return
    /// false (full rebuild).
    /// </summary>
    internal static bool TryGetSingleCharDelta(string oldText, string newText, out int offset)
    {
        offset = -1;
        if (oldText.Length != newText.Length)
        {
            return false;
        }

        var first = -1;
        for (var i = 0; i < oldText.Length; i++)
        {
            if (oldText[i] != newText[i])
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            return false; // identical — the caller's equality guard owns this case
        }

        for (var i = oldText.Length - 1; i > first; i--)
        {
            if (oldText[i] != newText[i])
            {
                return false;
            }
        }

        offset = first;
        return true;
    }

    // Subscribe the editor -> session direction exactly once per TextEditor
    // instance (idempotent via the ReferenceEquals check).
    private void EnsureEditorWriteBack(TextEditor editor)
    {
        if (ReferenceEquals(_writeBackEditor, editor))
        {
            return;
        }

        if (_writeBackEditor is not null)
        {
            _writeBackEditor.TextChanged -= OnEditorTextChanged;
        }

        _writeBackEditor = editor;
        editor.TextChanged += OnEditorTextChanged;
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorWriteBack || _boundSession is null)
        {
            return;
        }

        // Push typed text into the session so IsDirty / Save button / dirty star
        // track edits, and Save persists what the user actually typed.
        _boundSession.SourceText = (sender as TextEditor)?.Document?.Text
            ?? _writeBackEditor?.Document?.Text
            ?? string.Empty;
    }

    private void AttachFirstVisualLinesProbe(TextEditor editor)
    {
        if (_firstVisualLinesLogged)
        {
            return;
        }

        var textView = editor.TextArea?.TextView;
        if (textView is null)
        {
            return;
        }

        EventHandler? layoutHandler = null;
        layoutHandler = (_, _) =>
        {
            if (_firstVisualLinesLogged)
            {
                textView.LayoutUpdated -= layoutHandler!;
                return;
            }

            try
            {
                var visualLineCount = textView.VisualLines.Count;
                if (visualLineCount <= 0)
                {
                    return;
                }

                _firstVisualLinesLogged = true;
                textView.LayoutUpdated -= layoutHandler!;
                StartupDiag.DiagMs(
                    "pane-seq",
                    "editview-texteditor-first-visual-lines",
                    $"visualLineCount={visualLineCount} textViewBounds={textView.Bounds.Width:F0}x{textView.Bounds.Height:F0} documentLines={editor.Document?.LineCount ?? 0}");
            }
            catch (VisualLinesInvalidException)
            {
                // not ready yet — wait for next LayoutUpdated
            }
        };
        textView.LayoutUpdated += layoutHandler;
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

        _editorTextEditor = this.FindControl<TextEditor>("EditorTextEditor");
        _previewScrollViewer = this.FindControl<ScrollViewer>("PreviewScrollViewer");
        _previewDocumentView = this.FindControl<MarkdownDocumentView>("PreviewDocumentView");
        // Structural lookup FIRST: the named Border was silently dropped by an
        // upstream merge once already (the sync contract was dead wiring in
        // Applicate — the fork injects its preview at runtime, so no .axaml
        // carries the name). A visual-tree type scan survives future upstream
        // rewrites; the named path stays as a cheap fast-path fallback.
        _previewSourceLineSync =
            this.GetVisualDescendants().OfType<ISourceLineScrollSyncPreview>().FirstOrDefault()
            ?? this.FindControl<Border>("PreviewDocumentFrame")?.Child as ISourceLineScrollSyncPreview;
        var editorVisuals = _editorTextEditor?
            .GetVisualDescendants()
            .ToArray();
        _editorScrollViewer = editorVisuals?
            .OfType<ScrollViewer>()
            .FirstOrDefault();

        if (_editorTextEditor is null
            || _editorScrollViewer is null
            || _previewScrollViewer is null
            || (_previewDocumentView is null && _previewSourceLineSync is null))
        {
            if (attempt < MaxScrollSyncAttachAttempts)
            {
                AttachScrollSynchronizationAsync(attempt + 1);
            }

            return;
        }

        StartupDiag.DiagMs(
            "pane-seq",
            "editview-texteditor-found",
            $"editorBounds={_editorTextEditor.Bounds.Width:F0}x{_editorTextEditor.Bounds.Height:F0} attempt={attempt}");

        // Ensure the editor -> session write-back is wired even when the editor
        // resolves here (after the initial bind) rather than in ApplySourceTextToEditor.
        EnsureEditorWriteBack(_editorTextEditor);

        // SPIKE: TextPresenter-based scroll-sync removed because AvaloniaEdit
        // exposes TextView (with VisualLines) instead of TextPresenter.TextLayout.
        // Two-pane scroll sync is non-functional in this spike — measurement
        // only. Production migration must port HitTestPoint /
        // HitTestTextPosition usage to AvaloniaEdit's TextView /
        // GetVisualPosition API.

        // Trace the TextEditor's first visual-lines built event — direct
        // analog of "editview-textpresenter-shaped" for the legacy TextBox.
        AttachFirstVisualLinesProbe(_editorTextEditor);

        _editorScrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
        _previewScrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
        AttachScrollBarDragHandlers(_editorScrollViewer);
        AttachScrollBarDragHandlers(_previewScrollViewer);
        if (_previewDocumentView is not null)
        {
            _previewDocumentView.DocumentRendered += OnPreviewDocumentRendered;
            _previewDocumentView.DocumentRenderInvalidated += OnPreviewDocumentRenderInvalidated;
        }
        if (_previewSourceLineSync is not null)
        {
            _previewSourceLineSync.SourceLineScrollSyncPreviewRendered += OnPreviewDocumentRendered;
            _previewSourceLineSync.PreviewSourceLineChanged += OnPreviewSourceLineChanged;
        }

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

        if (_previewSourceLineSync is not null)
        {
            _previewSourceLineSync.SourceLineScrollSyncPreviewRendered -= OnPreviewDocumentRendered;
            _previewSourceLineSync.PreviewSourceLineChanged -= OnPreviewSourceLineChanged;
        }

        _editorTextEditor = null;
        _editorScrollViewer = null;
        _previewScrollViewer = null;
        _previewDocumentView = null;
        _previewSourceLineSync = null;
        _isSynchronizingScroll = false;
        _ignoreEditorScrollUntil = DateTime.MinValue;
        _ignorePreviewSourceLineUntil = DateTime.MinValue;
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
            if (DateTime.UtcNow < _ignoreEditorScrollUntil)
            {
                return;
            }

            SynchronizePreviewToEditor();
            return;
        }

        if (ReferenceEquals(sender, _previewScrollViewer))
        {
            SynchronizeEditorToPreview();
        }
    }

    private void OnPreviewDocumentRendered(object? sender, EventArgs e)
        // The editor owns the position (seeded at entry; held through 1-char
        // toggle Replace) — the rendered-event editor->preview re-assert IS the
        // re-render restore. Unconditional; SyncEnabled gates inside.
        => SynchronizePreviewToEditor();

    private void OnPreviewDocumentRenderInvalidated(object? sender, EventArgs e)
    {
        // The preview is about to rebuild and its source-line anchors are stale.
        // The rendered event will restore synchronization after the new layout pass.
    }

    private void SynchronizePreviewToEditor()
    {
        if (_previewScrollViewer is null
            || !TryGetEditorSourceLineAtViewportAnchor(out var sourceLine))
        {
            return;
        }

        // The preview's sync toggle gates the whole line loop (default ON).
        if (_previewSourceLineSync is { SyncEnabled: false })
        {
            return;
        }

        if (_previewSourceLineSync is not null)
        {
            _ignorePreviewSourceLineUntil = DateTime.UtcNow + ScrollSyncFeedbackGuard;
            _previewSourceLineSync.ScrollToSourceLine(sourceLine);
            return;
        }

        if (_previewDocumentView is null
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

    private void OnPreviewSourceLineChanged(object? sender, SourceLineScrollSyncEventArgs e)
    {
        if (_isSynchronizingScroll || DateTime.UtcNow < _ignorePreviewSourceLineUntil)
        {
            return;
        }

        if (_previewSourceLineSync is { SyncEnabled: false })
        {
            return;
        }

        ScrollEditorToSourceLine(e.SourceLine);
    }

    private bool TryGetEditorSourceLineAtViewportAnchor(out int sourceLine)
    {
        sourceLine = 0;
        if (_editorTextEditor?.TextArea?.TextView is not { } textView
            || _editorScrollViewer is null)
        {
            return false;
        }

        try
        {
            var visualLines = textView.VisualLines;
            if (visualLines.Count == 0)
            {
                return false;
            }

            var targetY = _editorScrollViewer.Offset.Y + GetViewportAnchorY(_editorScrollViewer);
            var selected = visualLines[0];
            foreach (var visualLine in visualLines)
            {
                if (visualLine.VisualTop > targetY)
                {
                    break;
                }

                selected = visualLine;
                if (visualLine.VisualTop + visualLine.Height >= targetY)
                {
                    break;
                }
            }

            var startLine = Math.Max(0, selected.FirstDocumentLine.LineNumber - 1);
            var endLine = Math.Max(startLine, selected.LastDocumentLine.LineNumber - 1);
            sourceLine = startLine;
            if (endLine > startLine && selected.Height > 1)
            {
                var ratio = Math.Clamp((targetY - selected.VisualTop) / selected.Height, 0, 1);
                sourceLine = Math.Clamp(
                    startLine + (int)Math.Round((endLine - startLine) * ratio, MidpointRounding.AwayFromZero),
                    startLine,
                    endLine);
            }

            return true;
        }
        catch (VisualLinesInvalidException)
        {
            return false;
        }
    }

    private bool TryGetEditorVerticalOffsetForSourceLine(int sourceLine, out double offsetY)
    {
        offsetY = 0;
        if (_editorTextEditor?.TextArea?.TextView is not { } textView
            || _editorTextEditor.Document is not { } document
            || _editorScrollViewer is null
            || sourceLine < 0
            || sourceLine >= document.LineCount)
        {
            return false;
        }

        var lineNumber = sourceLine + 1;
        try
        {
            // Height-tree lookup: soft-wrap-aware and the exact metric
            // VisualLine.VisualTop is derived from, so this write mapping stays
            // consistent with TryGetEditorSourceLineAtViewportAnchor's read
            // mapping by construction. The former line*DefaultLineHeight
            // fallback ignored wrapped lines and silently drifted the editor
            // tens of source lines behind the preview on prose documents
            // (runtime trace 2026-07-04: write placed line 236 at Y=4815, the
            // read mapped Y=4815 back to line 162 — the panes were never
            // actually in sync, and every preview re-render re-anchored to the
            // editor's REAL line, which read as a huge jump).
            var visualTop = textView.GetVisualTopByDocumentLine(lineNumber);
            offsetY = Math.Max(0, visualTop - GetViewportAnchorY(_editorScrollViewer));
            return true;
        }
        catch (InvalidOperationException)
        {
            // The TextView has no document attached yet (attach/detach
            // transition window) — no mapping is available this tick.
            return false;
        }
    }

    private void ScrollEditorToSourceLine(int sourceLine)
    {
        if (_editorTextEditor?.Document is not { } document || document.LineCount <= 0)
        {
            return;
        }

        // ONE sync contract: the target line lands at the editor's 38%-viewport
        // anchor — the same reference point BOTH panes read and write
        // (preview writes its anchor the same way). ScrollToLine was
        // middle-of-viewport with a 30% dead-zone (AvaloniaEdit LineMiddle +
        // MinimumScrollFraction), one of the three inconsistent reference
        // points that made panes settle on different chunks. The offset comes
        // from the height tree (wrap-aware), so it matches what the read side
        // will report back for the landed position.
        if (_editorScrollViewer is null
            || !TryGetEditorVerticalOffsetForSourceLine(sourceLine, out var editorOffsetY))
        {
            return;
        }

        _ignoreEditorScrollUntil = DateTime.UtcNow + ScrollSyncFeedbackGuard;
        _isSynchronizingScroll = true;
        try
        {
            SetSynchronizedVerticalOffset(_editorScrollViewer, editorOffsetY);
        }
        finally
        {
            _isSynchronizingScroll = false;
        }
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
        // SPIKE: format-button helper is a no-op in this spike because the
        // source pane no longer exposes TextBox.SelectionStart/End. Production
        // migration must port to TextEditor.TextArea.Selection API.
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
            var editor = this.FindControl<TextEditor>("EditorTextEditor");
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
