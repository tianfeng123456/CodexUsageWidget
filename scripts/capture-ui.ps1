[CmdletBinding()]
param(
    [string]$Executable,
    [string]$SettingsPath = (
        Join-Path $env:LOCALAPPDATA 'CodexUsageWidget\settings.json'),
    [int]$TargetProcessId = 0,
    [int]$InitialWaitSeconds = 5,
    [int]$StateTimeoutMilliseconds = 6500,
    [int]$PinnedHoldMilliseconds = 5500,
    [int]$ExpectedCollapseMinimumMilliseconds = 0,
    [int]$ExpectedCollapseMaximumMilliseconds = 150,
    [string]$OutputDirectory,
    [switch]$LeaveRunning
)

# Keep this file ASCII-only so it works in both Windows PowerShell 5.1 and
# PowerShell 7 without relying on the source file's encoding.
Set-StrictMode -Version 2.0
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
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ('CodexWidgetUiAuditNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class CodexWidgetUiAuditNative
{
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width
        {
            get { return Math.Max(0, Right - Left); }
        }

        public int Height
        {
            get { return Math.Max(0, Bottom - Top); }
        }
    }

    public sealed class WindowInfo
    {
        public IntPtr Handle { get; set; }
        public Rect Bounds { get; set; }
        public string Title { get; set; }
        public string ClassName { get; set; }
        public bool Enabled { get; set; }
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr hwnd,
        StringBuilder value,
        int maximumCharacterCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        IntPtr hwnd,
        StringBuilder value,
        int maximumCharacterCount);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(
        uint flags,
        bool inherit,
        uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(
        IntPtr handle,
        int index,
        StringBuilder value,
        int valueBytes,
        out int requiredBytes);

    [DllImport("user32.dll")]
    private static extern void mouse_event(
        uint flags,
        uint dx,
        uint dy,
        uint data,
        UIntPtr extraInfo);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint NativeGetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "SetProcessDpiAwarenessContext")]
    private static extern bool NativeSetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetProcessDPIAware")]
    private static extern bool NativeSetProcessDpiAware();

    public static WindowInfo[] GetVisibleWindows(int processId)
    {
        List<WindowInfo> result = new List<WindowInfo>();
        EnumWindowsProc callback = delegate(IntPtr hwnd, IntPtr ignored)
        {
            uint owner;
            GetWindowThreadProcessId(hwnd, out owner);
            if (owner != (uint)processId || !IsWindowVisible(hwnd))
            {
                return true;
            }

            Rect rect;
            if (!GetWindowRect(hwnd, out rect) || rect.Width <= 0 || rect.Height <= 0)
            {
                return true;
            }

            int titleLength = GetWindowTextLength(hwnd);
            StringBuilder title = new StringBuilder(Math.Max(1, titleLength + 1));
            GetWindowText(hwnd, title, title.Capacity);

            StringBuilder className = new StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);

            WindowInfo info = new WindowInfo();
            info.Handle = hwnd;
            info.Bounds = rect;
            info.Title = title.ToString();
            info.ClassName = className.ToString();
            info.Enabled = IsWindowEnabled(hwnd);
            result.Add(info);
            return true;
        };

        EnumWindows(callback, IntPtr.Zero);
        return result.ToArray();
    }

    public static uint GetDpi(IntPtr hwnd)
    {
        try
        {
            uint dpi = NativeGetDpiForWindow(hwnd);
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
                // Windows 11 always has one of the two APIs.
            }
        }
    }

    public static void LeftButtonDown()
    {
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
    }

    public static void LeftButtonUp()
    {
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }

    public static void MouseMove()
    {
        mouse_event(0x0001, 0, 0, 0, UIntPtr.Zero);
    }

    public static string GetInputDesktopName()
    {
        const uint DesktopReadObjects = 0x0001;
        const int UoiName = 2;
        IntPtr desktop = OpenInputDesktop(
            0,
            false,
            DesktopReadObjects);
        if (desktop == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            StringBuilder name = new StringBuilder(256);
            int requiredBytes;
            return GetUserObjectInformation(
                desktop,
                UoiName,
                name,
                name.Capacity * sizeof(char),
                out requiredBytes)
                ? name.ToString()
                : null;
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    public static int GetForegroundProcessId()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return 0;
        }

        uint processId;
        GetWindowThreadProcessId(foreground, out processId);
        return unchecked((int)processId);
    }
}
'@
}

[CodexWidgetUiAuditNative]::EnablePerMonitorDpiAwareness()
Add-Type -AssemblyName System.Windows.Forms

$inputDesktopName = [CodexWidgetUiAuditNative]::GetInputDesktopName()
if (-not [string]::Equals(
        $inputDesktopName,
        'Default',
        [StringComparison]::OrdinalIgnoreCase)) {
    $displayDesktopName = if (
        [string]::IsNullOrWhiteSpace($inputDesktopName)) {
        '<unavailable>'
    }
    else {
        $inputDesktopName
    }
    throw (
        'UI audit requires the unlocked interactive Default desktop. ' +
        'Current input desktop: ' + $displayDesktopName)
}

$foregroundProcessId =
    [CodexWidgetUiAuditNative]::GetForegroundProcessId()
if ($foregroundProcessId -gt 0) {
    $foregroundProcess = Get-Process `
        -Id $foregroundProcessId `
        -ErrorAction SilentlyContinue
    if ($null -ne $foregroundProcess -and
        $foregroundProcess.ProcessName -in @('LockApp', 'LogonUI')) {
        throw (
            'UI audit requires an unlocked interactive session. ' +
            'Foreground process: ' + $foregroundProcess.ProcessName)
    }
}

$script:Results = New-Object System.Collections.ArrayList
$script:Screenshots = [ordered]@{}
$script:FailureCount = 0
$script:AuditProcess = $null
$script:OwnsProcess = $false
$script:MainHandle = [IntPtr]::Zero
$script:OutputDirectory = $OutputDirectory
$script:StartedAt = [DateTimeOffset]::Now
$script:AbortReason = $null
$script:AuditSucceeded = $false
$script:OriginalOutputFiles = @{}
Get-ChildItem -LiteralPath $OutputDirectory -File -ErrorAction Stop |
    ForEach-Object {
        $script:OriginalOutputFiles[$_.FullName] =
            [IO.File]::ReadAllBytes($_.FullName)
    }

$requiredCases = @(
    'executable-and-process',
    'main-window-discovery',
    'display-and-dpi',
    'collapsed-80x80',
    'hover-expanded-420x540',
    'tab-today',
    'tab-last-7-days',
    'tab-current-month',
    'tab-all-time',
    'weekly-quota-overlay',
    'pin-holds-expanded',
    'drag-changes-position',
    'unpin-auto-collapses',
    'edge-expansion-restores-anchor',
    'settings-open-close',
    'final-collapsed-state'
)

function Add-AuditResult {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [ValidateSet('PASS', 'FAIL', 'SKIP')]
        [string]$Status,
        [Parameter(Mandatory = $true)]
        [string]$Detail,
        [string]$Evidence = ''
    )

    $entry = [pscustomobject][ordered]@{
        name = $Name
        status = $Status
        detail = $Detail
        evidence = $Evidence
    }
    $script:Results.Add($entry) | Out-Null

    $color = 'Gray'
    if ($Status -eq 'PASS') {
        $color = 'Green'
    }
    elseif ($Status -eq 'FAIL') {
        $color = 'Red'
    }
    elseif ($Status -eq 'SKIP') {
        $color = 'Yellow'
    }

    Write-Host ('[{0}] {1}: {2}' -f $Status, $Name, $Detail) -ForegroundColor $color
    if (-not [string]::IsNullOrWhiteSpace($Evidence)) {
        Write-Host ('       evidence: {0}' -f $Evidence)
    }
}

function Assert-AuditCondition {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Save-VirtualScreenScreenshot {
    param([string]$Path)

    $screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bitmap = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $size = New-Object System.Drawing.Size $screen.Width, $screen.Height
            $graphics.CopyFromScreen($screen.Left, $screen.Top, 0, 0, $size)
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

function Save-FailureEvidence {
    $script:FailureCount++
    $path = Join-Path $script:OutputDirectory (
        'failure-{0:00}.png' -f $script:FailureCount)
    try {
        Save-VirtualScreenScreenshot -Path $path
        $script:Screenshots[('failure-{0:00}' -f $script:FailureCount)] = $path
        return $path
    }
    catch {
        return ''
    }
}

function Invoke-AuditCase {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action
    )

    try {
        $rawDetail = @(& $Action)
        $detail = 'Completed.'
        if ($rawDetail.Count -gt 0) {
            $detailParts = @($rawDetail | ForEach-Object { [string]$_ })
            $detail = [string]::Join(' ', $detailParts)
        }

        Add-AuditResult -Name $Name -Status 'PASS' -Detail $detail
        return $true
    }
    catch {
        $message = $_.Exception.Message
        $evidence = Save-FailureEvidence
        Add-AuditResult -Name $Name -Status 'FAIL' -Detail $message -Evidence $evidence
        return $false
    }
}

function Add-MissingCasesAsSkipped {
    param([string]$Reason)

    foreach ($caseName in $requiredCases) {
        $existing = @($script:Results | Where-Object { $_.name -eq $caseName })
        if ($existing.Count -eq 0) {
            Add-AuditResult -Name $caseName -Status 'SKIP' -Detail $Reason
        }
    }
}

function Get-WindowRecord {
    param(
        [Diagnostics.Process]$Process,
        [IntPtr]$Handle
    )

    if ($null -eq $Process -or $Process.HasExited -or
        $Handle -eq [IntPtr]::Zero -or
        -not [CodexWidgetUiAuditNative]::IsWindow($Handle)) {
        return $null
    }

    $windows = @([CodexWidgetUiAuditNative]::GetVisibleWindows($Process.Id))
    foreach ($window in $windows) {
        if ($window.Handle -eq $Handle) {
            return $window
        }
    }

    return $null
}

function Get-WindowScale {
    param([IntPtr]$Handle)

    $dpi = [double][CodexWidgetUiAuditNative]::GetDpi($Handle)
    return $dpi / 96.0
}

function Get-LogicalWindowDescription {
    param($Window)

    $scale = Get-WindowScale -Handle $Window.Handle
    $logicalWidth = [Math]::Round($Window.Bounds.Width / $scale, 1)
    $logicalHeight = [Math]::Round($Window.Bounds.Height / $scale, 1)
    return ('{0}x{1} logical px ({2}x{3} physical px, {4} DPI)' -f
        $logicalWidth,
        $logicalHeight,
        $Window.Bounds.Width,
        $Window.Bounds.Height,
        [CodexWidgetUiAuditNative]::GetDpi($Window.Handle))
}

function Wait-ForMainWindow {
    param(
        [Diagnostics.Process]$Process,
        [int]$TimeoutMilliseconds = 15000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw ('The process exited before its main window appeared (exit code {0}).' -f
                $Process.ExitCode)
        }

        $best = $null
        $bestScore = [double]::PositiveInfinity
        $windows = @([CodexWidgetUiAuditNative]::GetVisibleWindows($Process.Id))
        foreach ($window in $windows) {
            $scale = Get-WindowScale -Handle $window.Handle
            $logicalWidth = $window.Bounds.Width / $scale
            $logicalHeight = $window.Bounds.Height / $scale
            $collapsedScore =
                [Math]::Abs($logicalWidth - 80) +
                [Math]::Abs($logicalHeight - 80)
            $expandedScore =
                [Math]::Abs($logicalWidth - 420) +
                [Math]::Abs($logicalHeight - 540)
            $score = [Math]::Min($collapsedScore, $expandedScore)
            if ($score -lt $bestScore) {
                $best = $window
                $bestScore = $score
            }
        }

        if ($null -ne $best -and $bestScore -le 30) {
            return $best.Handle
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw 'A visible 80x80 or 420x540 widget window was not found.'
}

function Wait-ForLogicalWindowSize {
    param(
        [Diagnostics.Process]$Process,
        [IntPtr]$Handle,
        [double]$ExpectedWidth,
        [double]$ExpectedHeight,
        [int]$TimeoutMilliseconds,
        [double]$Tolerance = 4
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $window = Get-WindowRecord -Process $Process -Handle $Handle
        if ($null -ne $window) {
            $scale = Get-WindowScale -Handle $Handle
            $logicalWidth = $window.Bounds.Width / $scale
            $logicalHeight = $window.Bounds.Height / $scale
            if ([Math]::Abs($logicalWidth - $ExpectedWidth) -le $Tolerance -and
                [Math]::Abs($logicalHeight - $ExpectedHeight) -le $Tolerance) {
                return $window
            }
        }

        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)

    return $null
}

function Wait-ForSettingsWindow {
    param(
        [Diagnostics.Process]$Process,
        [IntPtr]$MainHandle,
        [int]$TimeoutMilliseconds = 5000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $windows = @([CodexWidgetUiAuditNative]::GetVisibleWindows($Process.Id))
        foreach ($window in $windows) {
            if ($window.Handle -eq $MainHandle) {
                continue
            }

            $scale = Get-WindowScale -Handle $window.Handle
            $logicalWidth = $window.Bounds.Width / $scale
            $logicalHeight = $window.Bounds.Height / $scale
            if ([Math]::Abs($logicalWidth - 460) -le 8 -and
                [Math]::Abs($logicalHeight - 590) -le 8) {
                return $window
            }
        }

        Start-Sleep -Milliseconds 75
    } while ([DateTime]::UtcNow -lt $deadline)

    return $null
}

function Wait-ForWindowToDisappear {
    param(
        [Diagnostics.Process]$Process,
        [IntPtr]$Handle,
        [int]$TimeoutMilliseconds = 4000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $window = Get-WindowRecord -Process $Process -Handle $Handle
        if ($null -eq $window) {
            return $true
        }

        Start-Sleep -Milliseconds 75
    } while ([DateTime]::UtcNow -lt $deadline)

    return $false
}

function Invoke-LeftClick {
    param(
        [int]$X,
        [int]$Y
    )

    [CodexWidgetUiAuditNative]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 80
    [CodexWidgetUiAuditNative]::LeftButtonDown()
    Start-Sleep -Milliseconds 70
    [CodexWidgetUiAuditNative]::LeftButtonUp()
    Start-Sleep -Milliseconds 100
}

function Move-PointerOutsideWindow {
    param(
        $Window,
        [int]$SettleMilliseconds = 100
    )

    $screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $candidates = @(
        [pscustomobject]@{ X = $screen.Left + 12; Y = $screen.Top + 12 },
        [pscustomobject]@{ X = $screen.Right - 13; Y = $screen.Top + 12 },
        [pscustomobject]@{ X = $screen.Left + 12; Y = $screen.Bottom - 13 },
        [pscustomobject]@{ X = $screen.Right - 13; Y = $screen.Bottom - 13 }
    )

    foreach ($point in $candidates) {
        $outside =
            $point.X -lt ($Window.Bounds.Left - 20) -or
            $point.X -gt ($Window.Bounds.Right + 20) -or
            $point.Y -lt ($Window.Bounds.Top - 20) -or
            $point.Y -gt ($Window.Bounds.Bottom + 20)
        if ($outside) {
            [CodexWidgetUiAuditNative]::SetCursorPos($point.X, $point.Y) | Out-Null
            [CodexWidgetUiAuditNative]::MouseMove()
            if ($SettleMilliseconds -gt 0) {
                Start-Sleep -Milliseconds $SettleMilliseconds
            }
            return
        }
    }

    throw 'No safe pointer position exists outside the widget.'
}

function Invoke-AutomationButton {
    param(
        $Window,
        [string]$AutomationId
    )

    $root = [System.Windows.Automation.AutomationElement]::FromHandle(
        $Window.Handle)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $button = $root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    if ($null -eq $button) {
        throw "Automation button not found: $AutomationId"
    }

    $pattern = $button.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Start-Sleep -Milliseconds 120
}

function Wait-ForAutomationIdPrefixCount {
    param(
        $Window,
        [string]$Prefix,
        [int]$ExpectedCount,
        [int]$TimeoutMilliseconds = 10000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle(
            $Window.Handle)
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

        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)

    return @()
}

function Invoke-PinButtonClick {
    param($Window)

    Invoke-AutomationButton -Window $Window -AutomationId 'PinButton'
}

function Invoke-SettingsButtonClick {
    param($Window)

    Invoke-AutomationButton -Window $Window -AutomationId 'SettingsButton'
}

function Invoke-WeeklyQuotaButtonClick {
    param($Window)

    Invoke-AutomationButton `
        -Window $Window `
        -AutomationId 'WeeklyQuotaTrendButton'
}

function Invoke-WeeklyQuotaCloseClick {
    param($Window)

    Invoke-AutomationButton `
        -Window $Window `
        -AutomationId 'WeeklyQuotaCloseButton'
}

function Get-WindowLogicalPixel {
    param(
        $Window,
        [double]$LogicalX,
        [double]$LogicalY
    )

    $scale = Get-WindowScale -Handle $Window.Handle
    $x = [int][Math]::Round(
        $Window.Bounds.Left + ($LogicalX * $scale))
    $y = [int][Math]::Round(
        $Window.Bounds.Top + ($LogicalY * $scale))
    $bitmap = New-Object System.Drawing.Bitmap 1, 1
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size 1, 1))
        }
        finally {
            $graphics.Dispose()
        }

        return $bitmap.GetPixel(0, 0)
    }
    finally {
        $bitmap.Dispose()
    }
}

function Save-WindowScreenshot {
    param(
        $Window,
        [string]$Path,
        [double]$MarginLogical = 18
    )

    # WPF applies the native window bounds before the newly measured visual
    # tree is guaranteed to have reached the compositor.  Allow one stable
    # frame so captures never preserve the transparent transition state.
    Start-Sleep -Milliseconds 250

    # Expansion can change size and then move the HWND to the opposite side
    # of its collapsed anchor. Refresh the native rectangle after that frame
    # instead of cropping with the pre-layout position returned by the wait.
    $currentWindow = Get-WindowRecord `
        -Process $script:AuditProcess `
        -Handle $Window.Handle
    if ($null -ne $currentWindow) {
        $Window = $currentWindow
    }

    $screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $scale = Get-WindowScale -Handle $Window.Handle
    $margin = [int][Math]::Round($MarginLogical * $scale)
    $left = [Math]::Max($screen.Left, $Window.Bounds.Left - $margin)
    $top = [Math]::Max($screen.Top, $Window.Bounds.Top - $margin)
    $right = [Math]::Min($screen.Right, $Window.Bounds.Right + $margin)
    $bottom = [Math]::Min($screen.Bottom, $Window.Bounds.Bottom + $margin)
    $width = [Math]::Max(1, $right - $left)
    $height = [Math]::Max(1, $bottom - $top)

    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $size = New-Object System.Drawing.Size $width, $height
            $graphics.CopyFromScreen($left, $top, 0, 0, $size)
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

function Get-TabAccentScore {
    param(
        $Window,
        [int]$TabIndex
    )

    $scale = Get-WindowScale -Handle $Window.Handle
    $tabCenterLogical = 69 + (92 * $TabIndex)
    $sampleLeft = [int][Math]::Round(
        $Window.Bounds.Left + (($tabCenterLogical - 28) * $scale))
    $sampleTop = [int][Math]::Round(
        $Window.Bounds.Top + (132 * $scale))
    $sampleWidth = [Math]::Max(28, [int][Math]::Round(56 * $scale))
    $sampleHeight = [Math]::Max(16, [int][Math]::Round(28 * $scale))

    $bitmap = New-Object System.Drawing.Bitmap $sampleWidth, $sampleHeight
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $size = New-Object System.Drawing.Size $sampleWidth, $sampleHeight
            $graphics.CopyFromScreen(
                $sampleLeft,
                $sampleTop,
                0,
                0,
                $size)
        }
        finally {
            $graphics.Dispose()
        }

        # The selected indicator is a short, two-DIP horizontal mint line.
        # Score the longest contiguous run instead of the total green pixel
        # count so text antialiasing and a tinted desktop cannot look selected.
        $longestRun = 0
        $qualifyingRows = 0
        $minimumRowRun = [Math]::Max(
            4,
            [int][Math]::Round(8 * $scale))
        for ($y = 0; $y -lt $sampleHeight; $y++) {
            $currentRun = 0
            $rowLongestRun = 0
            for ($x = 0; $x -lt $sampleWidth; $x++) {
                $color = $bitmap.GetPixel($x, $y)
                $isAccent =
                    $color.G -ge 90 -and
                    $color.G -ge ($color.R + 20) -and
                    $color.G -ge ($color.B + 5)
                if ($isAccent) {
                    $currentRun++
                    $rowLongestRun = [Math]::Max(
                        $rowLongestRun,
                        $currentRun)
                }
                else {
                    $currentRun = 0
                }
            }

            $longestRun = [Math]::Max($longestRun, $rowLongestRun)
            if ($rowLongestRun -ge $minimumRowRun) {
                $qualifyingRows++
            }
        }

        $minimumRows = [Math]::Max(
            2,
            [int][Math]::Round(1.5 * $scale))
        if ($qualifyingRows -lt $minimumRows) {
            return 0
        }

        # Normalize to logical pixels so the same thresholds work from
        # 100 percent through 200 percent display scaling.
        return [int][Math]::Round($longestRun / $scale)
    }
    finally {
        $bitmap.Dispose()
    }
}

function Invoke-TabSelection {
    param(
        [int]$TabIndex,
        [string]$ScreenshotName
    )

    $window = Wait-ForLogicalWindowSize `
        -Process $script:AuditProcess `
        -Handle $script:MainHandle `
        -ExpectedWidth 420 `
        -ExpectedHeight 540 `
        -TimeoutMilliseconds 2000
    Assert-AuditCondition ($null -ne $window) 'The widget is not expanded.'

    $scale = Get-WindowScale -Handle $window.Handle
    $x = [int][Math]::Round(
        $window.Bounds.Left + ((69 + (92 * $TabIndex)) * $scale))
    $y = [int][Math]::Round(
        $window.Bounds.Top + (123 * $scale))
    Invoke-LeftClick -X $x -Y $y
    Start-Sleep -Milliseconds 250

    $window = Wait-ForLogicalWindowSize `
        -Process $script:AuditProcess `
        -Handle $script:MainHandle `
        -ExpectedWidth 420 `
        -ExpectedHeight 540 `
        -TimeoutMilliseconds 2000
    Assert-AuditCondition ($null -ne $window) 'The widget collapsed while a tab was selected.'

    $scores = @()
    for ($index = 0; $index -lt 4; $index++) {
        $scores += Get-TabAccentScore -Window $window -TabIndex $index
    }

    $targetScore = [int]$scores[$TabIndex]
    $otherMaximum = 0
    for ($index = 0; $index -lt 4; $index++) {
        if ($index -ne $TabIndex) {
            $otherMaximum = [Math]::Max($otherMaximum, [int]$scores[$index])
        }
    }

    Assert-AuditCondition `
        ($targetScore -ge 10) `
        ('No mint selection indicator was detected for tab {0}; scores={1}.' -f
            $TabIndex,
            [string]::Join(',', [string[]]$scores))
    Assert-AuditCondition `
        ($targetScore -gt ($otherMaximum + 4)) `
        ('Tab {0} is not uniquely selected; scores={1}.' -f
            $TabIndex,
            [string]::Join(',', [string[]]$scores))

    $path = Join-Path $script:OutputDirectory $ScreenshotName
    Save-WindowScreenshot -Window $window -Path $path
    $key = [IO.Path]::GetFileNameWithoutExtension($ScreenshotName)
    $script:Screenshots[$key] = $path

    return ('selected; accent scores={0}; screenshot={1}' -f
        [string]::Join(',', [string[]]$scores),
        $path)
}

function Invoke-WindowDrag {
    param(
        $Window,
        [int]$DeltaX,
        [int]$DeltaY,
        [double]$StartLogicalX = 150,
        [double]$StartLogicalY = 38
    )

    $scale = Get-WindowScale -Handle $Window.Handle
    $startX = [int][Math]::Round(
        $Window.Bounds.Left + ($StartLogicalX * $scale))
    $startY = [int][Math]::Round(
        $Window.Bounds.Top + ($StartLogicalY * $scale))
    $endX = $startX + $DeltaX
    $endY = $startY + $DeltaY

    [CodexWidgetUiAuditNative]::SetForegroundWindow($Window.Handle) | Out-Null
    [CodexWidgetUiAuditNative]::SetCursorPos($startX, $startY) | Out-Null
    [CodexWidgetUiAuditNative]::MouseMove()
    Start-Sleep -Milliseconds 120

    [CodexWidgetUiAuditNative]::LeftButtonDown()
    try {
        Start-Sleep -Milliseconds 120
        for ($step = 1; $step -le 12; $step++) {
            $x = [int][Math]::Round(
                $startX + (($DeltaX * $step) / 12.0))
            $y = [int][Math]::Round(
                $startY + (($DeltaY * $step) / 12.0))
            [CodexWidgetUiAuditNative]::SetCursorPos($x, $y) | Out-Null
            [CodexWidgetUiAuditNative]::MouseMove()
            Start-Sleep -Milliseconds 25
        }
    }
    finally {
        [CodexWidgetUiAuditNative]::LeftButtonUp()
    }

    Start-Sleep -Milliseconds 400
}

function Get-MatchingExistingProcess {
    param([string]$ExecutablePath)

    $processName = [IO.Path]::GetFileNameWithoutExtension($ExecutablePath)
    $candidates = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
    foreach ($candidate in $candidates) {
        try {
            if ([string]::Equals(
                    [IO.Path]::GetFullPath($candidate.Path),
                    $ExecutablePath,
                    [StringComparison]::OrdinalIgnoreCase)) {
                return $candidate
            }
        }
        catch {
            # Access to Process.Path can fail for elevated processes. The caller
            # can still attach explicitly with -TargetProcessId.
        }
    }

    return $null
}

$script:OriginalSettingsBytes = $null
if ($LeaveRunning -and $TargetProcessId -eq 0) {
    throw (
        '-LeaveRunning is only safe with -TargetProcessId. A process started ' +
        'by this audit holds the temporary baseline settings in memory and ' +
        'could overwrite the restored user settings later.')
}

try {
if ($TargetProcessId -eq 0) {
    $existingBeforeAudit =
        Get-MatchingExistingProcess -ExecutablePath $Executable
    if ($null -ne $existingBeforeAudit) {
        throw (
            'Close CodexUsageWidget before running the deterministic Circle ' +
            'baseline, or attach explicitly with -TargetProcessId.')
    }

    if (-not (Test-Path -LiteralPath $SettingsPath -PathType Leaf)) {
        throw "Settings file not found: $SettingsPath"
    }

    $script:OriginalSettingsBytes = [IO.File]::ReadAllBytes($SettingsPath)
    $baselineSettings =
        Get-Content -LiteralPath $SettingsPath -Raw |
        ConvertFrom-Json
    $baselineSettings |
        Add-Member `
            -NotePropertyName 'collapsedMode' `
            -NotePropertyValue 'Circle' `
            -Force
    $baselineSettings |
        Add-Member `
            -NotePropertyName 'isPinned' `
            -NotePropertyValue $false `
            -Force
    $baselineSettings |
        Add-Member `
            -NotePropertyName 'autoCollapse' `
            -NotePropertyValue $true `
            -Force
    $baselineJson = $baselineSettings | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText(
        $SettingsPath,
        $baselineJson,
        [Text.UTF8Encoding]::new($false))
}

$prerequisiteOk = Invoke-AuditCase -Name 'executable-and-process' -Action {
    Assert-AuditCondition `
        (Test-Path -LiteralPath $Executable -PathType Leaf) `
        ('Executable not found: {0}' -f $Executable)

    if ($TargetProcessId -gt 0) {
        $script:AuditProcess = Get-Process -Id $TargetProcessId -ErrorAction Stop
        $script:OwnsProcess = $false
        return ('attached to requested PID {0}' -f $script:AuditProcess.Id)
    }

    $script:AuditProcess = Start-Process -FilePath $Executable -PassThru
    $script:OwnsProcess = $true
    return ('started PID {0}' -f $script:AuditProcess.Id)
}

if (-not $prerequisiteOk) {
    $script:AbortReason = 'The executable could not be started or attached.'
}

if ($null -eq $script:AbortReason) {
    Start-Sleep -Seconds ([Math]::Max(0, $InitialWaitSeconds))
    $mainWindowOk = Invoke-AuditCase -Name 'main-window-discovery' -Action {
        $script:MainHandle = Wait-ForMainWindow `
            -Process $script:AuditProcess `
            -TimeoutMilliseconds 15000
        $window = Get-WindowRecord `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle
        Assert-AuditCondition ($null -ne $window) 'The main window disappeared.'
        return ('HWND=0x{0}; {1}' -f
            $script:MainHandle.ToInt64().ToString('X'),
            (Get-LogicalWindowDescription -Window $window))
    }

    if (-not $mainWindowOk) {
        $script:AbortReason = 'The widget main window was not available.'
    }
}

if ($null -eq $script:AbortReason) {
    $displayOk = Invoke-AuditCase -Name 'display-and-dpi' -Action {
        $screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $window = Get-WindowRecord `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle
        Assert-AuditCondition ($null -ne $window) 'The main window disappeared.'
        $dpi = [CodexWidgetUiAuditNative]::GetDpi($window.Handle)
        Assert-AuditCondition `
            ($screen.Width -ge 800 -and $screen.Height -ge 600) `
            ('The virtual screen is too small: {0}x{1}.' -f
                $screen.Width,
                $screen.Height)
        return ('virtual screen={0}x{1} at ({2},{3}); widget DPI={4}; calibrated for 1600x1000 at 96 DPI and scaled per monitor' -f
            $screen.Width,
            $screen.Height,
            $screen.Left,
            $screen.Top,
            $dpi)
    }

    if (-not $displayOk) {
        $script:AbortReason = 'The display environment could not be measured.'
    }
}

if ($null -eq $script:AbortReason) {
    $collapsedOk = Invoke-AuditCase -Name 'collapsed-80x80' -Action {
        $window = Get-WindowRecord `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle
        Assert-AuditCondition ($null -ne $window) 'The main window disappeared.'
        Move-PointerOutsideWindow -Window $window

        $collapsed = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 80 `
            -ExpectedHeight 80 `
            -TimeoutMilliseconds $StateTimeoutMilliseconds

        if ($null -eq $collapsed) {
            $expanded = Wait-ForLogicalWindowSize `
                -Process $script:AuditProcess `
                -Handle $script:MainHandle `
                -ExpectedWidth 420 `
                -ExpectedHeight 540 `
                -TimeoutMilliseconds 300
            Assert-AuditCondition `
                ($null -ne $expanded) `
                'The main window is neither 80x80 nor 420x540.'

            # A persisted pin is the only valid reason for an expanded clean
            # start while the pointer remains outside.
            Invoke-PinButtonClick -Window $expanded
            Move-PointerOutsideWindow -Window $expanded
            $collapsed = Wait-ForLogicalWindowSize `
                -Process $script:AuditProcess `
                -Handle $script:MainHandle `
                -ExpectedWidth 80 `
                -ExpectedHeight 80 `
                -TimeoutMilliseconds $StateTimeoutMilliseconds
        }

        Assert-AuditCondition `
            ($null -ne $collapsed) `
            'The widget did not reach its unpinned collapsed state. Auto-collapse may be disabled.'

        $path = Join-Path $script:OutputDirectory 'collapsed.png'
        Save-WindowScreenshot -Window $collapsed -Path $path
        $script:Screenshots['collapsed'] = $path
        return ('{0}; screenshot={1}' -f
            (Get-LogicalWindowDescription -Window $collapsed),
            $path)
    }

    if (-not $collapsedOk) {
        $script:AbortReason = 'The initial collapsed state could not be established.'
    }
}

if ($null -eq $script:AbortReason) {
    $expandedOk = Invoke-AuditCase -Name 'hover-expanded-420x540' -Action {
        $beforeHover = Get-WindowRecord `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle
        Assert-AuditCondition `
            ($null -ne $beforeHover) `
            'The main window disappeared before hover expansion.'
        Move-PointerOutsideWindow -Window $beforeHover
        $collapsed = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 80 `
            -ExpectedHeight 80 `
            -TimeoutMilliseconds $StateTimeoutMilliseconds
        Assert-AuditCondition ($null -ne $collapsed) 'The widget is not collapsed.'

        $centerX = [int](($collapsed.Bounds.Left + $collapsed.Bounds.Right) / 2)
        $centerY = [int](($collapsed.Bounds.Top + $collapsed.Bounds.Bottom) / 2)
        [CodexWidgetUiAuditNative]::SetCursorPos($centerX, $centerY) | Out-Null
        [CodexWidgetUiAuditNative]::MouseMove()

        $expanded = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 420 `
            -ExpectedHeight 540 `
            -TimeoutMilliseconds 4000
        Assert-AuditCondition `
            ($null -ne $expanded) `
            'Hover did not expand the widget to 420x540.'

        $path = Join-Path $script:OutputDirectory 'expanded.png'
        Save-WindowScreenshot -Window $expanded -Path $path
        $script:Screenshots['expanded'] = $path
        return ('{0}; screenshot={1}' -f
            (Get-LogicalWindowDescription -Window $expanded),
            $path)
    }

    if (-not $expandedOk) {
        $script:AbortReason = 'Hover expansion failed.'
    }
}

if ($null -eq $script:AbortReason) {
    $tabCases = @(
        [pscustomobject]@{
            Name = 'tab-today'
            Index = 0
            Screenshot = 'expanded-today.png'
        },
        [pscustomobject]@{
            Name = 'tab-last-7-days'
            Index = 1
            Screenshot = 'expanded-last-7-days.png'
        },
        [pscustomobject]@{
            Name = 'tab-current-month'
            Index = 2
            Screenshot = 'expanded-current-month.png'
        },
        [pscustomobject]@{
            Name = 'tab-all-time'
            Index = 3
            Screenshot = 'expanded-all-time.png'
        }
    )

    foreach ($tabCase in $tabCases) {
        $case = $tabCase
        Invoke-AuditCase -Name $case.Name -Action {
            Invoke-TabSelection `
                -TabIndex $case.Index `
                -ScreenshotName $case.Screenshot
        } | Out-Null
    }
}

if ($null -eq $script:AbortReason) {
    Invoke-AuditCase -Name 'weekly-quota-overlay' -Action {
        $expanded = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 420 `
            -ExpectedHeight 540 `
            -TimeoutMilliseconds 2000
        Assert-AuditCondition ($null -ne $expanded) 'The widget is not expanded.'

        $before = Get-WindowLogicalPixel `
            -Window $expanded `
            -LogicalX 10 `
            -LogicalY 190
        Invoke-WeeklyQuotaButtonClick -Window $expanded
        Start-Sleep -Milliseconds 450

        $stillExpanded = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 420 `
            -ExpectedHeight 540 `
            -TimeoutMilliseconds 2000
        Assert-AuditCondition `
            ($null -ne $stillExpanded) `
            'Opening weekly quota history changed or collapsed the main HWND.'

        $opened = Get-WindowLogicalPixel `
            -Window $stillExpanded `
            -LogicalX 10 `
            -LogicalY 190
        $beforeBrightness = $before.R + $before.G + $before.B
        $openedBrightness = $opened.R + $opened.G + $opened.B
        Assert-AuditCondition `
            ($openedBrightness -lt ($beforeBrightness - 60)) `
            ('The in-window weekly quota scrim was not detected; brightness {0}->{1}.' -f
                $beforeBrightness,
                $openedBrightness)

        $dayElements = @(
            Wait-ForAutomationIdPrefixCount `
                -Window $stillExpanded `
                -Prefix 'WeeklyQuotaDay_' `
                -ExpectedCount 7 `
                -TimeoutMilliseconds 15000
        )
        Assert-AuditCondition `
            ($dayElements.Count -eq 7) `
            ('Expected seven weekly quota day elements, found {0}.' -f
                $dayElements.Count)
        $dayAutomationIds = @(
            $dayElements | ForEach-Object { $_.Current.AutomationId }
        )
        Assert-AuditCondition `
            (($dayAutomationIds | Select-Object -Unique).Count -eq 7) `
            'Weekly quota day AutomationIds are not unique.'

        # Reproduce the real interaction that previously terminated the
        # process: Run.Text binds TwoWay by default, so hovering a day updates
        # HoveredWeeklyQuotaDay and exercises every read-only tooltip binding.
        $hoverTarget = $dayElements[
            [Math]::Min(3, $dayElements.Count - 1)]
        $hoverBounds = $hoverTarget.Current.BoundingRectangle
        Assert-AuditCondition `
            ($hoverBounds.Width -gt 0 -and $hoverBounds.Height -gt 0) `
            'The weekly quota hover target has no visible bounds.'
        $hoverX = [int][Math]::Round(
            $hoverBounds.Left + ($hoverBounds.Width / 2))
        $hoverY = [int][Math]::Round(
            $hoverBounds.Top + ($hoverBounds.Height / 2))
        [CodexWidgetUiAuditNative]::SetCursorPos($hoverX, $hoverY) | Out-Null
        [CodexWidgetUiAuditNative]::MouseMove()
        Start-Sleep -Milliseconds 350
        $script:AuditProcess.Refresh()
        Assert-AuditCondition `
            (-not $script:AuditProcess.HasExited) `
            'The widget terminated while hovering a weekly quota day.'
        $hoveredExpanded = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 420 `
            -ExpectedHeight 540 `
            -TimeoutMilliseconds 1000
        Assert-AuditCondition `
            ($null -ne $hoveredExpanded) `
            'The widget disappeared while showing weekly quota hover details.'

        $path = Join-Path $script:OutputDirectory 'weekly-quota-overlay.png'
        Save-WindowScreenshot -Window $hoveredExpanded -Path $path
        $script:Screenshots['weekly-quota-overlay'] = $path

        Invoke-WeeklyQuotaCloseClick -Window $hoveredExpanded
        Start-Sleep -Milliseconds 250
        $afterClose = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 420 `
            -ExpectedHeight 540 `
            -TimeoutMilliseconds 2000
        Assert-AuditCondition `
            ($null -ne $afterClose) `
            'Closing weekly quota history collapsed the main window.'

        $restored = Get-WindowLogicalPixel `
            -Window $afterClose `
            -LogicalX 10 `
            -LogicalY 190
        $restoredBrightness = $restored.R + $restored.G + $restored.B
        Assert-AuditCondition `
            ($restoredBrightness -gt ($openedBrightness + 60)) `
            ('The weekly quota overlay did not close; brightness {0}->{1}.' -f
                $openedBrightness,
                $restoredBrightness)

        return ('opened and closed inside the 420x540 HWND; seven daily observations exposed to UI Automation; real day hover remained alive; scrim brightness {0}->{1}->{2}; screenshot={3}' -f
            $beforeBrightness,
            $openedBrightness,
            $restoredBrightness,
            $path)
    } | Out-Null
}

if ($null -eq $script:AbortReason) {
    $pinOk = Invoke-AuditCase -Name 'pin-holds-expanded' -Action {
        $expanded = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 420 `
            -ExpectedHeight 540 `
            -TimeoutMilliseconds 2000
        Assert-AuditCondition ($null -ne $expanded) 'The widget is not expanded.'

        Invoke-PinButtonClick -Window $expanded
        Move-PointerOutsideWindow -Window $expanded
        Start-Sleep -Milliseconds $PinnedHoldMilliseconds

        $stillExpanded = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 420 `
            -ExpectedHeight 540 `
            -TimeoutMilliseconds 500
        Assert-AuditCondition `
            ($null -ne $stillExpanded) `
            'The widget collapsed while pinned and the pointer was outside.'

        $path = Join-Path $script:OutputDirectory 'pinned-expanded.png'
        Save-WindowScreenshot -Window $stillExpanded -Path $path
        $script:Screenshots['pinned-expanded'] = $path
        return ('remained expanded for {0} ms; screenshot={1}' -f
            $PinnedHoldMilliseconds,
            $path)
    }

    if (-not $pinOk) {
        $script:AbortReason = 'Pin behavior failed, so drag and unpin checks are unsafe.'
    }
}

if ($null -eq $script:AbortReason) {
    $dragOk = Invoke-AuditCase -Name 'drag-changes-position' -Action {
        $before = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 420 `
            -ExpectedHeight 540 `
            -TimeoutMilliseconds 1000
        Assert-AuditCondition ($null -ne $before) 'The pinned widget is not expanded.'

        $screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $scale = Get-WindowScale -Handle $before.Handle
        $preferredX = [int][Math]::Round(90 * $scale)
        $preferredY = [int][Math]::Round(55 * $scale)
        $deltaX = $preferredX
        $deltaY = $preferredY
        if (($before.Bounds.Right + $preferredX + 12) -gt $screen.Right) {
            $deltaX = -$preferredX
        }
        if (($before.Bounds.Bottom + $preferredY + 12) -gt $screen.Bottom) {
            $deltaY = -$preferredY
        }

        Invoke-WindowDrag `
            -Window $before `
            -DeltaX $deltaX `
            -DeltaY $deltaY

        $after = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 420 `
            -ExpectedHeight 540 `
            -TimeoutMilliseconds 2000
        Assert-AuditCondition ($null -ne $after) 'The widget disappeared after the drag.'

        $actualX = $after.Bounds.Left - $before.Bounds.Left
        $actualY = $after.Bounds.Top - $before.Bounds.Top
        $distanceLogical = [Math]::Sqrt(
            ($actualX * $actualX) + ($actualY * $actualY)) / $scale
        Assert-AuditCondition `
            ($distanceLogical -ge 30) `
            ('The drag moved only {0:N1} logical px.' -f $distanceLogical)

        $path = Join-Path $script:OutputDirectory 'dragged-expanded.png'
        Save-WindowScreenshot -Window $after -Path $path
        $script:Screenshots['dragged-expanded'] = $path
        return ('position changed from ({0},{1}) to ({2},{3}), {4:N1} logical px; screenshot={5}' -f
            $before.Bounds.Left,
            $before.Bounds.Top,
            $after.Bounds.Left,
            $after.Bounds.Top,
            $distanceLogical,
            $path)
    }

    if (-not $dragOk) {
        # Continue: the pin can still be released at the current window position.
        Write-Host '       continuing with unpin verification after drag failure'
    }
}

if ($null -eq $script:AbortReason) {
    $unpinOk = Invoke-AuditCase -Name 'unpin-auto-collapses' -Action {
        $expanded = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 420 `
            -ExpectedHeight 540 `
            -TimeoutMilliseconds 1500
        Assert-AuditCondition ($null -ne $expanded) 'The widget is not expanded.'

        Invoke-PinButtonClick -Window $expanded
        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        Move-PointerOutsideWindow -Window $expanded -SettleMilliseconds 0
        $collapsed = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 80 `
            -ExpectedHeight 80 `
            -TimeoutMilliseconds ($ExpectedCollapseMaximumMilliseconds + 750)
        $stopwatch.Stop()

        Assert-AuditCondition `
            ($null -ne $collapsed) `
            ('The widget did not collapse within {0} ms after unpinning.' -f
                $ExpectedCollapseMaximumMilliseconds)
        Assert-AuditCondition `
            ($stopwatch.ElapsedMilliseconds -ge $ExpectedCollapseMinimumMilliseconds) `
            ('The widget collapsed too quickly ({0} ms); expected at least {1} ms.' -f
                $stopwatch.ElapsedMilliseconds,
                $ExpectedCollapseMinimumMilliseconds)
        Assert-AuditCondition `
            ($stopwatch.ElapsedMilliseconds -le $ExpectedCollapseMaximumMilliseconds) `
            ('The widget collapsed too slowly ({0} ms); expected at most {1} ms.' -f
                $stopwatch.ElapsedMilliseconds,
                $ExpectedCollapseMaximumMilliseconds)

        $path = Join-Path $script:OutputDirectory 'unpinned-collapsed.png'
        Save-WindowScreenshot -Window $collapsed -Path $path
        $script:Screenshots['unpinned-collapsed'] = $path
        return ('collapsed after {0} ms; screenshot={1}' -f
            $stopwatch.ElapsedMilliseconds,
            $path)
    }

    if (-not $unpinOk) {
        $script:AbortReason = 'The widget could not be returned to an unpinned state.'
    }
}

if ($null -eq $script:AbortReason) {
    $edgeOk = Invoke-AuditCase -Name 'edge-expansion-restores-anchor' -Action {
        $collapsed = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 80 `
            -ExpectedHeight 80 `
            -TimeoutMilliseconds 1500
        Assert-AuditCondition ($null -ne $collapsed) 'The widget is not collapsed.'

        $centerX = [int](($collapsed.Bounds.Left + $collapsed.Bounds.Right) / 2)
        $centerY = [int](($collapsed.Bounds.Top + $collapsed.Bounds.Bottom) / 2)
        [CodexWidgetUiAuditNative]::SetCursorPos($centerX, $centerY) | Out-Null
        [CodexWidgetUiAuditNative]::MouseMove()
        $expanded = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 420 `
            -ExpectedHeight 540 `
            -TimeoutMilliseconds 4000
        Assert-AuditCondition `
            ($null -ne $expanded) `
            'The widget did not expand before the edge drag.'

        $workArea = [System.Windows.Forms.Screen]::FromHandle(
            $script:MainHandle).WorkingArea
        $scale = Get-WindowScale -Handle $expanded.Handle
        $collapsedPhysicalWidth = [int][Math]::Round(80 * $scale)
        $collapsedPhysicalHeight = [int][Math]::Round(80 * $scale)
        $targetLeft = $workArea.Right - $collapsedPhysicalWidth
        $targetTop = [Math]::Max(
            $workArea.Top,
            [Math]::Min(
                $expanded.Bounds.Top,
                $workArea.Bottom - $collapsedPhysicalHeight))

        # Starting 70 logical pixels from the left keeps the mouse pointer
        # visible at the right edge even while most of the expanded panel is
        # temporarily outside the work area.
        Invoke-WindowDrag `
            -Window $expanded `
            -DeltaX ($targetLeft - $expanded.Bounds.Left) `
            -DeltaY ($targetTop - $expanded.Bounds.Top) `
            -StartLogicalX 70 `
            -StartLogicalY 38

        $dragged = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 420 `
            -ExpectedHeight 540 `
            -TimeoutMilliseconds 2000
        Assert-AuditCondition `
            ($null -ne $dragged) `
            'The widget disappeared during the edge drag.'
        $expandedPhysicalWidth = [int][Math]::Round(420 * $scale)
        $leftExpandedTarget =
            $targetLeft + $collapsedPhysicalWidth - $expandedPhysicalWidth
        $dragTolerance = [Math]::Max(
            4,
            [int][Math]::Round(12 * $scale))
        # With immediate pointer-state rechecks, the app may already have
        # converted the right-edge anchor into its final left-expansion
        # position before this sample. Both positions represent the same
        # collapsed anchor and the next assertions verify that anchor directly.
        $dragReachedEdgeAnchor =
            [Math]::Abs($dragged.Bounds.Left - $targetLeft) -le
                $dragTolerance -or
            [Math]::Abs($dragged.Bounds.Left - $leftExpandedTarget) -le
                $dragTolerance
        Assert-AuditCondition `
            $dragReachedEdgeAnchor `
            ('The edge drag stopped at x={0}; expected anchor x={1} or left-expansion x={2}.' -f
                $dragged.Bounds.Left,
                $targetLeft,
                $leftExpandedTarget)

        Move-PointerOutsideWindow -Window $dragged
        $edgeCollapsed = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 80 `
            -ExpectedHeight 80 `
            -TimeoutMilliseconds ($ExpectedCollapseMaximumMilliseconds + 750)
        Assert-AuditCondition `
            ($null -ne $edgeCollapsed) `
            'The edge-positioned widget did not collapse.'

        $anchorLeft = $edgeCollapsed.Bounds.Left
        $anchorTop = $edgeCollapsed.Bounds.Top
        $edgeDistance = [Math]::Abs(
            $workArea.Right - $edgeCollapsed.Bounds.Right)
        Assert-AuditCondition `
            ($edgeDistance -le [Math]::Max(4, [int][Math]::Round(12 * $scale))) `
            ('The collapsed widget is {0} physical px from the work-area right edge.' -f
                $edgeDistance)

        $edgeCollapsedPath = Join-Path `
            $script:OutputDirectory `
            'edge-collapsed.png'
        Save-WindowScreenshot -Window $edgeCollapsed -Path $edgeCollapsedPath
        $script:Screenshots['edge-collapsed'] = $edgeCollapsedPath

        $centerX = [int]((
            $edgeCollapsed.Bounds.Left +
            $edgeCollapsed.Bounds.Right) / 2)
        $centerY = [int]((
            $edgeCollapsed.Bounds.Top +
            $edgeCollapsed.Bounds.Bottom) / 2)
        [CodexWidgetUiAuditNative]::SetCursorPos($centerX, $centerY) | Out-Null
        [CodexWidgetUiAuditNative]::MouseMove()
        $edgeExpanded = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 420 `
            -ExpectedHeight 540 `
            -TimeoutMilliseconds 4000
        Assert-AuditCondition `
            ($null -ne $edgeExpanded) `
            'The right-edge widget did not expand.'
        # Size can settle one compositor frame before the left-expansion
        # position. Re-read the HWND bounds before containment and pointer
        # assertions so both use the final geometry.
        Start-Sleep -Milliseconds 120
        $settledEdgeExpanded = Get-WindowRecord `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle
        Assert-AuditCondition `
            ($null -ne $settledEdgeExpanded) `
            'The right-edge widget disappeared while expansion settled.'
        $edgeExpanded = $settledEdgeExpanded

        $containTolerance = [Math]::Max(
            4,
            [int][Math]::Round(4 * $scale))
        $fullyContained =
            $edgeExpanded.Bounds.Left -ge ($workArea.Left - $containTolerance) -and
            $edgeExpanded.Bounds.Top -ge ($workArea.Top - $containTolerance) -and
            $edgeExpanded.Bounds.Right -le ($workArea.Right + $containTolerance) -and
            $edgeExpanded.Bounds.Bottom -le ($workArea.Bottom + $containTolerance)
        Assert-AuditCondition `
            $fullyContained `
            ('Expanded bounds ({0},{1})-({2},{3}) exceed work area ({4},{5})-({6},{7}).' -f
                $edgeExpanded.Bounds.Left,
                $edgeExpanded.Bounds.Top,
                $edgeExpanded.Bounds.Right,
                $edgeExpanded.Bounds.Bottom,
                $workArea.Left,
                $workArea.Top,
                $workArea.Right,
                $workArea.Bottom)

        $edgeExpandedPath = Join-Path `
            $script:OutputDirectory `
            'edge-expanded-contained.png'
        Save-WindowScreenshot -Window $edgeExpanded -Path $edgeExpandedPath
        $script:Screenshots['edge-expanded-contained'] = $edgeExpandedPath

        # Right-edge expansion relocates the panel to the left of its fixed
        # 80x80 anchor. The pointer that triggered expansion can therefore be
        # outside the settled 420x540 bounds already, leaving no MouseLeave for
        # an outside-to-outside move. Re-enter once before validating collapse.
        $insideX = [int][Math]::Round(
            $edgeExpanded.Bounds.Left + (200 * $scale))
        $insideY = [int][Math]::Round(
            $edgeExpanded.Bounds.Top + (50 * $scale))
        [CodexWidgetUiAuditNative]::SetCursorPos(
            $insideX,
            $insideY) | Out-Null
        [CodexWidgetUiAuditNative]::MouseMove()
        Start-Sleep -Milliseconds 120
        Move-PointerOutsideWindow -Window $edgeExpanded
        $restored = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 80 `
            -ExpectedHeight 80 `
            -TimeoutMilliseconds ($ExpectedCollapseMaximumMilliseconds + 750)
        Assert-AuditCondition `
            ($null -ne $restored) `
            'The right-edge widget did not collapse after expansion.'

        $anchorTolerance = [Math]::Max(
            3,
            [int][Math]::Round(3 * $scale))
        Assert-AuditCondition `
            ([Math]::Abs($restored.Bounds.Left - $anchorLeft) -le $anchorTolerance -and
                [Math]::Abs($restored.Bounds.Top - $anchorTop) -le $anchorTolerance) `
            ('Collapsed anchor changed from ({0},{1}) to ({2},{3}).' -f
                $anchorLeft,
                $anchorTop,
                $restored.Bounds.Left,
                $restored.Bounds.Top)

        $restoredPath = Join-Path `
            $script:OutputDirectory `
            'edge-anchor-restored.png'
        Save-WindowScreenshot -Window $restored -Path $restoredPath
        $script:Screenshots['edge-anchor-restored'] = $restoredPath
        return ('edge anchor=({0},{1}); expanded panel stayed inside work area; restored within {2} physical px; screenshots={3}, {4}, {5}' -f
            $anchorLeft,
            $anchorTop,
            $anchorTolerance,
            $edgeCollapsedPath,
            $edgeExpandedPath,
            $restoredPath)
    }

    if (-not $edgeOk) {
        $script:AbortReason = 'Right-edge expansion and anchor restoration failed.'
    }
}

if ($null -eq $script:AbortReason) {
    $settingsOk = Invoke-AuditCase -Name 'settings-open-close' -Action {
        $collapsed = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 80 `
            -ExpectedHeight 80 `
            -TimeoutMilliseconds 1200
        Assert-AuditCondition ($null -ne $collapsed) 'The widget is not collapsed.'

        $centerX = [int](($collapsed.Bounds.Left + $collapsed.Bounds.Right) / 2)
        $centerY = [int](($collapsed.Bounds.Top + $collapsed.Bounds.Bottom) / 2)
        [CodexWidgetUiAuditNative]::SetCursorPos($centerX, $centerY) | Out-Null
        [CodexWidgetUiAuditNative]::MouseMove()
        $expanded = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 420 `
            -ExpectedHeight 540 `
            -TimeoutMilliseconds 4000
        Assert-AuditCondition ($null -ne $expanded) 'The widget did not expand for settings.'

        # The size can reach 420x540 one compositor frame before the
        # right-edge anchor finishes moving the panel left. Click against the
        # settled native rectangle so the settings glyph cannot be missed.
        Start-Sleep -Milliseconds 100
        $settledExpanded = Get-WindowRecord `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle
        if ($null -ne $settledExpanded) {
            $expanded = $settledExpanded
        }

        Invoke-SettingsButtonClick -Window $expanded
        $settings = Wait-ForSettingsWindow `
            -Process $script:AuditProcess `
            -MainHandle $script:MainHandle `
            -TimeoutMilliseconds 5000
        Assert-AuditCondition `
            ($null -ne $settings) `
            'The 460x590 settings window did not appear.'

        $settingsRoot =
            [System.Windows.Automation.AutomationElement]::FromHandle(
                $settings.Handle)
        $obsoleteSkinCondition =
            New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::
                    AutomationIdProperty,
                'SkinModeComboBox')
        $obsoleteSkinCombo = $settingsRoot.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $obsoleteSkinCondition)
        Assert-AuditCondition `
            ($null -eq $obsoleteSkinCombo) `
            'The removed skin selector is still exposed in settings.'

        $themeCondition =
            New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::
                    AutomationIdProperty,
                'ThemeModeComboBox')
        $themeCombo = $settingsRoot.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $themeCondition)
        Assert-AuditCondition `
            ($null -ne $themeCombo) `
            'The System/Light/Dark theme selector was not exposed to UI Automation.'
        Assert-AuditCondition `
            ($themeCombo.Current.ControlType -eq [System.Windows.Automation.ControlType]::ComboBox) `
            'The accessible theme element is not a ComboBox.'

        $transparencyCondition =
            New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::
                    AutomationIdProperty,
                'GlassTransparencySlider')
        $transparencySlider = $settingsRoot.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $transparencyCondition)
        Assert-AuditCondition `
            ($null -ne $transparencySlider) `
            'The 0-100 glass transparency slider was not exposed to UI Automation.'
        Assert-AuditCondition `
            ($transparencySlider.Current.ControlType -eq [System.Windows.Automation.ControlType]::Slider) `
            'The accessible glass transparency element is not a Slider.'

        $rangeValuePattern = $transparencySlider.GetCurrentPattern(
            [System.Windows.Automation.RangeValuePattern]::Pattern)
        Assert-AuditCondition `
            ($rangeValuePattern.Current.Minimum -eq 0 -and
                $rangeValuePattern.Current.Maximum -eq 100) `
            'The glass transparency slider did not expose the 0-100 range.'

        $languageCondition =
            New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::
                    AutomationIdProperty,
                'LanguageModeComboBox')
        $languageCombo = $settingsRoot.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $languageCondition)
        Assert-AuditCondition `
            ($null -ne $languageCombo) `
            'The System/zh-Hans/English language selector was not exposed to UI Automation.'
        Assert-AuditCondition `
            ($languageCombo.Current.ControlType -eq [System.Windows.Automation.ControlType]::ComboBox) `
            'The accessible language element is not a ComboBox.'

        $path = Join-Path $script:OutputDirectory 'settings.png'
        Save-WindowScreenshot -Window $settings -Path $path
        $script:Screenshots['settings'] = $path
        $settingsDescription = Get-LogicalWindowDescription -Window $settings

        $scale = Get-WindowScale -Handle $settings.Handle
        $closeX = [int][Math]::Round(
            $settings.Bounds.Right - (24 * $scale))
        $closeY = [int][Math]::Round(
            $settings.Bounds.Top + (29 * $scale))
        Invoke-LeftClick -X $closeX -Y $closeY
        $closed = Wait-ForWindowToDisappear `
            -Process $script:AuditProcess `
            -Handle $settings.Handle `
            -TimeoutMilliseconds 4000
        Assert-AuditCondition $closed 'The settings window did not close.'
        # Let WPF restore the modal owner's hit-test state before the final
        # pointer move. Moving in the same native frame can precede the
        # synthetic MouseEnter and therefore has no matching MouseLeave.
        Start-Sleep -Milliseconds 180

        $main = Get-WindowRecord `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle
        Assert-AuditCondition `
            ($null -ne $main) `
            'The main widget disappeared when settings closed.'
        return ('opened at {0}, theme/transparency/language selectors verified, closed successfully; screenshot={1}' -f
            $settingsDescription,
            $path)
    }

    if (-not $settingsOk) {
        $script:AbortReason = 'Settings interaction failed.'
    }
}

if ($null -eq $script:AbortReason) {
    $finalOk = Invoke-AuditCase -Name 'final-collapsed-state' -Action {
        $window = Get-WindowRecord `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle
        Assert-AuditCondition ($null -ne $window) 'The main window disappeared.'
        $scale = Get-WindowScale -Handle $window.Handle
        $logicalWidth = $window.Bounds.Width / $scale
        if ([Math]::Abs($logicalWidth - 420) -le 1.5) {
            # A modal owner can retain its pre-dialog mouse-over state until
            # the first real pointer sample. Deliberately re-enter once so
            # this case validates the app's actual MouseLeave path.
            $insideX = [int][Math]::Round(
                $window.Bounds.Left + (200 * $scale))
            $insideY = [int][Math]::Round(
                $window.Bounds.Top + (50 * $scale))
            [CodexWidgetUiAuditNative]::SetCursorPos(
                $insideX,
                $insideY) | Out-Null
            [CodexWidgetUiAuditNative]::MouseMove()
            Start-Sleep -Milliseconds 120
        }
        Move-PointerOutsideWindow -Window $window

        $collapsed = Wait-ForLogicalWindowSize `
            -Process $script:AuditProcess `
            -Handle $script:MainHandle `
            -ExpectedWidth 80 `
            -ExpectedHeight 80 `
            -TimeoutMilliseconds ($ExpectedCollapseMaximumMilliseconds + 750)
        Assert-AuditCondition `
            ($null -ne $collapsed) `
            'The widget did not finish in its 80x80 collapsed state.'

        $path = Join-Path $script:OutputDirectory 'final-collapsed.png'
        Save-WindowScreenshot -Window $collapsed -Path $path
        $script:Screenshots['final-collapsed'] = $path
        return ('{0}; screenshot={1}' -f
            (Get-LogicalWindowDescription -Window $collapsed),
            $path)
    }

    if (-not $finalOk) {
        $script:AbortReason = 'The final collapsed state failed.'
    }
}

if ($null -ne $script:AbortReason) {
    Add-MissingCasesAsSkipped -Reason $script:AbortReason
}
else {
    Add-MissingCasesAsSkipped -Reason 'A required case was not executed.'
}

$script:AuditSucceeded =
    @($script:Results | Where-Object { $_.status -eq 'FAIL' }).Count -eq 0 -and
    @($script:Results | Where-Object { $_.status -eq 'SKIP' }).Count -eq 0

}
finally {
    if ($null -ne $script:AuditProcess -and
        $script:OwnsProcess -and
        -not $LeaveRunning) {
        try {
            $script:AuditProcess.Refresh()
            if (-not $script:AuditProcess.HasExited) {
                Stop-Process -Id $script:AuditProcess.Id -Force
                $script:AuditProcess.WaitForExit(5000) | Out-Null
            }
        }
        catch {
            Write-Warning ('Could not stop the audit-owned process: {0}' -f
                $_.Exception.Message)
        }
    }

    if ($null -ne $script:OriginalSettingsBytes) {
        [IO.File]::WriteAllBytes(
            $SettingsPath,
            $script:OriginalSettingsBytes)
    }

    if (-not $script:AuditSucceeded) {
        try {
            Get-ChildItem -LiteralPath $OutputDirectory -File |
                Where-Object {
                    -not $script:OriginalOutputFiles.ContainsKey(
                        $_.FullName)
                } |
                ForEach-Object {
                    Remove-Item -LiteralPath $_.FullName -Force
                }
            foreach ($entry in $script:OriginalOutputFiles.GetEnumerator()) {
                [IO.File]::WriteAllBytes(
                    [string]$entry.Key,
                    [byte[]]$entry.Value)
            }
        }
        catch {
            Write-Warning ('Could not restore prior UI evidence: {0}' -f
                $_.Exception.Message)
        }
    }
}

$passCount = @($script:Results | Where-Object { $_.status -eq 'PASS' }).Count
$failCount = @($script:Results | Where-Object { $_.status -eq 'FAIL' }).Count
$skipCount = @($script:Results | Where-Object { $_.status -eq 'SKIP' }).Count
$overall = 'PASS'
if ($failCount -gt 0 -or $skipCount -gt 0) {
    $overall = 'FAIL'
}

$screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
$processIdForReport = $null
if ($null -ne $script:AuditProcess) {
    $processIdForReport = $script:AuditProcess.Id
}

$report = [ordered]@{
    result = $overall
    startedAt = $script:StartedAt.ToString('o')
    completedAt = [DateTimeOffset]::Now.ToString('o')
    executable = $Executable
    executableBytes = (Get-Item -LiteralPath $Executable).Length
    sha256 = (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
    processId = $processIdForReport
    processStartedByAudit = $script:OwnsProcess
    processLeftRunning = (
        $null -ne $script:AuditProcess -and
        (-not $script:OwnsProcess -or $LeaveRunning))
    baselineCollapsedMode = 'Circle'
    originalSettingsRestored = ($null -ne $script:OriginalSettingsBytes)
    display = [ordered]@{
        virtualLeft = $screen.Left
        virtualTop = $screen.Top
        virtualWidth = $screen.Width
        virtualHeight = $screen.Height
    }
    counts = [ordered]@{
        pass = $passCount
        fail = $failCount
        skip = $skipCount
    }
    results = @($script:Results)
    screenshots = $script:Screenshots
}

$reportPath = if ($overall -eq 'PASS') {
    Join-Path $OutputDirectory 'ui-audit.json'
}
else {
    $failedReportDirectory = Join-Path $projectRoot 'artifacts\runtime'
    [IO.Directory]::CreateDirectory($failedReportDirectory) | Out-Null
    Join-Path $failedReportDirectory (
        'ui-audit-failed-' +
        [DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss') +
        '.json')
}
$json = $report | ConvertTo-Json -Depth 8
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($reportPath, $json, $utf8WithoutBom)

Write-Host ''
if ($overall -eq 'PASS') {
    Write-Host ('RESULT: PASS ({0} passed)' -f $passCount) -ForegroundColor Green
}
else {
    Write-Host ('RESULT: FAIL ({0} passed, {1} failed, {2} skipped)' -f
        $passCount,
        $failCount,
        $skipCount) -ForegroundColor Red
}
Write-Host ('Report: {0}' -f $reportPath)
foreach ($entry in $script:Screenshots.GetEnumerator()) {
    Write-Host ('Screenshot [{0}]: {1}' -f $entry.Key, $entry.Value)
}

if ($overall -eq 'PASS') {
    exit 0
}

exit 1
