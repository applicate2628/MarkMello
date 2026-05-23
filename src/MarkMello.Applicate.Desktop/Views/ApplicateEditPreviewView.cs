using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using MarkMello.Applicate.Desktop.Diagnostics;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Domain;
using MarkMello.Presentation.ViewModels;
using MarkMello.Presentation.Views;
using SysMath = System.Math;

namespace MarkMello.Applicate.Desktop.Views;

internal sealed class ApplicateEditPreviewView : UserControl, ISourceLineScrollSyncPreview, IDisposable
{
    // F-02 fix: horizontal sides come from
    // ApplicateDocumentLayout.CalculatePreviewDocumentPadding ->
    // ReadingLayoutMetrics.GetDocumentHorizontalPadding so both consumer
    // surfaces (viewer + edit-preview) use one source of truth. The
    // previous Thickness(72, 96, 72, 160) literal sum (72 + 72 = 144)
    // happened to match the canonical 144 px horizontal padding by
    // coincidence at default ContentWidth = 820 only; any custom
    // ContentWidth made the consumer column drift from the viewer.
    // F-16 note: vertical 96/160 reserve preview-toolbar gap (top) and
    // end-of-doc breathing room (bottom).
    private const double PreviewDocumentPaddingTop = 96;
    private const double PreviewDocumentPaddingBottom = 160;
    private static readonly TimeSpan WebPreviewDebounce = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan ResizeContentWidthDebounce = TimeSpan.FromMilliseconds(140);

    // Window after a programmatic scroll during which the OPPOSITE side's
    // scroll events are ignored, suppressing the editor↔preview ping-pong
    // loop. 200ms covers a typical Avalonia scroll animation tick + the
    // round-trip into WebView2's renderer thread.
    private static readonly TimeSpan SyncOriginGuard = TimeSpan.FromMilliseconds(200);

    private readonly IApplicateSharedWebViewHost? _sharedHost;
    private readonly Grid _root = new() { UseLayoutRounding = true };
    private readonly Grid _surface = new() { UseLayoutRounding = true };

    // F-01 fix: reserve a right strip for the Avalonia ScrollBar overlay.
    // WebView2 HWND fills _webSlot via NativeControlHost - without this margin
    // the HWND would paint into the scrollbar's Avalonia airspace via Win32
    // z-order. Width comes from ApplicateDocumentLayout.GetWebSlotScrollBarGutter
    // which reads the canonical ScrollBarSize theme resource, so the slot and
    // the painted bar always agree.
    private readonly Panel _webSlot = new() { UseLayoutRounding = true, Margin = ApplicateDocumentLayout.GetWebSlotScrollBarGutter() };
    private readonly ApplicateRendererFailureView _failureView;
    private WebViewHostScrollBarOverlay? _scrollBarOverlay;
    private readonly ToggleButton _syncToggle;
    private readonly DispatcherTimer _webRenderTimer;
    private readonly DispatcherTimer _resizeContentWidthTimer;
    private EditorSessionViewModel? _session;
    private ScrollViewer? _hostScrollViewer;
    private ScrollBarVisibility? _hostScrollViewerVerticalMode;
    private TextEditor? _editorTextEditor;
    private ScrollViewer? _editorScrollViewer;
    private TextBox? _dropTargetTextBox;
    private MainWindowViewModel? _viewModel;
    private bool _isAttachedToHost;
    private bool _hostEventsWired;
    private bool _syncEnabled;
    private DateTime _ignoreEditorScrollUntil;
    private DateTime _ignorePreviewScrollUntil;
    // EP-A startup gate. Mirrors ApplicateViewerView._hasValidBounds. The
    // FIRST render after edit-preview becomes visible would otherwise fire
    // while _webSlot.Bounds is still 0x0 (Avalonia hasn't measured the
    // newly-cascade-visible slot yet) — CalculatePreviewWidths falls back
    // to preferredColumnWidth (ContentWidth + 144 padding) so the renderer
    // paints the document at the fallback width, then ~16-140 ms later
    // the bounds settle, OnSizeChanged debounces ApplyAvailableWidth, the
    // renderer gets the real (smaller) width, and the user sees the
    // document visibly reflow ("становится криво"). The gate flips once
    // on the first valid measurement of _webSlot; subsequent activations
    // and tab-switches keep it true.
    private bool _hasValidSlotBounds;

    public event EventHandler? SourceLineScrollSyncPreviewRendered;

    public event EventHandler<SourceLineScrollSyncEventArgs>? PreviewSourceLineChanged;

    public ApplicateEditPreviewView(IApplicateSharedWebViewHost? sharedHost)
    {
        _sharedHost = sharedHost;

        _failureView = new ApplicateRendererFailureView
        {
            IsVisible = false,
            // F-01 fix: same right margin as _webSlot keeps the failure surface
            // flush against the scrollbar overlay strip; both consume the
            // canonical ScrollBarSize theme resource via ApplicateDocumentLayout.
            Margin = ApplicateDocumentLayout.GetWebSlotScrollBarGutter(),
        };

        _surface.Children.Add(_webSlot);
        _surface.Children.Add(_failureView);

        // Avalonia ScrollBar overlay (Option A — Chromium-authority +
        // Avalonia-mirror per consultant blueprint .scratch/codex-prompts/
        // option-a-avalonia-scrollbar-overlay-blueprint.md). Replaces the
        // WebKit ::-webkit-scrollbar so thumb-drag uses Avalonia pointer
        // capture (no sideways release-zone) and runs in the same layout
        // pass as the rest of the visual tree (no IPC mouse-thumb lag).
        if (_sharedHost is not null)
        {
            _scrollBarOverlay = new WebViewHostScrollBarOverlay(_sharedHost.View);
            _surface.Children.Add(_scrollBarOverlay.Control);
        }

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
        _resizeContentWidthTimer = new DispatcherTimer { Interval = ResizeContentWidthDebounce };
        _resizeContentWidthTimer.Tick += OnResizeContentWidthTimerTick;

        _webSlot.PropertyChanged += OnWebSlotPropertyChanged;
    }

    private void OnWebSlotPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.BoundsProperty && e.Property != Visual.IsVisibleProperty)
        {
            return;
        }
        ApplicateTrace.ModeToggle($"_webSlot.{e.Property.Name}: {e.OldValue} -> {e.NewValue}");
        if (e.Property == Visual.BoundsProperty)
        {
            ApplicateTrace.DiagMs(
                "pane-seq",
                "webslot-bounds-changed",
                $"oldBounds={(e.OldValue as Rect?)?.Width:F0}x{(e.OldValue as Rect?)?.Height:F0} newBounds={_webSlot.Bounds.Width:F0}x{_webSlot.Bounds.Height:F0}");
        }
        else
        {
            ApplicateTrace.DiagMs(
                "pane-seq",
                "webslot-visible-changed",
                $"isVisible={_webSlot.IsVisible}");
        }

        // After permanent attach is complete, slot resize events (window
        // resize, splitter drag, sidebar toggle) still need to flow into the
        // WebView's MinHeight + AvailableContentWidth so the Chromium HWND
        // viewport matches the visible slot. Without this, the HWND retains
        // the size set at attach time and Chromium's scrollbar drag math
        // operates against a viewport that no longer matches the visible
        // track length — user sees "mouse outpaces thumb".
        if (e.Property == Visual.BoundsProperty
            && _sharedHost is not null
            && _isAttachedToHost
            && _webSlot.Bounds.Width > 0
            && _webSlot.Bounds.Height > 0)
        {
            // EP-A startup gate: first valid slot measurement releases the
            // gate and fires one immediate render against the now-known
            // column width, replacing the would-be-stale 964 fallback render
            // that would otherwise paint before bounds settled.
            if (!_hasValidSlotBounds)
            {
                _hasValidSlotBounds = true;
                if (_session is not null && IsEffectivelyVisible)
                {
                    QueueWebPreviewRender(immediate: true);
                }
                // Fall through to ApplyAvailableWidth so the slot-based
                // MinHeight overwrites any stale value set during the
                // pre-bounds window. Previously this path early-returned,
                // leaving MinHeight=editPreview.Bounds.Height (toolbar+
                // slot), causing the View to overflow upward — exactly
                // the first-edit-activation overlap symptom.
            }

            ApplyAvailableWidth(deferWebContentWidth: true);
        }
    }

    private static Border BuildPreviewToolbar(ToggleButton syncToggle)
    {
        var label = new TextBlock
        {
            Text = "PREVIEW",
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.Classes.Add("mm-editor-toolbar-label");

        var leftGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { label },
        };

        var rightGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { syncToggle },
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

    // TODO(architectural-cleanliness): toolbar-toggle dimensions should live in
    // ApplicateScrollBars.axaml or a Themes/Controls.axaml style class
    // ("mm-editor-toolbar-toggle"). Until then this single helper is the only
    // place that mints toolbar toggles, so every consumer reads from one
    // source instead of duplicating literals across BuildSyncToggle /
    // BuildHidePreviewToggle.
    private static ToggleButton CreateToolbarToggle(string glyph)
    {
        return new ToggleButton
        {
            Width = 28,
            Height = 24,
            MinWidth = 28,
            MinHeight = 24,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new TextBlock
            {
                Text = glyph,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            IsChecked = false,
            IsThreeState = false,
        };
    }

    private ToggleButton BuildSyncToggle()
    {
        var toggle = CreateToolbarToggle("⇅");
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
            ForwardEditorScrollToPreview();
        }
    }

    private void EnsureEditorWiring()
    {
        if (_editorTextEditor is not null && _editorScrollViewer is not null)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var editor = topLevel.GetVisualDescendants()
            .OfType<TextEditor>()
            .FirstOrDefault(static editor => string.Equals(editor.Name, "EditorTextEditor", StringComparison.Ordinal));
        if (editor is null)
        {
            return;
        }

        var scrollViewer = editor.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
        if (scrollViewer is null)
        {
            return;
        }

        _editorTextEditor = editor;
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
        _editorTextEditor = null;
    }

    private static readonly string[] MarkdownInsertExtensions =
        new[] { ".md", ".markdown", ".mdown", ".markdn", ".txt" };

    private static readonly string[] ImageInsertExtensions =
        new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg" };

    private void EnsureEditorDropWiring()
    {
        if (_dropTargetTextBox is not null)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var textBox = topLevel.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(static tb => string.Equals(tb.Name, "EditorTextBox", StringComparison.Ordinal));
        if (textBox is null)
        {
            return;
        }

        DragDrop.SetAllowDrop(textBox, true);
        textBox.AddHandler(DragDrop.DragOverEvent, OnEditorDragOver);
        textBox.AddHandler(DragDrop.DropEvent, OnEditorDrop);
        _dropTargetTextBox = textBox;
    }

    private void TeardownEditorDropWiring()
    {
        if (_dropTargetTextBox is null)
        {
            return;
        }

        _dropTargetTextBox.RemoveHandler(DragDrop.DragOverEvent, OnEditorDragOver);
        _dropTargetTextBox.RemoveHandler(DragDrop.DropEvent, OnEditorDrop);
        _dropTargetTextBox = null;
    }

    private void OnEditorDragOver(object? sender, DragEventArgs e)
    {
        if (TryGetFirstFilePath(e) is { Length: > 0 } path
            && IsInsertableFile(path))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void OnEditorDrop(object? sender, DragEventArgs e)
    {
        if (_session is null || _dropTargetTextBox is null)
        {
            return;
        }

        var path = TryGetFirstFilePath(e);
        if (string.IsNullOrEmpty(path) || !IsInsertableFile(path))
        {
            return;
        }

        // Mark handled BEFORE the await so the routed event does not bubble
        // to the window-level OnDrop. Avalonia processes routed events
        // synchronously, but this handler is async void — by the time the
        // await resumes, the event has already finished routing and the
        // window's OnDrop would have opened the dropped .md as a new tab
        // in addition to the in-place insert.
        e.Handled = true;

        try
        {
            var insertText = await BuildInsertTextAsync(path, _session.CurrentPath).ConfigureAwait(true);
            if (string.IsNullOrEmpty(insertText))
            {
                return;
            }
            InsertAtCaret(insertText);
        }
        catch
        {
            // Best-effort: an unreadable file just no-ops; user can retry.
        }
    }

    private static string? TryGetFirstFilePath(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
        {
            return null;
        }

        foreach (var item in files)
        {
            if (item is IStorageFile file)
            {
                var path = file.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static bool IsInsertableFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return MarkdownInsertExtensions.Contains(ext) || ImageInsertExtensions.Contains(ext);
    }

    private static Task<string?> BuildInsertTextAsync(string sourcePath, string? currentDocumentPath)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();

        if (ImageInsertExtensions.Contains(ext))
        {
            return BuildImageInsertAsync(sourcePath, currentDocumentPath, ext)!;
        }

        if (MarkdownInsertExtensions.Contains(ext))
        {
            var displayName = Path.GetFileNameWithoutExtension(sourcePath);
            var target = BuildRelativeLinkTarget(sourcePath, currentDocumentPath);
            return Task.FromResult<string?>($"[{displayName}]({target})");
        }

        return Task.FromResult<string?>(null);
    }

    private static string BuildRelativeLinkTarget(string sourcePath, string? currentDocumentPath)
    {
        var hostDir = string.IsNullOrWhiteSpace(currentDocumentPath)
            ? null
            : Path.GetDirectoryName(currentDocumentPath);

        if (string.IsNullOrWhiteSpace(hostDir))
        {
            return EncodeMarkdownLinkTarget(sourcePath);
        }

        try
        {
            var relative = Path.GetRelativePath(hostDir, sourcePath).Replace('\\', '/');
            return EncodeMarkdownLinkTarget(relative);
        }
        catch (ArgumentException)
        {
            return EncodeMarkdownLinkTarget(sourcePath);
        }
    }

    private static string EncodeMarkdownLinkTarget(string target)
    {
        if (target.Contains(' ') || target.Contains('(') || target.Contains(')'))
        {
            return "<" + target + ">";
        }
        return target;
    }

    private static async Task<string> BuildImageInsertAsync(
        string sourcePath,
        string? currentDocumentPath,
        string ext)
    {
        var altText = Path.GetFileNameWithoutExtension(sourcePath);
        var documentDirectory = string.IsNullOrWhiteSpace(currentDocumentPath)
            ? null
            : Path.GetDirectoryName(currentDocumentPath);

        if (!string.IsNullOrWhiteSpace(documentDirectory) && Directory.Exists(documentDirectory))
        {
            var imagesDir = Path.Combine(documentDirectory, "images");
            Directory.CreateDirectory(imagesDir);

            var fileName = Path.GetFileName(sourcePath);
            var targetPath = Path.Combine(imagesDir, fileName);
            var sourceBytes = await File.ReadAllBytesAsync(sourcePath).ConfigureAwait(true);

            targetPath = await ReserveTargetPathAsync(targetPath, sourceBytes).ConfigureAwait(true);

            if (!File.Exists(targetPath))
            {
                await File.WriteAllBytesAsync(targetPath, sourceBytes).ConfigureAwait(true);
            }

            var relative = "images/" + Path.GetFileName(targetPath).Replace('\\', '/');
            return $"![{altText}]({EncodeMarkdownLinkTarget(relative)})";
        }

        var bytes = await File.ReadAllBytesAsync(sourcePath).ConfigureAwait(true);
        var base64 = Convert.ToBase64String(bytes);
        var mime = MimeTypeFromExtension(ext);
        return $"![{altText}](data:{mime};base64,{base64})";
    }

    private static async Task<string> ReserveTargetPathAsync(string desiredPath, byte[] sourceBytes)
    {
        if (File.Exists(desiredPath))
        {
            var existing = await File.ReadAllBytesAsync(desiredPath).ConfigureAwait(true);
            if (existing.AsSpan().SequenceEqual(sourceBytes))
            {
                return desiredPath;
            }

            var directory = Path.GetDirectoryName(desiredPath)!;
            var nameOnly = Path.GetFileNameWithoutExtension(desiredPath);
            var extension = Path.GetExtension(desiredPath);
            for (var i = 1; i < 1000; i++)
            {
                var candidate = Path.Combine(directory, $"{nameOnly}-{i}{extension}");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
                var candidateBytes = await File.ReadAllBytesAsync(candidate).ConfigureAwait(true);
                if (candidateBytes.AsSpan().SequenceEqual(sourceBytes))
                {
                    return candidate;
                }
            }
        }

        return desiredPath;
    }

    private static string MimeTypeFromExtension(string ext) => ext switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };

    private void InsertAtCaret(string insertText)
    {
        if (_session is null || _dropTargetTextBox is null)
        {
            return;
        }

        var caret = _dropTargetTextBox.CaretIndex;
        var currentText = _session.SourceText;
        if (caret < 0 || caret > currentText.Length)
        {
            caret = currentText.Length;
        }

        var charBefore = caret > 0 ? currentText[caret - 1] : '\n';
        var charAfter = caret < currentText.Length ? currentText[caret] : '\n';
        var leading = charBefore == '\n' ? string.Empty : "\n";
        var trailing = charAfter == '\n' ? string.Empty : "\n";
        var finalText = leading + insertText + trailing;

        _session.SourceText = currentText.Insert(caret, finalText);

        var newCaret = caret + finalText.Length;
        Dispatcher.UIThread.Post(() =>
        {
            if (_dropTargetTextBox is null)
            {
                return;
            }
            _dropTargetTextBox.CaretIndex = SysMath.Min(newCaret, _dropTargetTextBox.Text?.Length ?? 0);
            _dropTargetTextBox.Focus();
        }, DispatcherPriority.Background);
    }

    private void OnEditorScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (!_syncEnabled)
        {
            return;
        }

        if (DateTime.UtcNow < _ignoreEditorScrollUntil)
        {
            return;
        }

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
            _ignorePreviewScrollUntil = DateTime.UtcNow + SyncOriginGuard;
            _sharedHost.View.ScrollToProgress(percent);
        }
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

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.GetVisualParent() is not null)
        {
            AttachSession(DataContext as EditorSessionViewModel);
        }
        else if (DataContext is null)
        {
            AttachSession(null);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplicateTrace.ModeToggle($"EditPreview.OnAttachedToVisualTree Bounds={Bounds} _webSlot.Bounds={_webSlot.Bounds}");

        // Subscribe to ancestor visibility flips. The bridge owns
        // editSlot.IsVisible — when it flips true we become the active
        // consumer and AttachTo runs from OnEffectiveVisibilityChanged.
        PropertyChanged += OnEditPreviewPropertyChanged;
        AttachedToVisualTree += OnAnyAttachmentChange;
        DetachedFromVisualTree += OnAnyAttachmentChange;
        AttachAncestorVisibilityListeners();
        OnEffectiveVisibilityChanged();

        // Always wire host events even before we own the WebView — the
        // DocumentRendered/Failure subscriptions are idempotent and survive
        // viewer↔edit transitions.
        WireSharedHostEvents();
        AttachViewModel(TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel);

        AttachSession(DataContext as EditorSessionViewModel);
        UpdateHostScrollMode();
        Dispatcher.UIThread.Post(EnsureEditorDropWiring, DispatcherPriority.Background);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        PropertyChanged -= OnEditPreviewPropertyChanged;
        AttachedToVisualTree -= OnAnyAttachmentChange;
        DetachedFromVisualTree -= OnAnyAttachmentChange;
        DetachAncestorVisibilityListeners();
        RestoreHostScrollMode();
        TeardownEditorDropWiring();
        TeardownEditorWiring();
        AttachViewModel(null);
        AttachSession(null);
        _webRenderTimer.Stop();
        _resizeContentWidthTimer.Stop();
        UnwireSharedHostEvents();

        base.OnDetachedFromVisualTree(e);
    }

    private void OnEditPreviewPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty)
        {
            OnEffectiveVisibilityChanged();
        }
    }

    private readonly System.Collections.Generic.List<Avalonia.Visual> _ancestorListeners = new();

    private void OnAnyAttachmentChange(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DetachAncestorVisibilityListeners();
        AttachAncestorVisibilityListeners();
        OnEffectiveVisibilityChanged();
    }

    private void AttachAncestorVisibilityListeners()
    {
        for (Avalonia.Visual? v = this; v is not null; v = v.GetVisualParent())
        {
            v.PropertyChanged += OnAncestorPropertyChanged;
            _ancestorListeners.Add(v);
        }
    }

    private void DetachAncestorVisibilityListeners()
    {
        foreach (var v in _ancestorListeners)
        {
            v.PropertyChanged -= OnAncestorPropertyChanged;
        }
        _ancestorListeners.Clear();
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty)
        {
            OnEffectiveVisibilityChanged();
        }
    }

    private void OnEffectiveVisibilityChanged()
    {
        ApplicateTrace.DiagMs(
            "pane-seq",
            "editpreview-effvis-changed",
            $"isEffectivelyVisible={IsEffectivelyVisible} webSlotBounds={_webSlot.Bounds.Width:F0}x{_webSlot.Bounds.Height:F0} hasHost={_sharedHost is not null}");
        if (_sharedHost is null)
        {
            return;
        }

        if (IsEffectivelyVisible)
        {
            var intent = new ApplicateWebMountIntent(
                ViewerChromeEnabled: false,
                DocumentScrollEnabled: true,
                WheelProxyEnabled: false);
            _sharedHost.AttachTo(_webSlot, intent);
            _isAttachedToHost = true;
            // F-05 fix: signal consumer ownership of the scrollbar overlay
            // so its scroll-state mirror is no longer dormant.
            if (_scrollBarOverlay is not null)
            {
                _scrollBarOverlay.IsAttachedToHost = true;
            }
            // Issue a fresh render against the current session. The host's
            // RequestRender fast-path commits immediately if the source is
            // unchanged from a prior prewarm/render.
            if (_session is not null)
            {
                QueueWebPreviewRender(immediate: true);
            }
        }
        else
        {
            // Edit-preview is no longer the active consumer (mode toggle
            // back to viewer). Clear the local flag so the next visibility
            // flip will re-AttachTo cleanly.
            _isAttachedToHost = false;
            // F-05 fix: hand consumer ownership of the scrollbar overlay
            // back to the inactive state.
            if (_scrollBarOverlay is not null)
            {
                _scrollBarOverlay.IsAttachedToHost = false;
            }
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyAvailableWidth(deferWebContentWidth: true);
    }

    private void AttachSession(EditorSessionViewModel? session)
    {
        if (ReferenceEquals(_session, session))
        {
            ApplicateTrace.ModeToggle("EditPreview.AttachSession noop (same ref)");
            return;
        }
        ApplicateTrace.ModeToggle($"EditPreview.AttachSession from={(_session is not null)} to={(session is not null)}");

        // EP-B: a session swap means any previously-displayed failure overlay
        // belongs to the prior document. Clear it eagerly — the next render
        // will re-raise RendererFailed if the new document also fails, and
        // a successful fast-path Commit (HasLoadedDocumentForSource → true)
        // would otherwise leave the stale overlay visible because Commit
        // fires no DocumentRendered event for OnSharedDocumentRendered to
        // pick up.
        _failureView.IsVisible = false;

        if (_session is not null)
        {
            _session.PropertyChanged -= OnSessionPropertyChanged;
        }

        _session = session;

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
            ApplySession();
            return;
        }

        if (e.PropertyName is nameof(EditorSessionViewModel.SourceText)
            or nameof(EditorSessionViewModel.CurrentPath)
            or nameof(EditorSessionViewModel.FileName))
        {
            // Tab-switch in edit-mode flows through
            // MainWindowViewModel.ApplyLoadedDocument(preserveEditModeAfterLoad=true),
            // which MUTATES the EditorSession instance in place rather than
            // swapping the reference. Result: OnDataContextChanged does NOT
            // fire, AttachSession is NOT called, and ApplyAvailableWidth is
            // NOT invoked synchronously for the new source. Without this
            // synchronous re-apply, _sharedHost.View.MinHeight and
            // AvailableContentWidth stay at values computed for the previous
            // document until 140 ms after the next _webSlot BoundsChanged
            // event — and during that window the WebView2 HWND can be
            // reflowed with stale geometry against the new document load,
            // contributing to the "render on tab-switch is wrong" symptom.
            ApplyAvailableWidth(deferWebContentWidth: true);
            QueueWebPreviewRender(immediate: false);
        }
    }

    private void ApplySession()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_session is null)
        {
            // No session: stop the debounce timer; the host stays attached
            // to _webSlot (permanent mount) and its slot.IsVisible is owned
            // by the outer bridge. No render request is issued.
            _webRenderTimer.Stop();
        }
        else
        {
            QueueWebPreviewRender(immediate: true);
        }
        ApplyAvailableWidth();
        UpdateHostScrollMode();
        var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        ApplicateTrace.ModeToggle($"ApplySession elapsed={elapsedMs:F1}ms");
    }

    private void WireSharedHostEvents()
    {
        if (_sharedHost is null || _hostEventsWired)
        {
            return;
        }

        _sharedHost.View.DocumentRendered += OnSharedDocumentRendered;
        _sharedHost.View.ViewerInteractionRequested += OnSharedViewerInteractionRequested;
        _sharedHost.View.ScrollStateChanged += OnSharedScrollStateChanged;
        _sharedHost.View.PreviewSourceLineChanged += OnSharedPreviewSourceLineChanged;
        _sharedHost.View.HeadingsChanged += OnSharedHeadingsChanged;
        _sharedHost.View.ActiveHeadingChanged += OnSharedActiveHeadingChanged;
        _sharedHost.RendererFailed += OnSharedRendererFailed;
        _hostEventsWired = true;
    }

    private void UnwireSharedHostEvents()
    {
        if (_sharedHost is null || !_hostEventsWired)
        {
            return;
        }

        _sharedHost.View.DocumentRendered -= OnSharedDocumentRendered;
        _sharedHost.View.ViewerInteractionRequested -= OnSharedViewerInteractionRequested;
        _sharedHost.View.ScrollStateChanged -= OnSharedScrollStateChanged;
        _sharedHost.View.PreviewSourceLineChanged -= OnSharedPreviewSourceLineChanged;
        _sharedHost.View.HeadingsChanged -= OnSharedHeadingsChanged;
        _sharedHost.View.ActiveHeadingChanged -= OnSharedActiveHeadingChanged;
        _sharedHost.RendererFailed -= OnSharedRendererFailed;
        _hostEventsWired = false;
    }

    private void AttachViewModel(MainWindowViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.ScrollToHeadingRequested -= OnViewModelScrollToHeadingRequested;
        }

        _viewModel = viewModel;

        if (_viewModel is not null)
        {
            _viewModel.ScrollToHeadingRequested += OnViewModelScrollToHeadingRequested;
        }
    }

    private void OnSharedScrollStateChanged(object? sender, ApplicateWebDocumentScrollEventArgs e)
    {
        if (!_syncEnabled)
        {
            return;
        }

        if (DateTime.UtcNow < _ignorePreviewScrollUntil)
        {
            return;
        }

        ForwardPreviewScrollToEditor(e.ProgressPercent);
    }

    private void OnSharedPreviewSourceLineChanged(object? sender, ApplicateWebPreviewSourceLineEventArgs e)
    {
        if (!_isAttachedToHost)
        {
            return;
        }

        PreviewSourceLineChanged?.Invoke(this, new SourceLineScrollSyncEventArgs(e.SourceLine));
    }

    private void OnSharedHeadingsChanged(object? sender, System.Collections.Generic.IReadOnlyList<DocumentHeading> headings)
    {
        if (!_isAttachedToHost || _viewModel is null)
        {
            return;
        }

        _viewModel.UpdateDocumentHeadings(headings);
    }

    private void OnSharedActiveHeadingChanged(object? sender, string id)
    {
        if (!_isAttachedToHost || _viewModel is null)
        {
            return;
        }

        _viewModel.UpdateActiveHeadingFromRenderer(id);
    }

    private void OnSharedDocumentRendered(object? sender, EventArgs e)
    {
        ApplicateTrace.ModeToggle("SharedView Rendered (edit-preview)");
        // Render committed: hide any visible failure overlay.
        _failureView.IsVisible = false;
        SourceLineScrollSyncPreviewRendered?.Invoke(this, EventArgs.Empty);
    }

    private void OnSharedRendererFailed(object? sender, ApplicateRendererFailureEvent e)
    {
        // Consumer-side filter: react only when this view is the active host
        // consumer. The host fires RendererFailed once per failure but both
        // consumers (viewer + edit-preview) are subscribed; without this
        // filter the inactive surface would also show the failure overlay.
        // The single source of truth for "active consumer" is _isAttachedToHost.
        if (!_isAttachedToHost)
        {
            return;
        }

        _failureView.ShowFailure(
            e,
            retry: e.Kind == ApplicateRendererFailureKind.DocumentRenderFailed ? RetryCurrentRender : null);
    }

    private void RetryCurrentRender() => _sharedHost?.RetryRender();

    private void OnSharedViewerInteractionRequested(object? sender, EventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel { HasOpenOverlay: true } viewModel)
        {
            viewModel.CloseOverlayCommand.Execute(null);
        }
    }

    private void OnViewModelScrollToHeadingRequested(object? sender, string id)
    {
        if (!_isAttachedToHost || _sharedHost is null)
        {
            return;
        }

        _sharedHost.View.ScrollToHeading(id);
    }

    private void QueueWebPreviewRender(bool immediate)
    {
        if (_sharedHost is null || _session is null)
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
        if (_session is null || _sharedHost is null)
        {
            return;
        }

        // Only issue render requests when this consumer actually owns the
        // WebView (i.e. it is effectively visible and was AttachTo'd from
        // OnEffectiveVisibilityChanged). Without this gate the editor's
        // debounced source-changed callback would steal the WebView away
        // from the viewer mid-render.
        if (!_isAttachedToHost || !IsEffectivelyVisible)
        {
            return;
        }

        // EP-A startup gate. Suppress the FIRST render until _webSlot has
        // been measured at non-zero size. Without this, GetPreviewHostWidth
        // would return 0 (or stale fallback control width), CalculatePreview-
        // Widths would return preferredColumnWidth (= ContentWidth + 144 px
        // padding, default 964 px), and the renderer would paint the
        // document at that fallback width — then ~16-140 ms later the
        // bounds settle, OnSizeChanged's debounced ApplyAvailableWidth
        // pushes the correct width, and the document visibly reflows. The
        // gate is flipped in OnWebSlotPropertyChanged on the first valid
        // bounds, which then queues a single immediate render against the
        // real width. Once flipped the gate stays true for the lifetime
        // of this control, so subsequent activations and tab-switches
        // proceed without it.
        if (!_hasValidSlotBounds)
        {
            return;
        }

        var source = new MarkdownSource(
            _session.CurrentPath ?? string.Empty,
            _session.FileName,
            _session.SourceText);
        var widths = CalculatePreviewWidths(
            GetPreviewHostWidth(),
            _session.ReadingPreferences,
            ResolveDocumentPadding(_session.ReadingPreferences));

        var request = new ApplicateWebRenderRequest(
            ReadingPreferences: CreateWebPreviewPreferences(_session.ReadingPreferences),
            ImageSourceResolver: _session.ImageSourceResolver,
            AvailableContentWidth: widths.WebColumnWidth);
        _sharedHost.RequestRender(source, request);
    }

    public void ScrollToSourceLine(int sourceLine)
    {
        if (!_isAttachedToHost || _sharedHost is null || sourceLine < 0)
        {
            return;
        }

        _sharedHost.View.ScrollToSourceLine(sourceLine);
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

    private void OnResizeContentWidthTimerTick(object? sender, EventArgs e)
    {
        _resizeContentWidthTimer.Stop();
        ApplyAvailableWidth();
    }

    private void ApplyAvailableWidth(bool deferWebContentWidth = false)
    {
        var preferences = _session?.ReadingPreferences ?? ReadingPreferences.Default;
        var widths = CalculatePreviewWidths(
            GetPreviewHostWidth(),
            preferences,
            ResolveDocumentPadding(preferences));

        if (_sharedHost is not null && _isAttachedToHost)
        {
            if (deferWebContentWidth)
            {
                _resizeContentWidthTimer.Stop();
                _resizeContentWidthTimer.Start();
            }
            else
            {
                _resizeContentWidthTimer.Stop();
                _sharedHost.View.AvailableContentWidth = widths.WebColumnWidth;
            }

            // View.MinHeight intentionally NOT set here. The View has
            // VerticalAlignment=Stretch (set in its ctor) and is parented
            // to _webSlot, so layout naturally arranges it at the slot's
            // allocated height. Previously this method set
            // MinHeight=hostHeight (slotHeight / surfaceHeight /
            // Bounds.Height fallback chain); the Bounds.Height fallback
            // included the Row 0 toolbar height, leaking View overflow
            // into the toolbar's airspace on first activation, and the
            // chain itself competed with ViewerView's ApplyColumnWidth
            // for ownership of the same shared-View property — verified
            // by trace 2026-05-19 as the source of the "PREVIEW
            // overlapped by WebView" symptom. Stretch is the single
            // source of truth for height.
        }
    }

    private double GetPreviewHostWidth()
        => ResolvePreviewHostWidth(_webSlot.Bounds.Width, _surface.Bounds.Width, Bounds.Width);

    internal static double ResolvePreviewHostWidth(
        double slotWidth,
        double surfaceWidth,
        double controlWidth)
    {
        if (double.IsFinite(slotWidth) && slotWidth > 0)
        {
            return slotWidth;
        }

        if (double.IsFinite(surfaceWidth) && surfaceWidth > 0)
        {
            return surfaceWidth;
        }

        return controlWidth;
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
        => double.IsFinite(hostHeight) && hostHeight > 0 ? hostHeight : 1;

    internal static ReadingPreferences CreateWebPreviewPreferences(ReadingPreferences preferences)
        => ReadingPreferences.Normalize(preferences) with { DocumentMinimapMode = DocumentMinimapMode.Off };

    /// <summary>
    /// Resolves the document column padding the edit-preview surface should
    /// apply for a given <paramref name="preferences"/>. The horizontal sides
    /// flow through <see cref="ApplicateDocumentLayout.CalculatePreviewDocumentPadding"/>
    /// (which reads the canonical horizontal padding helper); the vertical
    /// sides keep the edit-preview's own top/bottom constants
    /// (<see cref="PreviewDocumentPaddingTop"/>,
    /// <see cref="PreviewDocumentPaddingBottom"/>).
    /// </summary>
    private static Thickness ResolveDocumentPadding(ReadingPreferences preferences)
        => ApplicateDocumentLayout.CalculatePreviewDocumentPadding(
            preferences,
            PreviewDocumentPaddingTop,
            PreviewDocumentPaddingBottom);

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

        // Always disable the outer host ScrollViewer: scroll lives inside
        // the WebView. Otherwise the outer scroll would lift the toolbar
        // (Row 0) along with the content when it scrolls.
        _hostScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }

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

    /// <summary>Test seam — exposes the failure overlay visibility.</summary>
    internal bool IsFailureViewVisibleForTesting => _failureView.IsVisible;

    /// <summary>Test seam — exposes the inner web slot panel.</summary>
    internal Panel WebSlotForTesting => _webSlot;

    public void Dispose()
    {
        _webRenderTimer.Stop();
        _resizeContentWidthTimer.Stop();
        RestoreHostScrollMode();
        AttachSession(null);
        UnwireSharedHostEvents();
        _webRenderTimer.Tick -= OnWebRenderTimerTick;
        _resizeContentWidthTimer.Tick -= OnResizeContentWidthTimerTick;
        _scrollBarOverlay?.Dispose();
        _scrollBarOverlay = null;
    }
}

internal readonly record struct ApplicateEditPreviewWidths(double NativeContentWidth, double WebColumnWidth);
