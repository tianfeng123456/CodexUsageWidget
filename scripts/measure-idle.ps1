param(
    [string]$Executable = (
        Join-Path $PSScriptRoot '..\dist\CodexUsageWidget.exe'),
    [ValidateRange(65, 3600)]
    [int]$DurationSeconds = 65,
    [ValidateRange(5, 300)]
    [int]$WarmupSeconds = 12,
    [ValidateRange(3, 60)]
    [int]$IndexStabilitySeconds = 5,
    [ValidateSet('Glow', 'Circle', 'Capsule')]
    [string]$CollapsedMode = 'Circle',
    [switch]$PrimeWeeklyQuotaOverlay,
    [switch]$PrimeFromPinnedStartup,
    [ValidateRange(1000, 30000)]
    [int]$AutomationTimeoutMilliseconds = 8000,
    [string]$OutputPath = (
        Join-Path $PSScriptRoot '..\docs\idle-performance.json')
)

$ErrorActionPreference = 'Stop'
$Executable = [IO.Path]::GetFullPath($Executable)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class CodexUsageIdleNative
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
    public static extern bool GetWindowRect(IntPtr windowHandle, out Rect rectangle);

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
    private static extern void mouse_event(
        uint flags,
        uint dx,
        uint dy,
        uint data,
        UIntPtr extraInfo);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint NativeGetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "SetProcessDpiAwarenessContext")]
    private static extern bool NativeSetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetProcessDPIAware")]
    private static extern bool NativeSetProcessDpiAware();

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

    public static void EnablePerMonitorDpiAwareness()
    {
        try
        {
            // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
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

    public static void NotifyMouseMove()
    {
        const uint MouseEventMove = 0x0001;
        mouse_event(MouseEventMove, 0, 0, 0, UIntPtr.Zero);
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
}
'@

[CodexUsageIdleNative]::EnablePerMonitorDpiAwareness()

function Get-WindowMeasurement {
    param(
        [Parameter(Mandatory = $true)]
        [IntPtr]$WindowHandle
    )

    $nativeBounds = New-Object CodexUsageIdleNative+Rect
    if (-not [CodexUsageIdleNative]::GetWindowRect(
            $WindowHandle,
            [ref]$nativeBounds)) {
        throw 'The widget main-window bounds could not be read.'
    }

    $dpi = [double][CodexUsageIdleNative]::GetDpiForWindow($WindowHandle)
    $scale = $dpi / 96.0
    return [pscustomobject]@{
        WindowHandle = $WindowHandle
        Left = $nativeBounds.Left
        Top = $nativeBounds.Top
        Right = $nativeBounds.Right
        Bottom = $nativeBounds.Bottom
        Dpi = [int]$dpi
        LogicalWidth = ($nativeBounds.Right - $nativeBounds.Left) / $scale
        LogicalHeight = ($nativeBounds.Bottom - $nativeBounds.Top) / $scale
    }
}

function Test-WindowLogicalSize {
    param(
        [Parameter(Mandatory = $true)]
        $Measurement,
        [Parameter(Mandatory = $true)]
        [double]$ExpectedWidth,
        [Parameter(Mandatory = $true)]
        [double]$ExpectedHeight,
        [double]$Tolerance = 1.5
    )

    return (
        [Math]::Abs($Measurement.LogicalWidth - $ExpectedWidth) -le
            $Tolerance -and
        [Math]::Abs($Measurement.LogicalHeight - $ExpectedHeight) -le
            $Tolerance)
}

function Assert-WindowLogicalSize {
    param(
        [Parameter(Mandatory = $true)]
        $Measurement,
        [Parameter(Mandatory = $true)]
        [double]$ExpectedWidth,
        [Parameter(Mandatory = $true)]
        [double]$ExpectedHeight,
        [Parameter(Mandatory = $true)]
        [string]$Phase
    )

    if (-not (Test-WindowLogicalSize `
                -Measurement $Measurement `
                -ExpectedWidth $ExpectedWidth `
                -ExpectedHeight $ExpectedHeight)) {
        $expectedDescription = 'at the expected size'
        if ($ExpectedWidth -eq 80 -and $ExpectedHeight -eq 80) {
            $expectedDescription = 'collapsed'
        }

        throw (
            'The widget is not {0} during {1}: {2:N1}x{3:N1} logical px.' -f
            $expectedDescription,
            $Phase,
            $Measurement.LogicalWidth,
            $Measurement.LogicalHeight)
    }
}

function Wait-ForWindowLogicalSize {
    param(
        [Parameter(Mandatory = $true)]
        [IntPtr]$WindowHandle,
        [Parameter(Mandatory = $true)]
        [double]$ExpectedWidth,
        [Parameter(Mandatory = $true)]
        [double]$ExpectedHeight,
        [Parameter(Mandatory = $true)]
        [int]$TimeoutMilliseconds,
        [Parameter(Mandatory = $true)]
        [string]$Phase
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $measurement = Get-WindowMeasurement -WindowHandle $WindowHandle
        if (Test-WindowLogicalSize `
                -Measurement $measurement `
                -ExpectedWidth $ExpectedWidth `
                -ExpectedHeight $ExpectedHeight) {
            return $measurement
        }

        Start-Sleep -Milliseconds 50
    }
    while ($stopwatch.ElapsedMilliseconds -lt $TimeoutMilliseconds)

    throw ((
        'Timed out waiting for the widget during {0}; expected ' +
        '{1:N1}x{2:N1}, last observed {3:N1}x{4:N1} logical px.') -f
        $Phase,
        $ExpectedWidth,
        $ExpectedHeight,
        $measurement.LogicalWidth,
        $measurement.LogicalHeight)
}

function Find-AutomationElementById {
    param(
        [Parameter(Mandatory = $true)]
        [IntPtr]$WindowHandle,
        [Parameter(Mandatory = $true)]
        [string]$AutomationId
    )

    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle(
            $WindowHandle)
        $condition =
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::
                    AutomationIdProperty,
                $AutomationId)
        return $root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $condition)
    }
    catch [System.Windows.Automation.ElementNotAvailableException] {
        return $null
    }
}

function Test-AutomationElementVisible {
    param($Element)

    if ($null -eq $Element) {
        return $false
    }

    try {
        return -not $Element.Current.IsOffscreen
    }
    catch [System.Windows.Automation.ElementNotAvailableException] {
        return $false
    }
}

function Wait-ForVisibleAutomationElement {
    param(
        [Parameter(Mandatory = $true)]
        [IntPtr]$WindowHandle,
        [Parameter(Mandatory = $true)]
        [string]$AutomationId,
        [Parameter(Mandatory = $true)]
        [int]$TimeoutMilliseconds
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $element = Find-AutomationElementById `
            -WindowHandle $WindowHandle `
            -AutomationId $AutomationId
        if (Test-AutomationElementVisible -Element $element) {
            return $element
        }

        Start-Sleep -Milliseconds 50
    }
    while ($stopwatch.ElapsedMilliseconds -lt $TimeoutMilliseconds)

    throw (
        "Timed out waiting for UI Automation element '$AutomationId' " +
        'to become visible.')
}

function Wait-ForHiddenAutomationElement {
    param(
        [Parameter(Mandatory = $true)]
        [IntPtr]$WindowHandle,
        [Parameter(Mandatory = $true)]
        [string]$AutomationId,
        [Parameter(Mandatory = $true)]
        [int]$TimeoutMilliseconds
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $element = Find-AutomationElementById `
            -WindowHandle $WindowHandle `
            -AutomationId $AutomationId
        if (-not (Test-AutomationElementVisible -Element $element)) {
            return
        }

        Start-Sleep -Milliseconds 50
    }
    while ($stopwatch.ElapsedMilliseconds -lt $TimeoutMilliseconds)

    throw (
        "Timed out waiting for UI Automation element '$AutomationId' " +
        'to become hidden.')
}

function Invoke-AutomationElement {
    param(
        [Parameter(Mandatory = $true)]
        $Element,
        [Parameter(Mandatory = $true)]
        [string]$AutomationId
    )

    try {
        $invokePattern = $Element.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern)
        $invokePattern.Invoke()
    }
    catch {
        throw (
            "Could not invoke UI Automation element '$AutomationId': " +
            $_.Exception.Message)
    }
}

function Move-CursorOutsideWindow {
    param(
        [Parameter(Mandatory = $true)]
        $Measurement
    )

    $virtualScreen = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $candidates = @(
        [System.Drawing.Point]::new(
            $virtualScreen.Left + 4,
            $virtualScreen.Top + 4),
        [System.Drawing.Point]::new(
            $virtualScreen.Right - 5,
            $virtualScreen.Top + 4),
        [System.Drawing.Point]::new(
            $virtualScreen.Left + 4,
            $virtualScreen.Bottom - 5),
        [System.Drawing.Point]::new(
            $virtualScreen.Right - 5,
            $virtualScreen.Bottom - 5)
    )

    foreach ($candidate in $candidates) {
        if ($candidate.X -lt $Measurement.Left -or
            $candidate.X -ge $Measurement.Right -or
            $candidate.Y -lt $Measurement.Top -or
            $candidate.Y -ge $Measurement.Bottom) {
            [System.Windows.Forms.Cursor]::Position = $candidate
            [CodexUsageIdleNative]::NotifyMouseMove()
            return
        }
    }

    throw 'Could not find a cursor position outside the widget window.'
}

function Invoke-WeeklyQuotaOverlayPrime {
    param(
        [Parameter(Mandatory = $true)]
        [IntPtr]$WindowHandle,
        [Parameter(Mandatory = $true)]
        [int]$TimeoutMilliseconds,
        [Parameter(Mandatory = $true)]
        [double]$CollapsedWidth,
        [Parameter(Mandatory = $true)]
        [double]$CollapsedHeight,
        [switch]$StartPinned
    )

    if ($StartPinned) {
        $expanded = Wait-ForWindowLogicalSize `
            -WindowHandle $WindowHandle `
            -ExpectedWidth 420 `
            -ExpectedHeight 540 `
            -TimeoutMilliseconds $TimeoutMilliseconds `
            -Phase 'pinned weekly-quota priming start'
    }
    else {
        $collapsed = Get-WindowMeasurement -WindowHandle $WindowHandle
        Assert-WindowLogicalSize `
            -Measurement $collapsed `
            -ExpectedWidth $CollapsedWidth `
            -ExpectedHeight $CollapsedHeight `
            -Phase 'weekly-quota priming start'

        $anchorCenter = [System.Drawing.Point]::new(
            [int](($collapsed.Left + $collapsed.Right) / 2),
            [int](($collapsed.Top + $collapsed.Bottom) / 2))
        [System.Windows.Forms.Cursor]::Position = $anchorCenter
        [CodexUsageIdleNative]::NotifyMouseMove()
        $expanded = Wait-ForWindowLogicalSize `
            -WindowHandle $WindowHandle `
            -ExpectedWidth 420 `
            -ExpectedHeight 540 `
            -TimeoutMilliseconds $TimeoutMilliseconds `
            -Phase 'weekly-quota priming expansion'

        # The collapsed anchor center remains inside the panel whether it
        # expands left/right or up/down. Re-sample that point after expansion
        # so WPF has a real mouse-over state before UI Automation traverses the
        # expanded tree, including when the panel relocates at a screen edge.
        [System.Windows.Forms.Cursor]::Position = $anchorCenter
        [CodexUsageIdleNative]::NotifyMouseMove()
        Start-Sleep -Milliseconds 120
        $expanded = Wait-ForWindowLogicalSize `
            -WindowHandle $WindowHandle `
            -ExpectedWidth 420 `
            -ExpectedHeight 540 `
            -TimeoutMilliseconds $TimeoutMilliseconds `
            -Phase 'settled weekly-quota priming expansion'
    }

    $trendButton = Wait-ForVisibleAutomationElement `
        -WindowHandle $WindowHandle `
        -AutomationId 'WeeklyQuotaTrendButton' `
        -TimeoutMilliseconds $TimeoutMilliseconds
    Invoke-AutomationElement `
        -Element $trendButton `
        -AutomationId 'WeeklyQuotaTrendButton'
    $null = Wait-ForVisibleAutomationElement `
        -WindowHandle $WindowHandle `
        -AutomationId 'WeeklyQuotaOverlay' `
        -TimeoutMilliseconds $TimeoutMilliseconds
    $openedAt = [DateTimeOffset]::Now

    $closeButton = Wait-ForVisibleAutomationElement `
        -WindowHandle $WindowHandle `
        -AutomationId 'WeeklyQuotaCloseButton' `
        -TimeoutMilliseconds $TimeoutMilliseconds
    Invoke-AutomationElement `
        -Element $closeButton `
        -AutomationId 'WeeklyQuotaCloseButton'
    Wait-ForHiddenAutomationElement `
        -WindowHandle $WindowHandle `
        -AutomationId 'WeeklyQuotaOverlay' `
        -TimeoutMilliseconds $TimeoutMilliseconds
    $closedAt = [DateTimeOffset]::Now

    $pinButton = Wait-ForVisibleAutomationElement `
        -WindowHandle $WindowHandle `
        -AutomationId 'PinButton' `
        -TimeoutMilliseconds $TimeoutMilliseconds
    if (-not $StartPinned) {
        # Give the test a deterministic state transition after the overlay:
        # pin once, move the pointer out, then explicitly unpin. This avoids
        # depending on whether an Automation Invoke generated a routed
        # MouseLeave notification on a particular Windows build.
        Invoke-AutomationElement `
            -Element $pinButton `
            -AutomationId 'PinButton'
    }

    Move-CursorOutsideWindow `
        -Measurement (Get-WindowMeasurement -WindowHandle $WindowHandle)
    Invoke-AutomationElement `
        -Element $pinButton `
        -AutomationId 'PinButton'
    $recollapsed = Wait-ForWindowLogicalSize `
        -WindowHandle $WindowHandle `
        -ExpectedWidth $CollapsedWidth `
        -ExpectedHeight $CollapsedHeight `
        -TimeoutMilliseconds $TimeoutMilliseconds `
        -Phase 'weekly-quota priming collapse'

    return [ordered]@{
        requested = $true
        performed = $true
        startMode = $(if ($StartPinned) { 'Pinned' } else { 'Hover' })
        trendButtonAutomationId = 'WeeklyQuotaTrendButton'
        overlayAutomationId = 'WeeklyQuotaOverlay'
        closeButtonAutomationId = 'WeeklyQuotaCloseButton'
        openedAt = $openedAt.ToString('O')
        closedAt = $closedAt.ToString('O')
        expandedLogicalWidth = [Math]::Round(
            $expanded.LogicalWidth,
            1)
        expandedLogicalHeight = [Math]::Round(
            $expanded.LogicalHeight,
            1)
        recollapsedLogicalWidth = [Math]::Round(
            $recollapsed.LogicalWidth,
            1)
        recollapsedLogicalHeight = [Math]::Round(
            $recollapsed.LogicalHeight,
            1)
    }
}

$processName = [IO.Path]::GetFileNameWithoutExtension($Executable)
$existing = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
if ($existing.Count -gt 0) {
    throw "Close the existing $processName process before measuring."
}

function Get-DatabaseSnapshot {
    $dataDirectory = Join-Path $env:LOCALAPPDATA 'CodexUsageWidget'
    $files = @(
        Get-ChildItem `
            -LiteralPath $dataDirectory `
            -File `
            -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Name -like 'usage-index-*.db' -or
                $_.Name -like 'usage-index-*.db-wal' -or
                $_.Name -like 'usage-index-*.db-shm'
            }
    )

    $snapshot = [ordered]@{}
    foreach ($file in $files) {
        $snapshot[$file.FullName] = [ordered]@{
            length = $file.Length
            lastWriteTimeUtc = $file.LastWriteTimeUtc.ToString('O')
        }
    }

    return $snapshot
}

function Compare-DatabaseSnapshot {
    param($Before, $After)

    $paths = @($Before.Keys) + @($After.Keys) | Sort-Object -Unique
    $changes = New-Object System.Collections.ArrayList
    foreach ($path in $paths) {
        $beforeValue = $Before[$path]
        $afterValue = $After[$path]
        if ($null -eq $beforeValue -or
            $null -eq $afterValue -or
            $beforeValue.length -ne $afterValue.length -or
            $beforeValue.lastWriteTimeUtc -ne $afterValue.lastWriteTimeUtc) {
            $changes.Add([ordered]@{
                    path = $path
                    before = $beforeValue
                    after = $afterValue
                }) | Out-Null
        }
    }

    return @($changes)
}

$databaseBeforeLaunch = Get-DatabaseSnapshot
$process = $null
$settingsPath = Join-Path $env:LOCALAPPDATA 'CodexUsageWidget\settings.json'
$originalSettingsBytes = $null
$expectedCollapsedWidth = switch ($CollapsedMode) {
    'Glow' { 32 }
    'Capsule' { 208 }
    default { 80 }
}
$expectedCollapsedHeight = if ($CollapsedMode -eq 'Glow') { 32 } else { 80 }
try {
    if ($PrimeFromPinnedStartup -and -not $PrimeWeeklyQuotaOverlay) {
        throw (
            '-PrimeFromPinnedStartup requires -PrimeWeeklyQuotaOverlay.')
    }

    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
        throw "Settings file not found: $settingsPath"
    }

    $originalSettingsBytes = [IO.File]::ReadAllBytes($settingsPath)
    $settings = Get-Content -LiteralPath $settingsPath -Raw |
        ConvertFrom-Json
    # Normalize the idle surface and collapse behavior for a deterministic
    # measurement, then restore the exact original bytes in finally.
    $settings |
        Add-Member `
            -NotePropertyName 'collapsedMode' `
            -NotePropertyValue $CollapsedMode `
            -Force
    $settings.isPinned = [bool](
        $PrimeWeeklyQuotaOverlay -and $PrimeFromPinnedStartup)
    $settings.autoCollapse = $true
    $settingsJson = $settings | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText(
        $settingsPath,
        $settingsJson,
        [Text.UTF8Encoding]::new($false))

    [System.Windows.Forms.Cursor]::Position =
        [System.Drawing.Point]::new(12, 12)
    $process = Start-Process -FilePath $Executable -PassThru
    Start-Sleep -Seconds $WarmupSeconds
    $process.Refresh()
    if ($process.HasExited) {
        throw "The widget exited during warm-up with code $($process.ExitCode)."
    }

    $windowHandle = [CodexUsageIdleNative]::FindVisibleWindow($process.Id)
    if ($windowHandle -eq [IntPtr]::Zero) {
        throw 'The widget main window was not available after warm-up.'
    }

    if (-not $PrimeFromPinnedStartup) {
        $warmupMeasurement =
            Get-WindowMeasurement -WindowHandle $windowHandle
        Move-CursorOutsideWindow -Measurement $warmupMeasurement
        $initialMeasurement = Wait-ForWindowLogicalSize `
            -WindowHandle $windowHandle `
            -ExpectedWidth $expectedCollapsedWidth `
            -ExpectedHeight $expectedCollapsedHeight `
            -TimeoutMilliseconds $AutomationTimeoutMilliseconds `
            -Phase 'warm-up collapse'
        Assert-WindowLogicalSize `
            -Measurement $initialMeasurement `
            -ExpectedWidth $expectedCollapsedWidth `
            -ExpectedHeight $expectedCollapsedHeight `
            -Phase 'warm-up completion'
    }

    $weeklyQuotaPriming = [ordered]@{
        requested = [bool]$PrimeWeeklyQuotaOverlay
        performed = $false
    }
    if ($PrimeWeeklyQuotaOverlay) {
        $weeklyQuotaPriming = Invoke-WeeklyQuotaOverlayPrime `
            -WindowHandle $windowHandle `
            -TimeoutMilliseconds $AutomationTimeoutMilliseconds `
            -CollapsedWidth $expectedCollapsedWidth `
            -CollapsedHeight $expectedCollapsedHeight `
            -StartPinned:$PrimeFromPinnedStartup
    }

    $databaseAfterWarmup = Get-DatabaseSnapshot
    $warmupDatabaseChanges = @(
        Compare-DatabaseSnapshot `
            -Before $databaseBeforeLaunch `
            -After $databaseAfterWarmup)

    Start-Sleep -Seconds $IndexStabilitySeconds
    $process.Refresh()
    if ($process.HasExited) {
        throw 'The widget exited during the index-stability check.'
    }

    $databaseBefore = Get-DatabaseSnapshot
    $stabilityDatabaseChanges = @(
        Compare-DatabaseSnapshot `
            -Before $databaseAfterWarmup `
            -After $databaseBefore)
    if ($stabilityDatabaseChanges.Count -gt 0) {
        throw (
            'The SQLite index did not become stable after warm-up; wait for ' +
            'initial indexing to finish before measuring idle behavior.')
    }

    $measurementAtStart =
        Get-WindowMeasurement -WindowHandle $windowHandle
    Assert-WindowLogicalSize `
        -Measurement $measurementAtStart `
        -ExpectedWidth $expectedCollapsedWidth `
        -ExpectedHeight $expectedCollapsedHeight `
        -Phase 'idle observation start'

    $cpuBefore = $process.TotalProcessorTime
    $startedAt = [DateTimeOffset]::Now
    $privatePeak = [long]$process.PrivateMemorySize64
    $workingSetPeak = [long]$process.WorkingSet64

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $DurationSeconds) {
        $remaining = $DurationSeconds - $stopwatch.Elapsed.TotalSeconds
        Start-Sleep -Milliseconds (
            [int][Math]::Min(5000, [Math]::Max(100, $remaining * 1000)))
        $process.Refresh()
        if ($process.HasExited) {
            throw "The widget exited during the idle observation."
        }

        $privatePeak = [Math]::Max(
            $privatePeak,
            [long]$process.PrivateMemorySize64)
        $workingSetPeak = [Math]::Max(
            $workingSetPeak,
            [long]$process.WorkingSet64)
    }

    $stopwatch.Stop()
    $process.Refresh()
    $cpuAfter = $process.TotalProcessorTime
    $measurementAtEnd =
        Get-WindowMeasurement -WindowHandle $windowHandle
    $collapsedAtEnd = Test-WindowLogicalSize `
        -Measurement $measurementAtEnd `
        -ExpectedWidth $expectedCollapsedWidth `
        -ExpectedHeight $expectedCollapsedHeight
    $databaseAfter = Get-DatabaseSnapshot
    $cpuDeltaSeconds = ($cpuAfter - $cpuBefore).TotalSeconds
    $wallSeconds = $stopwatch.Elapsed.TotalSeconds
    $singleCorePercent = $cpuDeltaSeconds / $wallSeconds * 100
    $wholeMachinePercent =
        $singleCorePercent / [Environment]::ProcessorCount
    $databaseChanges = @(
        Compare-DatabaseSnapshot `
            -Before $databaseBefore `
            -After $databaseAfter)
    $databaseWritesObserved = $databaseChanges.Count -gt 0
    $validationFailures = New-Object System.Collections.ArrayList
    if (-not $collapsedAtEnd) {
        $validationFailures.Add(
            ((
                'The widget was not collapsed at observation end: ' +
                '{0:N1}x{1:N1} logical px.') -f
                $measurementAtEnd.LogicalWidth,
                $measurementAtEnd.LogicalHeight)) | Out-Null
    }

    if ($databaseWritesObserved) {
        $validationFailures.Add(
            (
                'SQLite DB/WAL/SHM length or last-write time changed during ' +
                'the idle observation.')) | Out-Null
    }

    $result = [ordered]@{
        executable = $Executable
        sha256 = (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
        executableBytes = (Get-Item -LiteralPath $Executable).Length
        startedAt = $startedAt.ToString('O')
        completedAt = [DateTimeOffset]::Now.ToString('O')
        warmupSeconds = $WarmupSeconds
        indexStabilitySeconds = $IndexStabilitySeconds
        observationSeconds = [Math]::Round($wallSeconds, 3)
        primeWeeklyQuotaOverlay = [bool]$PrimeWeeklyQuotaOverlay
        primeFromPinnedStartup = [bool]$PrimeFromPinnedStartup
        collapsedMode = $CollapsedMode
        expectedCollapsedLogicalWidth = $expectedCollapsedWidth
        expectedCollapsedLogicalHeight = $expectedCollapsedHeight
        automationTimeoutMilliseconds = $AutomationTimeoutMilliseconds
        weeklyQuotaPriming = $weeklyQuotaPriming
        windowLogicalWidth = [Math]::Round(
            $measurementAtStart.LogicalWidth,
            1)
        windowLogicalHeight = [Math]::Round(
            $measurementAtStart.LogicalHeight,
            1)
        windowDpi = $measurementAtStart.Dpi
        startWindowLogicalWidth = [Math]::Round(
            $measurementAtStart.LogicalWidth,
            1)
        startWindowLogicalHeight = [Math]::Round(
            $measurementAtStart.LogicalHeight,
            1)
        startWindowDpi = $measurementAtStart.Dpi
        collapsedAtObservationStart = $true
        endWindowLogicalWidth = [Math]::Round(
            $measurementAtEnd.LogicalWidth,
            1)
        endWindowLogicalHeight = [Math]::Round(
            $measurementAtEnd.LogicalHeight,
            1)
        endWindowDpi = $measurementAtEnd.Dpi
        collapsedAtObservationEnd = $collapsedAtEnd
        warmupDatabaseChanges = $warmupDatabaseChanges
        stabilityDatabaseChanges = $stabilityDatabaseChanges
        processorCount = [Environment]::ProcessorCount
        cpuDeltaSeconds = [Math]::Round($cpuDeltaSeconds, 6)
        singleCoreCpuPercent = [Math]::Round($singleCorePercent, 4)
        wholeMachineCpuPercent = [Math]::Round($wholeMachinePercent, 4)
        privateMemoryBytes = [long]$process.PrivateMemorySize64
        workingSetBytes = [long]$process.WorkingSet64
        peakPrivateMemoryBytes = $privatePeak
        peakWorkingSetBytes = $workingSetPeak
        databaseFileCount = $databaseAfter.Count
        databaseChanges = $databaseChanges
        databaseWritesObserved = $databaseWritesObserved
        validationPassed = $validationFailures.Count -eq 0
        validationFailures = @($validationFailures)
    }

    $directory = Split-Path -Parent $OutputPath
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $json = $result | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText(
        $OutputPath,
        $json,
        [Text.UTF8Encoding]::new($false))
    $json

    if ($validationFailures.Count -gt 0) {
        throw (
            'Idle validation failed: ' +
            ($validationFailures -join ' '))
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(3000)) {
            $process.Kill()
            $process.WaitForExit()
        }
    }

    if ($null -ne $originalSettingsBytes) {
        [IO.File]::WriteAllBytes($settingsPath, $originalSettingsBytes)
    }
}
