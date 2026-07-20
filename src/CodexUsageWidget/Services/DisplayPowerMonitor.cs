using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CodexUsageWidget.Services;

internal enum SessionDisplayState
{
    Off = 0,
    On = 1,
    Dimmed = 2,
}

internal sealed class SessionDisplayStateChangedEventArgs(
    SessionDisplayState state) : EventArgs
{
    public SessionDisplayState State { get; } = state;
}

/// <summary>
/// Receives the session-scoped display state broadcast without polling.
/// Interactive desktop applications should use GUID_SESSION_DISPLAY_STATUS
/// rather than the physical console display notification.
/// </summary>
internal sealed class DisplayPowerMonitor : IDisposable
{
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtPowerSettingChange = 0x8013;
    private const uint DeviceNotifyWindowHandle = 0;
    private static readonly Guid SessionDisplayStatus =
        new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");

    private readonly Window window;
    private HwndSource? source;
    private IntPtr notificationHandle;
    private bool disposed;
    private SessionDisplayState state = SessionDisplayState.On;

    public DisplayPowerMonitor(Window window)
    {
        this.window = window ?? throw new ArgumentNullException(nameof(window));
        window.SourceInitialized += WindowOnSourceInitialized;
        window.Closed += WindowOnClosed;
        TryAttach();
    }

    public event EventHandler<SessionDisplayStateChangedEventArgs>?
        DisplayStateChanged;

    public SessionDisplayState State => state;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        window.SourceInitialized -= WindowOnSourceInitialized;
        window.Closed -= WindowOnClosed;
        Detach();
    }

    private void WindowOnSourceInitialized(object? sender, EventArgs e) =>
        TryAttach();

    private void WindowOnClosed(object? sender, EventArgs e) => Dispose();

    private void TryAttach()
    {
        if (disposed || source is not null)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        source = HwndSource.FromHwnd(handle);
        source?.AddHook(WindowProc);
        var setting = SessionDisplayStatus;
        notificationHandle = RegisterPowerSettingNotification(
            handle,
            ref setting,
            DeviceNotifyWindowHandle);
    }

    private void Detach()
    {
        if (notificationHandle != IntPtr.Zero)
        {
            _ = UnregisterPowerSettingNotification(notificationHandle);
            notificationHandle = IntPtr.Zero;
        }

        source?.RemoveHook(WindowProc);
        source = null;
    }

    private IntPtr WindowProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        _ = hwnd;
        _ = handled;
        if (message != WmPowerBroadcast ||
            wParam.ToInt64() != PbtPowerSettingChange ||
            lParam == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
        if (setting.PowerSetting != SessionDisplayStatus ||
            setting.DataLength < sizeof(uint))
        {
            return IntPtr.Zero;
        }

        var dataOffset = Marshal.SizeOf<PowerBroadcastSetting>();
        var rawState = Marshal.ReadInt32(lParam, dataOffset);
        if (!Enum.IsDefined(typeof(SessionDisplayState), rawState))
        {
            return IntPtr.Zero;
        }

        var next = (SessionDisplayState)rawState;
        if (next == state)
        {
            return IntPtr.Zero;
        }

        state = next;
        DisplayStateChanged?.Invoke(
            this,
            new SessionDisplayStateChangedEventArgs(next));
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(
        IntPtr recipient,
        ref Guid powerSettingGuid,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(
        IntPtr handle);
}
