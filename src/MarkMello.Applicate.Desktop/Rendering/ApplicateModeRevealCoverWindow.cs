using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using MarkMello.Applicate.Desktop.Diagnostics;
using System.Runtime.InteropServices;
using SysMath = System.Math;

namespace MarkMello.Applicate.Desktop.Rendering;

internal sealed class ApplicateModeRevealCoverWindow : IDisposable
{
    private static readonly IBrush FallbackLightBrush = new SolidColorBrush(Color.FromRgb(0xFC, 0xFA, 0xF6));
    private static readonly IBrush FallbackDarkBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x11, 0x0E));

    private Window? _window;
    private Border? _shield;
    private Window? _owner;
    private Control? _host;
    private PixelSize _pixelSize;

    public bool Show(Control host)
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
        var background = ResolveShieldBrush(host);
        _owner = owner;
        _host = host;
        _pixelSize = pixelSize;
        _shield = new Border
        {
            Background = background,
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

        ApplicateTrace.DiagMs(
            "pane-seq",
            "bridge-cover-window-shown",
            $"screen={topLeft.X},{topLeft.Y} size={size.Width:F0}x{size.Height:F0} px={pixelSize.Width}x{pixelSize.Height} brush={DescribeBrush(background)}");
        return true;
    }

    public void Hide()
    {
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

    private static IBrush ResolveShieldBrush(Control host)
    {
        const string backgroundBrushKey = "MmBackgroundBrush";
        if (host.TryFindResource(backgroundBrushKey, host.ActualThemeVariant, out var resource)
            && resource is IBrush brush)
        {
            return brush;
        }

        var app = Avalonia.Application.Current;
        if (app?.TryGetResource(backgroundBrushKey, app.ActualThemeVariant, out var appResource) == true
            && appResource is IBrush appBrush)
        {
            return appBrush;
        }

        return IsDarkVariant(host.ActualThemeVariant ?? app?.ActualThemeVariant)
            ? FallbackDarkBrush
            : FallbackLightBrush;
    }

    private static bool IsDarkVariant(ThemeVariant? variant)
        => variant == ThemeVariant.Dark;

    private static string DescribeBrush(IBrush brush)
        => brush is ISolidColorBrush solid
            ? $"#{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}"
            : brush.GetType().Name;

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
