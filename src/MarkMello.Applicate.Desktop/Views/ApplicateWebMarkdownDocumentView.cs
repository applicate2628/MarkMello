using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using MarkMello.Application.Abstractions;
using MarkMello.Applicate.Desktop.Diagnostics;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Applicate.Desktop.Views.Minimap;
using MarkMello.Domain;
using MarkMello.Presentation;
using MarkMello.Presentation.Services;
using MarkMello.Presentation.Views.Markdown;
using Microsoft.Extensions.DependencyInjection;
using SysMath = System.Math;

namespace MarkMello.Applicate.Desktop.Views;

public sealed class ApplicateWebThemeChangeSentEventArgs(string theme, long requestId) : EventArgs
{
    public string Theme { get; } = theme;

    public long RequestId { get; } = requestId;
}

public sealed class ApplicateWebThemeAppliedEventArgs(string theme, long requestId) : EventArgs
{
    public string Theme { get; } = theme;

    public long RequestId { get; } = requestId;
}

public sealed class ApplicateWebMarkdownDocumentView : UserControl, IDisposable
{
    private const double MaxRendererReportedMinimapReservedWidth = 2000;
    private const int NativeOffscreenMargin = 512;
    private const int IUnknownVtableSlotCount = 3;
    private static readonly TimeSpan DuplicateThemePostWindow = TimeSpan.FromMilliseconds(100);
    private const string CoreWebView2InteropTypeName = "Avalonia.Controls.Win.WebView2.Interop.ICoreWebView2";
    private static readonly int CoreWebView2PostWebMessageAsJsonVtableSlot =
        ResolveCoreWebView2PostWebMessageAsJsonVtableSlot();
    private static int s_shellDocumentSequence;

    public static readonly StyledProperty<MarkdownSource?> SourceProperty =
        AvaloniaProperty.Register<ApplicateWebMarkdownDocumentView, MarkdownSource?>(nameof(Source));

    public static readonly StyledProperty<ReadingPreferences> ReadingPreferencesProperty =
        AvaloniaProperty.Register<ApplicateWebMarkdownDocumentView, ReadingPreferences>(
            nameof(ReadingPreferences),
            ReadingPreferences.Default);

    public static readonly StyledProperty<IImageSourceResolver?> ImageSourceResolverProperty =
        AvaloniaProperty.Register<ApplicateWebMarkdownDocumentView, IImageSourceResolver?>(nameof(ImageSourceResolver));

    public static readonly StyledProperty<double> AvailableContentWidthProperty =
        AvaloniaProperty.Register<ApplicateWebMarkdownDocumentView, double>(nameof(AvailableContentWidth), double.NaN);

    public static readonly StyledProperty<bool> ViewerChromeEnabledProperty =
        AvaloniaProperty.Register<ApplicateWebMarkdownDocumentView, bool>(nameof(ViewerChromeEnabled), true);

    public static readonly StyledProperty<bool> DocumentScrollEnabledProperty =
        AvaloniaProperty.Register<ApplicateWebMarkdownDocumentView, bool>(nameof(DocumentScrollEnabled), true);

    public static readonly StyledProperty<bool> WheelProxyEnabledProperty =
        AvaloniaProperty.Register<ApplicateWebMarkdownDocumentView, bool>(nameof(WheelProxyEnabled), false);

    private readonly IApplicateHtmlMarkdownRenderer _renderer;
    private readonly ApplicateRenderedBodyCache _renderedBodyCache;
    private readonly NativeWebView _webView;
    private CancellationTokenSource? _renderCancellation;
    private string? _currentGeneratedDocumentPath;
    private readonly int _shellDocumentId = Interlocked.Increment(ref s_shellDocumentSequence);
    private bool _isLoadingGeneratedDocument;
    private bool _hasLoadedDocument;
    private bool _awaitingLayoutReady;
    private bool _hasLayoutReady;
    private bool _hasMinimapState;
    private bool _lastLayoutReadyWasCached;
    private bool _requiresPostReadyEnhancements;
    private bool _postReadyEnhancementsComplete = true;
    private bool _documentRenderedRaised;
    private bool _documentRevealReadyRaised;
    private long _activeRevealRenderId;
    private bool _disposed;
    private double _scrollTop;
    private double _scrollHeight;
    private double _clientHeight;
    private bool _isUpdatingInputs;
    private int _intentionalReparentDepth;
    private MarkMello.Presentation.ViewModels.MainWindowViewModel? _mainWindowViewModel;
    private readonly bool _shellMode;
    private readonly IApplicateShellAssetBundleFactory? _shellAssetFactory;
    private bool _shellNavigated;
    private bool _shellDocumentReadyConsumed;
    private TaskCompletionSource<bool>? _shellReady;
    // PE r2 item F: tracks the theme name most recently inlined into the
    // generated HTML via ApplyInitialTheme. Used at OnWebMessageReceived's
    // user-document document-ready branch to suppress redundant SendTheme()
    // when the inlined theme already matches GetThemeName(). UI-thread-only
    // (every ApplyInitialTheme call site runs on the Avalonia UI thread:
    // QueueRenderShellAsync and RenderAsync are both queued via QueueRender
    // on the UI thread). Null until first ApplyInitialTheme runs; the
    // suppression guard treats null as "do not suppress" (safe fallback).
    private string? _inlinedTheme;
    private string? _lastPostedTheme;
    private long _lastPostedThemeTimestamp;
    private long _themeRequestSequence;
    private bool _isWebWidthDragging;
    private long _renderSequence;
    private NativeWindowPlacement? _pendingNativeHiddenPaintPlacement;
    private bool _documentRevealPending;
    private readonly HashSet<string> _postedRendererDocumentCacheKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<long, object> _pendingRendererCacheFallbackLoads = new();

    static ApplicateWebMarkdownDocumentView()
    {
        SourceProperty.Changed.AddClassHandler<ApplicateWebMarkdownDocumentView>((view, _) => view.OnRenderInputChanged());
        ImageSourceResolverProperty.Changed.AddClassHandler<ApplicateWebMarkdownDocumentView>((view, _) => view.OnRenderInputChanged());
        ReadingPreferencesProperty.Changed.AddClassHandler<ApplicateWebMarkdownDocumentView>((view, _) => view.OnLiveInputChanged());
        AvailableContentWidthProperty.Changed.AddClassHandler<ApplicateWebMarkdownDocumentView>((view, _) => view.OnLiveInputChanged());
        ViewerChromeEnabledProperty.Changed.AddClassHandler<ApplicateWebMarkdownDocumentView>((view, _) => view.OnLiveInputChanged());
        DocumentScrollEnabledProperty.Changed.AddClassHandler<ApplicateWebMarkdownDocumentView>((view, _) => view.OnLiveInputChanged());
        WheelProxyEnabledProperty.Changed.AddClassHandler<ApplicateWebMarkdownDocumentView>((view, _) => view.OnLiveInputChanged());
    }

    public ApplicateWebMarkdownDocumentView(IApplicateHtmlMarkdownRenderer renderer)
        : this(renderer, shellAssetFactory: null)
    {
    }

    public ApplicateWebMarkdownDocumentView(
        IApplicateHtmlMarkdownRenderer renderer,
        IApplicateShellAssetBundleFactory? shellAssetFactory)
        : this(renderer, shellAssetFactory, new ApplicateRenderedBodyCache())
    {
    }

    internal ApplicateWebMarkdownDocumentView(
        IApplicateHtmlMarkdownRenderer renderer,
        IApplicateShellAssetBundleFactory? shellAssetFactory,
        ApplicateRenderedBodyCache renderedBodyCache)
    {
        ApplicateTrace.DiagMs("startup-webview", "webview-view-ctor-start");
        _renderer = renderer;
        _renderedBodyCache = renderedBodyCache;
        _shellAssetFactory = shellAssetFactory;
        // Shell mode requires both the env-var flag AND the factory injection.
        // Missing either falls back to legacy per-document Navigate.
        _shellMode = ApplicateRendererShellMode.IsEnabled && shellAssetFactory is not null;
        ApplicateTrace.DiagMs("startup-webview", "native-webview-ctor-start");
        _webView = new ApplicateNativeWebView
        {
            ClipToBounds = true,
            ContextFlyout = null,
            ContextMenu = null,
            Focusable = true,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };
        ApplicateTrace.DiagMs("startup-webview", "native-webview-ctor-end");

        _webView.EnvironmentRequested += OnEnvironmentRequested;
        _webView.NavigationStarted += OnNavigationStarted;
        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.NewWindowRequested += OnNewWindowRequested;
        _webView.WebMessageReceived += OnWebMessageReceived;

        Content = _webView;
        UseLayoutRounding = true;
        ClipToBounds = true;
        ActualThemeVariantChanged += OnThemeChanged;
        AddHandler(KeyDownEvent, OnWebViewKeyDown, handledEventsToo: true);
        ApplicateTrace.DiagMs("startup-webview", "webview-view-ctor-end");
    }

    public MarkdownSource? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public ReadingPreferences ReadingPreferences
    {
        get => GetValue(ReadingPreferencesProperty);
        set => SetValue(ReadingPreferencesProperty, value);
    }

    public IImageSourceResolver? ImageSourceResolver
    {
        get => GetValue(ImageSourceResolverProperty);
        set => SetValue(ImageSourceResolverProperty, value);
    }

    public double AvailableContentWidth
    {
        get => GetValue(AvailableContentWidthProperty);
        set => SetValue(AvailableContentWidthProperty, value);
    }

    public bool ViewerChromeEnabled
    {
        get => GetValue(ViewerChromeEnabledProperty);
        set => SetValue(ViewerChromeEnabledProperty, value);
    }

    public bool DocumentScrollEnabled
    {
        get => GetValue(DocumentScrollEnabledProperty);
        set => SetValue(DocumentScrollEnabledProperty, value);
    }

    public bool WheelProxyEnabled
    {
        get => GetValue(WheelProxyEnabledProperty);
        set => SetValue(WheelProxyEnabledProperty, value);
    }

    public event EventHandler? DocumentRendered;

    public event EventHandler? DocumentRevealReady;

    public event EventHandler<ApplicateWebThemeChangeSentEventArgs>? ThemeChangeSent;

    public event EventHandler<ApplicateWebThemeAppliedEventArgs>? ThemeApplied;

    public event EventHandler? DocumentRenderInvalidated;

    public event EventHandler<ApplicateWebDocumentScrollEventArgs>? ScrollStateChanged;

    public event EventHandler<ApplicateWebMinimapStateEventArgs>? MinimapStateChanged;

    public event EventHandler<ApplicateWebMinimapSettledEventArgs>? MinimapSettled;

    public event EventHandler<ApplicateWebModeToggleSettledEventArgs>? ModeToggleTransactionSettled;

    public event EventHandler<ApplicateWebWidthDragEventArgs>? WidthDragRequested;

    public event EventHandler<ApplicateWebWheelEventArgs>? WheelRequested;

    public event EventHandler? ViewerInteractionRequested;

    public event EventHandler<ApplicateWebPreviewSourceLineEventArgs>? PreviewSourceLineChanged;

    public event EventHandler? FallbackRequested;

    /// <summary>
    /// Fires when the renderer reports the current document's heading list
    /// after a chrome rebuild (initial render + each load-document swap).
    /// Drives the Avalonia-side Table of Contents panel.
    /// </summary>
    public event EventHandler<IReadOnlyList<MarkMello.Presentation.ViewModels.DocumentHeading>>? HeadingsChanged;

    /// <summary>
    /// Fires when the renderer's IntersectionObserver picks a new top-visible
    /// heading as the user scrolls. The payload is the heading's stable slug
    /// id; consumers highlight the matching TOC row.
    /// </summary>
    public event EventHandler<string>? ActiveHeadingChanged;

    /// <summary>
    /// Fires when the renderer acks a <c>mode-settle-probe</c> after two
    /// requestAnimationFrame ticks elapse — i.e. once CSS reflow on the new
    /// slot bounds has propagated and one paint has happened. The shared host
    /// uses this to defer <see cref="SetNativeWebViewVisibility"/> on the
    /// Commit fast-path (Ctrl+E same-document reparent), so the user never
    /// sees the HWND repaint at the old document width before the renderer
    /// catches up. One-shot semantics: the host subscribes for one toggle,
    /// unsubscribes on either signal or timeout fallback, and re-arms on the
    /// next toggle.
    /// </summary>
    public event EventHandler? ModeToggleSettled;

    internal bool HasLoadedDocumentForSource(MarkdownSource? source)
        => _hasLoadedDocument && !_awaitingLayoutReady && Equals(Source, source);

    internal bool LastLayoutReadyWasCached => _lastLayoutReadyWasCached;

    internal ApplicateWebInputUpdateAction UpdateInputs(
        MarkdownSource? source,
        ReadingPreferences readingPreferences,
        IImageSourceResolver? imageSourceResolver,
        double availableContentWidth,
        bool viewerChromeEnabled,
        bool documentScrollEnabled = true,
        bool wheelProxyEnabled = false,
        bool deferLivePreferencesUntilModeSettleProbe = false,
        bool skipFrameWaitUntilRenderReady = false)
    {
        var sourceChanged = !Equals(Source, source);
        var shouldPrepareDocumentReveal = ShouldPrepareDocumentReveal(
            sourceChanged,
            _hasLoadedDocument,
            source);
        if (shouldPrepareDocumentReveal)
        {
            PrepareNativeDocumentReveal(TimeSpan.Zero);
        }
        else if (source is null && _documentRevealPending)
        {
            RevealNativeDocument(TimeSpan.Zero);
        }

        var action = DetermineInputUpdateAction(
            sourceChanged: sourceChanged,
            imageSourceResolverChanged: !ReferenceEquals(ImageSourceResolver, imageSourceResolver),
            hasLoadedDocument: _hasLoadedDocument,
            readingPreferencesChanged: ReadingPreferences != readingPreferences,
            availableContentWidthChanged: !AreEqual(AvailableContentWidth, availableContentWidth),
            viewerChromeEnabledChanged: ViewerChromeEnabled != viewerChromeEnabled,
            documentScrollEnabledChanged: DocumentScrollEnabled != documentScrollEnabled,
            wheelProxyEnabledChanged: WheelProxyEnabled != wheelProxyEnabled);

        ApplicateTrace.ModeToggle(
            $"Web.UpdateInputs action={action} oldPath={Source?.Path ?? "(null)"} newPath={source?.Path ?? "(null)"} hasLoaded={_hasLoadedDocument} awaiting={_awaitingLayoutReady} theme={GetThemeName()} chrome={viewerChromeEnabled}");

        _isUpdatingInputs = true;
        try
        {
            ReadingPreferences = readingPreferences;
            ImageSourceResolver = imageSourceResolver;
            AvailableContentWidth = availableContentWidth;
            ViewerChromeEnabled = viewerChromeEnabled;
            DocumentScrollEnabled = documentScrollEnabled;
            WheelProxyEnabled = wheelProxyEnabled;
            Source = source;
        }
        finally
        {
            _isUpdatingInputs = false;
        }

        if (action == ApplicateWebInputUpdateAction.Render)
        {
            QueueRender(skipFrameWaitUntilRenderReady);
            return action;
        }

        if (action == ApplicateWebInputUpdateAction.ApplyLivePreferences
            && !deferLivePreferencesUntilModeSettleProbe)
        {
            ApplyReadingPreferences();
        }

        return action;
    }

    /// <summary>
    /// Scroll the document by a delta from the host. Used when the host owns
    /// wheel routing (WheelProxyEnabled = true) and needs to translate a
    /// wheel event back into a document scroll.
    /// </summary>
    internal void ScrollDocumentBy(double deltaY)
    {
        if (!_hasLoadedDocument)
        {
            return;
        }

        PostRendererMessage(new { type = "scroll-by", deltaY });
    }

    /// <summary>
    /// Scroll the renderer to the block carrying the given
    /// <c>data-mm-block-index</c> attribute. No-op when the document has not
    /// loaded yet or no matching element exists.
    /// </summary>
    internal void ScrollToBlock(int blockIndex)
    {
        if (!_hasLoadedDocument || blockIndex < 0)
        {
            return;
        }

        PostRendererMessage(new { type = "scroll-to-block", blockIndex });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // The WebView2 child HWND occludes any Avalonia overlay that draws in
        // its rectangle (Windows airspace). The upstream "Unsaved changes"
        // modal lives in BodyPanel as a sibling overlay and would otherwise
        // be partially covered. Hide the HWND while that modal is open so the
        // dialog renders cleanly. Other upstream popups (settings, app menu)
        // are intentionally non-overlay top-level windows so they already
        // sit above the WebView and do not need this treatment.
        _mainWindowViewModel =
            TopLevel.GetTopLevel(this)?.DataContext as MarkMello.Presentation.ViewModels.MainWindowViewModel;
        if (_mainWindowViewModel is not null)
        {
            _mainWindowViewModel.PropertyChanged += OnMainWindowViewModelPropertyChanged;

            // SyncWebViewAirspaceVisibility tracks _hiddenForPrompt internally
            // and only writes IsVisible on prompt-state transitions, so it is
            // safe to call here even though OnAttachedToVisualTree fires
            // asynchronously (during Measure / ContentPresenter template
            // application, after any BeginIntentionalReparent using-block has
            // already disposed). See the SyncWebViewAirspaceVisibility body
            // for the full WHY.
            SyncWebViewAirspaceVisibility();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_mainWindowViewModel is not null)
        {
            _mainWindowViewModel.PropertyChanged -= OnMainWindowViewModelPropertyChanged;
            _mainWindowViewModel = null;

            // On intentional reparent (shared-host AttachTo), the host owns
            // _webView.IsVisible — specifically it parks the native HWND
            // BEFORE the reparent as anti-airspace-leak (see
            // ApplicateSharedWebViewHost.AttachTo: ParkNativeWebViewForReparent()).
            // Restoring to true here unconditionally would undo that hide and
            // expose the HWND at previous bounds during the layout-pass gap.
            // The same `_intentionalReparentDepth == 0` guard governs
            // CancelRender below — gating this restoration the same way
            // keeps the two policies aligned.
            if (_intentionalReparentDepth == 0)
            {
                _webView.IsVisible = true;
            }
        }

        // Skip cancelling the in-flight render when the detach is part of an
        // intentional shared-host reparent: the WebView2 instance is kept alive
        // by NativeWebView.BeginReparenting and we want navigation to continue
        // straight into the new parent without restart. Real detaches (control
        // disposed, window closed) still cancel as before.
        if (_intentionalReparentDepth == 0)
        {
            CancelRender();
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnMainWindowViewModelPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MarkMello.Presentation.ViewModels.MainWindowViewModel.IsDirtyPromptOpen))
        {
            SyncWebViewAirspaceVisibility();
        }
    }

    // Tracks whether THIS method previously hid _webView for the dirty-prompt
    // modal — enables coexistence with the shared-host anti-airspace-leak path
    // (which also writes _webView.IsVisible=false before intentional reparents).
    // Without this tracker, SyncWebViewAirspaceVisibility would unconditionally
    // restore IsVisible=true on every OnAttachedToVisualTree firing — and
    // OnAttachedToVisualTree fires asynchronously during the Measure pass,
    // AFTER any BeginIntentionalReparent using-block has already disposed —
    // so the host's pre-reparent hide would be silently undone, leaving the
    // HWND visible at OLD bounds for the layout-pass gap on every reparent.
    private bool _hiddenForPrompt;

    private void SyncWebViewAirspaceVisibility()
    {
        var shouldHideForPrompt = _mainWindowViewModel?.IsDirtyPromptOpen == true;
        if (shouldHideForPrompt && !_hiddenForPrompt)
        {
            // Prompt just opened (or first attach while open) — hide HWND
            // so the Avalonia overlay isn't occluded by Win32 airspace.
            _hiddenForPrompt = true;
            _webView.IsVisible = false;
        }
        else if (!shouldHideForPrompt && _hiddenForPrompt)
        {
            // Prompt just closed — restore the visibility WE took away.
            _hiddenForPrompt = false;
            _webView.IsVisible = true;
        }
        // else: no transition we own. Critical: do NOT write _webView.IsVisible
        // here when the prompt is closed and we never hid it — that would
        // override the shared-host's anti-airspace-leak hide set just before
        // an intentional reparent.
    }

    /// <summary>
    /// Begin an intentional reparent that keeps the native WebView adapter and
    /// in-flight render alive across the detach + re-attach pair. Used by the
    /// shared-host service when moving the view between viewer and edit-mode
    /// preview panels. Disposing the returned scope ends the reparent.
    /// </summary>
    internal IDisposable BeginIntentionalReparent()
    {
        _intentionalReparentDepth++;
        var inner = _webView.BeginReparenting(false);
        return new ReparentScope(this, inner);
    }

    /// <summary>
    /// Hide or show the underlying native HWND. Used by the shared-host
    /// service to suppress the WebView's backing store during the brief
    /// window between reparenting into a new slot and Avalonia's next
    /// layout pass propagating the new bounds to the HWND — without this,
    /// the HWND repaints at its previous (warmup) position and size for a
    /// single frame and visibly leaks over the tab strip and chrome area.
    /// </summary>
    internal void SetNativeWebViewVisibility(bool isVisible)
    {
        ApplicateTrace.ModeToggle($"SetNativeWebViewVisibility({isVisible}) viewId={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this):X8} wrapper.Bounds={_webView.Bounds}");
        SetNativeWebViewWindowVisibility(isVisible);
    }

    internal void ParkNativeWebViewForReparent()
    {
        ApplicateTrace.ModeToggle($"ParkNativeWebViewForReparent viewId={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this):X8} wrapper.Bounds={_webView.Bounds}");

        if (!OperatingSystem.IsWindows())
        {
            SetNativeWebViewVisibility(false);
            return;
        }

        var handle = _webView.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (!TryCaptureNativeWebViewPlacement(handle, out var placement))
        {
            SetNativeWebViewWindowVisibility(false);
            return;
        }

        _pendingNativeHiddenPaintPlacement = null;
        var offscreenX = CalculateNativeOffscreenX(handle, placement.Width);
        var flags = NativeMethods.SwpNoZOrder
            | NativeMethods.SwpNoActivate
            | NativeMethods.SwpNoOwnerZOrder
            | NativeMethods.SwpNoCopyBits;
        SetNativeWebViewTreeVisibility(handle, isVisible: false);
        var moved = NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            offscreenX,
            placement.Y,
            placement.Width,
            placement.Height,
            flags);
        ApplicateTrace.ModeToggle(
            $"ParkNativeWebViewForReparent moved={moved} saved={placement.X},{placement.Y},{placement.Width}x{placement.Height} offscreenX={offscreenX}");
    }

    private void SetNativeWebViewWindowVisibility(bool isVisible)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = _webView.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            if (isVisible)
            {
                SyncNativeWebViewWindowSize(handle);
            }
            else
            {
                _pendingNativeHiddenPaintPlacement = null;
                ReleaseNativeWebViewFocusBeforeHide(handle);
            }

            SetNativeWebViewTreeVisibility(handle, isVisible);
            return;
        }

        // Non-Windows fallback: preserve the old control-level visibility path
        // where there is no HWND airspace race to hide directly.
        _webView.IsVisible = isVisible;
    }

    private void ReleaseNativeWebViewFocusBeforeHide(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var focused = NativeMethods.GetFocus();
        var focusSource = "thread";
        if (!IsNativeFocusInsideWebView(handle, focused))
        {
            if (!TryGetForegroundFocusedWindow(out var foregroundFocused)
                || !IsNativeFocusInsideWebView(handle, foregroundFocused))
            {
                return;
            }

            focused = foregroundFocused;
            focusSource = "foreground";
        }

        var focusTarget = TopLevel.GetTopLevel(_webView)?.TryGetPlatformHandle()?.Handle
            ?? NativeMethods.GetParent(handle);
        if (focusTarget == IntPtr.Zero
            || focusTarget == handle
            || IsNativeFocusInsideWebView(handle, focusTarget))
        {
            return;
        }

        var previous = NativeMethods.SetFocus(focusTarget);
        var setFocusError = previous == IntPtr.Zero
            ? Marshal.GetLastWin32Error()
            : 0;
        var focusedAfter = NativeMethods.GetFocus();
        if (IsNativeFocusInsideWebView(handle, focusedAfter))
        {
            ApplicateTrace.DiagMs(
                "pane-seq",
                "native-focus-release-failed",
                $"source={focusSource} focus=0x{focused.ToInt64():X} target=0x{focusTarget.ToInt64():X} previous=0x{previous.ToInt64():X} after=0x{focusedAfter.ToInt64():X} error={setFocusError}");
            return;
        }

        ApplicateTrace.ModeToggle(
            $"ReleaseNativeWebViewFocusBeforeHide source={focusSource} focus=0x{focused.ToInt64():X} target=0x{focusTarget.ToInt64():X} previous=0x{previous.ToInt64():X} after=0x{focusedAfter.ToInt64():X} error={setFocusError}");
    }

    private static bool IsNativeFocusInsideWebView(IntPtr root, IntPtr focused)
        => root != IntPtr.Zero
           && focused != IntPtr.Zero
           && (focused == root || NativeMethods.IsChild(root, focused));

    private static bool TryGetForegroundFocusedWindow(out IntPtr focused)
    {
        var info = new NativeGuiThreadInfo
        {
            CbSize = Marshal.SizeOf<NativeGuiThreadInfo>(),
        };
        if (!NativeMethods.GetGUIThreadInfo(0, ref info))
        {
            focused = IntPtr.Zero;
            return false;
        }

        focused = info.FocusWindow;
        return focused != IntPtr.Zero;
    }

    internal void PrepareNativeWebViewForHiddenPaint()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = _webView.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SyncNativeWebViewWindowSize(handle);
        if (!TryCaptureNativeWebViewPlacement(handle, out var placement))
        {
            return;
        }

        _pendingNativeHiddenPaintPlacement = placement;
        var offscreenX = CalculateNativeOffscreenX(handle, placement.Width);
        var flags = NativeMethods.SwpNoZOrder
            | NativeMethods.SwpNoActivate
            | NativeMethods.SwpNoOwnerZOrder
            | NativeMethods.SwpNoCopyBits;
        SyncNativeWebViewChildTree(handle);
        var moved = NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            offscreenX,
            placement.Y,
            placement.Width,
            placement.Height,
            flags);
        SetNativeWebViewTreeVisibility(handle, isVisible: true);
        ApplicateTrace.ModeToggle(
            $"PrepareNativeWebViewForHiddenPaint moved={moved} saved={placement.X},{placement.Y},{placement.Width}x{placement.Height} offscreenX={offscreenX}");
    }

    internal void CompleteNativeWebViewHiddenPaint()
    {
        if (!OperatingSystem.IsWindows())
        {
            SetNativeWebViewVisibility(true);
            return;
        }

        var handle = _webView.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            SetNativeWebViewVisibility(true);
            return;
        }

        var placementSource = "pending";
        if (_pendingNativeHiddenPaintPlacement is not { } placement)
        {
            SyncNativeWebViewWindowSize(handle);
            if (!TryCaptureNativeWebViewPlacement(handle, out placement))
            {
                SetNativeWebViewVisibility(true);
                return;
            }

            placementSource = "current";
        }

        _pendingNativeHiddenPaintPlacement = null;
        SetNativeWebViewTreeVisibility(handle, isVisible: false);
        // Do not copy the offscreen backing store into the visible slot. The
        // 2026-05-24 frame capture showed WebView2 can expose one fragmented
        // text frame when Windows preserves those bits during the move back.
        // Force a normal repaint at the settled bounds instead. Transactional
        // mode switches intentionally use the current hidden placement here:
        // moving the HWND offscreen before the renderer's rAF-settle ACK can
        // stall that ACK, so the no-copy repaint belongs at reveal time.
        var flags = NativeMethods.SwpNoZOrder
            | NativeMethods.SwpNoActivate
            | NativeMethods.SwpNoOwnerZOrder
            | NativeMethods.SwpNoCopyBits;
        var restored = NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            placement.X,
            placement.Y,
            placement.Width,
            placement.Height,
            flags);
        SyncNativeWebViewChildTree(handle);
        SetNativeWebViewTreeVisibility(handle, isVisible: true);
        ApplicateTrace.ModeToggle(
            $"CompleteNativeWebViewHiddenPaint restored={restored} source={placementSource} saved={placement.X},{placement.Y},{placement.Width}x{placement.Height}");
    }

    private void SyncNativeWebViewWindowSize(IntPtr handle)
    {
        var bounds = _webView.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var scaling = TopLevel.GetTopLevel(_webView)?.RenderScaling ?? 1.0;
        var width = SysMath.Max(1, (int)SysMath.Round(bounds.Width * scaling, MidpointRounding.AwayFromZero));
        var height = SysMath.Max(1, (int)SysMath.Round(bounds.Height * scaling, MidpointRounding.AwayFromZero));
        var flags = NativeMethods.SwpNoMove
            | NativeMethods.SwpNoZOrder
            | NativeMethods.SwpNoActivate
            | NativeMethods.SwpNoOwnerZOrder
            | NativeMethods.SwpNoCopyBits;
        var ok = NativeMethods.SetWindowPos(handle, IntPtr.Zero, 0, 0, width, height, flags);
        SyncNativeWebViewChildTree(handle);
        var parent = NativeMethods.GetParent(handle);
        _ = NativeMethods.GetWindowRect(handle, out var rect);
        ApplicateTrace.ModeToggle(
            $"SyncNativeWebViewWindowSize ok={ok} dips={bounds.Width:F1}x{bounds.Height:F1} scale={scaling:F2} px={width}x{height} parent=0x{parent.ToInt64():X} rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}");
    }

    private static void SetNativeWebViewTreeVisibility(IntPtr root, bool isVisible)
    {
        if (root == IntPtr.Zero)
        {
            return;
        }

        var windows = EnumerateNativeDescendants(root);
        if (isVisible)
        {
            _ = NativeMethods.ShowWindow(root, NativeMethods.SwShow);
            foreach (var child in windows)
            {
                _ = NativeMethods.ShowWindow(child, NativeMethods.SwShow);
            }
            return;
        }

        for (var index = windows.Count - 1; index >= 0; index--)
        {
            _ = NativeMethods.ShowWindow(windows[index], NativeMethods.SwHide);
        }
        _ = NativeMethods.ShowWindow(root, NativeMethods.SwHide);
    }

    private static void SyncNativeWebViewChildTree(IntPtr root)
    {
        if (root == IntPtr.Zero)
        {
            return;
        }

        var syncedCount = SyncNativeWebViewDirectChildren(root);
        if (syncedCount > 0)
        {
            ApplicateTrace.ModeToggle($"SyncNativeWebViewChildTree synced={syncedCount}");
        }
    }

    private static int SyncNativeWebViewDirectChildren(IntPtr parent)
    {
        var syncedCount = 0;
        foreach (var child in EnumerateDirectNativeChildren(parent))
        {
            if (NativeMethods.GetClientRect(parent, out var parentClient))
            {
                var width = SysMath.Max(1, parentClient.Right - parentClient.Left);
                var height = SysMath.Max(1, parentClient.Bottom - parentClient.Top);
                var flags = NativeMethods.SwpNoZOrder
                    | NativeMethods.SwpNoActivate
                    | NativeMethods.SwpNoOwnerZOrder
                    | NativeMethods.SwpNoCopyBits;
                if (NativeMethods.SetWindowPos(child, IntPtr.Zero, 0, 0, width, height, flags))
                {
                    syncedCount++;
                }
            }

            syncedCount += SyncNativeWebViewDirectChildren(child);
        }

        return syncedCount;
    }

    private static List<IntPtr> EnumerateNativeDescendants(IntPtr root)
    {
        var descendants = new List<IntPtr>();
        NativeMethods.EnumChildWindows(
            root,
            (windowHandle, _) =>
            {
                descendants.Add(windowHandle);
                return true;
            },
            IntPtr.Zero);
        return descendants;
    }

    private static List<IntPtr> EnumerateDirectNativeChildren(IntPtr parent)
    {
        var children = new List<IntPtr>();
        NativeMethods.EnumChildWindows(
            parent,
            (windowHandle, _) =>
            {
                if (NativeMethods.GetParent(windowHandle) == parent)
                {
                    children.Add(windowHandle);
                }
                return true;
            },
            IntPtr.Zero);
        return children;
    }

    private static bool TryCaptureNativeWebViewPlacement(IntPtr handle, out NativeWindowPlacement placement)
    {
        placement = default;
        var parent = NativeMethods.GetParent(handle);
        if (parent == IntPtr.Zero || !NativeMethods.GetWindowRect(handle, out var rect))
        {
            return false;
        }

        var topLeft = new NativePoint(rect.Left, rect.Top);
        if (!NativeMethods.ScreenToClient(parent, ref topLeft))
        {
            return false;
        }

        var width = SysMath.Max(1, rect.Right - rect.Left);
        var height = SysMath.Max(1, rect.Bottom - rect.Top);
        placement = new NativeWindowPlacement(topLeft.X, topLeft.Y, width, height);
        return true;
    }

    private static int CalculateNativeOffscreenX(IntPtr handle, int width)
    {
        var virtualLeft = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen);
        var parent = NativeMethods.GetParent(handle);
        if (parent == IntPtr.Zero || !NativeMethods.GetWindowRect(parent, out var parentRect))
        {
            return virtualLeft - width - NativeOffscreenMargin;
        }

        return virtualLeft - parentRect.Left - width - NativeOffscreenMargin;
    }

    internal void PrepareNativeRendererForReveal(TimeSpan duration)
    {
        PostRendererMessage(new
        {
            type = "mode-reveal-prepare",
            durationMs = ToRendererDurationMs(duration)
        });
    }

    internal void RevealNativeRenderer(TimeSpan duration)
    {
        PostRendererMessage(new
        {
            type = "mode-reveal-start",
            durationMs = ToRendererDurationMs(duration)
        });
    }

    private void PrepareNativeDocumentReveal(TimeSpan duration)
    {
        _documentRevealPending = true;
        PostRendererMessage(new
        {
            type = "document-reveal-prepare",
            durationMs = ToRendererDurationMs(duration),
            theme = GetThemeName()
        });
    }

    private void RevealNativeDocument(TimeSpan duration)
    {
        if (!_documentRevealPending)
        {
            return;
        }

        _documentRevealPending = false;
        PostRendererMessage(new
        {
            type = "document-reveal-start",
            durationMs = ToRendererDurationMs(duration)
        });
    }

    private static int ToRendererDurationMs(TimeSpan duration)
        => (int)SysMath.Clamp(
            SysMath.Round(duration.TotalMilliseconds, MidpointRounding.AwayFromZero),
            ReadingPreferences.MinModeSwitchSmoothDurationMs,
            ReadingPreferences.MaxModeSwitchSmoothDurationMs);

    private sealed class ReparentScope : IDisposable
    {
        private readonly ApplicateWebMarkdownDocumentView _owner;
        private IDisposable? _inner;

        public ReparentScope(ApplicateWebMarkdownDocumentView owner, IDisposable inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public void Dispose()
        {
            if (_inner is null)
            {
                return;
            }

            try
            {
                _inner.Dispose();
            }
            finally
            {
                _inner = null;
                _owner._intentionalReparentDepth--;
            }
        }
    }

    private void OnRenderInputChanged()
    {
        if (_isUpdatingInputs)
        {
            return;
        }

        QueueRender();
    }

    private void OnLiveInputChanged()
    {
        if (_isUpdatingInputs)
        {
            return;
        }

        ApplyReadingPreferences();
    }

    internal static ApplicateWebInputUpdateAction DetermineInputUpdateAction(
        bool sourceChanged,
        bool imageSourceResolverChanged,
        bool hasLoadedDocument,
        bool readingPreferencesChanged,
        bool availableContentWidthChanged,
        bool viewerChromeEnabledChanged,
        bool documentScrollEnabledChanged = false,
        bool wheelProxyEnabledChanged = false)
    {
        if (sourceChanged || imageSourceResolverChanged || !hasLoadedDocument)
        {
            return ApplicateWebInputUpdateAction.Render;
        }

        return readingPreferencesChanged
               || availableContentWidthChanged
               || viewerChromeEnabledChanged
               || documentScrollEnabledChanged
               || wheelProxyEnabledChanged
            ? ApplicateWebInputUpdateAction.ApplyLivePreferences
            : ApplicateWebInputUpdateAction.None;
    }

    internal static bool ShouldPrepareDocumentRevealForTesting(
        bool sourceChanged,
        bool hasLoadedDocument,
        MarkdownSource? nextSource)
        => ShouldPrepareDocumentReveal(sourceChanged, hasLoadedDocument, nextSource);

    private static bool ShouldPrepareDocumentReveal(
        bool sourceChanged,
        bool hasLoadedDocument,
        MarkdownSource? nextSource)
        => sourceChanged && hasLoadedDocument && nextSource is not null;

    private static bool AreEqual(double left, double right)
        => double.IsNaN(left) && double.IsNaN(right) || SysMath.Abs(left - right) <= double.Epsilon;

    private void QueueRender(bool skipFrameWaitUntilRenderReady = false)
    {
        if (_disposed)
        {
            return;
        }

        DocumentRenderInvalidated?.Invoke(this, EventArgs.Empty);
        var renderId = ++_renderSequence;
        _hasLoadedDocument = false;
        _awaitingLayoutReady = false;
        _hasLayoutReady = false;
        _hasMinimapState = false;
        _lastLayoutReadyWasCached = false;
        _activeRevealRenderId = renderId;
        _requiresPostReadyEnhancements = false;
        _postReadyEnhancementsComplete = true;
        _documentRenderedRaised = false;
        _documentRevealReadyRaised = false;
        _scrollTop = 0;
        _scrollHeight = 0;
        _clientHeight = 0;
        CancelRender();

        var source = Source;
        ApplicateTrace.ModeToggle(
            $"Web.QueueRender id={renderId} source={source?.Path ?? "(null)"} shell={_shellMode} theme={GetThemeName()}");
        if (_shellMode)
        {
            _renderCancellation = new CancellationTokenSource();
            _ = QueueRenderShellAsync(source, renderId, skipFrameWaitUntilRenderReady, _renderCancellation.Token);
            return;
        }

        if (source is null)
        {
            DeleteCurrentGeneratedDocument();
            _webView.Navigate(new Uri("about:blank"));
            return;
        }

        _renderCancellation = new CancellationTokenSource();
        _ = RenderAsync(source, _renderCancellation.Token);
    }

    private async Task QueueRenderShellAsync(
        MarkdownSource? source,
        long renderId,
        bool skipFrameWaitUntilRenderReady,
        CancellationToken cancellationToken)
    {
        try
        {
            ApplicateTrace.ModeToggle($"Web.RenderShell start id={renderId} source={source?.Path ?? "(null)"}");
            if (!_shellNavigated)
            {
                // PE r2 item A — race-safe shell-init latch. _shellReady MUST be
                // initialized before NavigateToShellAsync (Sonnet MUST-FIX 1: the
                // document-ready IPC at OnWebMessageReceived calls TrySetResult
                // and silently drops if the TCS is null; combined with the
                // _shellDocumentReadyConsumed latch it would hang any subsequent
                // wait on _shellReady.Task). _shellNavigated MUST be set BEFORE
                // the await on NavigateToShellAsync so any parallel caller
                // (pre-warm vs lazy, or rapid back-to-back RequestRenders) sees
                // the in-flight navigation and falls through to wait on the same
                // TCS instead of issuing a duplicate Navigate. On exception we
                // roll back both so the next render attempt can retry.
                _shellReady ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _shellNavigated = true;
                ApplicateTrace.ModeToggle($"Web.RenderShell navigate-shell id={renderId}");
                try
                {
                    await NavigateToShellAsync(cancellationToken).ConfigureAwait(true);
                }
                catch
                {
                    _shellNavigated = false;
                    throw;
                }
            }

            // Wait for shell's first document-ready before posting load-document.
            // Without this gate, PostRendererMessage races with the renderer-shell
            // page load — the renderer's message listener doesn't exist yet.
            if (_shellReady is not null)
            {
                ApplicateTrace.ModeToggle($"Web.RenderShell wait-shell-ready id={renderId}");
                await _shellReady.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
                ApplicateTrace.ModeToggle($"Web.RenderShell shell-ready id={renderId}");
            }

            if (source is null)
            {
                ApplicateTrace.ModeToggle($"Web.RenderShell post-clear id={renderId}");
                PostRendererMessage(new { type = "clear-document" });
                return;
            }

            var readingPreferences = ReadingPreferences;
            var imageSourceResolver = ImageSourceResolver;
            var renderedFromMarkdown = false;
            var body = await _renderedBodyCache
                .GetOrRenderAsync(
                    source,
                    readingPreferences,
                    imageSourceResolver,
                    async ct =>
                    {
                        renderedFromMarkdown = true;
                        ApplicateTrace.ModeToggle($"Web.RenderShell render-body-start id={renderId} source={source.Path}");
                        var renderedBody = await _renderer
                            .RenderBodyAsync(source, readingPreferences, imageSourceResolver, ct)
                            .ConfigureAwait(true);
                        ApplicateTrace.ModeToggle(
                            $"Web.RenderShell render-body-end id={renderId} source={source.Path} htmlLength={renderedBody.BodyHtml.Length} theme={GetThemeName()}");
                        return renderedBody;
                    },
                    cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (!renderedFromMarkdown)
            {
                ApplicateTrace.ModeToggle(
                    $"Web.RenderShell render-body-cache-hit id={renderId} source={source.Path} htmlLength={body.BodyHtml.Length} theme={GetThemeName()}");
            }

            ConfigureDocumentRevealGate(
                renderId,
                requiresPostReadyEnhancements: body.HasMermaidBlock || body.HasCodeBlockWithSyntax);
            var theme = GetThemeName();
            var rendererCacheKey = body.RendererCacheKeySuffix is { Length: > 0 } suffix
                ? ApplicateRendererDocumentCacheKeys.Create(theme, source.Path, suffix)
                : null;
            object fullLoadDocumentMessage = new
            {
                type = "load-document",
                html = body.BodyHtml,
                documentName = source.FileName,
                theme,
                hasMermaid = body.HasMermaidBlock,
                hasHljs = body.HasCodeBlockWithSyntax,
                renderId,
                skipFrameWait = skipFrameWaitUntilRenderReady,
                cacheKey = rendererCacheKey
            };
            object rendererMessage = fullLoadDocumentMessage;

            if (rendererCacheKey is not null && _postedRendererDocumentCacheKeys.Contains(rendererCacheKey))
            {
                rendererMessage = new
                {
                    type = "load-cached-document",
                    cacheKey = rendererCacheKey,
                    documentName = source.FileName,
                    theme,
                    hasMermaid = body.HasMermaidBlock,
                    hasHljs = body.HasCodeBlockWithSyntax,
                    renderId,
                    skipFrameWait = skipFrameWaitUntilRenderReady
                };
                _pendingRendererCacheFallbackLoads[renderId] = fullLoadDocumentMessage;
                ApplicateTrace.ModeToggle($"Web.RenderShell post-cached-load id={renderId} source={source.Path} cacheKey={rendererCacheKey}");
            }
            else
            {
                if (rendererCacheKey is not null)
                {
                    _postedRendererDocumentCacheKeys.Add(rendererCacheKey);
                }

                _pendingRendererCacheFallbackLoads.Remove(renderId);
            }

            PostRendererMessage(rendererMessage);
            ApplicateTrace.ModeToggle($"Web.RenderShell post-load id={renderId} source={source.Path}");
        }
        catch (OperationCanceledException)
        {
            ApplicateTrace.ModeToggle($"Web.RenderShell canceled id={renderId}");
            // Superseded render; later QueueRender owns state.
        }
        catch
        {
            FallbackRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task NavigateToShellAsync(CancellationToken cancellationToken)
    {
        if (_shellAssetFactory is null)
        {
            throw new InvalidOperationException("Shell mode requires IApplicateShellAssetBundleFactory.");
        }

        var bundle = await _shellAssetFactory.GetAsync(cancellationToken).ConfigureAwait(true);
        var html = ApplicateHtmlDocumentTemplate.BuildShell(
            ReadingPreferences,
            bundle.Base,
            bundle.Mermaid,
            bundle.Highlight);
        // PE r2 item F: capture the theme name inlined into the shell HTML so
        // the user-document document-ready branch in OnWebMessageReceived can
        // suppress redundant SendTheme() when the renderer's first message
        // arrives with the same theme already applied.
        var shellTheme = GetThemeName();
        _inlinedTheme = shellTheme;
        html = ApplyInitialTheme(html, shellTheme);

        var folder = GetGeneratedDocumentFolder();
        Directory.CreateDirectory(folder);
        var shellPath = Path.Combine(folder, $"renderer-shell-{_shellDocumentId}.html");
        await File.WriteAllTextAsync(shellPath, html, Encoding.UTF8, cancellationToken).ConfigureAwait(true);

        _currentGeneratedDocumentPath = shellPath;
        _isLoadingGeneratedDocument = true;
        _webView.Navigate(new Uri(shellPath));
    }

    /// <summary>
    /// Pre-warm the renderer shell so the first user <c>RequestRender</c> does
    /// not pay the ~502 ms <c>navigate-shell → shell-ready</c> gap on the
    /// user-visible critical path. Idempotent: re-entrant calls after the
    /// shell has already navigated return immediately. No-op when shell mode
    /// is disabled (the legacy per-document <c>Navigate</c> path has no shell
    /// to pre-warm).
    ///
    /// <para><b>TCS init order is load-bearing</b> (PE r2 item A, Sonnet
    /// MUST-FIX 1). <c>_shellReady</c> MUST be created BEFORE
    /// <see cref="NavigateToShellAsync"/> runs. Otherwise the shell's
    /// <c>document-ready</c> IPC fires at <c>OnWebMessageReceived</c> while
    /// <c>_shellReady</c> is still <c>null</c>, <c>_shellReady?.TrySetResult</c>
    /// is silently dropped, AND <c>_shellDocumentReadyConsumed = true</c>
    /// prevents the gate from re-firing — the user's later
    /// <see cref="QueueRenderShellAsync"/> awaits <c>_shellReady.Task</c>
    /// forever → startup hang.</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation for the pre-warm I/O.
    /// Cancelling does NOT throw past the caller's await; on failure or
    /// cancellation the lazy <see cref="QueueRenderShellAsync"/> path
    /// regains ownership at the next user render.</param>
    internal async Task EnsureShellReadyAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        // Idempotency guard. When navigation is already in flight, converge on
        // the same shell-ready TCS instead of treating "Navigate() was issued"
        // as "document-ready IPC has arrived".
        if (_shellNavigated)
        {
            if (_shellReady is not null)
            {
                await _shellReady.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
            }

            return;
        }

        // Legacy mode has no shell to pre-warm; the path silently no-ops so
        // the host can call EnsureShellReadyAsync unconditionally at app boot
        // without needing to inspect shell-mode state itself.
        if (!_shellMode || _shellAssetFactory is null)
        {
            return;
        }

        try
        {
            // Step 1 (TCS init): MUST happen before NavigateToShellAsync.
            // See XML doc above for the silent-drop hang this prevents.
            _shellReady ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Step 2 (idempotency latch — race-safe ordering): set BEFORE
            // the await so any parallel lazy QueueRenderShellAsync (driven
            // by the user's first RequestRender that fires while pre-warm
            // is still awaiting bundle.GetAsync inside NavigateToShellAsync)
            // sees the in-flight navigation and falls through to wait on
            // the same _shellReady TCS instead of issuing a duplicate
            // Navigate. The roll-back in the catch block restores the
            // false latch so the lazy path can retry.
            _shellNavigated = true;

            try
            {
                // Step 3 (navigate): drives the asset-bundle load, HTML write,
                // and _webView.Navigate(shellPath). The Navigate must run on
                // the Avalonia UI thread; callers are responsible for posting
                // the pre-warm entry through Dispatcher.UIThread when needed.
                await NavigateToShellAsync(cancellationToken).ConfigureAwait(true);
            }
            catch
            {
                _shellNavigated = false;
                throw;
            }

            // Step 4 (await IPC): the shell page must finish parsing and post
            // document-ready before the user's first load-document IPC can be
            // processed. Wait on the same TCS the lazy QueueRenderShellAsync
            // path waits on, so by the time this method returns the user
            // critical path is unblocked end-to-end (PE r2 §5 acceptance gate:
            // shell-prewarm-ready must mean fully-ready, not just navigated).
            if (_shellReady is not null)
            {
                await _shellReady.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Pre-warm cancelled (window closed, dispose race). Leave
            // _shellNavigated == false so the lazy path takes over on the
            // next user render; QueueRenderShellAsync will retry the shell
            // navigation. _shellReady's TCS remains pending so a cancelled
            // pre-warm does not poison later lazy render attempts.
            throw;
        }
        catch
        {
            // Asset-bundle load failed, file write failed, or Navigate
            // threw. Leave _shellNavigated == false so the lazy path retries
            // at the next user render. The lazy path's catch routes through
            // FallbackRequested if its own attempt also fails.
            throw;
        }
    }

    private async Task RenderAsync(MarkdownSource source, CancellationToken cancellationToken)
    {
        try
        {
            var document = await _renderer
                .RenderAsync(source, ReadingPreferences, ImageSourceResolver, cancellationToken)
                .ConfigureAwait(true);

            string? generatedDocumentPath = null;
            try
            {
                // PE r2 item F: capture the theme inlined into the user-doc
                // HTML so the document-ready branch can suppress redundant
                // SendTheme() when the renderer's message arrives with the
                // matching theme. Always overwrites the prior shell-theme
                // value so the suppression compares against what's actually
                // sitting in the user-visible document.
                var docTheme = GetThemeName();
                _inlinedTheme = docTheme;
                var html = ApplyInitialTheme(document.Html, docTheme);
                generatedDocumentPath = await WriteGeneratedDocumentAsync(html, cancellationToken)
                    .ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();

                var previousGeneratedDocumentPath = _currentGeneratedDocumentPath;
                _currentGeneratedDocumentPath = generatedDocumentPath;
                generatedDocumentPath = null;
                DeleteGeneratedDocument(previousGeneratedDocumentPath);

                _isLoadingGeneratedDocument = true;
                _webView.Navigate(new Uri(_currentGeneratedDocumentPath));
            }
            finally
            {
                DeleteGeneratedDocument(generatedDocumentPath);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded render. The next source/preference change owns the state.
        }
        catch
        {
            _isLoadingGeneratedDocument = false;
            FallbackRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CancelRender()
    {
        _renderCancellation?.Cancel();
        _renderCancellation?.Dispose();
        _renderCancellation = null;
    }

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        ApplicateTrace.DiagMs("startup-webview", "environment-requested");
        e.EnableDevTools = false;
        if (e is WindowsWebView2EnvironmentRequestedEventArgs windows)
        {
            windows.UserDataFolder = GetWebViewUserDataFolder();
            windows.IsInPrivateModeEnabled = true;
        }
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        var request = e.Request?.ToString();
        var allowed = _isLoadingGeneratedDocument
            ? ApplicateWebResourcePolicy.IsAllowedInitialDocumentNavigation(request, GetGeneratedDocumentFolder())
            : ApplicateWebResourcePolicy.IsAllowedNavigation(request);

        if (!allowed)
        {
            e.Cancel = true;
        }
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        _isLoadingGeneratedDocument = false;
        // NavigationCompleted with IsSuccess=false on our local file:// pipeline
        // is dominated by superseded-navigate events (rapid tab switches cancel
        // the in-flight navigate). Real WebView load errors surface through the
        // exception catches in RenderShell / render-generated-document, which
        // is the single source of truth for "FallbackRequested". Empirically
        // confirmed via diagnostic logging 2026-05-19: every observed
        // user-visible false-fire of the failure view originated here, never
        // from the catches. Branch removed.
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        // The browser context menu "Open link in new window" raises this
        // event with the requested URL. Route it through the same logic as
        // a left-click on a link in renderer.js: local markdown files open
        // as new tabs, external URLs launch via the system default browser.
        var requestUri = e.Request?.ToString();
        if (string.IsNullOrWhiteSpace(requestUri))
        {
            return;
        }
        _ = HandleNewWindowAsync(requestUri);
    }

    private async System.Threading.Tasks.Task HandleNewWindowAsync(string href)
    {
        if (TryResolveLocalLink(href, out var localTarget))
        {
            await HandleLocalLinkAsync(localTarget).ConfigureAwait(true);
            return;
        }

        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return;
        }

        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is not null)
        {
            await launcher.LaunchUriAsync(uri).ConfigureAwait(true);
        }
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Body))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(e.Body);
            if (!document.RootElement.TryGetProperty("type", out var typeProperty))
            {
                return;
            }

            var type = typeProperty.GetString();
            if (type == "document-ready")
            {
                if (_shellMode && !_shellDocumentReadyConsumed)
                {
                    // First document-ready in shell mode = empty shell page loaded.
                    // Not a user document yet — do NOT fire DocumentRendered.
                    // SendReadingPreferences below triggers the renderer's
                    // applyReadingPreferences path; the actual user-document
                    // document-ready arrives later via load-document's
                    // scheduleLayoutReady wrapper.
                    _shellDocumentReadyConsumed = true;
                    _shellReady?.TrySetResult(true);
                    SendTheme();
                    SendMinimapPolicy();
                    SendReadingPreferences();
                    return;
                }
                _hasLoadedDocument = true;
                BeginAwaitingLayoutReady();
                // PE r2 item F: skip SendTheme() when the theme already
                // inlined into the document HTML via ApplyInitialTheme
                // matches the current GetThemeName(). The renderer's HTML
                // already carries data-theme=<inlined>, so a SendTheme()
                // here would be a no-op IPC round-trip costing ~10-30 ms on
                // the post-load critical path. _inlinedTheme is null only
                // on cold paths where ApplyInitialTheme never ran (e.g.
                // legacy non-shell mode racing this handler); in that case
                // fall through to SendTheme() as the safe baseline.
                // Shell-empty document-ready (line above, _shellMode &&
                // !_shellDocumentReadyConsumed branch) is OUT OF SCOPE per
                // PE r2 §6 - the shell pre-warm pays no user-visible cost.
                // Theme changes after document load still propagate via
                // OnThemeChanged -> SendTheme(), which is independent of
                // this guard.
                var currentTheme = GetThemeName();
                if (_inlinedTheme is not null
                    && string.Equals(_inlinedTheme, currentTheme, StringComparison.Ordinal))
                {
                    ApplicateTrace.Diag("perf-msg", "send-theme suppressed=true",
                        $"inlined={_inlinedTheme} current={currentTheme}");
                }
                else
                {
                    ApplicateTrace.Diag("perf-msg", "send-theme suppressed=false",
                        $"inlined={_inlinedTheme ?? "(null)"} current={currentTheme}");
                    SendTheme();
                }
                SendMinimapPolicy();
                SendReadingPreferences();
                return;
            }

            if (IsLayoutReadyMessage(document.RootElement))
            {
                ApplicateTrace.DiagMs("diag-gate", "ipc-layout-ready",
                    $"awaiting={_awaitingLayoutReady} hasLoaded={_hasLoadedDocument} hasLayoutBefore={_hasLayoutReady} cached={ReadBoolean(document.RootElement, "cached")}");
                _lastLayoutReadyWasCached = ReadBoolean(document.RootElement, "cached");
                HandleScrollMessage(document.RootElement);
                _hasLayoutReady = true;
                CompleteLayoutReady();
                return;
            }

            if (IsPostReadyEnhancementsCompleteMessage(document.RootElement))
            {
                HandlePostReadyEnhancementsComplete(document.RootElement);
                return;
            }

            if (type == "scroll")
            {
                HandleScrollMessage(document.RootElement);
                return;
            }

            if (type == "debug-log")
            {
                if (document.RootElement.TryGetProperty("text", out var textProp))
                {
                    var text = textProp.GetString();
                    if (text is null)
                    {
                        return;
                    }
                    System.Console.Error.WriteLine($"[renderer-debug] {text}");
                }
                return;
            }

            if (type == "theme-applied")
            {
                HandleThemeAppliedMessage(document.RootElement);
                return;
            }

            if (type == "perf-mark")
            {
                // Round-2 perf-engineer plan item C, [renderer-perf] group.
                // The renderer signals a milestone; the host stamps elapsed-ms
                // against its own process-anchored Stopwatch (avoids clock-skew
                // between renderer performance.now() and host wall clock) and
                // forwards as `[renderer-perf] <name> ms=<elapsed>`.
                if (document.RootElement.TryGetProperty("name", out var nameProp))
                {
                    var name = nameProp.GetString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        string extras = string.Empty;
                        if (document.RootElement.TryGetProperty("detail", out var detailProp)
                            && detailProp.ValueKind == JsonValueKind.String)
                        {
                            var detailText = detailProp.GetString();
                            if (!string.IsNullOrEmpty(detailText))
                            {
                                extras = $"detail={detailText}";
                            }
                        }
                        ApplicateTrace.DiagMs("renderer-perf", name, extras);
                    }
                }
                return;
            }

            if (type == "document-cache-miss")
            {
                HandleDocumentCacheMissMessage(document.RootElement);
                return;
            }

            if (type == "minimap-state")
            {
                HandleMinimapStateMessage(document.RootElement);
                return;
            }

            if (type == "width-drag")
            {
                HandleWidthDragMessage(document.RootElement);
                return;
            }

            if (type == "wheel")
            {
                HandleWheelMessage(document.RootElement);
                return;
            }

            if (IsViewerInteractionMessage(document.RootElement))
            {
                ViewerInteractionRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (type == "csp-violation")
            {
                var blocked = document.RootElement.TryGetProperty("blockedURI", out var b) ? b.GetString() : "";
                var directive = document.RootElement.TryGetProperty("violatedDirective", out var d) ? d.GetString() : "";
                var sourceFile = document.RootElement.TryGetProperty("sourceFile", out var s) ? s.GetString() : "";
                var line = document.RootElement.TryGetProperty("lineNumber", out var l) && l.TryGetInt32(out var lineVal) ? lineVal : 0;
                Console.Error.WriteLine($"[CSP] {directive} blocked {blocked} at {sourceFile}:{line}");
                return;
            }

            if (type == "link-clicked")
            {
                _ = HandleLinkClickedAsync(document.RootElement);
                return;
            }

            if (type == "drag-hover")
            {
                HandleDragHoverMessage(document.RootElement);
                return;
            }

            if (type == "drop-file")
            {
                _ = HandleDropFileMessageAsync(document.RootElement);
                return;
            }

            if (type == "host-shortcut")
            {
                if (document.RootElement.TryGetProperty("combo", out var comboElement))
                {
                    var combo = comboElement.GetString();
                    if (!string.IsNullOrEmpty(combo))
                    {
                        HostShortcutHandler?.Invoke(combo);
                    }
                }
                return;
            }

            if (type == "headings-updated")
            {
                HandleHeadingsUpdatedMessage(document.RootElement);
                return;
            }

            if (type == "active-heading-changed")
            {
                if (document.RootElement.TryGetProperty("id", out var idElement)
                    && idElement.ValueKind == JsonValueKind.String)
                {
                    var id = idElement.GetString();
                    if (!string.IsNullOrEmpty(id))
                    {
                        ActiveHeadingChanged?.Invoke(this, id);
                    }
                }
                return;
            }

            if (type == "preview-source-line")
            {
                HandlePreviewSourceLineMessage(document.RootElement);
                return;
            }

            if (type == "mode-toggle-settled")
            {
                HandleModeToggleSettledMessage(document.RootElement);
                return;
            }

            if (type == "minimap-settled")
            {
                HandleMinimapSettledMessage(document.RootElement);
                return;
            }

            if (type == "debug-log")
            {
                if (document.RootElement.TryGetProperty("message", out var messageElement))
                {
                    var message = messageElement.GetString();
                    if (!string.IsNullOrEmpty(message))
                    {
                        Console.Error.WriteLine(message);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Ignore malformed renderer messages; the WebView cannot drive shell state through them.
        }
    }

    // Window-level KeyBindings (Ctrl+E, Ctrl+O, etc.) declared in
    // MainWindow.axaml stop firing when keyboard focus lives inside the
    // WebView2 native HWND. The renderer's wireHostShortcuts captures the
    // host's accelerator combos in JS and posts them via the host-shortcut
    // message. This static delegate is set by ApplicateMainWindow at
    // construction and forwards combos to MainWindowViewModel commands.
    // Static because the routing is window-level and the wiring should be
    // identical for both the viewer's WebView and the shared edit-preview
    // WebView; one delegate covers both.
    internal static Action<string>? HostShortcutHandler;

    private void HandleDragHoverMessage(JsonElement root)
    {
        if (!root.TryGetProperty("hovering", out var prop) || prop.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return;
        }

        var hovering = prop.GetBoolean();
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.DataContext is MarkMello.Presentation.ViewModels.MainWindowViewModel vm)
        {
            vm.IsDragHovering = hovering;
        }
    }

    private async Task HandleDropFileMessageAsync(JsonElement root)
    {
        if (!root.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("text", out var textProp) || textProp.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var name = nameProp.GetString();
        var text = textProp.GetString();
        if (string.IsNullOrWhiteSpace(name) || text is null)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.DataContext is not MarkMello.Presentation.ViewModels.MainWindowViewModel vm)
        {
            return;
        }

        // Stamp dropped content into the user's temp folder under the
        // original filename so the tab tab title matches the source file
        // and re-dropping the same file dedupes through the existing
        // OpenDocumentsService FilePath check (which mirrors VM.Document).
        // When two different files share a name, we add a short content
        // hash suffix to keep them distinct.
        var tempDir = Path.Combine(Path.GetTempPath(), "MarkMello", "Dropped");
        Directory.CreateDirectory(tempDir);
        var safeName = MakeSafeFileName(name);
        var tempPath = Path.Combine(tempDir, safeName);

        try
        {
            if (File.Exists(tempPath))
            {
                var existingContent = await File.ReadAllTextAsync(tempPath, Encoding.UTF8).ConfigureAwait(true);
                if (!string.Equals(existingContent, text, StringComparison.Ordinal))
                {
                    var nameWithoutExt = Path.GetFileNameWithoutExtension(safeName);
                    var ext = Path.GetExtension(safeName);
                    var hash = ShortContentHash(text);
                    tempPath = Path.Combine(tempDir, $"{nameWithoutExt}-{hash}{ext}");
                    if (!File.Exists(tempPath))
                    {
                        await File.WriteAllTextAsync(tempPath, text, Encoding.UTF8).ConfigureAwait(true);
                    }
                }
            }
            else
            {
                await File.WriteAllTextAsync(tempPath, text, Encoding.UTF8).ConfigureAwait(true);
            }

            await vm.OpenDroppedFileAsync(tempPath).ConfigureAwait(true);
        }
        catch
        {
            // VM surfaces load errors through its own state; nothing to add here.
        }
    }

    private static string MakeSafeFileName(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            buffer[i] = Array.IndexOf(invalid, c) >= 0 ? '_' : c;
        }
        return new string(buffer);
    }

    private static string ShortContentHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash[..4]).ToLowerInvariant();
    }

    private void HandleScrollMessage(JsonElement root)
    {
        var scrollTop = ReadDouble(root, "scrollTop");
        var scrollHeight = ReadDouble(root, "scrollHeight");
        var clientHeight = ReadDouble(root, "clientHeight");
        _scrollTop = scrollTop;
        _scrollHeight = scrollHeight;
        _clientHeight = clientHeight;
        if (scrollHeight <= 0 || clientHeight <= 0)
        {
            return;
        }

        var maximum = SysMath.Max(0, scrollHeight - clientHeight);
        var progress = maximum > 0
            ? SysMath.Clamp(scrollTop / maximum * 100.0, 0, 100)
            : 0;

        int? topBlockIndex = null;
        if (root.TryGetProperty("topBlockIndex", out var topBlockProperty)
            && topBlockProperty.ValueKind == JsonValueKind.Number
            && topBlockProperty.TryGetInt32(out var parsedTopBlock)
            && parsedTopBlock >= 0)
        {
            topBlockIndex = parsedTopBlock;
        }

        ScrollStateChanged?.Invoke(
            this,
            new ApplicateWebDocumentScrollEventArgs(progress, scrollTop, scrollHeight, clientHeight, topBlockIndex));
    }

    private void HandleHeadingsUpdatedMessage(JsonElement root)
    {
        if (!root.TryGetProperty("headings", out var headingsArray)
            || headingsArray.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var headings = new List<MarkMello.Presentation.ViewModels.DocumentHeading>(headingsArray.GetArrayLength());
        foreach (var entry in headingsArray.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            if (!entry.TryGetProperty("id", out var idProp)
                || idProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var id = idProp.GetString();
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }
            int level = 1;
            if (entry.TryGetProperty("level", out var levelProp)
                && levelProp.ValueKind == JsonValueKind.Number
                && levelProp.TryGetInt32(out var parsedLevel))
            {
                level = SysMath.Clamp(parsedLevel, 1, 6);
            }
            var text = entry.TryGetProperty("text", out var textProp)
                       && textProp.ValueKind == JsonValueKind.String
                ? textProp.GetString() ?? string.Empty
                : string.Empty;
            // Indent is pre-computed here so the host-side TOC row can bind
            // to a primitive double without a value converter — keeps the
            // XAML/code-built TOC layout simple.
            var indent = (level - 1) * 12.0;
            headings.Add(new MarkMello.Presentation.ViewModels.DocumentHeading(id, level, text, indent));
        }

        HeadingsChanged?.Invoke(this, headings);
    }

    private void HandlePreviewSourceLineMessage(JsonElement root)
    {
        if (!root.TryGetProperty("sourceLine", out var lineProperty)
            || lineProperty.ValueKind != JsonValueKind.Number
            || !lineProperty.TryGetInt32(out var sourceLine)
            || sourceLine < 0)
        {
            return;
        }

        PreviewSourceLineChanged?.Invoke(this, new ApplicateWebPreviewSourceLineEventArgs(sourceLine));
    }

    /// <summary>
    /// Send a <c>scroll-to-heading</c> IPC message to the renderer. The
    /// renderer looks up the element by id and smoothly scrolls it into view.
    /// Used by the Avalonia-side Table of Contents panel when the user clicks
    /// a TOC entry.
    /// </summary>
    public void ScrollToHeading(string headingId)
    {
        if (string.IsNullOrEmpty(headingId))
        {
            return;
        }

        PostRendererMessage(new { type = "scroll-to-heading", id = headingId });
    }

    public void ScrollToSourceLine(int sourceLine)
    {
        if (sourceLine < 0)
        {
            return;
        }

        PostRendererMessage(new { type = "scroll-to-source-line", sourceLine });
    }

    /// <summary>
    /// Send an <c>open-find-bar</c> IPC message to the renderer. The
    /// renderer toggles the in-document find bar (same controller as the
    /// Ctrl+F keystroke). Used by the magnifier toolbar button so the user
    /// can open Search without focusing the WebView first.
    /// </summary>
    public void OpenFindBar()
    {
        PostRendererMessage(new { type = "open-find-bar" });
    }

    private void HandleMinimapStateMessage(JsonElement root)
    {
        if (!TryReadMinimapState(root, out var state) || state is null)
        {
            return;
        }

        MinimapStateChanged?.Invoke(this, state);
        _hasMinimapState = true;
        CompleteLayoutReady();
    }

    private void HandleMinimapSettledMessage(JsonElement root)
    {
        if (!TryReadMinimapSettledState(root, out var settled) || settled is null)
        {
            return;
        }

        MinimapSettled?.Invoke(this, settled);
    }

    private void HandleModeToggleSettledMessage(JsonElement root)
    {
        if (!TryReadModeToggleSettledState(root, out var settled) || settled is null)
        {
            return;
        }

        // Renderer ack to the host-sent mode-settle-probe. Two rAFs have
        // elapsed in the renderer, so CSS reflow on any new slot bounds has
        // propagated and one paint has happened.
        if (settled.IsTransactional)
        {
            ModeToggleTransactionSettled?.Invoke(this, settled);
            return;
        }

        ModeToggleSettled?.Invoke(this, EventArgs.Empty);
    }

    internal static bool TryReadMinimapState(JsonElement root, out ApplicateWebMinimapStateEventArgs? state)
    {
        state = null;
        if (!root.TryGetProperty("visible", out var visibleProperty)
            || visibleProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        var visible = visibleProperty.GetBoolean();
        if (!visible)
        {
            state = new ApplicateWebMinimapStateEventArgs(visible: false, reservedWidth: 0);
            return true;
        }

        if (!root.TryGetProperty("reservedWidth", out var reservedWidthProperty)
            || reservedWidthProperty.ValueKind != JsonValueKind.Number
            || !reservedWidthProperty.TryGetDouble(out var reservedWidth)
            || !double.IsFinite(reservedWidth)
            || reservedWidth < 0
            || reservedWidth > MaxRendererReportedMinimapReservedWidth)
        {
            return false;
        }

        state = new ApplicateWebMinimapStateEventArgs(visible: true, reservedWidth);
        return true;
    }

    internal static bool TryReadMinimapSettledState(JsonElement root, out ApplicateWebMinimapSettledEventArgs? settled)
    {
        settled = null;
        if (!root.TryGetProperty("type", out var typeProperty)
            || typeProperty.ValueKind != JsonValueKind.String
            || !string.Equals(typeProperty.GetString(), "minimap-settled", StringComparison.Ordinal)
            || !root.TryGetProperty("transactionGeneration", out var generationProperty)
            || generationProperty.ValueKind != JsonValueKind.Number
            || !generationProperty.TryGetInt64(out var transactionGeneration)
            || transactionGeneration <= 0
            || !TryReadMinimapState(root, out var state)
            || state is null)
        {
            return false;
        }

        settled = new ApplicateWebMinimapSettledEventArgs(transactionGeneration, state);
        return true;
    }

    internal static bool TryReadModeToggleSettledState(
        JsonElement root,
        out ApplicateWebModeToggleSettledEventArgs? settled)
    {
        settled = null;
        if (!root.TryGetProperty("type", out var typeProperty)
            || typeProperty.ValueKind != JsonValueKind.String
            || !string.Equals(typeProperty.GetString(), "mode-toggle-settled", StringComparison.Ordinal))
        {
            return false;
        }

        if (!root.TryGetProperty("transactionGeneration", out var generationProperty))
        {
            settled = new ApplicateWebModeToggleSettledEventArgs(0);
            return true;
        }

        if (generationProperty.ValueKind != JsonValueKind.Number
            || !generationProperty.TryGetInt64(out var transactionGeneration)
            || transactionGeneration <= 0)
        {
            return false;
        }

        settled = new ApplicateWebModeToggleSettledEventArgs(transactionGeneration);
        return true;
    }

    internal static bool IsViewerInteractionMessage(JsonElement root)
        => root.TryGetProperty("type", out var typeProperty)
           && typeProperty.ValueKind == JsonValueKind.String
           && string.Equals(typeProperty.GetString(), "viewer-interaction", StringComparison.Ordinal);

    internal static bool IsLayoutReadyMessage(JsonElement root)
        => root.TryGetProperty("type", out var typeProperty)
           && typeProperty.ValueKind == JsonValueKind.String
           && string.Equals(typeProperty.GetString(), "layout-ready", StringComparison.Ordinal);

    internal static bool IsPostReadyEnhancementsCompleteMessage(JsonElement root)
        => root.TryGetProperty("type", out var typeProperty)
           && typeProperty.ValueKind == JsonValueKind.String
           && string.Equals(typeProperty.GetString(), "post-ready-enhancements-complete", StringComparison.Ordinal);

    private void ConfigureDocumentRevealGate(long renderId, bool requiresPostReadyEnhancements)
    {
        _activeRevealRenderId = renderId;
        _requiresPostReadyEnhancements = requiresPostReadyEnhancements;
        _postReadyEnhancementsComplete = !requiresPostReadyEnhancements;
        _documentRenderedRaised = false;
        _documentRevealReadyRaised = false;
        ApplicateTrace.DiagMs(
            "diag-gate",
            "document-reveal-gate-configured",
            $"renderId={renderId} requiresPostReady={requiresPostReadyEnhancements}");
    }

    private void HandlePostReadyEnhancementsComplete(JsonElement root)
    {
        if (!root.TryGetProperty("renderId", out var renderIdProperty)
            || renderIdProperty.ValueKind != JsonValueKind.Number
            || !renderIdProperty.TryGetInt64(out var renderId)
            || renderId <= 0)
        {
            return;
        }

        if (renderId != _activeRevealRenderId)
        {
            ApplicateTrace.DiagMs(
                "diag-gate",
                "post-ready-enhancements-stale",
                $"renderId={renderId} active={_activeRevealRenderId}");
            return;
        }

        _postReadyEnhancementsComplete = true;
        ApplicateTrace.DiagMs(
            "diag-gate",
            "post-ready-enhancements-complete",
            $"renderId={renderId} requiresPostReady={_requiresPostReadyEnhancements}");
        CompleteDocumentRevealReady();
    }

    private void HandleThemeAppliedMessage(JsonElement root)
    {
        if (!root.TryGetProperty("theme", out var themeProperty)
            || themeProperty.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(themeProperty.GetString())
            || !root.TryGetProperty("requestId", out var requestIdProperty)
            || requestIdProperty.ValueKind != JsonValueKind.Number
            || !requestIdProperty.TryGetInt64(out var requestId)
            || requestId <= 0)
        {
            return;
        }

        var theme = NormalizeRendererThemeName(themeProperty.GetString()!);
        ApplicateTrace.DiagMs(
            "renderer-perf",
            "theme-applied-ack",
            $"theme={theme} requestId={requestId}");
        ThemeApplied?.Invoke(this, new ApplicateWebThemeAppliedEventArgs(theme, requestId));
    }

    private void HandleDocumentCacheMissMessage(JsonElement root)
    {
        if (!root.TryGetProperty("renderId", out var renderIdProperty)
            || renderIdProperty.ValueKind != JsonValueKind.Number
            || !renderIdProperty.TryGetInt64(out var renderId)
            || renderId <= 0)
        {
            return;
        }

        if (renderId != _activeRevealRenderId)
        {
            _pendingRendererCacheFallbackLoads.Remove(renderId);
            ApplicateTrace.ModeToggle($"Web.RenderShell cached-load-miss-stale id={renderId} active={_activeRevealRenderId}");
            return;
        }

        if (!_pendingRendererCacheFallbackLoads.Remove(renderId, out var fallbackLoad))
        {
            ApplicateTrace.ModeToggle($"Web.RenderShell cached-load-miss-no-fallback id={renderId}");
            return;
        }

        ApplicateTrace.ModeToggle($"Web.RenderShell cached-load-miss-fallback id={renderId}");
        PostRendererMessage(fallbackLoad);
    }

    private void CompleteLayoutReady()
    {
        ApplicateTrace.DiagMs("diag-gate", "complete-layout-ready-enter",
            $"hasLoaded={_hasLoadedDocument} hasLayout={_hasLayoutReady} awaiting={_awaitingLayoutReady} minimap={_hasMinimapState} willFire={ShouldCompleteRender(_hasLoadedDocument, _hasLayoutReady, _hasMinimapState) && _awaitingLayoutReady}");
        if (!ShouldCompleteRender(_hasLoadedDocument, _hasLayoutReady, _hasMinimapState)
            || !_awaitingLayoutReady)
        {
            return;
        }

        _awaitingLayoutReady = false;
        CompleteDocumentRevealReady();
    }

    private void CompleteDocumentRenderVisualReady()
    {
        if (_documentRenderedRaised
            || !_hasLoadedDocument
            || !_hasLayoutReady
            || !_postReadyEnhancementsComplete)
        {
            return;
        }

        _documentRenderedRaised = true;
        RevealNativeDocument(TimeSpan.Zero);
        DocumentRendered?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteDocumentRevealReady()
    {
        if (_documentRevealReadyRaised
            || !_hasLoadedDocument
            || !_hasLayoutReady
            || !_postReadyEnhancementsComplete)
        {
            return;
        }

        CompleteDocumentRenderVisualReady();
        _documentRevealReadyRaised = true;
        _pendingRendererCacheFallbackLoads.Remove(_activeRevealRenderId);
        ApplicateTrace.DiagMs(
            "diag-gate",
            "document-reveal-ready",
            $"renderId={_activeRevealRenderId} requiresPostReady={_requiresPostReadyEnhancements}");
        DocumentRevealReady?.Invoke(this, EventArgs.Empty);
    }

    internal static bool ShouldCompleteRenderForTesting(
        bool hasLoadedDocument,
        bool hasLayoutReady,
        bool hasMinimapState)
        => ShouldCompleteRender(hasLoadedDocument, hasLayoutReady, hasMinimapState);

    private static bool ShouldCompleteRender(
        bool hasLoadedDocument,
        bool hasLayoutReady,
        bool hasMinimapState)
        // hasMinimapState dropped from the gate 2026-05-19: in shell-mode the
        // renderer's minimap path is policy-gated (F-07), refresh-content-gated,
        // and reading-preferences-gated; the minimap-state message can post much
        // later than layout-ready (or never when conditions deny minimap show).
        // Holding DocumentRendered on it leaves slot.IsVisible=false forever —
        // exact regression observed 2026-05-19 06:32. Slot commits at layout-ready;
        // minimap reservation flows separately via MinimapStateChanged.
        => hasLoadedDocument && hasLayoutReady;

    private void HandleWidthDragMessage(JsonElement root)
    {
        if (!root.TryGetProperty("phase", out var phaseProperty)
            || !TryReadWidthDragPhase(phaseProperty.GetString(), out var phase))
        {
            return;
        }

        var deltaX = ReadDouble(root, "deltaX");
        if (!double.IsFinite(deltaX) || SysMath.Abs(deltaX) > 5000)
        {
            return;
        }

        // Gate host→renderer reading-preferences echo while a web-side width
        // drag is in flight. Renderer owns the visual preview locally; an echo
        // at ~60Hz per move costs an Avalonia layout + InvokeScript IPC + JSON
        // parse on every frame, observable as drag lag on heavy formula docs.
        // Phase End triggers one fresh SendReadingPreferences with the final
        // host-clamped maxWidth so the renderer reconciles.
        if (phase == ApplicateWebWidthDragPhase.Start)
        {
            _isWebWidthDragging = true;
        }
        else if (phase == ApplicateWebWidthDragPhase.End)
        {
            _isWebWidthDragging = false;
        }

        WidthDragRequested?.Invoke(this, new ApplicateWebWidthDragEventArgs(phase, deltaX));

        if (phase == ApplicateWebWidthDragPhase.End && _hasLoadedDocument)
        {
            SendReadingPreferences();
        }
    }

    private void HandleWheelMessage(JsonElement root)
    {
        if (TryReadWheelMessage(root, out var wheel) && wheel is not null)
        {
            WheelRequested?.Invoke(this, wheel);
        }
    }

    internal static bool TryReadWheelMessage(JsonElement root, out ApplicateWebWheelEventArgs? wheel)
    {
        wheel = null;
        if (!root.TryGetProperty("deltaY", out var deltaYProperty)
            || deltaYProperty.ValueKind != JsonValueKind.Number
            || !deltaYProperty.TryGetDouble(out var deltaY)
            || !double.IsFinite(deltaY)
            || SysMath.Abs(deltaY) > 10000)
        {
            return false;
        }

        var deltaMode = 0;
        if (root.TryGetProperty("deltaMode", out var deltaModeProperty)
            && (deltaModeProperty.ValueKind != JsonValueKind.Number
                || !deltaModeProperty.TryGetInt32(out deltaMode)
                || deltaMode is < 0 or > 2))
        {
            return false;
        }

        wheel = new ApplicateWebWheelEventArgs(deltaY, deltaMode);
        return true;
    }

    private async Task HandleLinkClickedAsync(JsonElement root)
    {
        if (!root.TryGetProperty("href", out var hrefProperty))
        {
            return;
        }

        var href = hrefProperty.GetString();
        if (string.IsNullOrWhiteSpace(href))
        {
            return;
        }

        if (TryGetAnchor(href, out var anchor))
        {
            SendScrollTo(anchor);
            return;
        }

        // Relative-path links to local markdown/text files open as app tabs;
        // other existing local files are handed to the OS default app.
        if (TryResolveLocalLink(href, out var localTarget))
        {
            await HandleLocalLinkAsync(localTarget).ConfigureAwait(true);
            return;
        }

        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return;
        }

        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is not null)
        {
            await launcher.LaunchUriAsync(uri).ConfigureAwait(true);
        }
    }

    internal static bool TryResolveLocalMarkdownLinkForTesting(
        string href,
        string? sourcePath,
        out string resolvedPath)
        => TryResolveLocalLinkForTesting(
            href,
            sourcePath,
            MarkdownLocalLinkKind.MarkdownDocument,
            out resolvedPath);

    internal static bool TryResolveLocalFileLinkForTesting(
        string href,
        string? sourcePath,
        out string resolvedPath)
        => TryResolveLocalLinkForTesting(
            href,
            sourcePath,
            MarkdownLocalLinkKind.ExternalFile,
            out resolvedPath);

    private bool TryResolveLocalLink(string href, out MarkdownLocalLinkTarget target)
        => MarkdownLocalLinkResolver.TryResolve(href, Source?.Path, File.Exists, out target);

    private static bool TryResolveLocalLinkForTesting(
        string href,
        string? sourcePath,
        MarkdownLocalLinkKind expectedKind,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (!MarkdownLocalLinkResolver.TryResolve(href, sourcePath, File.Exists, out var target)
            || target.Kind != expectedKind)
        {
            return false;
        }

        resolvedPath = target.Path;
        return true;
    }

    private async Task HandleLocalLinkAsync(MarkdownLocalLinkTarget target)
    {
        if (target.Kind == MarkdownLocalLinkKind.MarkdownDocument)
        {
            var openDocs = App.Services?.GetService<Editing.IOpenDocumentsService>();
            if (openDocs is not null)
            {
                try
                {
                    await openDocs.OpenAsync(target.Path).ConfigureAwait(true);
                }
                catch (System.IO.IOException)
                {
                    // File moved or unreadable; surface nothing — user sees
                    // the click had no effect and can investigate manually.
                }
            }

            return;
        }

        await LaunchLocalFileAsync(target.Path).ConfigureAwait(true);
    }

    private async Task LaunchLocalFileAsync(string path)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var file = await topLevel.StorageProvider.TryGetFileFromPathAsync(path).ConfigureAwait(true);
        if (file is null)
        {
            return;
        }

        await topLevel.Launcher.LaunchFileAsync(file).ConfigureAwait(true);
    }

    private void ApplyReadingPreferences()
    {
        // While a web-side width drag is active, the renderer owns the visual
        // preview locally (see widthHandleDragging guard at renderer.ts:856).
        // Echoing reading-preferences per drag move costs ~60Hz of host work
        // (Avalonia layout + InvokeScript + JSON parse). The End message
        // triggers a fresh SendReadingPreferences in HandleWidthDragMessage.
        if (_isWebWidthDragging && _hasLoadedDocument)
        {
            return;
        }
        if (!_hasLoadedDocument)
        {
            // Render not finished yet. Two cases:
            // - No render in flight: a live-preference change after an
            //   earlier source has been cleared. Kick a fresh render off.
            // - Render IS in flight: do not cancel it. The in-flight render
            //   will send the up-to-date AvailableContentWidth to the
            //   renderer in SendReadingPreferences on document-ready, so the
            //   user still sees content at the correct width. Cancelling
            //   would only thrash the layout pipeline without changing the
            //   final committed state.
            if (_renderCancellation is not null)
            {
                return;
            }
            if (Source is not null)
            {
                QueueRender();
            }

            return;
        }

        SendReadingPreferences();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (_hasLoadedDocument)
        {
            SendThemeFromThemeVariantChange();
        }
    }

    private void SendThemeFromThemeVariantChange()
    {
        var theme = GetThemeName();
        var now = Stopwatch.GetTimestamp();
        if (IsDuplicateThemePostWithinWindow(
                theme,
                _lastPostedTheme,
                _lastPostedThemeTimestamp > 0
                    ? Stopwatch.GetElapsedTime(_lastPostedThemeTimestamp, now)
                    : TimeSpan.MaxValue,
                DuplicateThemePostWindow))
        {
            ApplicateTrace.Diag(
                "perf-msg",
                "send-theme suppressed=true",
                $"reason=duplicate-theme-burst theme={theme}");
            return;
        }

        SendTheme(theme, now);
    }

    private void SendTheme()
    {
        SendTheme(GetThemeName(), Stopwatch.GetTimestamp());
    }

    private void SendTheme(string theme, long timestamp)
    {
        var requestId = ++_themeRequestSequence;
        _lastPostedTheme = theme;
        _lastPostedThemeTimestamp = timestamp;
        ThemeChangeSent?.Invoke(this, new ApplicateWebThemeChangeSentEventArgs(theme, requestId));
        PostRendererMessage(new { type = "theme", theme, requestId });
    }

    internal static bool IsDuplicateThemePostWithinWindow(
        string theme,
        string? lastTheme,
        TimeSpan elapsedSinceLastPost,
        TimeSpan duplicateWindow)
        => theme.Equals(lastTheme, StringComparison.Ordinal)
           && elapsedSinceLastPost >= TimeSpan.Zero
           && elapsedSinceLastPost <= duplicateWindow;

    private string GetThemeName()
    {
        // Empirically verified 2026-05-19 via DIAG-THEME trace:
        // `ActualThemeVariant` becomes NULL whenever the View is briefly
        // orphaned from the visual tree — specifically during the
        // BeginIntentionalReparent block in ApplicateSharedWebViewHost.
        // AttachTo where Children.Remove + Children.Add transit the View
        // out of and back into a parented state. Each such transient
        // null fires ActualThemeVariantChanged → OnThemeChanged →
        // SendTheme. Without this fallback, the null-variant arm hits
        // the "light" branch below, which is then written by the
        // renderer as `documentElement.dataset.theme = "light"`. The
        // renderer's `renderer.css` intentionally maps `:root` defaults
        // to the cream Light palette (`--mm-document-background:
        // #fcfaf6`) — there is no `[data-theme="light"]` override,
        // because "light" IS root. Result: a classic-white user sees a
        // cream flash on every edit/reading toggle. Fix: when this
        // View's inherited ActualThemeVariant is unresolved (null
        // during reparent transit), fall back to the application-level
        // ActualThemeVariant, which the trace confirmed remains stable
        // at `ClassicWhite` throughout the reparent window.
        var variant = ActualThemeVariant
            ?? Avalonia.Application.Current?.ActualThemeVariant;
        return variant == ThemeVariant.Dark
            ? "dark"
            : ReadingPreferences.LightPalette == LightPaletteMode.White
                || Equals(variant?.Key, AvaloniaThemeService.ClassicWhiteThemeVariantKey)
                ? "classic-white"
                : "light";
    }

    internal static string ApplyInitialThemeForTesting(string html, string theme)
        => ApplyInitialTheme(html, theme);

    private static string ApplyInitialTheme(string html, string theme)
    {
        var normalizedTheme = NormalizeRendererThemeName(theme);
        if (html.Contains("data-theme=", StringComparison.Ordinal))
        {
            return html;
        }

        const string htmlTag = "<html";
        var htmlTagIndex = html.IndexOf(htmlTag, StringComparison.Ordinal);
        if (htmlTagIndex < 0)
        {
            return html;
        }

        return html.Insert(htmlTagIndex + htmlTag.Length, $" data-theme=\"{normalizedTheme}\"");
    }

    private static string NormalizeRendererThemeName(string theme)
        => theme.Equals("dark", StringComparison.OrdinalIgnoreCase)
            ? "dark"
            : theme.Equals("classic-white", StringComparison.OrdinalIgnoreCase)
                ? "classic-white"
                : "light";

    private void SendReadingPreferences()
    {
        // Note: do NOT reset _hasLayoutReady / _hasMinimapState here. Callers that
        // need to await a fresh renderer ack (initial document-ready handler at
        // line 357) call BeginAwaitingLayoutReady() explicitly. Live preference
        // updates (font size, width drag, chrome toggle) must not invalidate
        // readiness — otherwise the renderer is forced to re-emit layout-ready
        // and minimap-state on every drag delta, causing visible lag.
        PostRendererMessage(BuildReadingPreferencesMessage("reading-preferences"));
    }

    private object BuildReadingPreferencesMessage(string type)
    {
        var maxWidth = double.IsFinite(AvailableContentWidth) && AvailableContentWidth > 0
            ? AvailableContentWidth
            : ReadingPreferences.ContentWidth;
        return new
        {
            type,
            fontFamily = ReadingPreferences.FontFamily.ToString().ToLowerInvariant(),
            fontSize = ReadingPreferences.FontSize,
            lineHeight = ReadingPreferences.LineHeight,
            maxWidth,
            // F-06 fix: host's clamp floor sourced from the canonical
            // owner (ApplicateDocumentLayout). Renderer uses this to limit
            // drag preview so it doesn't go below where host will clamp on
            // echo; without it, drag visually pulls to renderer-local min
            // (200) but host re-clamps, snapping the document wider on
            // release. Previous reference (ApplicateViewerView.MinManualContentWidth)
            // inverted the dependency direction: a shared lower layer was
            // reading a constant from one specific consumer slot.
            minMaxWidth = ApplicateDocumentLayout.MinManualContentWidth,
            minimapMode = ReadingPreferences.DocumentMinimapMode.ToString().ToLowerInvariant(),
            viewerChromeEnabled = ViewerChromeEnabled,
            documentScrollEnabled = DocumentScrollEnabled,
            wheelProxyEnabled = WheelProxyEnabled,
            widthResizerVisibility = ToRendererWidthResizerVisibility(ReadingPreferences.WidthResizerVisibility),
            viewportWidth = _webView.Bounds.Width,
            viewportHeight = _webView.Bounds.Height
        };
    }

    private object BuildReadingPreferencesMessage(
        string type,
        long transactionGeneration,
        bool skipFrameWait = false)
    {
        if (transactionGeneration <= 0)
        {
            return BuildReadingPreferencesMessage(type);
        }

        var maxWidth = double.IsFinite(AvailableContentWidth) && AvailableContentWidth > 0
            ? AvailableContentWidth
            : ReadingPreferences.ContentWidth;
        return new
        {
            type,
            fontFamily = ReadingPreferences.FontFamily.ToString().ToLowerInvariant(),
            fontSize = ReadingPreferences.FontSize,
            lineHeight = ReadingPreferences.LineHeight,
            maxWidth,
            minMaxWidth = ApplicateDocumentLayout.MinManualContentWidth,
            minimapMode = ReadingPreferences.DocumentMinimapMode.ToString().ToLowerInvariant(),
            viewerChromeEnabled = ViewerChromeEnabled,
            documentScrollEnabled = DocumentScrollEnabled,
            wheelProxyEnabled = WheelProxyEnabled,
            widthResizerVisibility = ToRendererWidthResizerVisibility(ReadingPreferences.WidthResizerVisibility),
            viewportWidth = _webView.Bounds.Width,
            viewportHeight = _webView.Bounds.Height,
            transactionGeneration,
            skipFrameWait
        };
    }

    private void BeginAwaitingLayoutReady()
    {
        _awaitingLayoutReady = true;
        _hasLayoutReady = false;
        _hasMinimapState = false;
    }

    internal static string ToRendererWidthResizerVisibility(WidthResizerVisibility visibility)
        => visibility == WidthResizerVisibility.Always ? "always" : "on-hover";

    private void SendMinimapPolicy()
    {
        PostRendererMessage(
            new
            {
                type = "minimap-policy",
                minimapPolicy = new
                {
                    minHostWidth = ApplicateDocumentMinimapBuildPolicy.MinHostWidth,
                    minScrollableViewportRatio = ApplicateDocumentMinimapBuildPolicy.MinScrollableViewportRatio,
                    maxDetailedDocumentHeight = ApplicateDocumentMinimapBuildPolicy.MaxDetailedDocumentHeight
                }
            });
    }

    private void SendScrollTo(string anchor)
    {
        PostRendererMessage(new { type = "scroll-to", anchor });
    }

    public void SetHostScrollbarMode(bool active)
    {
        PostRendererMessage(new { type = "host-scrollbar", active });
    }

    /// <summary>
    /// Send a one-shot <c>mode-settle-probe</c> to the renderer. The renderer
    /// schedules two requestAnimationFrame ticks (so CSS reflow on any new
    /// slot bounds has propagated and one paint has happened) and posts back
    /// <c>mode-toggle-settled</c>, which surfaces here as the
    /// <see cref="ModeToggleSettled"/> event. The shared host pairs this with
    /// a short timeout fallback so a dropped ack never hangs the toggle.
    /// Safe to call before any user document is loaded — the renderer still
    /// honours the probe and returns after the two rAFs.
    /// </summary>
    internal void RequestModeToggleSettleProbe()
    {
        ApplicateTrace.DiagMs("pane-seq", "host-revealgate-probe-sent");
        // Carry the same preference payload as a live preference update. C#
        // posts host messages through asynchronous WebView2 script calls, so
        // the settle probe must be self-contained: even if the prior
        // reading-preferences message is still crossing that boundary, the
        // renderer applies these values synchronously before ACKing reveal.
        PostRendererMessage(BuildReadingPreferencesMessage("mode-settle-probe"));
    }

    internal void RequestModeToggleSettleProbe(long transactionGeneration, bool skipFrameWait = false)
    {
        if (transactionGeneration <= 0)
        {
            RequestModeToggleSettleProbe();
            return;
        }

        ApplicateTrace.DiagMs(
            "pane-seq",
            "host-transaction-settle-probe-sent",
            $"transactionGeneration={transactionGeneration} skipFrameWait={skipFrameWait}");
        PostRendererMessage(BuildReadingPreferencesMessage(
            "mode-settle-probe",
            transactionGeneration,
            skipFrameWait));
    }

    internal void RequestMinimapSettleProbe(long transactionGeneration)
    {
        if (transactionGeneration <= 0)
        {
            return;
        }

        PostRendererMessage(new { type = "minimap-settle-probe", transactionGeneration });
    }

    public void ScrollToProgress(double progressPercent)
    {
        var progress = double.IsFinite(progressPercent)
            ? SysMath.Clamp(progressPercent, 0, 100)
            : 0;
        PostRendererMessage(new { type = "scroll-to-progress", progressPercent = progress });
    }

    internal void ResetHostShortcutsForModeSwitch()
        => PostRendererMessage(new { type = "host-shortcuts-reset" });

    private void PostRendererMessage(object message)
    {
        var payload = JsonSerializer.Serialize(message);
        var isModeSettleProbe = payload.Contains("\"type\":\"mode-settle-probe\"", StringComparison.Ordinal);
        var postStart = isModeSettleProbe ? Stopwatch.GetTimestamp() : 0;
        if (TryPostRendererMessageNative(payload))
        {
            if (isModeSettleProbe)
            {
                ApplicateTrace.DiagMs(
                    "pane-seq",
                    "host-renderer-message-native-post-end",
                    $"elapsedMs={Stopwatch.GetElapsedTime(postStart).TotalMilliseconds:F2}");
            }

            return;
        }

        if (isModeSettleProbe)
        {
            ApplicateTrace.DiagMs(
                "pane-seq",
                "host-renderer-message-fallback-invoke",
                $"elapsedMs={Stopwatch.GetElapsedTime(postStart).TotalMilliseconds:F2}");
        }

        _ = InvokeRendererAsync($"window.postMessage({payload},'*');");
    }

    private bool TryPostRendererMessageNative(string payload)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (_webView.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle platformHandle
            || platformHandle.CoreWebView2 == IntPtr.Zero)
        {
            return false;
        }

        var isModeSettleProbe = payload.Contains("\"type\":\"mode-settle-probe\"", StringComparison.Ordinal);
        try
        {
            InvokeCoreWebView2PostWebMessageAsJson(platformHandle.CoreWebView2, payload);
            return true;
        }
        catch (Exception ex)
        {
            if (isModeSettleProbe)
            {
                ApplicateTrace.DiagMs(
                    "pane-seq",
                    "host-renderer-message-native-post-failed",
                    $"reason={ex.GetType().Name}");
            }

            return false;
        }
    }

    private static int ResolveCoreWebView2PostWebMessageAsJsonVtableSlot()
    {
        var interopType = typeof(NativeWebView).Assembly.GetType(CoreWebView2InteropTypeName);
        if (interopType is null)
        {
            return -1;
        }

        var methods = interopType.GetMethods();
        Array.Sort(methods, static (left, right) => left.MetadataToken.CompareTo(right.MetadataToken));
        for (var index = 0; index < methods.Length; index++)
        {
            if (methods[index].Name.Equals("PostWebMessageAsJson", StringComparison.Ordinal))
            {
                return IUnknownVtableSlotCount + index;
            }
        }

        return -1;
    }

    private static void InvokeCoreWebView2PostWebMessageAsJson(IntPtr coreWebView2, string payload)
    {
        if (CoreWebView2PostWebMessageAsJsonVtableSlot < IUnknownVtableSlotCount)
        {
            throw new InvalidOperationException("CoreWebView2 PostWebMessageAsJson vtable slot was not resolved.");
        }

        var vtable = Marshal.ReadIntPtr(coreWebView2);
        var entry = Marshal.ReadIntPtr(
            vtable,
            CoreWebView2PostWebMessageAsJsonVtableSlot * IntPtr.Size);
        if (entry == IntPtr.Zero)
        {
            throw new InvalidOperationException("CoreWebView2 PostWebMessageAsJson vtable entry is null.");
        }

        var postWebMessageAsJson = Marshal.GetDelegateForFunctionPointer<CoreWebView2PostWebMessageAsJsonDelegate>(entry);
        var hresult = postWebMessageAsJson(coreWebView2, payload);
        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CoreWebView2PostWebMessageAsJsonDelegate(
        IntPtr coreWebView2,
        [MarshalAs(UnmanagedType.LPWStr)] string webMessageAsJson);

    private async Task InvokeRendererAsync(string script)
    {
        try
        {
            await _webView.InvokeScript(script).ConfigureAwait(true);
        }
        catch
        {
            // Script invocation is best-effort; a failed message should not tear down the native shell.
        }
    }

    private static bool TryGetAnchor(string href, out string anchor)
    {
        if (MarkdownHeadingAnchorSlugger.TryNormalizeFragment(href, out anchor))
        {
            return true;
        }

        if (Uri.TryCreate(href, UriKind.Absolute, out var uri)
            && uri.Fragment.Length > 1
            && uri.Scheme.Equals("applicate-renderer", StringComparison.OrdinalIgnoreCase))
        {
            return MarkdownHeadingAnchorSlugger.TryNormalizeFragment(uri.Fragment, out anchor);
        }

        anchor = string.Empty;
        return false;
    }

    private static double ReadDouble(JsonElement root, string name)
        => root.TryGetProperty(name, out var property) && property.TryGetDouble(out var value) && double.IsFinite(value)
            ? value
            : 0;

    private static bool ReadBoolean(JsonElement root, string name)
        => root.TryGetProperty(name, out var property)
           && property.ValueKind is JsonValueKind.True or JsonValueKind.False
           && property.GetBoolean();

    private static bool TryReadWidthDragPhase(string? phase, out ApplicateWebWidthDragPhase result)
    {
        result = phase switch
        {
            "start" => ApplicateWebWidthDragPhase.Start,
            "move" => ApplicateWebWidthDragPhase.Move,
            "end" => ApplicateWebWidthDragPhase.End,
            _ => default
        };
        return phase is "start" or "move" or "end";
    }

    private static string GetWebViewUserDataFolder()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localAppData) ? Path.GetTempPath() : localAppData;
        return Path.Combine(root, "MarkMello", "Applicate", "WebView2");
    }

    private static async Task<string> WriteGeneratedDocumentAsync(string html, CancellationToken cancellationToken)
    {
        var folder = GetGeneratedDocumentFolder();
        Directory.CreateDirectory(folder);
        CleanupOldGeneratedDocuments(folder);

        var path = Path.Combine(folder, $"document-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(path, html, Encoding.UTF8, cancellationToken).ConfigureAwait(true);
        return path;
    }

    private static string GetGeneratedDocumentFolder()
        => Path.Combine(Path.GetTempPath(), "MarkMello", "Applicate", "GeneratedWebDocuments");

    private static void CleanupOldGeneratedDocuments(string folder)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var path in Directory.EnumerateFiles(folder, "document-*.html"))
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    DeleteGeneratedDocument(path);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void DeleteCurrentGeneratedDocument()
    {
        DeleteGeneratedDocument(_currentGeneratedDocumentPath);
        _currentGeneratedDocumentPath = null;
    }

    private static void DeleteGeneratedDocument(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void OnWebViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.F5
            || (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.R or Key.L)
            || e.Key == Key.BrowserBack
            || e.Key == Key.BrowserForward)
        {
            e.Handled = true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelRender();
        DeleteCurrentGeneratedDocument();
        _webView.EnvironmentRequested -= OnEnvironmentRequested;
        _webView.NavigationStarted -= OnNavigationStarted;
        _webView.NavigationCompleted -= OnNavigationCompleted;
        _webView.NewWindowRequested -= OnNewWindowRequested;
        _webView.WebMessageReceived -= OnWebMessageReceived;
        ActualThemeVariantChanged -= OnThemeChanged;
        RemoveHandler(KeyDownEvent, OnWebViewKeyDown);
        GC.SuppressFinalize(this);
    }

    private static class NativeMethods
    {
        public const int SwHide = 0;
        public const int SwShow = 5;
        public const int SmXVirtualScreen = 76;
        public const uint SwpNoMove = 0x0002;
        public const uint SwpNoZOrder = 0x0004;
        public const uint SwpNoActivate = 0x0010;
        public const uint SwpNoCopyBits = 0x0100;
        public const uint SwpNoOwnerZOrder = 0x0200;

        public delegate bool EnumWindowProc(IntPtr windowHandle, IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr windowHandle, int commandShow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            IntPtr windowHandle,
            IntPtr insertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetParent(IntPtr windowHandle);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr windowHandle, out NativeRect rect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetFocus();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetFocus(IntPtr windowHandle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsChild(IntPtr parentHandle, IntPtr windowHandle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetGUIThreadInfo(uint threadId, ref NativeGuiThreadInfo info);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ScreenToClient(IntPtr windowHandle, ref NativePoint point);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(
            IntPtr parentHandle,
            EnumWindowProc callback,
            IntPtr parameter);
    }

    private readonly record struct NativeWindowPlacement(int X, int Y, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeGuiThreadInfo
    {
        public int CbSize;
        public uint Flags;
        public IntPtr ActiveWindow;
        public IntPtr FocusWindow;
        public IntPtr CaptureWindow;
        public IntPtr MenuOwnerWindow;
        public IntPtr MoveSizeWindow;
        public IntPtr CaretWindow;
        public NativeRect CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}

public sealed class ApplicateWebDocumentScrollEventArgs(
    double progressPercent,
    double scrollTop,
    double scrollHeight,
    double clientHeight,
    int? topBlockIndex = null) : EventArgs
{
    public double ProgressPercent { get; } = progressPercent;

    public double ScrollTop { get; } = scrollTop;

    public double ScrollHeight { get; } = scrollHeight;

    public double ClientHeight { get; } = clientHeight;

    /// <summary>
    /// Block index of the topmost element with <c>data-mm-block-index</c> that
    /// is at or below the viewport top. Null when the renderer has not yet
    /// emitted block metadata or no annotated block exists.
    /// </summary>
    public int? TopBlockIndex { get; } = topBlockIndex;
}

public sealed class ApplicateWebMinimapStateEventArgs(
    bool visible,
    double reservedWidth) : EventArgs
{
    public bool Visible { get; } = visible;

    public double ReservedWidth { get; } = reservedWidth;
}

public sealed class ApplicateWebMinimapSettledEventArgs(
    long transactionGeneration,
    ApplicateWebMinimapStateEventArgs state) : EventArgs
{
    public long TransactionGeneration { get; } = transactionGeneration;

    public ApplicateWebMinimapStateEventArgs State { get; } = state;
}

public sealed class ApplicateWebModeToggleSettledEventArgs(long transactionGeneration) : EventArgs
{
    public long TransactionGeneration { get; } = transactionGeneration;

    public bool IsTransactional => TransactionGeneration > 0;
}

public sealed class ApplicateWebPreviewSourceLineEventArgs(int sourceLine) : EventArgs
{
    public int SourceLine { get; } = sourceLine;
}

public enum ApplicateWebWidthDragPhase
{
    Start,
    Move,
    End
}

public sealed class ApplicateWebWidthDragEventArgs(
    ApplicateWebWidthDragPhase phase,
    double deltaX) : EventArgs
{
    public ApplicateWebWidthDragPhase Phase { get; } = phase;

    public double DeltaX { get; } = deltaX;
}

public sealed class ApplicateWebWheelEventArgs(
    double deltaY,
    int deltaMode) : EventArgs
{
    public double DeltaY { get; } = deltaY;

    public int DeltaMode { get; } = deltaMode;
}

internal enum ApplicateWebInputUpdateAction
{
    None,
    ApplyLivePreferences,
    Render
}
