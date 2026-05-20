using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text;
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

public sealed class ApplicateWebMarkdownDocumentView : UserControl, IDisposable
{
    private const double MaxRendererReportedMinimapReservedWidth = 2000;

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
    private readonly NativeWebView _webView;
    private CancellationTokenSource? _renderCancellation;
    private string? _currentGeneratedDocumentPath;
    private bool _isLoadingGeneratedDocument;
    private bool _hasLoadedDocument;
    private bool _awaitingLayoutReady;
    private bool _hasLayoutReady;
    private bool _hasMinimapState;
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
    private bool _isWebWidthDragging;
    private long _renderSequence;

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
    {
        ApplicateTrace.DiagMs("startup-webview", "webview-view-ctor-start");
        _renderer = renderer;
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

    public event EventHandler? DocumentRenderInvalidated;

    public event EventHandler<ApplicateWebDocumentScrollEventArgs>? ScrollStateChanged;

    public event EventHandler<ApplicateWebMinimapStateEventArgs>? MinimapStateChanged;

    public event EventHandler<ApplicateWebWidthDragEventArgs>? WidthDragRequested;

    public event EventHandler<ApplicateWebWheelEventArgs>? WheelRequested;

    public event EventHandler? ViewerInteractionRequested;

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

    internal void UpdateInputs(
        MarkdownSource? source,
        ReadingPreferences readingPreferences,
        IImageSourceResolver? imageSourceResolver,
        double availableContentWidth,
        bool viewerChromeEnabled,
        bool documentScrollEnabled = true,
        bool wheelProxyEnabled = false)
    {
        var action = DetermineInputUpdateAction(
            sourceChanged: !Equals(Source, source),
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
            QueueRender();
            return;
        }

        if (action == ApplicateWebInputUpdateAction.ApplyLivePreferences)
        {
            ApplyReadingPreferences();
        }
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
            // _webView.IsVisible — specifically it sets visibility to false
            // BEFORE the reparent as anti-airspace-leak (see
            // ApplicateSharedWebViewHost.AttachTo: SetNativeWebViewVisibility(false)).
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
        _webView.IsVisible = isVisible;
    }

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

    private static bool AreEqual(double left, double right)
        => double.IsNaN(left) && double.IsNaN(right) || SysMath.Abs(left - right) <= double.Epsilon;

    private void QueueRender()
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
            _ = QueueRenderShellAsync(source, renderId, _renderCancellation.Token);
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

    private async Task QueueRenderShellAsync(MarkdownSource? source, long renderId, CancellationToken cancellationToken)
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
                using var registration = cancellationToken.Register(() => _shellReady.TrySetCanceled(cancellationToken));
                await _shellReady.Task.ConfigureAwait(true);
                ApplicateTrace.ModeToggle($"Web.RenderShell shell-ready id={renderId}");
            }

            if (source is null)
            {
                ApplicateTrace.ModeToggle($"Web.RenderShell post-clear id={renderId}");
                PostRendererMessage(new { type = "clear-document" });
                return;
            }

            ApplicateTrace.ModeToggle($"Web.RenderShell render-body-start id={renderId} source={source.Path}");
            var body = await _renderer
                .RenderBodyAsync(source, ReadingPreferences, ImageSourceResolver, cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            ApplicateTrace.ModeToggle(
                $"Web.RenderShell render-body-end id={renderId} source={source.Path} htmlLength={body.BodyHtml.Length} theme={GetThemeName()}");

            PostRendererMessage(new
            {
                type = "load-document",
                html = body.BodyHtml,
                documentName = source.FileName,
                theme = GetThemeName(),
                hasMermaid = body.HasMermaidBlock,
                hasHljs = body.HasCodeBlockWithSyntax,
                renderId,
            });
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
        var shellPath = Path.Combine(folder, "renderer-shell.html");
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

        // Idempotency guard. Mirrors the same _shellNavigated check at
        // QueueRenderShellAsync:521 so the pre-warm path and the lazy path
        // converge on the same shell instance.
        if (_shellNavigated)
        {
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
                using var registration = cancellationToken.Register(() => _shellReady.TrySetCanceled(cancellationToken));
                await _shellReady.Task.ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Pre-warm cancelled (window closed, dispose race). Leave
            // _shellNavigated == false so the lazy path takes over on the
            // next user render; QueueRenderShellAsync will retry the shell
            // navigation. _shellReady's TCS is still null-or-pending, which
            // QueueRenderShellAsync handles by either re-initialising or
            // awaiting the existing TCS.
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
                HandleScrollMessage(document.RootElement);
                _hasLayoutReady = true;
                CompleteLayoutReady();
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

            if (type == "mode-toggle-settled")
            {
                // Renderer ack to the host-sent mode-settle-probe. Two rAFs
                // have elapsed in the renderer, so CSS reflow on any new slot
                // bounds has propagated and one paint has happened. The host
                // listens once and uses this to flip HWND visibility on the
                // Commit fast-path; see ApplicateSharedWebViewHost.Commit().
                ModeToggleSettled?.Invoke(this, EventArgs.Empty);
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

    internal static bool IsViewerInteractionMessage(JsonElement root)
        => root.TryGetProperty("type", out var typeProperty)
           && typeProperty.ValueKind == JsonValueKind.String
           && string.Equals(typeProperty.GetString(), "viewer-interaction", StringComparison.Ordinal);

    internal static bool IsLayoutReadyMessage(JsonElement root)
        => root.TryGetProperty("type", out var typeProperty)
           && typeProperty.ValueKind == JsonValueKind.String
           && string.Equals(typeProperty.GetString(), "layout-ready", StringComparison.Ordinal);

    private void CompleteLayoutReady()
    {
        if (!ShouldCompleteRender(_hasLoadedDocument, _hasLayoutReady, _hasMinimapState)
            || !_awaitingLayoutReady)
        {
            return;
        }

        _awaitingLayoutReady = false;
        DocumentRendered?.Invoke(this, EventArgs.Empty);
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
            SendTheme();
        }
    }

    private void SendTheme()
    {
        PostRendererMessage(new { type = "theme", theme = GetThemeName() });
    }

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
            : Equals(variant?.Key, AvaloniaThemeService.ClassicWhiteThemeVariantKey)
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

        var maxWidth = double.IsFinite(AvailableContentWidth) && AvailableContentWidth > 0
            ? AvailableContentWidth
            : ReadingPreferences.ContentWidth;
        PostRendererMessage(
            new
            {
                type = "reading-preferences",
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
                widthResizerVisibility = ToRendererWidthResizerVisibility(ReadingPreferences.WidthResizerVisibility)
            });
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
        PostRendererMessage(new { type = "mode-settle-probe" });
    }

    public void ScrollToProgress(double progressPercent)
    {
        var progress = double.IsFinite(progressPercent)
            ? SysMath.Clamp(progressPercent, 0, 100)
            : 0;
        PostRendererMessage(new { type = "scroll-to-progress", progressPercent = progress });
    }

    private void PostRendererMessage(object message)
    {
        var payload = JsonSerializer.Serialize(message);
        _ = InvokeRendererAsync($"window.postMessage({payload},'*');");
    }

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
