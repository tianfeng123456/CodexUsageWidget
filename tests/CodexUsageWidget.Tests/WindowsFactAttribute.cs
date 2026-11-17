namespace CodexUsageWidget.Tests;
public sealed class WindowsFactAttribute : Xunit.FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows named activation event; macOS uses flock + NSDistributedNotificationCenter.";
    }
}
