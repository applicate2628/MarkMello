using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MarkMello.Applicate.Desktop.Diagnostics;
using SysMath = System.Math;

namespace MarkMello.Applicate.Desktop.Rendering;

internal static class ApplicateModeTransitionCapture
{
    public static Bitmap? TryCapture(Visual visual)
    {
        ArgumentNullException.ThrowIfNull(visual);

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var topLevel = TopLevel.GetTopLevel(visual);
        if (topLevel is null)
        {
            return null;
        }

        var bounds = visual.Bounds;
        var scaling = topLevel.RenderScaling;
        var width = SysMath.Max(1, (int)SysMath.Round(bounds.Width * scaling, MidpointRounding.AwayFromZero));
        var height = SysMath.Max(1, (int)SysMath.Round(bounds.Height * scaling, MidpointRounding.AwayFromZero));
        if (width <= 1 || height <= 1)
        {
            return null;
        }

        var topLeft = visual.PointToScreen(new Point(0, 0));
        var bitmap = CaptureScreenPixels(topLeft.X, topLeft.Y, width, height, scaling);
        ApplicateTrace.DiagMs(
            "pane-seq",
            bitmap is null ? "bridge-cover-capture-failed" : "bridge-cover-capture-ok",
            $"screen={topLeft.X},{topLeft.Y} px={width}x{height} scale={scaling:F2}");
        return bitmap;
    }

    private static Bitmap? CaptureScreenPixels(int x, int y, int width, int height, double scaling)
    {
        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        IntPtr oldObject = IntPtr.Zero;
        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                return null;
            }

            var bitmapInfo = new NativeMethods.BitmapInfo
            {
                Header = new NativeMethods.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = NativeMethods.BiRgb
                }
            };
            dib = NativeMethods.CreateDIBSection(
                screenDc,
                ref bitmapInfo,
                NativeMethods.DibRgbColors,
                out var bits,
                IntPtr.Zero,
                0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return null;
            }

            oldObject = NativeMethods.SelectObject(memoryDc, dib);
            if (oldObject == IntPtr.Zero)
            {
                return null;
            }

            var copied = NativeMethods.BitBlt(
                memoryDc,
                0,
                0,
                width,
                height,
                screenDc,
                x,
                y,
                NativeMethods.SrcCopy | NativeMethods.CaptureBlt);
            if (!copied)
            {
                return null;
            }

            return CopyToAvaloniaBitmap(bits, width, height, scaling);
        }
        finally
        {
            if (oldObject != IntPtr.Zero)
            {
                _ = NativeMethods.SelectObject(memoryDc, oldObject);
            }

            if (dib != IntPtr.Zero)
            {
                _ = NativeMethods.DeleteObject(dib);
            }

            if (memoryDc != IntPtr.Zero)
            {
                _ = NativeMethods.DeleteDC(memoryDc);
            }

            _ = NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static WriteableBitmap CopyToAvaloniaBitmap(IntPtr sourceBits, int width, int height, double scaling)
    {
        var sourceStride = width * 4;
        var bytes = new byte[sourceStride * height];
        Marshal.Copy(sourceBits, bytes, 0, bytes.Length);

        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96.0 * scaling, 96.0 * scaling),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
        using var framebuffer = bitmap.Lock();
        for (var row = 0; row < height; row++)
        {
            Marshal.Copy(
                bytes,
                row * sourceStride,
                IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes),
                sourceStride);
        }

        return bitmap;
    }

    private static class NativeMethods
    {
        public const uint SrcCopy = 0x00CC0020;
        public const uint CaptureBlt = 0x40000000;
        public const uint BiRgb = 0;
        public const uint DibRgbColors = 0;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr windowHandle);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int ReleaseDC(IntPtr windowHandle, IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateDIBSection(
            IntPtr dc,
            ref BitmapInfo bitmapInfo,
            uint usage,
            out IntPtr bits,
            IntPtr section,
            uint offset);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BitBlt(
            IntPtr destinationDc,
            int x,
            int y,
            int width,
            int height,
            IntPtr sourceDc,
            int sourceX,
            int sourceY,
            uint rasterOperation);

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfo
        {
            public BitmapInfoHeader Header;
            public uint RedMask;
            public uint GreenMask;
            public uint BlueMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
        }
    }
}
