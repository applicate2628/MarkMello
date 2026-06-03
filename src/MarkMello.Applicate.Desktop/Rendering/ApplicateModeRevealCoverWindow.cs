using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using MarkMello.Applicate.Desktop.Diagnostics;
using MarkMello.Applicate.Desktop.Views;
using Avalonia.Threading;
using System.Runtime.InteropServices;
using SysMath = System.Math;

namespace MarkMello.Applicate.Desktop.Rendering;

internal sealed class ApplicateModeRevealCoverWindow : IDisposable
{
    private static readonly IBrush FallbackLightBrush = new SolidColorBrush(Color.FromRgb(0xFC, 0xFA, 0xF6));
    private static readonly IBrush FallbackDarkBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x11, 0x0E));
    private static readonly IBrush FallbackLightTextBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x19, 0x15));
    private static readonly IBrush FallbackDarkTextBrush = new SolidColorBrush(Color.FromRgb(0xE7, 0xE4, 0xDF));
    private static readonly IBrush FallbackLightSoftTextBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x54, 0x4F));
    private static readonly IBrush FallbackDarkSoftTextBrush = new SolidColorBrush(Color.FromRgb(0xA2, 0x9E, 0x98));
    private static readonly IBrush FallbackLightAccentBrush = new SolidColorBrush(Color.FromRgb(0xBD, 0x59, 0x2F));
    private static readonly IBrush FallbackDarkAccentBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0x8A, 0x67));
    private static readonly Uri StartupLogoUri = new("avares://MarkMello.Presentation/Assets/Images/logo.png");
    // Startup-splash CONTENT fade-in only. The cover background stays opaque from the
    // first frame (Show sets Opacity=1.0), so the splash appears smoothly without any
    // white bleed; the document reveal (Hide) is unchanged — still an instant cut on
    // heavy docs, keeping the stale WebView backing hidden (ce455d2).
    private static readonly TimeSpan StartupSplashContentFadeIn = TimeSpan.FromMilliseconds(280);

    private Window? _window;
    private Border? _shield;
    private Window? _owner;
    private Control? _host;
    private PixelSize _pixelSize;
    private DispatcherTimer? _hideTimer;
    private long _hideGeneration;

    public bool Show(Control host, ThemeVariant? themeVariant = null)
        => Show(host, themeVariant, content: null, contentKind: "shield");

    public bool ShowStartupSplash(Control host, string? documentName = null, ThemeVariant? themeVariant = null)
        => Show(
            host,
            themeVariant,
            CreateStartupSplashContent(host, themeVariant, documentName),
            contentKind: "startup-splash");

    private bool Show(Control host, ThemeVariant? themeVariant, Control? content, string contentKind)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var owner = TopLevel.GetTopLevel(host) as Window;
        if (owner is null)
        {
            return false;
        }

        var bounds = host.Bounds;
        if (bounds.Width <= 1 || bounds.Height <= 1)
        {
            return false;
        }

        Hide();

        var topLeft = host.PointToScreen(new Point(0, 0));
        var size = new Size(bounds.Width, bounds.Height);
        var pixelSize = ResolveHostPixelSize(host, size);
        var background = ResolveShieldBrush(host, themeVariant);
        _owner = owner;
        _host = host;
        _pixelSize = pixelSize;
        _shield = new Border
        {
            Background = background,
            Child = content,
            ClipToBounds = true,
            Focusable = false,
            Height = size.Height,
            IsHitTestVisible = false,
            Width = size.Width
        };
        _window = new Window
        {
            Background = background,
            CanResize = false,
            Content = _shield,
            Focusable = false,
            Height = size.Height,
            Position = topLeft,
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = true,
            Opacity = 1.0,
            Width = size.Width,
            WindowDecorations = WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.Manual
        };

        _owner.PositionChanged += OnOwnerPositionChanged;
        _owner.SizeChanged += OnOwnerSizeChanged;
        _window.Show(owner);
        _window.Position = topLeft;
        _window.Topmost = true;
        if (TryGetPlatformHandle(_window, out var handle))
        {
            NativeMethods.SetWindowPos(
                handle,
                NativeMethods.HwndTopmost,
                topLeft.X,
                topLeft.Y,
                pixelSize.Width,
                pixelSize.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        }

        // Force a synchronous layout pass so the solid cover content is measured
        // and arranged before Show() returns. Callers that raise the cover
        // immediately BEFORE a synchronous teardown (the document-switch
        // cover-first path) rely on the cover being painted first; without this
        // the cover Window can present one frame of unsized/unpainted content,
        // leaking a sliver of the teardown the cover exists to hide. Mirrors the
        // mode-toggle bridge's post-Show UpdateLayout.
        _window.UpdateLayout();

        ApplicateTrace.DiagMs(
            "pane-seq",
            "bridge-cover-window-shown",
            $"screen={topLeft.X},{topLeft.Y} size={size.Width:F0}x{size.Height:F0} px={pixelSize.Width}x{pixelSize.Height} brush={DescribeBrush(background)} content={contentKind}");
        return true;
    }

    public void Hide()
    {
        CancelAnimatedHide();
        if (_owner is not null)
        {
            _owner.PositionChanged -= OnOwnerPositionChanged;
            _owner.SizeChanged -= OnOwnerSizeChanged;
            _owner = null;
        }
        _host = null;

        if (_window is null)
        {
            return;
        }

        _window.Content = null;
        _window.Close();
        _window = null;
        _shield = null;
        _pixelSize = default;
    }

    public void Hide(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || _window is null)
        {
            Hide();
            return;
        }

        CancelAnimatedHide();
        var generation = ++_hideGeneration;
        var window = _window;
        window.Transitions =
        [
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = duration,
                Easing = ApplicateMotion.Easing
            }
        ];
        window.Opacity = 0.0;

        DispatcherTimer? timer = null;
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            if (timer is not null)
            {
                timer.Stop();
                timer.Tick -= tick;
            }
            if (ReferenceEquals(_hideTimer, timer))
            {
                _hideTimer = null;
            }
            if (generation == _hideGeneration)
            {
                Hide();
            }
        };

        timer = new DispatcherTimer
        {
            Interval = duration + TimeSpan.FromMilliseconds(40)
        };
        timer.Tick += tick;
        _hideTimer = timer;
        timer.Start();

        ApplicateTrace.DiagMs(
            "pane-seq",
            "bridge-cover-window-hide-animated",
            $"durationMs={duration.TotalMilliseconds:F0}");
    }

    public bool UpdateBrush(Control host, ThemeVariant? themeVariant = null)
    {
        if (_window is null || _shield is null)
        {
            return false;
        }

        var background = ResolveShieldBrush(host, themeVariant);
        _window.Background = background;
        _shield.Background = background;
        ApplicateTrace.DiagMs("pane-seq", "bridge-cover-window-brush-updated", $"brush={DescribeBrush(background)}");
        return true;
    }

    public void Dispose()
        => Hide();

    private void OnOwnerPositionChanged(object? sender, PixelPointEventArgs e)
        => RepositionToHost();

    private void OnOwnerSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplicateTrace.DiagMs("pane-seq", "bridge-cover-owner-resized-hidden");
        Hide();
    }

    private void RepositionToHost()
    {
        if (_window is null || _host is null)
        {
            return;
        }

        var topLeft = _host.PointToScreen(new Point(0, 0));
        _pixelSize = ResolveHostPixelSize(_host, _host.Bounds.Size);
        _window.Position = topLeft;
        if (TryGetPlatformHandle(_window, out var handle))
        {
            NativeMethods.SetWindowPos(
                handle,
                NativeMethods.HwndTopmost,
                topLeft.X,
                topLeft.Y,
                _pixelSize.Width,
                _pixelSize.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        }

        ApplicateTrace.DiagMs(
            "pane-seq",
            "bridge-cover-window-repositioned",
            $"screen={topLeft.X},{topLeft.Y} px={_pixelSize.Width}x{_pixelSize.Height}");
    }

    private static PixelSize ResolveHostPixelSize(Control host, Size size)
    {
        // Window APIs take physical pixels; siblingPanel bounds are DIPs, so
        // derive cover geometry from the owning host, never from captured content.
        var scaling = TopLevel.GetTopLevel(host)?.RenderScaling ?? 1.0;
        return new PixelSize(
            SysMath.Max(1, (int)SysMath.Round(size.Width * scaling, MidpointRounding.AwayFromZero)),
            SysMath.Max(1, (int)SysMath.Round(size.Height * scaling, MidpointRounding.AwayFromZero)));
    }

    private static IBrush ResolveShieldBrush(Control host, ThemeVariant? themeVariant = null)
        => ResolveThemeBrush(
            host,
            "MmBackgroundBrush",
            themeVariant,
            IsDarkVariant(themeVariant ?? host.ActualThemeVariant ?? Avalonia.Application.Current?.ActualThemeVariant)
                ? FallbackDarkBrush
                : FallbackLightBrush);

    private static IBrush ResolveTextBrush(Control host, ThemeVariant? themeVariant = null)
        => ResolveThemeBrush(
            host,
            "MmTextBrush",
            themeVariant,
            IsDarkVariant(themeVariant ?? host.ActualThemeVariant ?? Avalonia.Application.Current?.ActualThemeVariant)
                ? FallbackDarkTextBrush
                : FallbackLightTextBrush);

    private static IBrush ResolveSoftTextBrush(Control host, ThemeVariant? themeVariant = null)
        => ResolveThemeBrush(
            host,
            "MmTextSoftBrush",
            themeVariant,
            IsDarkVariant(themeVariant ?? host.ActualThemeVariant ?? Avalonia.Application.Current?.ActualThemeVariant)
                ? FallbackDarkSoftTextBrush
                : FallbackLightSoftTextBrush);

    private static IBrush ResolveAccentBrush(Control host, ThemeVariant? themeVariant = null)
        => ResolveThemeBrush(
            host,
            "MmAccentBrush",
            themeVariant,
            IsDarkVariant(themeVariant ?? host.ActualThemeVariant ?? Avalonia.Application.Current?.ActualThemeVariant)
                ? FallbackDarkAccentBrush
                : FallbackLightAccentBrush);

    private static IBrush ResolveThemeBrush(
        Control host,
        string resourceKey,
        ThemeVariant? themeVariant,
        IBrush fallback)
    {
        var resolvedHostVariant = themeVariant ?? host.ActualThemeVariant;
        if (host.TryFindResource(resourceKey, resolvedHostVariant, out var resource)
            && resource is IBrush brush)
        {
            return brush;
        }

        var app = Avalonia.Application.Current;
        var resolvedAppVariant = themeVariant ?? app?.ActualThemeVariant;
        if (app?.TryGetResource(resourceKey, resolvedAppVariant, out var appResource) == true
            && appResource is IBrush appBrush)
        {
            return appBrush;
        }

        return fallback;
    }

    private static FontFamily ResolveFontFamily(Control host, string resourceKey, string fallback)
    {
        if (host.TryFindResource(resourceKey, host.ActualThemeVariant, out var resource)
            && resource is FontFamily family)
        {
            return family;
        }

        var app = Avalonia.Application.Current;
        if (app?.TryGetResource(resourceKey, app.ActualThemeVariant, out var appResource) == true
            && appResource is FontFamily appFamily)
        {
            return appFamily;
        }

        return new FontFamily(fallback);
    }

    private static bool IsDarkVariant(ThemeVariant? variant)
        => variant == ThemeVariant.Dark;

    private static Control CreateStartupSplashContent(
        Control host,
        ThemeVariant? themeVariant,
        string? documentName)
    {
        var textBrush = ResolveTextBrush(host, themeVariant);
        var softTextBrush = ResolveSoftTextBrush(host, themeVariant);
        var accentBrush = ResolveAccentBrush(host, themeVariant);
        var titleFont = ResolveFontFamily(host, "MmDocumentSerifFontFamily", "Georgia, Cambria, serif");
        var bodyFont = ResolveFontFamily(host, "MmDocumentSansFontFamily", "Segoe UI, system-ui, sans-serif");
        var status = string.IsNullOrWhiteSpace(documentName)
            ? "Preparing document..."
            : $"Preparing {documentName}...";

        var wordmark = new TextBlock
        {
            FontFamily = titleFont,
            FontSize = 44,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        wordmark.Inlines!.Add(new Run
        {
            Text = "Mark",
            Foreground = textBrush,
        });
        wordmark.Inlines.Add(new Run
        {
            Text = "Mello",
            Foreground = accentBrush,
        });

        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(32),
            MaxWidth = 520,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (TryCreateStartupLogo() is { } logo)
        {
            content.Children.Add(logo);
        }

        content.Children.Add(wordmark);
        content.Children.Add(new TextBlock
        {
            FontFamily = bodyFont,
            FontSize = 13,
            Foreground = softTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 0),
            MaxWidth = 420,
            Text = status,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });

        var root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0.0,
            Children =
            {
                content,
            },
        };
        // Fade the splash content in over the already-opaque cover background. The
        // change fires once the cover window is on screen (AttachedToVisualTree), so
        // it animates 0 -> 1; the opaque background means no white bleed during the
        // fade. Reveal (Hide) is untouched: instant cut on heavy docs as before.
        root.Transitions =
        [
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = StartupSplashContentFadeIn,
                Easing = ApplicateMotion.Easing,
            },
        ];
        root.AttachedToVisualTree += (_, _) =>
            Dispatcher.UIThread.Post(() => root.Opacity = 1.0, DispatcherPriority.Render);
        return root;
    }

    private static Image? TryCreateStartupLogo()
    {
        try
        {
            using var stream = AssetLoader.Open(StartupLogoUri);
            return new Image
            {
                Height = 160,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, -15),
                Source = new Bitmap(stream),
                Width = 160,
            };
        }
        catch (Exception ex)
        {
            ApplicateTrace.DiagMs(
                "pane-seq",
                "startup-splash-logo-load-failed",
                $"ex={ex.GetType().Name}");
            return null;
        }
    }

    private static string DescribeBrush(IBrush brush)
        => brush is ISolidColorBrush solid
            ? $"#{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}"
            : brush.GetType().Name;

    private void CancelAnimatedHide()
    {
        _hideGeneration++;
        if (_hideTimer is null)
        {
            return;
        }

        _hideTimer.Stop();
        _hideTimer = null;
    }

    private static bool TryGetPlatformHandle(Window window, out IntPtr handle)
    {
        handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        return handle != IntPtr.Zero;
    }

    private static class NativeMethods
    {
        public static readonly IntPtr HwndTopmost = new(-1);
        public const uint SwpNoActivate = 0x0010;
        public const uint SwpShowWindow = 0x0040;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint uFlags);
    }
}
