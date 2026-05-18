using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace MarkMello.Applicate.Desktop.Activation;

internal static class ApplicateForegroundWindowActivator
{
    public static void ActivateExternalRequest(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _ = NativeMethods.ShowWindow(handle, NativeMethods.SwShow);
        _ = NativeMethods.SetForegroundWindow(handle);
    }

    private static class NativeMethods
    {
        public const int SwShow = 5;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr windowHandle, int commandShow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr windowHandle);
    }
}
