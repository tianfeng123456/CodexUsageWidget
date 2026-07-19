using System.Runtime.InteropServices;

namespace CodexUsageWidget;

/// <summary>
/// The stable surfaces used by the event-driven glass snapshot.
/// </summary>
internal enum WidgetWindowShape
{
    Collapsed,
    Capsule,
    Expanded,
}

/// <summary>
/// Keeps the layered WPF window free of a native rectangular region.
/// The glass material is rendered only by the shaped WPF surfaces.
/// </summary>
internal static class WindowMaterialHelper
{
    public static bool ClearWindowRegion(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return SetWindowRgn(
                       windowHandle,
                       IntPtr.Zero,
                       redraw: true) != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(
        IntPtr windowHandle,
        IntPtr windowRegion,
        [MarshalAs(UnmanagedType.Bool)] bool redraw);
}
