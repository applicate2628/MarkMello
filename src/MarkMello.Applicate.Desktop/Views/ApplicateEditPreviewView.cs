using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
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
using MarkMello.Applicate.Desktop.Diagnostics;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Domain;
using MarkMello.Presentation.ViewModels;
using SysMath = System.Math;

namespace MarkMello.Applicate.Desktop.Views;

internal sealed class ApplicateEditPreviewView : UserControl, IDisposable
{
    private static readonly Thickness PreviewDocumentPadding = new(72, 96, 72, 160);
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
    private readonly ApplicateMarkdownDocumentView _nativePreview;
    private readonly ScrollViewer _nativeScroll;
    // Reserve a 12px right strip for the Avalonia ScrollBar overlay. WebView2
    // HWND fills _webSlot via NativeControlHost — without this margin the HWND
    // would paint into the scrollbar's Avalonia airspace via Win32 z-order.
    private readonly Panel _webSlot = new() { UseLayoutRounding = true, Margin = new Thickness(0, 0, 12, 0) };
    private WebViewHostScrollBarOverlay? _scrollBarOverlay;
    private Border _webRenderMask = null!;
    private readonly ToggleButton _syncToggle;
    private readonly DispatcherTimer _webRenderTimer;
    private readonly DispatcherTimer _resizeContentWidthTimer;
    private EditorSessionViewModel? _session;
    private ScrollViewer? _hostScrollViewer;
    private ScrollBarVisibility? _hostScrollViewerVerticalMode;
    private TextBox? _editorTextBox;
    private ScrollViewer? _editorScrollViewer;
    private TextBox? _dropTargetTextBox;
    private bool _isAttachedToHost;
    private bool _webPreviewFailed;
    private bool _hostEventsWired;
    private bool _syncEnabled;
    // Tracks whether the shared WebView is between DocumentRenderInvalidated
    // (new Navigate started) and DocumentRendered (new content ready). Hide
    // _webSlot during that window so the user does not see the previous
    // document's content while the new one is loading.
    private bool _isWebRenderInFlight;
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

        // Native HWND of WebView2 ignores Avalonia Opacity, so hiding the
        // slot via Opacity = 0 leaves the previous document painted. Cover
        // the WebView with an opaque mask while a new render is in flight
        // so the user sees a clean blank tile instead of stale content.
        // The mask is themed to MmBackgroundBrush so it matches the body
        // background in both light and dark variants.
        //
        // Z-order: mask is added BEFORE the scrollbar overlay so the
        // scrollbar stays visible on top of the mask. Otherwise the mask
        // (sized to fill the _surface) covers the right-edge scrollbar
        // strip too, producing the "scrollbar flicks on every tab change"
        // artifact — the user sees both the preview content AND the
        // scrollbar blink to background-color for the ~130ms render-in-
        // flight window. With the scrollbar above the mask, only the
        // WebView content is hidden; the scrollbar stays permanently
        // visible through the swap.
        _webRenderMask = new Border
        {
            Background = Avalonia.Application.Current?.TryGetResource(
                "MmBackgroundBrush",
                Avalonia.Application.Current.ActualThemeVariant,
                out var bg) == true && bg is IBrush bgBrush
                ? bgBrush
                : new SolidColorBrush(Colors.White),
            IsHitTestVisible = false,
            IsVisible = false,
            // Reserve the right-edge gutter for the scrollbar overlay so
            // the mask only covers the WebView body area (which is itself
            // inset by _webSlot.Margin = 12px right). The scrollbar strip
            // is a 12px column on the right edge; masking it would defeat
            // the permanent-visible scrollbar invariant.
            Margin = new Thickness(0, 0, 12, 0)
        };
        _surface.Children.Add(_webRenderMask);

        // Avalonia ScrollBar overlay (Option A — Chromium-authority +
        // Avalonia-mirror per consultant blueprint .scratch/codex-prompts/
        // option-a-avalonia-scrollbar-overlay-blueprint.md). Replaces the
        // WebKit ::-webkit-scrollbar so thumb-drag uses Avalonia pointer
        // capture (no sideways release-zone) and runs in the same layout
        // pass as the rest of the visual tree (no IPC mouse-thumb lag).
        // Native wheel/touch/keyboard scrolling continues to work in
        // Chromium because body.overflow-y stays auto. Only the visible
        // scrollbar element is swapped.
        //
        // Added LAST so it sits on top of _webRenderMask in z-order. See
        // the mask construction above for why this order matters.
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
        _webRenderMask.PropertyChanged += OnWebMaskPropertyChanged;
    }

    private void OnWebSlotPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.BoundsProperty && e.Property != Visual.IsVisibleProperty)
        {
            return;
        }
        ApplicateTrace.ModeToggle($"_webSlot.{e.Property.Name}: {e.OldValue} -> {e.NewValue}");
        // Retry the deferred AttachTo when the slot finally lays out. In the
        // pre-warm hot path the shared WebView is already rendered, so the
        // first ApplyVisuals runs while _webSlot.Bounds is still 0×0 and the
        // bounds-gate short-circuits the attach. Once Avalonia commits the
        // layout pass and Bounds become non-zero, we retry — the pre-resize
        // block in SharedHost.AttachTo can now see real target bounds and
        // resize the warmup-parent to match BEFORE the reparent.
        if (e.Property == Visual.BoundsProperty
            && _sharedHost is not null
            && !_isAttachedToHost
            && !_isWebRenderInFlight
            && _webSlot.Bounds.Width > 0
            && _webSlot.Bounds.Height > 0
            && ShouldUseWebPreview())
        {
            ApplyVisuals();
        }

        // After permanent attach is complete, slot resize events (window
        // resize, splitter drag, sidebar toggle) still need to flow into the
        // WebView's MinHeight + AvailableContentWidth so the Chromium HWND
        // viewport matches the visible slot. Without this, the HWND retains
        // the size set at attach time and Chromium's scrollbar drag math
        // operates against a viewport that no longer matches the visible
        // track length — user sees "mouse outpaces thumb".
        //
        // Codex consultant fork-side fix (gpt-5.5 xhigh, .scratch/codex-
        // prompts/webview-scrollbar-drag-asymmetry-residual-2026-05-16).
        if (e.Property == Visual.BoundsProperty
            && _sharedHost is not null
            && _isAttachedToHost
            && !_isWebRenderInFlight
            && _webSlot.Bounds.Width > 0
            && _webSlot.Bounds.Height > 0)
        {
            ApplyAvailableWidth(deferWebContentWidth: true);
        }
    }

    private void OnWebMaskPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty)
        {
            return;
        }
        ApplicateTrace.ModeToggle($"_webRenderMask.IsVisible: {e.OldValue} -> {e.NewValue}");
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

        // Markdown (and other text-like) drops are inserted as a relative-path
        // Markdown link, NOT as the file's content. This matches user
        // expectation when dragging a file in: a reference appears at the
        // caret, the file stays where it is. The link is rendered as
        // clickable in preview and stays portable when the document and
        // dropped file move together.
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
        // Compute a relative path from the host document's directory to the
        // dropped file when both exist on the same drive. Fall back to the
        // absolute path otherwise (untitled host, cross-drive, etc.) so the
        // link still resolves when the user saves the document later.
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
        // Wrap in angle brackets when the path contains spaces or other
        // characters that confuse the Markdown link parser. Angle-bracket
        // form (`<path with spaces>`) is supported by CommonMark and our
        // renderer pipeline.
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

        // Default: copy the image next to the markdown under an `images/`
        // subdirectory and emit a relative reference. Base64 inlining bloats
        // the document and slows rendering, so we only fall back to it when
        // the host document has not been saved yet (no anchor directory).
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

        // Untitled or unwritable directory: fall back to base64 so the user
        // still gets a working reference.
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

        // Block-level inserts (image links, md file links) should be on their
        // own line. Pad with newlines when the caret is adjacent to
        // non-newline content so the link does not merge with surrounding
        // text (which would yield "![alt](path)# Heading" parsed as inline
        // text).
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
        // Defer the heavy AttachSession work (which fires UpdateInputs, reparent
        // of the shared WebView, and ApplyVisuals) until the preview is mounted
        // into the visual tree. Running it directly here makes IDataTemplate.Build
        // synchronously do ~400ms of work, during which the OLD ContentControl
        // child (ViewerView) still owns the screen and the user sees reader-mode
        // content while their edit-toggle press has already flipped the button —
        // a visible parasitic frame on enter-edit. OnAttachedToVisualTree picks
        // the session back up from DataContext below.
        if (this.GetVisualParent() is not null)
        {
            AttachSession(DataContext as EditorSessionViewModel);
        }
        else if (DataContext is null)
        {
            // DataContext was cleared (e.g., a detached preview being recycled
            // with a null context). Tear the session down so any cached state
            // does not leak into the next mount.
            AttachSession(null);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplicateTrace.ModeToggle($"EditPreview.OnAttachedToVisualTree Bounds={Bounds} _webSlot.Bounds={_webSlot.Bounds}");

        // Permanent mount: reparent the shared WebView2 into _webSlot ONCE on
        // first tree attach, regardless of whether DataContext is a session
        // yet. This is the ONLY SetParent operation in the WebView's lifetime
        // — subsequent mode toggles flip editSlot.IsVisible, which cascades
        // to SetWindowPos(SWP_HIDEWINDOW) on the HWND via NativeControlHost
        // (verified by pre-flight probe at 15:24:17). Because editSlot is
        // IsVisible=false at the moment this attach runs (reader is the
        // initial mode), the HWND geometry-lag is invisible to the user.
        if (_sharedHost is not null && !_isAttachedToHost)
        {
            _sharedHost.AttachTo(_webSlot);
            _isAttachedToHost = true;
        }

        AttachSession(DataContext as EditorSessionViewModel);
        UpdateHostScrollMode();
        Dispatcher.UIThread.Post(EnsureEditorDropWiring, DispatcherPriority.Background);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        RestoreHostScrollMode();
        TeardownEditorDropWiring();
        TeardownEditorWiring();
        AttachSession(null);
        _webRenderTimer.Stop();
        _resizeContentWidthTimer.Stop();
        ReleaseSharedHost();

        base.OnDetachedFromVisualTree(e);
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
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        ApplyNativePreview();
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        ApplyRendererMode();
        var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        ApplyAvailableWidth();
        var t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        var freq = System.Diagnostics.Stopwatch.Frequency / 1000.0;
        ApplicateTrace.ModeToggle(
            $"ApplySession timing: native={(t1 - t0) / freq:F1}ms renderer={(t2 - t1) / freq:F1}ms width={(t3 - t2) / freq:F1}ms");
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

        // Skip native-side render work whenever WebView is the active
        // preview. _nativeScroll is hardcoded to IsVisible=false in
        // ApplyVisuals (the native surface is stubbed out in this build
        // because it flashed between Avalonia native and WebView during
        // source change), so feeding the heavy RenderedPreview document
        // into _nativePreview only pays the synchronous Avalonia layout
        // cost (measured 814ms on the wave_ports heavy-MathType file)
        // for no visible result. The fallback path explicitly re-applies
        // the document via OnSharedFallbackRequested so the native
        // surface still has fresh content when WebView actually fails.
        if (ShouldUseWebPreview())
        {
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
        // Permanent mount: never call _sharedHost.DetachFrom from here. The
        // shared WebView lives in _webSlot for the lifetime of this control
        // (which is permanent in editSlot since v0.3.x sibling-mount). On
        // session=null (mode toggle to reader, or document close), only the
        // event subscriptions need to come down — the HWND stays in place
        // and gets hidden by the cascade from editSlot.IsVisible=false. The
        // OLD DetachFrom path returned the View to the warmup parent and
        // forced a Win32 SetParent on the next enter-edit, which produced
        // the 154ms HWND geometry-lag bug (verified [hwnd-probe] evidence).
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
            return;
        }

        ForwardPreviewScrollToEditor(e.ProgressPercent);
    }

    private void OnSharedDocumentRendered(object? sender, EventArgs e)
    {
        ApplicateTrace.ModeToggle("SharedView Rendered -> renderInFlight=false");
        _isWebRenderInFlight = false;
        // Deferred-attach path: if we held off on AttachTo while the new
        // document was loading (see ApplyVisuals), do the reparent now —
        // content is committed in the WebView and visible-stale window is
        // closed. ApplyVisuals would also do the attach on its next call,
        // but doing it here lets us avoid the extra ApplyVisuals re-entry
        // and keeps the mask→content swap in one tick.
        if (_sharedHost is not null && !_isAttachedToHost && ShouldUseWebPreview())
        {
            _sharedHost.AttachTo(_webSlot);
            _isAttachedToHost = true;
        }
        ApplyVisuals();
    }

    private void OnSharedDocumentInvalidated(object? sender, EventArgs e)
    {
        ApplicateTrace.ModeToggle("SharedView Invalidated -> renderInFlight=true");
        _isWebRenderInFlight = true;
        ApplyVisuals();
    }

    private void OnSharedFallbackRequested(object? sender, EventArgs e)
    {
        _webPreviewFailed = true;
        ReleaseSharedHost();
        // Web failed → native surface becomes the active renderer. Feed
        // its Document now; ApplyNativePreview's WebView-gate would have
        // skipped this assignment during normal flow.
        ApplyNativePreview();
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
        var widths = CalculatePreviewWidths(GetPreviewHostWidth(), _session.ReadingPreferences, PreviewDocumentPadding);

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
        // Native preview is stubbed out in this build (see
        // ApplicateMainWindow.InstallNativeRendererStub) because its render
        // flashes between Avalonia native and WebView during source change,
        // producing visible jitter in edit mode.
        _nativeScroll.IsVisible = false;

        // Only mount the shared WebView into this preview's slot while the
        // preview is actually meant to display WebView output. Without this
        // gate, OnDetachedFromVisualTree's AttachSession(null) -> ApplySession
        // -> ApplyRendererMode -> ReleaseSharedHost detaches the View to the
        // warmup parent, and the immediate trailing ApplyVisuals call here
        // would re-attach it to the slot of the preview that is being torn
        // down — producing a visible double-reparent flicker and leaving the
        // native HWND briefly parented under a control whose visual tree is
        // mid-unmount.
        var shouldShowWeb = _sharedHost is not null && ShouldUseWebPreview();

        // Tab-switch sequence inside Avalonia's ContentControl rebinding fires
        // AttachSession(null) followed ~1.4s later by AttachSession(newSession).
        // During that null window, _session is null, so ShouldUseWebPreview()
        // returns false even though the shared WebView is still mounted in
        // _webSlot (_isAttachedToHost==True throughout). Hiding _webSlot here
        // would (a) make the WebView content vanish for the duration of the
        // gap, (b) take the sibling-mounted ScrollBar overlay with it (they
        // share _webSlot as parent), and (c) — if the second AttachSession
        // never arrives due to a race — leave the slot permanently invisible.
        // Both visible bugs ("scrollbar flickers with text on tab switch" and
        // "render disappears after several tab switches") come from this one
        // path.
        //
        // The else branch's original purpose was to prevent re-attach-into-
        // tearing-down-control during OnDetachedFromVisualTree. But that path
        // takes _isAttachedToHost down explicitly via ReleaseSharedHost (and
        // the visual tree detach itself hides everything cascading from the
        // parent's IsVisible=false). So gating on _isAttachedToHost is the
        // correct signal: while the WebView is mounted in our slot, the slot
        // must remain visible regardless of transient session-null states.
        var keepSlotVisibleAttached = _isAttachedToHost && _sharedHost is not null;

        if (shouldShowWeb || keepSlotVisibleAttached)
        {
            // Defer AttachTo while either (a) a new render is in flight
            // OR (b) the target slot has not laid out yet. Both cases
            // would leak a stale frame: (a) the WebView would reparent
            // its previous DOM into _webSlot for the 250-450ms before
            // the new HTML commits, and the Avalonia mask Border CANNOT
            // cover a native HWND (Win32 z-order — native child windows
            // always sit above their Avalonia siblings); (b) AttachTo
            // with target.Bounds=0×0 makes SharedHost skip the pre-resize
            // block, so the View keeps its warmup-parent size (1024×768)
            // for the 20-40ms until Avalonia's next layout pass propagates
            // the real slot bounds — visible as a brief CSS reflow when
            // AvailableContentWidth then changes from the pre-warm value
            // to the actual slot width. Retry path: OnWebSlotPropertyChanged
            // triggers ApplyVisuals when _webSlot.Bounds become non-zero.
            var canAttach = !_isWebRenderInFlight
                && _webSlot.Bounds.Width > 0
                && _webSlot.Bounds.Height > 0;
            if (!_isAttachedToHost && canAttach)
            {
                _sharedHost!.AttachTo(_webSlot);
                _isAttachedToHost = true;
            }
            // Keep _webSlot mounted with full layout footprint so the
            // SOURCE|PREVIEW toolbar row stays put. Mask covers _webSlot
            // when (a) a render is in flight or (b) we're still waiting
            // to attach (deferred). Once attached AND render committed,
            // the mask goes off and content shows immediately.
            _webSlot.IsVisible = true;
            _webSlot.Opacity = 1;
            var maskVisible = _isWebRenderInFlight || !_isAttachedToHost;
            _webRenderMask.IsVisible = maskVisible;
            // ScrollBar overlay stays permanently visible — its template-part
            // Transitions are cleared in WebViewHostScrollBarOverlay so it
            // never fades in/out independently. The mask above already
            // covers the WebView content during render-in-flight; the
            // scrollbar staying put through that is the desired UX.
        }
        else
        {
            _webSlot.IsVisible = false;
            _webRenderMask.IsVisible = false;
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

    private void OnResizeContentWidthTimerTick(object? sender, EventArgs e)
    {
        _resizeContentWidthTimer.Stop();
        ApplyAvailableWidth();
    }

    private void ApplyAvailableWidth(bool deferWebContentWidth = false)
    {
        var preferences = _session?.ReadingPreferences ?? ReadingPreferences.Default;
        var widths = CalculatePreviewWidths(GetPreviewHostWidth(), preferences, PreviewDocumentPadding);
        _nativePreview.AvailableContentWidth = widths.NativeContentWidth;
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

            // The WebView wrapper is added to `_webSlot` (the Border directly
            // containing the WebView2 NativeControlHost). Sizing MinHeight from
            // `_webSlot.Bounds.Height` keeps the HWND viewport in lock-step with
            // the slot the wrapper actually lives in — Chromium maps thumb-drag
            // delta against that viewport.
            //
            // Previously this used `_surface.Bounds.Height` (Row 1 of the outer
            // grid), which is taller than `_webSlot` (the slot lives one Border
            // deeper) and can drift during layout. The mismatch made the HWND
            // height-sample inconsistent with the visible track length, so the
            // thumb visually lagged the mouse during fast drag. Per Codex
            // consultant diagnosis (gpt-5.5 xhigh, .scratch/codex-prompts/
            // webview-scrollbar-drag-asymmetry-residual-2026-05-16).
            //
            // Fall through `_webSlot → _surface → Bounds` so the pre-warm path
            // (slot not yet measured) still resolves to a usable hostHeight,
            // and `CalculateWebPreviewMinHeight`'s `> 0` guard handles the
            // zero-bounds edge with the `1` sentinel.
            var slotHeight = _webSlot.Bounds.Height;
            var surfaceHeight = _surface.Bounds.Height;
            var hostHeight = slotHeight > 0
                ? slotHeight
                : (surfaceHeight > 0 ? surfaceHeight : Bounds.Height);
            _sharedHost.View.MinHeight = CalculateWebPreviewMinHeight(hostHeight);
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
        // Use the actual measured surface (Row 1) height — no 480px floor.
        // The previous `max(480, hostHeight)` forced the WebView2 HWND to be
        // at least 480 px tall regardless of the visible preview surface
        // height; on smaller windows or split-pane layouts the HWND then
        // overflowed into the toolbar area and Avalonia's surface clipping
        // hid part of the Chromium scrollbar track. Result: dragging the
        // thumb top-to-bottom in one mouse motion only covered a fraction
        // of the actual scroll range because the visible track length was
        // less than the HWND-internal track length. Codex consultant
        // diagnosis (gpt-5.5 xhigh, .scratch/codex-prompts/webview-drag-
        // quality-asymmetry-20260516-214356.md) verified by user.
        //
        // The `> 0` guard already handles the "host not yet measured" case,
        // falling through to the `: 1` minimal sentinel that lets the
        // WebView2 controller initialise. The 480 floor was defensive
        // overkill — viewer mode's `max(480, Bounds.Height)` at
        // ApplicateViewerView.cs:674 is harmless because window content
        // height is essentially always ≥ 480.
        => double.IsFinite(hostHeight) && hostHeight > 0 ? hostHeight : 1;

    internal static ReadingPreferences CreateWebPreviewPreferences(ReadingPreferences preferences)
        => ReadingPreferences.Normalize(preferences) with { DocumentMinimapMode = DocumentMinimapMode.Off };

    internal static double CalculatePreWarmColumnWidth(ReadingPreferences preferences)
    {
        // Match CalculatePreviewWidths' unconstrained-host path (hostWidth <= 0):
        // returns ContentWidth + horizontal PreviewDocumentPadding so the
        // pre-warm column matches the natural column width edit-mode would
        // pick on a wide host. On narrower hosts the edit-mode width-update
        // arrives via AvailableContentWidth setter → OnLiveInputChanged →
        // ApplyReadingPreferences (CSS-only, no DOM re-render).
        var normalized = ReadingPreferences.Normalize(preferences);
        return normalized.ContentWidth
            + PreviewDocumentPadding.Left
            + PreviewDocumentPadding.Right;
    }

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
        _resizeContentWidthTimer.Stop();
        RestoreHostScrollMode();
        AttachSession(null);
        ReleaseSharedHost();
        _webRenderTimer.Tick -= OnWebRenderTimerTick;
        _resizeContentWidthTimer.Tick -= OnResizeContentWidthTimerTick;
        _scrollBarOverlay?.Dispose();
        _scrollBarOverlay = null;
    }
}

internal readonly record struct ApplicateEditPreviewWidths(double NativeContentWidth, double WebColumnWidth);
