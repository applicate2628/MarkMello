using System.IO;
using System.Text.Json;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Styling;
using MarkMello.Application.Abstractions;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Applicate.Desktop.Views.Minimap;
using MarkMello.Domain;
using MarkMello.Presentation;
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
    private bool _hasReceivedDocumentReady;
    private int _intentionalReparentDepth;
    private MarkMello.Presentation.ViewModels.MainWindowViewModel? _mainWindowViewModel;
    private readonly bool _shellMode;
    private readonly IApplicateShellAssetBundleFactory? _shellAssetFactory;
    private bool _shellNavigated;
    private bool _shellDocumentReadyConsumed;
    private TaskCompletionSource<bool>? _shellReady;
    private bool _isWebWidthDragging;

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
        _renderer = renderer;
        _shellAssetFactory = shellAssetFactory;
        // Shell mode requires both the env-var flag AND the factory injection.
        // Missing either falls back to legacy per-document Navigate.
        _shellMode = ApplicateRendererShellMode.IsEnabled && shellAssetFactory is not null;
        _webView = new ApplicateNativeWebView
        {
            ClipToBounds = true,
            ContextFlyout = null,
            ContextMenu = null,
            Focusable = true,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };

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
        var sourceChanged = !Equals(Source, source);
        var imageSourceResolverChanged = !ReferenceEquals(ImageSourceResolver, imageSourceResolver);
        var action = DetermineInputUpdateAction(
            sourceChanged: sourceChanged,
            imageSourceResolverChanged: imageSourceResolverChanged,
            hasLoadedDocument: _hasLoadedDocument,
            readingPreferencesChanged: ReadingPreferences != readingPreferences,
            availableContentWidthChanged: !AreEqual(AvailableContentWidth, availableContentWidth),
            viewerChromeEnabledChanged: ViewerChromeEnabled != viewerChromeEnabled,
            documentScrollEnabledChanged: DocumentScrollEnabled != documentScrollEnabled,
            wheelProxyEnabledChanged: WheelProxyEnabled != wheelProxyEnabled);
        var oldLen = Source?.Content?.Length ?? -1;
        var newLen = source?.Content?.Length ?? -1;
        Console.Error.WriteLine(
            $"[mode-toggle] {DateTime.Now:HH:mm:ss.fff} UpdateInputs viewId={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this):X8} chrome={viewerChromeEnabled} sourceCh={sourceChanged} (lenOld={oldLen} lenNew={newLen} path={source?.Path}) resolverCh={imageSourceResolverChanged} hasLoaded={_hasLoadedDocument} action={action}");

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
            SyncWebViewAirspaceVisibility();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_mainWindowViewModel is not null)
        {
            _mainWindowViewModel.PropertyChanged -= OnMainWindowViewModelPropertyChanged;
            _mainWindowViewModel = null;
            _webView.IsVisible = true;
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

    private void SyncWebViewAirspaceVisibility()
    {
        _webView.IsVisible = _mainWindowViewModel?.IsDirtyPromptOpen != true;
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
        Console.Error.WriteLine($"[mode-toggle] {DateTime.Now:HH:mm:ss.fff} SetNativeWebViewVisibility({isVisible}) viewId={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this):X8} wrapper.Bounds={_webView.Bounds}");
        _webView.IsVisible = isVisible;
    }

    /// <summary>Inspect the inner NativeWebView visibility for diagnostics.</summary>
    internal bool NativeWebViewIsVisible => _webView.IsVisible;

    /// <summary>Inspect the inner NativeWebView bounds for diagnostics.</summary>
    internal Rect NativeWebViewBounds => _webView.Bounds;

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
        _hasLoadedDocument = false;
        _awaitingLayoutReady = false;
        _hasLayoutReady = false;
        _hasMinimapState = false;
        _hasReceivedDocumentReady = false;
        _scrollTop = 0;
        _scrollHeight = 0;
        _clientHeight = 0;
        CancelRender();

        var source = Source;
        if (_shellMode)
        {
            _renderCancellation = new CancellationTokenSource();
            _ = QueueRenderShellAsync(source, _renderCancellation.Token);
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

    private async Task QueueRenderShellAsync(MarkdownSource? source, CancellationToken cancellationToken)
    {
        try
        {
            if (!_shellNavigated)
            {
                _shellReady ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await NavigateToShellAsync(cancellationToken).ConfigureAwait(true);
                _shellNavigated = true;
            }

            // Wait for shell's first document-ready before posting load-document.
            // Without this gate, PostRendererMessage races with the renderer-shell
            // page load — the renderer's message listener doesn't exist yet.
            if (_shellReady is not null)
            {
                using var registration = cancellationToken.Register(() => _shellReady.TrySetCanceled(cancellationToken));
                await _shellReady.Task.ConfigureAwait(true);
            }

            if (source is null)
            {
                PostRendererMessage(new { type = "clear-document" });
                return;
            }

            var body = await _renderer
                .RenderBodyAsync(source, ReadingPreferences, ImageSourceResolver, cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            PostRendererMessage(new
            {
                type = "load-document",
                html = body.BodyHtml,
                documentName = source.FileName,
                hasMermaid = body.HasMermaidBlock,
                hasHljs = body.HasCodeBlockWithSyntax,
            });
        }
        catch (OperationCanceledException)
        {
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
        html = ApplyInitialTheme(html, GetThemeName());

        var folder = GetGeneratedDocumentFolder();
        Directory.CreateDirectory(folder);
        var shellPath = Path.Combine(folder, "renderer-shell.html");
        await File.WriteAllTextAsync(shellPath, html, Encoding.UTF8, cancellationToken).ConfigureAwait(true);

        _currentGeneratedDocumentPath = shellPath;
        _isLoadingGeneratedDocument = true;
        _webView.Navigate(new Uri(shellPath));
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
                var html = ApplyInitialTheme(document.Html, GetThemeName());
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
        if (!e.IsSuccess)
        {
            // Phase-aware: only treat a navigation failure as a real load error
            // before the renderer JS posts "document-ready" for the initial
            // load. After that, !IsSuccess almost always means a stale or
            // superseded navigation (cancelled by a subsequent Navigate, or
            // an internal WebView2 reload) — treating those as fallback was
            // causing edit-mode webview to flash-and-vanish: first nav
            // rendered and committed the surface, then a stale completion
            // raised FallbackRequested → _webPreviewFailed → flip to native.
            if (_hasReceivedDocumentReady)
            {
                return;
            }

            FallbackRequested?.Invoke(this, EventArgs.Empty);
        }
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
        if (TryResolveLocalMarkdownLink(href, out var resolvedMarkdownPath))
        {
            var openDocs = App.Services?.GetService<Editing.IOpenDocumentsService>();
            if (openDocs is not null)
            {
                try
                {
                    await openDocs.OpenAsync(resolvedMarkdownPath).ConfigureAwait(true);
                }
                catch (System.IO.IOException)
                {
                    // File moved or unreadable; silently no-op.
                }
                return;
            }
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
                _hasReceivedDocumentReady = true;
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
                SendTheme();
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
        if (!ShouldCompleteRender(_hasLoadedDocument, _hasLayoutReady, _hasMinimapState) || !_awaitingLayoutReady)
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
        => hasLoadedDocument && hasLayoutReady && hasMinimapState;

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

        // Relative-path links to local markdown/text files (typically inserted
        // via fork's edit-mode drag-drop) should open as a new tab in the
        // open-documents service instead of being launched as a web URL.
        if (TryResolveLocalMarkdownLink(href, out var resolvedMarkdownPath))
        {
            var openDocs = App.Services?.GetService<Editing.IOpenDocumentsService>();
            if (openDocs is not null)
            {
                try
                {
                    await openDocs.OpenAsync(resolvedMarkdownPath).ConfigureAwait(true);
                }
                catch (System.IO.IOException)
                {
                    // File moved or unreadable; surface nothing — user sees
                    // the click had no effect and can investigate manually.
                }
                return;
            }
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

    private static readonly string[] MarkdownLinkExtensions =
        { ".md", ".markdown", ".mdown", ".markdn", ".txt" };

    private bool TryResolveLocalMarkdownLink(string href, out string resolvedPath)
    {
        resolvedPath = string.Empty;

        // The renderer feeds `target.href` from the anchor element, which the
        // browser resolves against the document's base URL. For local
        // generated documents this yields a `file:///...` absolute URI that
        // points into the temp folder where the renderer HTML lives — not to
        // the user's source file. Strip the file:// prefix and re-resolve
        // against the current Source's directory to find the actual file the
        // user dropped a link to.
        string candidate;
        if (Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            candidate = uri.LocalPath;
        }
        else
        {
            candidate = href;
        }

        // Only treat .md/.markdown/.txt as openable in the tabs strip; other
        // extensions fall through to default browser launch.
        var ext = System.IO.Path.GetExtension(candidate).ToLowerInvariant();
        if (!MarkdownLinkExtensions.Contains(ext))
        {
            return false;
        }

        // Resolve relative to the current Source's directory when needed.
        if (!System.IO.Path.IsPathRooted(candidate))
        {
            var sourcePath = Source?.Path;
            var sourceDir = string.IsNullOrWhiteSpace(sourcePath)
                ? null
                : System.IO.Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrWhiteSpace(sourceDir))
            {
                return false;
            }
            candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(sourceDir, candidate));
        }

        if (!System.IO.File.Exists(candidate))
        {
            return false;
        }

        resolvedPath = candidate;
        return true;
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
            // - Render IS in flight: do not cancel it. Cancelling restarts
            //   the cycle and the canceled Navigate fires
            //   NavigationCompleted with IsSuccess=false before
            //   _hasReceivedDocumentReady is set, which trips
            //   FallbackRequested → _webPreviewFailed = true and breaks the
            //   shared preview permanently. The current in-flight render
            //   will send the up-to-date AvailableContentWidth to the
            //   renderer in SendReadingPreferences on document-ready, so the
            //   user still sees content at the correct width.
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
        => ActualThemeVariant == ThemeVariant.Dark ? "dark" : "light";

    internal static string ApplyInitialThemeForTesting(string html, string theme)
        => ApplyInitialTheme(html, theme);

    private static string ApplyInitialTheme(string html, string theme)
    {
        var normalizedTheme = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase)
            ? "dark"
            : "light";
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
                // Host's clamp floor — renderer uses this to limit drag preview
                // so it doesn't go below where host will clamp on echo. Without
                // it, drag visually pulls to renderer-local min (200) but host
                // re-clamps to 320, document snaps wider on release.
                minMaxWidth = ApplicateViewerView.MinManualContentWidth,
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
