using System;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Presentation.Localization;

namespace MarkMello.Applicate.Desktop.Views;

/// <summary>
/// User-visible error panel shown inside a renderer slot when the WebView2
/// pipeline fails. Three failure classes per design D3:
/// <list type="bullet">
///   <item><see cref="ApplicateRendererFailureKind.WebView2RuntimeMissing"/> —
///   terminal session failure; retry is not offered because re-initialising the
///   WebView2 environment inside a running process is not reliable.</item>
///   <item><see cref="ApplicateRendererFailureKind.DocumentRenderFailed"/> —
///   per-document render failure; the retry affordance is visible.</item>
///   <item><see cref="ApplicateRendererFailureKind.StaleNavigation"/> —
///   internal no-op; consumers should not display this user-facing.</item>
/// </list>
///
/// The view is invisible by default and is shown by the host's failure routing.
/// User-facing copy resolves through the application-owned
/// <see cref="ILocalizationService"/> resource and refreshes when its language
/// changes. English fallbacks keep resource-less construction deterministic.
/// </summary>
public sealed class ApplicateRendererFailureView : UserControl
{
    private readonly ILocalizationService? _localization;
    private readonly Border _root;
    private readonly TextBlock _title;
    private readonly TextBlock _body;
    private readonly TextBlock _documentLine;
    private readonly Button _retryButton;
    private readonly Button _copyDiagnosticsButton;
    private readonly StackPanel _actions;

    private ApplicateRendererFailureKind _failureKind = ApplicateRendererFailureKind.DocumentRenderFailed;
    private string? _documentPath;
    private Exception? _exception;
    private DateTime _timestamp = DateTime.UtcNow;
    private Action? _retryCallback;
    private Action<string>? _copyDiagnosticsCallback;
    private bool _isLocalizationSubscribed;

    public ApplicateRendererFailureView()
    {
        _localization = ResolveLocalization();

        _title = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Foreground = ResolveBrush("MmTextBrush", new SolidColorBrush(Color.FromRgb(0xE6, 0xE2, 0xDB))),
        };

        _body = new TextBlock
        {
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
            Margin = new Thickness(0, 14, 0, 0),
            Foreground = ResolveBrush("MmTextSoftBrush", new SolidColorBrush(Color.FromRgb(0xB0, 0xAA, 0xA0))),
        };

        _documentLine = new TextBlock
        {
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
            Margin = new Thickness(0, 8, 0, 0),
            Opacity = 0.75,
            IsVisible = false,
            Foreground = ResolveBrush("MmTextSoftBrush", new SolidColorBrush(Color.FromRgb(0xB0, 0xAA, 0xA0))),
        };

        _retryButton = new Button
        {
            Content = ResolveText("MmRendererFailureRetry", "Retry"),
            MinWidth = 120,
            Padding = new Thickness(18, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
            IsVisible = false,
        };
        _retryButton.Click += OnRetryClick;

        _copyDiagnosticsButton = new Button
        {
            Content = ResolveText("MmRendererFailureCopyDiagnostics", "Copy diagnostics"),
            MinWidth = 120,
            Padding = new Thickness(18, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        _copyDiagnosticsButton.Click += OnCopyDiagnosticsClick;

        _actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 0),
            Children =
            {
                _retryButton,
                _copyDiagnosticsButton,
            },
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(32, 48, 32, 48),
            Children =
            {
                _title,
                _body,
                _documentLine,
                _actions,
            },
        };

        _root = new Border
        {
            Background = ResolveBrush("MmBackgroundBrush", new SolidColorBrush(Color.FromRgb(0x14, 0x11, 0x0E))),
            Child = stack,
            UseLayoutRounding = true,
        };

        Content = _root;
        IsVisible = false;

        ActualThemeVariantChanged += OnAppearanceChanged;
        ResourcesChanged += OnResourcesChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;

        SubscribeToLocalization();
        RefreshLocalizedText();
    }

    /// <summary>
    /// The current failure class. Drives title text, body copy, and retry-
    /// button visibility. Default is <see cref="ApplicateRendererFailureKind.DocumentRenderFailed"/>
    /// so the view always has a valid presentation.
    /// </summary>
    public ApplicateRendererFailureKind FailureKind
    {
        get => _failureKind;
        set
        {
            if (_failureKind == value)
            {
                return;
            }

            _failureKind = value;
            ApplyFailureKind();
        }
    }

    /// <summary>
    /// Optional absolute path of the document whose render failed. Surfaces in
    /// the body of the panel and in the diagnostics payload. Null clears it.
    /// </summary>
    public string? DocumentPath
    {
        get => _documentPath;
        set
        {
            if (string.Equals(_documentPath, value, StringComparison.Ordinal))
            {
                return;
            }

            _documentPath = value;
            ApplyDocumentLine();
        }
    }

    /// <summary>
    /// Optional exception captured at the failure site. Surfaces only in the
    /// diagnostics payload — never rendered into the panel body — so file
    /// content and stack traces are not displayed to the user inadvertently.
    /// </summary>
    public Exception? FailureException
    {
        get => _exception;
        set => _exception = value;
    }

    /// <summary>
    /// Timestamp of the failure. Defaults to the construction moment; callers
    /// updating the view in place should refresh it.
    /// </summary>
    public DateTime Timestamp
    {
        get => _timestamp;
        set => _timestamp = value;
    }

    /// <summary>
    /// Callback invoked when the retry button is pressed. The retry button is
    /// only visible for <see cref="ApplicateRendererFailureKind.DocumentRenderFailed"/>.
    /// </summary>
    public Action? RetryCallback
    {
        get => _retryCallback;
        set => _retryCallback = value;
    }

    /// <summary>
    /// Optional clipboard sink for the diagnostics payload. When null, the
    /// panel falls back to <see cref="Avalonia.Application.Current"/> clipboard
    /// resolution at click time. The hook exists for tests; production code
    /// can leave it null.
    /// </summary>
    public Action<string>? CopyDiagnosticsCallback
    {
        get => _copyDiagnosticsCallback;
        set => _copyDiagnosticsCallback = value;
    }

    /// <summary>
    /// Apply the failure context in one call. Convenience for the host's
    /// future failure routing.
    /// </summary>
    public void ShowFailure(ApplicateRendererFailureEvent failure, Action? retry = null)
    {
        ArgumentNullException.ThrowIfNull(failure);

        _exception = failure.Exception;
        _timestamp = failure.Timestamp;
        DocumentPath = failure.DocumentPath;
        FailureKind = failure.Kind;
        RetryCallback = retry;
        IsVisible = true;
    }

    /// <summary>
    /// Build the diagnostics payload that would land on the clipboard.
    /// Exposed for tests; deterministic output, no app-current side effects.
    /// </summary>
    public string BuildDiagnosticsPayload()
    {
        var builder = new StringBuilder();
        builder.Append("MarkMello renderer failure").Append('\n');
        builder.Append("Kind: ").Append(_failureKind).Append('\n');
        builder.Append("Timestamp (UTC): ")
            .Append(_timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
            .Append('\n');

        if (!string.IsNullOrEmpty(_documentPath))
        {
            builder.Append("Document: ").Append(_documentPath).Append('\n');
        }

        if (_exception is not null)
        {
            builder.Append("Exception: ")
                .Append(_exception.GetType().FullName ?? _exception.GetType().Name)
                .Append('\n');
            if (!string.IsNullOrEmpty(_exception.Message))
            {
                builder.Append("Message: ").Append(_exception.Message).Append('\n');
            }
        }

        return builder.ToString();
    }

    internal bool IsRetryButtonVisibleForTesting => _retryButton.IsVisible;

    internal string TitleTextForTesting => _title.Text ?? string.Empty;

    internal string BodyTextForTesting => _body.Text ?? string.Empty;

    internal string DocumentLineTextForTesting => _documentLine.Text ?? string.Empty;

    internal bool DocumentLineVisibleForTesting => _documentLine.IsVisible;

    private void ApplyFailureKind()
    {
        switch (_failureKind)
        {
            case ApplicateRendererFailureKind.WebView2RuntimeMissing:
                _title.Text = ResolveText("MmRendererFailureTitleRuntime", "WebView2 Runtime is unavailable");
                _body.Text = ResolveText(
                    "MmRendererFailureDetailRuntime",
                    "Microsoft Edge WebView2 Runtime is required to display the document. Install it and restart the application.");
                _retryButton.IsVisible = false;
                break;

            case ApplicateRendererFailureKind.StaleNavigation:
                // Stale navigation is an internal no-op per D3. Keep the
                // surface neutral; consumers should normally not show this
                // class to the user.
                _title.Text = ResolveText("MmRendererFailureTitleStaleNavigation", "Loading canceled");
                _body.Text = ResolveText(
                    "MmRendererFailureDetailStaleNavigation",
                    "Opening the document was interrupted by a newer navigation.");
                _retryButton.IsVisible = false;
                break;

            case ApplicateRendererFailureKind.DocumentRenderFailed:
            default:
                _title.Text = ResolveText("MmRendererFailureTitle", "Could not display the document");
                _body.Text = ResolveText(
                    "MmRendererFailureDetailRender",
                    "An error occurred while preparing the preview. Try again or copy the diagnostics for a report.");
                _retryButton.IsVisible = true;
                break;
        }

        ApplyDocumentLine();
    }

    private void ApplyDocumentLine()
    {
        if (string.IsNullOrEmpty(_documentPath))
        {
            _documentLine.Text = string.Empty;
            _documentLine.IsVisible = false;
            return;
        }

        _documentLine.Text = _documentPath;
        _documentLine.IsVisible = true;
    }

    private void OnRetryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _retryCallback?.Invoke();
    }

    private void OnCopyDiagnosticsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var payload = BuildDiagnosticsPayload();
        if (_copyDiagnosticsCallback is not null)
        {
            _copyDiagnosticsCallback(payload);
            return;
        }

        TryCopyToSystemClipboard(payload);
    }

    private void TryCopyToSystemClipboard(string payload)
    {
        try
        {
            // Avalonia 12 surfaces clipboard through the focused TopLevel.
            // Phase 2 has no instantiation site yet; the production code path
            // is wired in Phase 4. This best-effort fallback exists so the
            // button is not silently dead when the consumer forgets to inject
            // a clipboard sink.
            var topLevel = TopLevel.GetTopLevel(this);
            var clipboard = topLevel?.Clipboard;
            _ = clipboard?.SetTextAsync(payload);
        }
        catch
        {
            // Swallow: clipboard access is best-effort in this fallback.
        }
    }

    private void OnAppearanceChanged(object? sender, EventArgs e) => RefreshBrushes();

    private void OnResourcesChanged(object? sender, ResourcesChangedEventArgs e) => RefreshBrushes();

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        SubscribeToLocalization();
        RefreshLocalizedText();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        => UnsubscribeFromLocalization();

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsLocalizationChangeNotification(e.PropertyName))
        {
            return;
        }

        RefreshLocalizedText();
    }

    private void RefreshLocalizedText()
    {
        _retryButton.Content = ResolveText("MmRendererFailureRetry", "Retry");
        _copyDiagnosticsButton.Content = ResolveText("MmRendererFailureCopyDiagnostics", "Copy diagnostics");
        ApplyFailureKind();
    }

    private void SubscribeToLocalization()
    {
        if (_localization is null || _isLocalizationSubscribed)
        {
            return;
        }

        _localization.PropertyChanged += OnLocalizationChanged;
        _isLocalizationSubscribed = true;
    }

    private void UnsubscribeFromLocalization()
    {
        if (_localization is null || !_isLocalizationSubscribed)
        {
            return;
        }

        _localization.PropertyChanged -= OnLocalizationChanged;
        _isLocalizationSubscribed = false;
    }

    private static bool IsLocalizationChangeNotification(string? propertyName)
        => string.IsNullOrEmpty(propertyName)
           || propertyName == nameof(ILocalizationService.SelectedLanguage)
           || propertyName == nameof(ILocalizationService.EffectiveLanguage)
           || propertyName == nameof(ILocalizationService.Culture)
           || propertyName == "Item"
           || propertyName == "Item[]";

    private string ResolveText(string resourceKey, string fallback)
        => _localization?[resourceKey] ?? fallback;

    private static ILocalizationService? ResolveLocalization()
    {
        var app = Avalonia.Application.Current;
        if (app is not null
            && app.TryGetResource("Localization", null, out var value)
            && value is ILocalizationService localization)
        {
            return localization;
        }

        return null;
    }

    private void RefreshBrushes()
    {
        _root.Background = ResolveBrush("MmBackgroundBrush", new SolidColorBrush(Color.FromRgb(0x14, 0x11, 0x0E)));
        var titleBrush = ResolveBrush("MmTextBrush", new SolidColorBrush(Color.FromRgb(0xE6, 0xE2, 0xDB)));
        var softBrush = ResolveBrush("MmTextSoftBrush", new SolidColorBrush(Color.FromRgb(0xB0, 0xAA, 0xA0)));
        _title.Foreground = titleBrush;
        _body.Foreground = softBrush;
        _documentLine.Foreground = softBrush;
    }

    private IBrush ResolveBrush(string key, IBrush fallback)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var resource) && resource is IBrush brush)
        {
            return brush;
        }

        if (Avalonia.Application.Current?.TryGetResource(
                key,
                Avalonia.Application.Current.ActualThemeVariant,
                out var appResource) == true && appResource is IBrush appBrush)
        {
            return appBrush;
        }

        return fallback;
    }
}
