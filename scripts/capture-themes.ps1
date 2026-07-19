[CmdletBinding()]
param(
    [string]$Executable,
    [string]$SettingsPath = (
        Join-Path $env:LOCALAPPDATA 'CodexUsageWidget\settings.json'),
    [string]$OutputDirectory,
    [ValidateSet('Circle', 'Capsule')]
    [string]$CollapsedMode = 'Circle',
    [ValidateRange(-1, 100)]
    [int]$GlassTransparencyPercent = -1
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Executable)) {
    $Executable = Join-Path $projectRoot 'dist\CodexUsageWidget.exe'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot 'docs\screenshots'
}

$Executable = [IO.Path]::GetFullPath($Executable)
$SettingsPath = [IO.Path]::GetFullPath($SettingsPath)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$expectedCollapsedWidth = if ($CollapsedMode -eq 'Capsule') { 208 } else { 80 }
$minimumScreenshotBytes = if ($GlassTransparencyPercent -eq 100) { 1024 } else { 4096 }

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class CodexThemeCaptureNative
{
    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr state);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        IntPtr state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr windowHandle,
        out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(
        IntPtr windowHandle,
        out Rect rectangle);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint NativeGetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "SetProcessDpiAwarenessContext")]
    private static extern bool NativeSetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetProcessDPIAware")]
    private static extern bool NativeSetProcessDpiAware();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(
        IntPtr windowHandle,
        IntPtr deviceContext,
        uint flags);

    public static void EnablePerMonitorDpiAwareness()
    {
        try
        {
            NativeSetProcessDpiAwarenessContext(new IntPtr(-4));
        }
        catch (EntryPointNotFoundException)
        {
            try
            {
                NativeSetProcessDpiAware();
            }
            catch (EntryPointNotFoundException)
            {
            }
        }
    }

    public static uint GetDpiForWindow(IntPtr windowHandle)
    {
        try
        {
            uint dpi = NativeGetDpiForWindow(windowHandle);
            return dpi == 0 ? 96U : dpi;
        }
        catch (EntryPointNotFoundException)
        {
            return 96U;
        }
    }

    public static IntPtr FindVisibleWindow(int processId)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows(
            delegate(IntPtr windowHandle, IntPtr ignored)
            {
                uint ownerProcessId;
                GetWindowThreadProcessId(windowHandle, out ownerProcessId);
                Rect rectangle;
                if (ownerProcessId == (uint)processId &&
                    IsWindowVisible(windowHandle) &&
                    GetWindowRect(windowHandle, out rectangle) &&
                    rectangle.Right > rectangle.Left &&
                    rectangle.Bottom > rectangle.Top)
                {
                    result = windowHandle;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);
        return result;
    }

    public static IntPtr[] GetVisibleWindowHandles(int processId)
    {
        List<IntPtr> result = new List<IntPtr>();
        EnumWindows(
            delegate(IntPtr windowHandle, IntPtr ignored)
            {
                uint ownerProcessId;
                GetWindowThreadProcessId(windowHandle, out ownerProcessId);
                Rect rectangle;
                if (ownerProcessId == (uint)processId &&
                    IsWindowVisible(windowHandle) &&
                    GetWindowRect(windowHandle, out rectangle) &&
                    rectangle.Right > rectangle.Left &&
                    rectangle.Bottom > rectangle.Top)
                {
                    result.Add(windowHandle);
                }

                return true;
            },
            IntPtr.Zero);
        return result.ToArray();
    }
}
'@

[CodexThemeCaptureNative]::EnablePerMonitorDpiAwareness()
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Wait-ForWidgetWindow {
    param(
        [Diagnostics.Process]$Process,
        [double]$ExpectedLogicalWidth,
        [double]$ExpectedLogicalHeight,
        [string]$StateName,
        [IntPtr]$ExpectedHandle = [IntPtr]::Zero,
        [int]$TimeoutMilliseconds = 15000)

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "The widget exited with code $($Process.ExitCode)."
        }

        $handle = if ($ExpectedHandle -ne [IntPtr]::Zero) {
            $ExpectedHandle
        }
        else {
            [CodexThemeCaptureNative]::FindVisibleWindow($Process.Id)
        }
        if ($handle -ne [IntPtr]::Zero) {
            $bounds = New-Object CodexThemeCaptureNative+Rect
            if ([CodexThemeCaptureNative]::GetWindowRect(
                    $handle,
                    [ref]$bounds)) {
                $dpi = [double][CodexThemeCaptureNative]::GetDpiForWindow($handle)
                $scale = $dpi / 96.0
                $logicalWidth = ($bounds.Right - $bounds.Left) / $scale
                $logicalHeight = ($bounds.Bottom - $bounds.Top) / $scale
                if ([Math]::Abs(
                        $logicalWidth - $ExpectedLogicalWidth) -le 1.5 -and
                    [Math]::Abs(
                        $logicalHeight - $ExpectedLogicalHeight) -le 1.5) {
                    return [pscustomobject]@{
                        Handle = $handle
                        Bounds = $bounds
                        Dpi = [int]$dpi
                        LogicalWidth = [Math]::Round($logicalWidth, 1)
                        LogicalHeight = [Math]::Round($logicalHeight, 1)
                    }
                }
            }
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw (
        'The widget did not reach its {0}x{1} {2} state.' -f
        $ExpectedLogicalWidth,
        $ExpectedLogicalHeight,
        $StateName)
}

function Wait-ForExpandedWindow {
    param(
        [Diagnostics.Process]$Process,
        [IntPtr]$ExpectedHandle = [IntPtr]::Zero,
        [int]$TimeoutMilliseconds = 15000)

    return Wait-ForWidgetWindow `
        -Process $Process `
        -ExpectedLogicalWidth 420 `
        -ExpectedLogicalHeight 540 `
        -StateName 'pinned' `
        -ExpectedHandle $ExpectedHandle `
        -TimeoutMilliseconds $TimeoutMilliseconds
}

function Wait-ForCollapsedWindow {
    param(
        [Diagnostics.Process]$Process,
        [int]$TimeoutMilliseconds = 15000)

    return Wait-ForWidgetWindow `
        -Process $Process `
        -ExpectedLogicalWidth $expectedCollapsedWidth `
        -ExpectedLogicalHeight 80 `
        -StateName 'collapsed' `
        -TimeoutMilliseconds $TimeoutMilliseconds
}

function Get-VisibleTopLevelWindowHandles {
    param([Diagnostics.Process]$Process)

    return @(
        [CodexThemeCaptureNative]::GetVisibleWindowHandles($Process.Id) |
            ForEach-Object { $_.ToInt64().ToString('X16') } |
            Sort-Object
    )
}

function Find-AutomationElement {
    param(
        [IntPtr]$WindowHandle,
        [string]$AutomationId)

    $root = [System.Windows.Automation.AutomationElement]::FromHandle(
        $WindowHandle)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Invoke-AutomationButton {
    param(
        [IntPtr]$WindowHandle,
        [string]$AutomationId)

    $button = Find-AutomationElement `
        -WindowHandle $WindowHandle `
        -AutomationId $AutomationId
    if ($null -eq $button) {
        throw "Automation button not found: $AutomationId"
    }

    $pattern = $button.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Wait-ForVisibleAutomationElement {
    param(
        [Diagnostics.Process]$Process,
        [IntPtr]$WindowHandle,
        [string]$AutomationId,
        [int]$TimeoutMilliseconds = 5000)

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "The widget exited with code $($Process.ExitCode)."
        }

        try {
            $element = Find-AutomationElement `
                -WindowHandle $WindowHandle `
                -AutomationId $AutomationId
            if ($null -ne $element) {
                $current = $element.Current
                $bounds = $current.BoundingRectangle
                if (-not $current.IsOffscreen -and
                    $bounds.Width -gt 0 -and
                    $bounds.Height -gt 0) {
                    return $element
                }
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }

        Start-Sleep -Milliseconds 75
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Automation element did not become visible: $AutomationId"
}

function Wait-ForAutomationIdPrefixCount {
    param(
        [Diagnostics.Process]$Process,
        [IntPtr]$WindowHandle,
        [string]$Prefix,
        [int]$ExpectedCount,
        [int]$TimeoutMilliseconds = 20000)

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "The widget exited with code $($Process.ExitCode)."
        }

        try {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle(
                $WindowHandle)
            $elements = $root.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            $matching = @(
                foreach ($element in $elements) {
                    $automationId = $element.Current.AutomationId
                    if ($automationId -and $automationId.StartsWith(
                            $Prefix,
                            [StringComparison]::Ordinal)) {
                        $element
                    }
                }
            )
            if ($matching.Count -eq $ExpectedCount) {
                return $matching
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }

        Start-Sleep -Milliseconds 125
    } while ([DateTime]::UtcNow -lt $deadline)

    throw (
        'Automation elements did not reach expected count: ' +
        "$Prefix expected=$ExpectedCount")
}

function Save-PrintedWindow {
    param(
        $Window,
        [string]$Path)

    $width = $Window.Bounds.Right - $Window.Bounds.Left
    $height = $Window.Bounds.Bottom - $Window.Bounds.Top
    $bitmap = New-Object System.Drawing.Bitmap(
        $width,
        $height,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $deviceContext = $graphics.GetHdc()
            try {
                $printed = [CodexThemeCaptureNative]::PrintWindow(
                    $Window.Handle,
                    $deviceContext,
                    2)
            }
            finally {
                $graphics.ReleaseHdc($deviceContext)
            }
        }
        finally {
            $graphics.Dispose()
        }

        if (-not $printed) {
            throw 'PrintWindow could not capture the WPF window.'
        }

        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }

    $file = Get-Item -LiteralPath $Path
    if ($file.Length -lt $minimumScreenshotBytes) {
        throw "The captured image is unexpectedly small: $($file.Length) bytes."
    }
}

if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw "Executable not found: $Executable"
}

if (-not (Test-Path -LiteralPath $SettingsPath -PathType Leaf)) {
    throw "Settings file not found: $SettingsPath"
}

if (Get-Process -Name 'CodexUsageWidget' -ErrorAction SilentlyContinue) {
    throw 'Close CodexUsageWidget before capturing themes.'
}

[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$originalSettings = [IO.File]::ReadAllBytes($SettingsPath)
$results = New-Object System.Collections.ArrayList
$completed = $false

try {
    foreach ($mode in @('System', 'Light', 'Dark')) {
        $settings = Get-Content -LiteralPath $SettingsPath -Raw |
            ConvertFrom-Json
        $settings.themeMode = $mode
        $settings |
            Add-Member `
                -NotePropertyName 'collapsedMode' `
                -NotePropertyValue $CollapsedMode `
                -Force
        if ($GlassTransparencyPercent -ge 0) {
            $settings |
                Add-Member `
                    -NotePropertyName 'glassTransparencyPercent' `
                    -NotePropertyValue $GlassTransparencyPercent `
                    -Force
        }
        $settings.isPinned = $false
        $settings.autoCollapse = $true
        $settings |
            ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath $SettingsPath -Encoding utf8

        $modeName = $mode.ToLowerInvariant()
        $collapsedScreenshot =
            Join-Path $OutputDirectory "theme-$modeName-collapsed.png"
        $collapsedProcess = $null
        try {
            $collapsedProcess = Start-Process -FilePath $Executable -PassThru
            $collapsedWindow =
                Wait-ForCollapsedWindow -Process $collapsedProcess
            Start-Sleep -Milliseconds 500
            $collapsedWindow =
                Wait-ForCollapsedWindow -Process $collapsedProcess
            Save-PrintedWindow `
                -Window $collapsedWindow `
                -Path $collapsedScreenshot
        }
        finally {
            if ($null -ne $collapsedProcess -and
                -not $collapsedProcess.HasExited) {
                $collapsedProcess.Kill()
                $collapsedProcess.WaitForExit()
            }
        }

        $settings = Get-Content -LiteralPath $SettingsPath -Raw |
            ConvertFrom-Json
        $settings.isPinned = $true
        $settings |
            ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath $SettingsPath -Encoding utf8

        $process = $null
        try {
            $process = Start-Process -FilePath $Executable -PassThru
            $window = Wait-ForExpandedWindow -Process $process
            Invoke-AutomationButton `
                -WindowHandle $window.Handle `
                -AutomationId 'RefreshButton'
            Start-Sleep -Seconds 3
            $window = Wait-ForExpandedWindow -Process $process

            $themeScreenshot = Join-Path $OutputDirectory "theme-$modeName.png"
            Save-PrintedWindow -Window $window -Path $themeScreenshot

            $topLevelHandlesBefore = @(
                Get-VisibleTopLevelWindowHandles -Process $process)
            Invoke-AutomationButton `
                -WindowHandle $window.Handle `
                -AutomationId 'WeeklyQuotaTrendButton'
            $overlay = Wait-ForVisibleAutomationElement `
                -Process $process `
                -WindowHandle $window.Handle `
                -AutomationId 'WeeklyQuotaOverlay'
            $dayElements = @(
                Wait-ForAutomationIdPrefixCount `
                    -Process $process `
                    -WindowHandle $window.Handle `
                    -Prefix 'WeeklyQuotaDay_' `
                    -ExpectedCount 7 `
                    -TimeoutMilliseconds 30000
            )
            Start-Sleep -Milliseconds 250

            $overlayWindow = Wait-ForExpandedWindow `
                -Process $process `
                -ExpectedHandle $window.Handle `
                -TimeoutMilliseconds 2000
            $topLevelHandlesAfter = @(
                Get-VisibleTopLevelWindowHandles -Process $process)
            $topLevelDifference = @(
                Compare-Object `
                    -ReferenceObject $topLevelHandlesBefore `
                    -DifferenceObject $topLevelHandlesAfter)
            if ($topLevelDifference.Count -ne 0) {
                throw (
                    'Opening WeeklyQuotaOverlay changed the visible top-level ' +
                    'HWND set.')
            }

            $overlayScreenshot =
                Join-Path $OutputDirectory "theme-$modeName-overlay.png"
            Save-PrintedWindow `
                -Window $overlayWindow `
                -Path $overlayScreenshot
            $results.Add([ordered]@{
                    mode = $mode
                    result = 'PASS'
                    collapsedMode = $CollapsedMode
                    logicalWidth = $overlayWindow.LogicalWidth
                    logicalHeight = $overlayWindow.LogicalHeight
                    dpi = $overlayWindow.Dpi
                    collapsedLogicalWidth =
                        $collapsedWindow.LogicalWidth
                    collapsedLogicalHeight =
                        $collapsedWindow.LogicalHeight
                    collapsedDpi = $collapsedWindow.Dpi
                    collapsedScreenshot = $collapsedScreenshot
                    collapsedImageBytes = (
                        Get-Item -LiteralPath $collapsedScreenshot).Length
                    screenshot = $themeScreenshot
                    imageBytes = (Get-Item -LiteralPath $themeScreenshot).Length
                    overlayScreenshot = $overlayScreenshot
                    overlayImageBytes = (
                        Get-Item -LiteralPath $overlayScreenshot).Length
                    overlayTriggerAutomationId = 'WeeklyQuotaTrendButton'
                    overlayAutomationId = $overlay.Current.AutomationId
                    overlayVisible = -not $overlay.Current.IsOffscreen
                    weeklyQuotaDayCount = $dayElements.Count
                    weeklyQuotaDays = @(
                        $dayElements |
                            Sort-Object { $_.Current.AutomationId } |
                            ForEach-Object {
                                [ordered]@{
                                    automationId =
                                        $_.Current.AutomationId
                                    name = $_.Current.Name
                                }
                            }
                    )
                    topLevelHwndCountBefore = $topLevelHandlesBefore.Count
                    topLevelHwndCountAfter = $topLevelHandlesAfter.Count
                    topLevelHwndUnchanged = $true
                }) | Out-Null
        }
        finally {
            if ($null -ne $process -and -not $process.HasExited) {
                $process.Kill()
                $process.WaitForExit()
            }
        }
    }

    $completed = $true
}
finally {
    [IO.File]::WriteAllBytes($SettingsPath, $originalSettings)
}

if ($completed) {
    $personalizePath =
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
    $appsUseLightTheme = $null
    try {
        $appsUseLightTheme = (
            Get-ItemProperty `
                -LiteralPath $personalizePath `
                -Name AppsUseLightTheme `
                -ErrorAction Stop).AppsUseLightTheme
    }
    catch {
    }

    [ordered]@{
        capturedAt = [DateTimeOffset]::Now.ToString('O')
        executable = $Executable
        sha256 = (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
        executableBytes = (Get-Item -LiteralPath $Executable).Length
        appsUseLightTheme = $appsUseLightTheme
        baselineCollapsedMode = $CollapsedMode
        glassTransparencyPercent = $GlassTransparencyPercent
        originalSettingsRestored = $true
        themes = @($results)
    } |
        ConvertTo-Json -Depth 8 |
        Set-Content `
            -LiteralPath (Join-Path $OutputDirectory 'theme-audit.json') `
            -Encoding utf8
}
