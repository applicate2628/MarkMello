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

    static ApplicateWebMarkdownDocumentView()
    {
        SourceProperty.Changed.AddClassHandler<ApplicateWebMarkdownDocumentView>((view, _) => view.OnRenderInputChanged());
        ImageSourceResolverProperty.Changed.AddClassHandler<ApplicateWebMarkdownDocumentView>((view, _) => view.OnRenderInputChanged());
        ReadingPreferencesProperty.Changed.AddClassHandler<ApplicateWebMarkdownDocumentView>((view, _) => view.OnLiveInputChanged());
        AvailableContentWidthProperty.Changed.AddClassHandler<ApplicateWebMarkdownDocumentView>((view, _) => view.OnLiveInputChanged());
        ViewerChromeEnabledProperty.Changed.AddClassHandler<ApplicateWebMarkdownDocumentView>((view, _) => view.OnLiveInputChanged());
    }

    public ApplicateWebMarkdownDocumentView(IApplicateHtmlMarkdownRenderer renderer)
    {
        _renderer = renderer;
        _webView = new NativeWebView
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
        AddHandler(DragDrop.DropEvent, OnDropIntoWebView, handledEventsToo: true);
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
        bool viewerChromeEnabled)
    {
        var action = DetermineInputUpdateAction(
            sourceChanged: !Equals(Source, source),
            imageSourceResolverChanged: !ReferenceEquals(ImageSourceResolver, imageSourceResolver),
            hasLoadedDocument: _hasLoadedDocument,
            readingPreferencesChanged: ReadingPreferences != readingPreferences,
            availableContentWidthChanged: !AreEqual(AvailableContentWidth, availableContentWidth),
            viewerChromeEnabledChanged: ViewerChromeEnabled != viewerChromeEnabled);

        _isUpdatingInputs = true;
        try
        {
            ReadingPreferences = readingPreferences;
            ImageSourceResolver = imageSourceResolver;
            AvailableContentWidth = availableContentWidth;
            ViewerChromeEnabled = viewerChromeEnabled;
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

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelRender();
        base.OnDetachedFromVisualTree(e);
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
        bool viewerChromeEnabledChanged)
    {
        if (sourceChanged || imageSourceResolverChanged || !hasLoadedDocument)
        {
            return ApplicateWebInputUpdateAction.Render;
        }

        return readingPreferencesChanged || availableContentWidthChanged || viewerChromeEnabledChanged
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
        _scrollTop = 0;
        _scrollHeight = 0;
        _clientHeight = 0;
        CancelRender();

        var source = Source;
        if (source is null)
        {
            DeleteCurrentGeneratedDocument();
            _webView.Navigate(new Uri("about:blank"));
            return;
        }

        _renderCancellation = new CancellationTokenSource();
        _ = RenderAsync(source, _renderCancellation.Token);
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
            FallbackRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
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

            if (type == "link-clicked")
            {
                _ = HandleLinkClickedAsync(document.RootElement);
            }
        }
        catch (JsonException)
        {
            // Ignore malformed renderer messages; the WebView cannot drive shell state through them.
        }
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

        ScrollStateChanged?.Invoke(
            this,
            new ApplicateWebDocumentScrollEventArgs(progress, scrollTop, scrollHeight, clientHeight));
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

        WidthDragRequested?.Invoke(this, new ApplicateWebWidthDragEventArgs(phase, deltaX));
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

    private void ApplyReadingPreferences()
    {
        if (!_hasLoadedDocument)
        {
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
        if (_hasLoadedDocument)
        {
            BeginAwaitingLayoutReady();
        }

        var maxWidth = double.IsFinite(AvailableContentWidth) && AvailableContentWidth > 0
            ? AvailableContentWidth
            : ReadingPreferences.ContentWidth;
        PostRendererMessage(
            new
            {
                type = "reading-preferences",
                fontSize = ReadingPreferences.FontSize,
                lineHeight = ReadingPreferences.LineHeight,
                maxWidth,
                minimapMode = ReadingPreferences.DocumentMinimapMode.ToString().ToLowerInvariant(),
                viewerChromeEnabled = ViewerChromeEnabled,
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

    private static void OnDropIntoWebView(object? sender, DragEventArgs e)
    {
        e.Handled = true;
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
        RemoveHandler(DragDrop.DropEvent, OnDropIntoWebView);
        RemoveHandler(KeyDownEvent, OnWebViewKeyDown);
        GC.SuppressFinalize(this);
    }
}

public sealed class ApplicateWebDocumentScrollEventArgs(
    double progressPercent,
    double scrollTop,
    double scrollHeight,
    double clientHeight) : EventArgs
{
    public double ProgressPercent { get; } = progressPercent;

    public double ScrollTop { get; } = scrollTop;

    public double ScrollHeight { get; } = scrollHeight;

    public double ClientHeight { get; } = clientHeight;
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
