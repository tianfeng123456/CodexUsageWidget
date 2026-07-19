using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexUsageWidget.Core;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace CodexUsageWidget.Services;

/// <summary>
/// Captures the pixels physically behind the widget once, then creates a
/// frozen, software-blurred image. There is deliberately no render loop:
/// callers request a new snapshot only after an explicit window action.
/// </summary>
internal static class FrostedBackdropSnapshotService
{
    private const double DipsPerInch = 96d;
    private const double CollapsedInsetDip = 5d;
    private const double CollapsedSurfaceWidthDip = 70d;
    private const double CollapsedSurfaceHeightDip = 70d;
    private const double CapsuleInsetXDip = 6d;
    private const double CapsuleInsetYDip = 8d;
    private const double CapsuleSurfaceWidthDip = 196d;
    private const double CapsuleSurfaceHeightDip = 64d;
    private const double ExpandedInsetDip = 6d;
    private const double ExpandedSurfaceWidthDip = 408d;
    private const double ExpandedSurfaceHeightDip = 528d;
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int MaximumCaptureDimension = 4096;
    private static readonly SemaphoreSlim CaptureGate = new(1, 1);

    public static BackdropSnapshot? Capture(
        IntPtr windowHandle,
        WidgetWindowShape shape,
        CancellationToken cancellationToken = default)
    {
        CaptureGate.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CaptureCore(windowHandle, shape, cancellationToken);
        }
        finally
        {
            CaptureGate.Release();
        }
    }

    private static BackdropSnapshot? CaptureCore(
        IntPtr windowHandle,
        WidgetWindowShape shape,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (windowHandle == IntPtr.Zero ||
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ||
            !GetWindowRect(windowHandle, out var windowRect))
        {
            return null;
        }

        var dpi = GetWindowDpi(windowHandle);
        var scale = dpi / DipsPerInch;
        var (insetXDip, insetYDip, surfaceWidthDip, surfaceHeightDip) =
            shape switch
        {
            WidgetWindowShape.Collapsed =>
                (
                    CollapsedInsetDip,
                    CollapsedInsetDip,
                    CollapsedSurfaceWidthDip,
                    CollapsedSurfaceHeightDip
                ),
            WidgetWindowShape.Capsule =>
                (
                    CapsuleInsetXDip,
                    CapsuleInsetYDip,
                    CapsuleSurfaceWidthDip,
                    CapsuleSurfaceHeightDip
                ),
            WidgetWindowShape.Expanded =>
                (
                    ExpandedInsetDip,
                    ExpandedInsetDip,
                    ExpandedSurfaceWidthDip,
                    ExpandedSurfaceHeightDip
                ),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        CapturePixelRect captureRect;
        try
        {
            // GetWindowRect is already in physical screen pixels. Convert its
            // origin back to DIPs only so the tested outward-rounding helper
            // can apply the shaped content inset at fractional DPI.
            captureRect = BackdropCaptureGeometry.FromDeviceIndependentBounds(
                (windowRect.Left / scale) + insetXDip,
                (windowRect.Top / scale) + insetYDip,
                surfaceWidthDip,
                surfaceHeightDip,
                scale,
                scale);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }

        if (!IsCaptureRectSafe(captureRect))
        {
            return null;
        }

        var originalAffinity = WdaNone;
        var hasOriginalAffinity =
            GetWindowDisplayAffinity(windowHandle, out originalAffinity);
        if (!SetWindowDisplayAffinity(
                windowHandle,
                WdaExcludeFromCapture))
        {
            return null;
        }

        DrawingBitmap? capturedBitmap = null;
        try
        {
            // Wait for DWM to honor the temporary capture exclusion so the
            // snapshot contains the desktop below the widget, not the widget
            // recursively painted into its own glass.
            _ = DwmFlush();
            capturedBitmap = new DrawingBitmap(
                captureRect.Width,
                captureRect.Height,
                DrawingPixelFormat.Format32bppArgb);
            using var graphics = DrawingGraphics.FromImage(capturedBitmap);
            graphics.CopyFromScreen(
                captureRect.X,
                captureRect.Y,
                0,
                0,
                new System.Drawing.Size(
                    captureRect.Width,
                    captureRect.Height),
                CopyPixelOperation.SourceCopy);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (ArgumentException)
        {
            capturedBitmap?.Dispose();
            return null;
        }
        catch (ExternalException)
        {
            capturedBitmap?.Dispose();
            return null;
        }
        finally
        {
            _ = SetWindowDisplayAffinity(
                windowHandle,
                hasOriginalAffinity ? originalAffinity : WdaNone);
            _ = DwmFlush();
        }

        if (capturedBitmap is null)
        {
            return null;
        }

        using (capturedBitmap)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Keep the glass bitmap at no more than one pixel per WPF DIP.
            // High-DPI screen pixels are downsampled once; this both softens
            // the material and bounds CPU/memory cost during explicit actions.
            var targetWidth = Math.Max(
                1,
                Math.Min(
                    captureRect.Width,
                    checked((int)Math.Round(surfaceWidthDip))));
            var targetHeight = Math.Max(
                1,
                Math.Min(
                    captureRect.Height,
                    checked((int)Math.Round(surfaceHeightDip))));

            using var sampledBitmap = Resample(
                capturedBitmap,
                targetWidth,
                targetHeight);
            cancellationToken.ThrowIfCancellationRequested();
            return ReadPixels(sampledBitmap);
        }
    }

    public static BitmapSource CreateBlurredImage(
        BackdropSnapshot snapshot,
        int radius = 18,
        int passes = 3)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var blurred = BackdropBlur.BlurBgra32(
            snapshot.Pixels,
            snapshot.Width,
            snapshot.Height,
            snapshot.Stride,
            radius,
            passes);
        var image = BitmapSource.Create(
            snapshot.Width,
            snapshot.Height,
            DipsPerInch,
            DipsPerInch,
            PixelFormats.Bgra32,
            palette: null,
            blurred,
            snapshot.Stride);
        image.Freeze();
        return image;
    }

    private static DrawingBitmap Resample(
        DrawingBitmap source,
        int width,
        int height)
    {
        if (source.Width == width && source.Height == height)
        {
            return source.Clone(
                new System.Drawing.Rectangle(0, 0, width, height),
                DrawingPixelFormat.Format32bppArgb);
        }

        var result = new DrawingBitmap(
            width,
            height,
            DrawingPixelFormat.Format32bppArgb);
        using var graphics = DrawingGraphics.FromImage(result);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.DrawImage(
            source,
            new System.Drawing.Rectangle(0, 0, width, height),
            0,
            0,
            source.Width,
            source.Height,
            GraphicsUnit.Pixel);
        return result;
    }

    private static BackdropSnapshot ReadPixels(DrawingBitmap bitmap)
    {
        var bitmapData = bitmap.LockBits(
            new System.Drawing.Rectangle(
                0,
                0,
                bitmap.Width,
                bitmap.Height),
            ImageLockMode.ReadOnly,
            DrawingPixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(bitmapData.Stride);
            var pixels = new byte[checked(stride * bitmap.Height)];
            if (bitmapData.Stride > 0)
            {
                Marshal.Copy(
                    bitmapData.Scan0,
                    pixels,
                    0,
                    pixels.Length);
            }
            else
            {
                for (var row = 0; row < bitmap.Height; row++)
                {
                    var sourceRow = IntPtr.Add(
                        bitmapData.Scan0,
                        row * bitmapData.Stride);
                    Marshal.Copy(
                        sourceRow,
                        pixels,
                        (bitmap.Height - row - 1) * stride,
                        stride);
                }
            }

            // GDI screen copies do not promise meaningful alpha. The WPF
            // backdrop itself must be opaque; translucency comes from the
            // theme tint layer rendered above it.
            for (var offset = 3; offset < pixels.Length; offset += 4)
            {
                pixels[offset] = byte.MaxValue;
            }

            return new BackdropSnapshot(
                pixels,
                bitmap.Width,
                bitmap.Height,
                stride);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }

    private static bool IsCaptureRectSafe(CapturePixelRect rect)
    {
        if (rect.Width <= 0 ||
            rect.Height <= 0 ||
            rect.Width > MaximumCaptureDimension ||
            rect.Height > MaximumCaptureDimension)
        {
            return false;
        }

        var virtualLeft = GetSystemMetrics(SmXVirtualScreen);
        var virtualTop = GetSystemMetrics(SmYVirtualScreen);
        var virtualWidth = GetSystemMetrics(SmCxVirtualScreen);
        var virtualHeight = GetSystemMetrics(SmCyVirtualScreen);
        if (virtualWidth <= 0 || virtualHeight <= 0)
        {
            return false;
        }

        var virtualRight = (long)virtualLeft + virtualWidth;
        var virtualBottom = (long)virtualTop + virtualHeight;
        return rect.X >= virtualLeft &&
               rect.Y >= virtualTop &&
               (long)rect.X + rect.Width <= virtualRight &&
               (long)rect.Y + rect.Height <= virtualBottom;
    }

    private static uint GetWindowDpi(IntPtr windowHandle)
    {
        try
        {
            var dpi = GetDpiForWindow(windowHandle);
            return dpi == 0 ? (uint)DipsPerInch : dpi;
        }
        catch (DllNotFoundException)
        {
            return (uint)DipsPerInch;
        }
        catch (EntryPointNotFoundException)
        {
            return (uint)DipsPerInch;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr windowHandle,
        out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(
        IntPtr windowHandle,
        uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(
        IntPtr windowHandle,
        out uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmFlush();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

internal sealed record BackdropSnapshot(
    byte[] Pixels,
    int Width,
    int Height,
    int Stride);
