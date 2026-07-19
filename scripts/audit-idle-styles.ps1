[CmdletBinding()]
param(
    [string]$Executable,
    [string]$SettingsPath = (
        Join-Path $env:LOCALAPPDATA 'CodexUsageWidget\settings.json'),
    [string]$OutputDirectory,
    [string]$OutputPath,
    [ValidateRange(1000, 30000)]
    [int]$StateTimeoutMilliseconds = 8000
)

# Keep this file ASCII-only for Windows PowerShell 5.1 compatibility.
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Executable)) {
    $Executable = Join-Path $projectRoot 'dist\CodexUsageWidget.exe'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot 'docs\screenshots'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $projectRoot 'docs\idle-style-audit.json'
}

$Executable = [IO.Path]::GetFullPath($Executable)
$SettingsPath = [IO.Path]::GetFullPath($SettingsPath)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

if (-not ('CodexIdleStyleAuditNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class CodexIdleStyleAuditNative
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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "SetProcessDpiAwarenessContext")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeSetProcessDpiAwarenessContext(
        IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(
        uint flags,
        uint dx,
        uint dy,
        uint data,
        UIntPtr extraInfo);

    public static IntPtr FindVisibleWindow(int targetProcessId)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows(delegate(IntPtr windowHandle, IntPtr state)
        {
            uint processId;
            GetWindowThreadProcessId(windowHandle, out processId);
            if (processId == (uint)targetProcessId &&
                IsWindowVisible(windowHandle))
            {
                result = windowHandle;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static uint GetDpi(IntPtr windowHandle)
    {
        try
        {
            uint dpi = GetDpiForWindow(windowHandle);
            return dpi == 0 ? 96u : dpi;
        }
        catch (EntryPointNotFoundException)
        {
            return 96u;
        }
    }

    public static void EnablePerMonitorDpiAwareness()
    {
        try
        {
            // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
            NativeSetProcessDpiAwarenessContext(new IntPtr(-4));
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    public static void NotifyMouseMove()
    {
        mouse_event(0x0001u, 0u, 0u, 0u, UIntPtr.Zero);
    }
}
'@
}

[CodexIdleStyleAuditNative]::EnablePerMonitorDpiAwareness()

function Get-WindowMeasurement {
    param([Parameter(Mandatory = $true)][IntPtr]$WindowHandle)

    $rectangle = New-Object CodexIdleStyleAuditNative+Rect
    if (-not [CodexIdleStyleAuditNative]::GetWindowRect(
            $WindowHandle,
            [ref]$rectangle)) {
        throw 'GetWindowRect failed.'
    }

    $dpi = [CodexIdleStyleAuditNative]::GetDpi($WindowHandle)
    $scale = $dpi / 96.0
    return [pscustomobject]@{
        Left = $rectangle.Left
        Top = $rectangle.Top
        Right = $rectangle.Right
        Bottom = $rectangle.Bottom
        PhysicalWidth = $rectangle.Right - $rectangle.Left
        PhysicalHeight = $rectangle.Bottom - $rectangle.Top
        LogicalWidth = ($rectangle.Right - $rectangle.Left) / $scale
        LogicalHeight = ($rectangle.Bottom - $rectangle.Top) / $scale
        Dpi = $dpi
    }
}

function Test-LogicalSize {
    param(
        $Measurement,
        [double]$Width,
        [double]$Height,
        [double]$Tolerance = 4)

    return (
        [Math]::Abs($Measurement.LogicalWidth - $Width) -le $Tolerance -and
        [Math]::Abs($Measurement.LogicalHeight - $Height) -le $Tolerance)
}

function Wait-ForWindow {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.Process]$Process,
        [int]$TimeoutMilliseconds)

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "The widget exited with code $($Process.ExitCode)."
        }

        $handle = [CodexIdleStyleAuditNative]::FindVisibleWindow($Process.Id)
        if ($handle -ne [IntPtr]::Zero) {
            return $handle
        }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)

    throw 'The widget main window did not appear.'
}

function Wait-ForLogicalSize {
    param(
        [Parameter(Mandatory = $true)][IntPtr]$WindowHandle,
        [double]$Width,
        [double]$Height,
        [int]$TimeoutMilliseconds,
        [string]$Phase)

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    $measurement = $null
    do {
        $measurement = Get-WindowMeasurement -WindowHandle $WindowHandle
        if (Test-LogicalSize $measurement $Width $Height) {
            return $measurement
        }
        Start-Sleep -Milliseconds 20
    } while ([DateTime]::UtcNow -lt $deadline)

    $actual = if ($null -eq $measurement) {
        'unknown'
    }
    else {
        '{0:N1}x{1:N1} at {2} DPI' -f `
            $measurement.LogicalWidth, `
            $measurement.LogicalHeight, `
            $measurement.Dpi
    }
    throw (
        "The widget did not reach ${Width}x${Height} during $Phase; " +
        "last observed size was $actual.")
}

function Set-PointerInside {
    param($Measurement)

    $targetX = [int](($Measurement.Left + $Measurement.Right) / 2)
    $targetY = [int](($Measurement.Top + $Measurement.Bottom) / 2)
    $start = [System.Windows.Forms.Cursor]::Position
    for ($step = 1; $step -le 12; $step++) {
        $progress = $step / 12.0
        $x = [int][Math]::Round(
            $start.X + (($targetX - $start.X) * $progress))
        $y = [int][Math]::Round(
            $start.Y + (($targetY - $start.Y) * $progress))
        [CodexIdleStyleAuditNative]::SetCursorPos($x, $y) | Out-Null
        [CodexIdleStyleAuditNative]::NotifyMouseMove()
        Start-Sleep -Milliseconds 12
    }
}

function Set-PointerOutside {
    param($Measurement)

    $screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $points = @(
        [System.Drawing.Point]::new($screen.Left + 4, $screen.Top + 4),
        [System.Drawing.Point]::new($screen.Right - 5, $screen.Top + 4),
        [System.Drawing.Point]::new($screen.Left + 4, $screen.Bottom - 5),
        [System.Drawing.Point]::new($screen.Right - 5, $screen.Bottom - 5)
    )
    foreach ($point in $points) {
        if ($point.X -lt ($Measurement.Left - 20) -or
            $point.X -ge ($Measurement.Right + 20) -or
            $point.Y -lt ($Measurement.Top - 20) -or
            $point.Y -ge ($Measurement.Bottom + 20)) {
            $start = [System.Windows.Forms.Cursor]::Position
            for ($step = 1; $step -le 12; $step++) {
                $progress = $step / 12.0
                $x = [int][Math]::Round(
                    $start.X + (($point.X - $start.X) * $progress))
                $y = [int][Math]::Round(
                    $start.Y + (($point.Y - $start.Y) * $progress))
                [CodexIdleStyleAuditNative]::SetCursorPos($x, $y) |
                    Out-Null
                [CodexIdleStyleAuditNative]::NotifyMouseMove()
                Start-Sleep -Milliseconds 15
            }
            return
        }
    }

    throw 'Could not find a pointer position outside the widget.'
}

function Save-WindowScreenshot {
    param(
        $Measurement,
        [Parameter(Mandatory = $true)][string]$Path)

    $bitmap = New-Object System.Drawing.Bitmap `
        $Measurement.PhysicalWidth, `
        $Measurement.PhysicalHeight
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen(
                $Measurement.Left,
                $Measurement.Top,
                0,
                0,
                [System.Drawing.Size]::new(
                    $Measurement.PhysicalWidth,
                    $Measurement.PhysicalHeight))
        }
        finally {
            $graphics.Dispose()
        }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}

function Set-IdleStyleSettings {
    param([ValidateSet('Circle', 'Capsule')][string]$Mode)

    $settings =
        Get-Content -LiteralPath $SettingsPath -Raw |
        ConvertFrom-Json
    $settings |
        Add-Member `
            -NotePropertyName 'collapsedMode' `
            -NotePropertyValue $Mode `
            -Force
    $settings |
        Add-Member `
            -NotePropertyName 'isPinned' `
            -NotePropertyValue $false `
            -Force
    $settings |
        Add-Member `
            -NotePropertyName 'autoCollapse' `
            -NotePropertyValue $true `
            -Force
    $settings |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $SettingsPath -Encoding utf8
}

if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw "Executable not found: $Executable"
}
if (-not (Test-Path -LiteralPath $SettingsPath -PathType Leaf)) {
    throw "Settings file not found: $SettingsPath"
}
if (Get-Process -Name 'CodexUsageWidget' -ErrorAction SilentlyContinue) {
    throw 'Close CodexUsageWidget before auditing idle styles.'
}

[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
[IO.Directory]::CreateDirectory(
    (Split-Path -Parent $OutputPath)) | Out-Null
$originalSettings = [IO.File]::ReadAllBytes($SettingsPath)
$results = New-Object System.Collections.ArrayList
$process = $null
$fatalError = $null

try {
    foreach ($style in @(
            [ordered]@{ Mode = 'Circle'; Width = 80 },
            [ordered]@{ Mode = 'Capsule'; Width = 208 })) {
        $process = $null
        try {
            Set-IdleStyleSettings -Mode $style.Mode
            [System.Windows.Forms.Cursor]::Position =
                [System.Drawing.Point]::new(12, 12)
            $process = Start-Process -FilePath $Executable -PassThru
            $handle = Wait-ForWindow `
                -Process $process `
                -TimeoutMilliseconds 15000
            $startupMeasurement =
                Get-WindowMeasurement -WindowHandle $handle
            Set-PointerOutside -Measurement $startupMeasurement
            $collapsed = Wait-ForLogicalSize `
                -WindowHandle $handle `
                -Width $style.Width `
                -Height 80 `
                -TimeoutMilliseconds $StateTimeoutMilliseconds `
                -Phase "$($style.Mode) startup"
            Start-Sleep -Milliseconds 400
            $collapsed = Wait-ForLogicalSize `
                -WindowHandle $handle `
                -Width $style.Width `
                -Height 80 `
                -TimeoutMilliseconds $StateTimeoutMilliseconds `
                -Phase "$($style.Mode) settled startup"

            $modeName = $style.Mode.ToLowerInvariant()
            $collapsedPath = Join-Path `
                $OutputDirectory `
                "idle-$modeName-collapsed.png"
            Save-WindowScreenshot `
                -Measurement $collapsed `
                -Path $collapsedPath

            $observations = New-Object System.Collections.ArrayList
            $intermediate = New-Object System.Collections.ArrayList
            $seen = @{}
            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            Set-PointerInside -Measurement $collapsed
            $expanded = $null
            while ($stopwatch.ElapsedMilliseconds -lt
                $StateTimeoutMilliseconds) {
                $sample = Get-WindowMeasurement -WindowHandle $handle
                $sampleWidth = [Math]::Round($sample.LogicalWidth, 1)
                $sampleHeight = [Math]::Round($sample.LogicalHeight, 1)
                $key = "$sampleWidth`x$sampleHeight"
                if (-not $seen.ContainsKey($key)) {
                    $seen[$key] = $true
                    $observations.Add([ordered]@{
                            elapsedMilliseconds =
                                $stopwatch.ElapsedMilliseconds
                            logicalWidth = $sampleWidth
                            logicalHeight = $sampleHeight
                        }) | Out-Null
                }

                $isCollapsed =
                    Test-LogicalSize $sample $style.Width 80
                $isExpanded = Test-LogicalSize $sample 420 540
                if (-not $isCollapsed -and -not $isExpanded) {
                    $intermediate.Add([ordered]@{
                            elapsedMilliseconds =
                                $stopwatch.ElapsedMilliseconds
                            logicalWidth = $sampleWidth
                            logicalHeight = $sampleHeight
                        }) | Out-Null
                }
                if ($isExpanded) {
                    $expanded = $sample
                    break
                }
                Start-Sleep -Milliseconds 10
            }
            $stopwatch.Stop()

            if ($null -eq $expanded) {
                throw 'Hover did not expand directly to 420x540.'
            }
            if ($intermediate.Count -gt 0) {
                throw (
                    'An intermediate top-level window size was observed: ' +
                    (($intermediate |
                        ForEach-Object {
                            "$($_.logicalWidth)x$($_.logicalHeight)"
                        }) -join ', '))
            }

            Start-Sleep -Milliseconds 250
            $expanded = Wait-ForLogicalSize `
                -WindowHandle $handle `
                -Width 420 `
                -Height 540 `
                -TimeoutMilliseconds $StateTimeoutMilliseconds `
                -Phase "$($style.Mode) expansion"
            $expandedPath = Join-Path `
                $OutputDirectory `
                "idle-$modeName-expanded.png"
            Save-WindowScreenshot `
                -Measurement $expanded `
                -Path $expandedPath

            Set-PointerInside -Measurement $expanded
            Start-Sleep -Milliseconds 100
            Set-PointerOutside -Measurement $expanded
            $recollapsed = Wait-ForLogicalSize `
                -WindowHandle $handle `
                -Width $style.Width `
                -Height 80 `
                -TimeoutMilliseconds $StateTimeoutMilliseconds `
                -Phase "$($style.Mode) recollapse"

            $results.Add([ordered]@{
                    mode = $style.Mode
                    status = 'PASS'
                    expectedCollapsedLogicalWidth = $style.Width
                    expectedCollapsedLogicalHeight = 80
                    collapsedLogicalWidth =
                        [Math]::Round($collapsed.LogicalWidth, 1)
                    collapsedLogicalHeight =
                        [Math]::Round($collapsed.LogicalHeight, 1)
                    expandedLogicalWidth =
                        [Math]::Round($expanded.LogicalWidth, 1)
                    expandedLogicalHeight =
                        [Math]::Round($expanded.LogicalHeight, 1)
                    recollapsedLogicalWidth =
                        [Math]::Round($recollapsed.LogicalWidth, 1)
                    recollapsedLogicalHeight =
                        [Math]::Round($recollapsed.LogicalHeight, 1)
                    dpi = $collapsed.Dpi
                    hoverToExpandedMilliseconds =
                        $stopwatch.ElapsedMilliseconds
                    observedTopLevelSizes = @($observations)
                    intermediateTopLevelSizes = @($intermediate)
                    directExpansion = $true
                    collapsedScreenshot = $collapsedPath
                    expandedScreenshot = $expandedPath
                }) | Out-Null
        }
        catch {
            $fatalError = $_
            $results.Add([ordered]@{
                    mode = $style.Mode
                    status = 'FAIL'
                    error = $_.Exception.Message
                    observedTopLevelSizes = @($observations)
                    cursorPosition = [ordered]@{
                        x = [System.Windows.Forms.Cursor]::Position.X
                        y = [System.Windows.Forms.Cursor]::Position.Y
                    }
                }) | Out-Null
            break
        }
        finally {
            if ($null -ne $process) {
                try {
                    $process.Refresh()
                    if (-not $process.HasExited) {
                        Stop-Process -Id $process.Id -Force
                        $process.WaitForExit(5000) | Out-Null
                    }
                }
                catch {
                }
            }
        }
    }
}
finally {
    [IO.File]::WriteAllBytes($SettingsPath, $originalSettings)
}

$passed = @($results | Where-Object { $_.status -eq 'PASS' }).Count
$failed = @($results | Where-Object { $_.status -eq 'FAIL' }).Count
$report = [ordered]@{
    capturedAt = [DateTimeOffset]::Now.ToString('O')
    status = $(if ($failed -eq 0 -and $passed -eq 2) { 'PASS' } else { 'FAIL' })
    executable = $Executable
    executableSha256 = (
        Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
    executableBytes = (Get-Item -LiteralPath $Executable).Length
    expectedExpandedLogicalWidth = 420
    expectedExpandedLogicalHeight = 540
    sampleIntervalMilliseconds = 10
    counts = [ordered]@{
        pass = $passed
        fail = $failed
    }
    styles = @($results)
    originalSettingsRestored = $true
}
$report |
    ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $OutputPath -Encoding utf8

if ($null -ne $fatalError) {
    throw (
        "Idle-style audit failed; report: $OutputPath. " +
        $fatalError.Exception.Message)
}

Write-Host "Idle-style UI audit passed: $OutputPath"
