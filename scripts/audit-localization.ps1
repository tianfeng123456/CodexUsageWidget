[CmdletBinding()]
param(
    [string]$Executable,
    [string]$SettingsPath = (
        Join-Path $env:LOCALAPPDATA 'CodexUsageWidget\settings.json'),
    [string]$OutputPath
)

# Keep this file ASCII-only so Windows PowerShell 5.1 does not depend on the
# script source encoding. Localized expectations are read from the same XAML
# resource dictionaries as the application.
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Executable)) {
    $Executable = Join-Path $projectRoot 'dist\CodexUsageWidget.exe'
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $projectRoot 'docs\localization-audit.json'
}

$Executable = [IO.Path]::GetFullPath($Executable)
$SettingsPath = [IO.Path]::GetFullPath($SettingsPath)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$englishResourcePath = Join-Path `
    $projectRoot `
    'src\CodexUsageWidget\Resources\Strings.en.xaml'
$chineseResourcePath = Join-Path `
    $projectRoot `
    'src\CodexUsageWidget\Resources\Strings.zh-Hans.xaml'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ('CodexLocalizationAuditNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class CodexLocalizationAuditNative
{
    private delegate bool EnumWindowsCallback(
        IntPtr windowHandle,
        IntPtr state);

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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    public static void SendVirtualKey(IntPtr windowHandle, int virtualKey)
    {
        const uint WindowMessageKeyDown = 0x0100;
        const uint WindowMessageKeyUp = 0x0101;
        if (!PostMessage(
                windowHandle,
                WindowMessageKeyDown,
                new IntPtr(virtualKey),
                IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (!PostMessage(
                windowHandle,
                WindowMessageKeyUp,
                new IntPtr(virtualKey),
                new IntPtr(unchecked((long)0xC0000000))))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public static IntPtr[] GetVisibleWindows(int processId)
    {
        List<IntPtr> result = new List<IntPtr>();
        EnumWindows(
            delegate(IntPtr windowHandle, IntPtr ignored)
            {
                uint ownerProcessId;
                GetWindowThreadProcessId(windowHandle, out ownerProcessId);
                if (ownerProcessId == (uint)processId &&
                    IsWindowVisible(windowHandle))
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
}

function Read-ResourceStrings {
    param([Parameter(Mandatory = $true)][string]$Path)

    $document = New-Object System.Xml.XmlDocument
    $document.PreserveWhitespace = $true
    $document.Load($Path)
    $result = @{}
    foreach ($node in $document.SelectNodes('//*')) {
        if ($null -eq $node.Attributes) {
            continue
        }

        foreach ($attribute in $node.Attributes) {
            if ($attribute.LocalName -eq 'Key' -and
                -not [string]::IsNullOrWhiteSpace($attribute.Value)) {
                $result[$attribute.Value] = $node.InnerText
            }
        }
    }

    return $result
}

function Get-ResourceString {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Resources,
        [Parameter(Mandatory = $true)][string]$Key)

    if (-not $Resources.ContainsKey($Key)) {
        throw "Missing localization resource: $Key"
    }

    return [string]$Resources[$Key]
}

function Get-AutomationElementById {
    param(
        [Parameter(Mandatory = $true)][IntPtr]$WindowHandle,
        [Parameter(Mandatory = $true)][string]$AutomationId)

    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle(
            $WindowHandle)
        $condition = New-Object System.Windows.Automation.PropertyCondition(
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

function Wait-ForApplicationWindowByElementId {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][string]$AutomationId,
        [IntPtr]$ExcludeHandle = [IntPtr]::Zero,
        [int]$TimeoutMilliseconds = 12000)

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "The widget exited with code $($Process.ExitCode)."
        }

        foreach ($handle in
            [CodexLocalizationAuditNative]::GetVisibleWindows($Process.Id)) {
            if ($ExcludeHandle -ne [IntPtr]::Zero -and
                $handle -eq $ExcludeHandle) {
                continue
            }

            $element = Get-AutomationElementById `
                -WindowHandle $handle `
                -AutomationId $AutomationId
            if ($null -ne $element) {
                return [pscustomobject]@{
                    Handle = $handle
                    Element = $element
                }
            }
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Automation element did not appear: $AutomationId"
}

function Get-VisibleElementByName {
    param(
        [Parameter(Mandatory = $true)][IntPtr]$WindowHandle,
        [Parameter(Mandatory = $true)][string]$Name)

    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle(
            $WindowHandle)
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name)
        $elements = $root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $condition)
        foreach ($element in $elements) {
            if (-not $element.Current.IsOffscreen) {
                return $element
            }
        }
    }
    catch [System.Windows.Automation.ElementNotAvailableException] {
    }

    return $null
}

function Wait-ForVisibleNames {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][IntPtr]$WindowHandle,
        [Parameter(Mandatory = $true)][string[]]$Names,
        [int]$TimeoutMilliseconds = 8000)

    $pending = @($Names)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "The widget exited with code $($Process.ExitCode)."
        }

        $pending = @(
            foreach ($name in $pending) {
                $element = Get-VisibleElementByName `
                    -WindowHandle $WindowHandle `
                    -Name $name
                if ($null -eq $element) {
                    $name
                }
            }
        )
        if ($pending.Count -eq 0) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw (
        'Localized UI text did not become visible: ' +
        ($pending -join ', '))
}

function Invoke-AutomationButtonById {
    param(
        [Parameter(Mandatory = $true)][IntPtr]$WindowHandle,
        [Parameter(Mandatory = $true)][string]$AutomationId)

    $button = Get-AutomationElementById `
        -WindowHandle $WindowHandle `
        -AutomationId $AutomationId
    if ($null -eq $button) {
        throw "Automation button not found: $AutomationId"
    }

    $pattern = $button.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Find-ButtonByName {
    param(
        [Parameter(Mandatory = $true)][IntPtr]$WindowHandle,
        [Parameter(Mandatory = $true)][string]$Name)

    $root = [System.Windows.Automation.AutomationElement]::FromHandle(
        $WindowHandle)
    $nameCondition =
        New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name)
    $typeCondition =
        New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::
                ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)
    $condition = New-Object System.Windows.Automation.AndCondition(
        $nameCondition,
        $typeCondition)
    return $root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Invoke-NamedButton {
    param(
        [Parameter(Mandatory = $true)][IntPtr]$WindowHandle,
        [Parameter(Mandatory = $true)][string]$Name)

    $button = Find-ButtonByName `
        -WindowHandle $WindowHandle `
        -Name $Name
    if ($null -eq $button) {
        throw "Named button not found: $Name"
    }

    $pattern = $button.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Get-ListItemByName {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][string]$Name)

    $nameCondition =
        New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name)
    $typeCondition =
        New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::
                ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem)
    $condition = New-Object System.Windows.Automation.AndCondition(
        $nameCondition,
        $typeCondition)

    foreach ($handle in
        [CodexLocalizationAuditNative]::GetVisibleWindows($Process.Id)) {
        try {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle(
                $handle)
            $item = $root.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                $condition)
            if ($null -ne $item -and -not $item.Current.IsOffscreen) {
                return $item
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }
    }

    return $null
}

function Select-Language {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][IntPtr]$SettingsHandle,
        [Parameter(Mandatory = $true)][string]$ItemName,
        [ValidateRange(0, 2)][int]$TargetIndex)

    $combo = Get-AutomationElementById `
        -WindowHandle $SettingsHandle `
        -AutomationId 'LanguageModeComboBox'
    if ($null -eq $combo) {
        throw 'LanguageModeComboBox was not found.'
    }

    $combo.SetFocus()
    Start-Sleep -Milliseconds 100
    [CodexLocalizationAuditNative]::SendVirtualKey($SettingsHandle, 0x24)
    for ($index = 0; $index -lt $TargetIndex; $index++) {
        [CodexLocalizationAuditNative]::SendVirtualKey(
            $SettingsHandle,
            0x28)
    }
    Start-Sleep -Milliseconds 150
    $null = $Process
    $null = $ItemName
}

function Open-Settings {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][IntPtr]$MainHandle)

    Invoke-AutomationButtonById `
        -WindowHandle $MainHandle `
        -AutomationId 'SettingsButton'
    return Wait-ForApplicationWindowByElementId `
        -Process $Process `
        -AutomationId 'LanguageModeComboBox' `
        -ExcludeHandle $MainHandle
}

function Wait-ForWindowToDisappear {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][IntPtr]$WindowHandle,
        [int]$TimeoutMilliseconds = 8000)

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "The widget exited with code $($Process.ExitCode)."
        }

        $handles = @(
            [CodexLocalizationAuditNative]::GetVisibleWindows($Process.Id))
        if ($handles -notcontains $WindowHandle) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw 'The settings window did not close after saving.'
}

function Save-Settings {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][IntPtr]$SettingsHandle,
        [Parameter(Mandatory = $true)][string]$SaveButtonName)

    Invoke-NamedButton `
        -WindowHandle $SettingsHandle `
        -Name $SaveButtonName
    Wait-ForWindowToDisappear `
        -Process $Process `
        -WindowHandle $SettingsHandle
}

function Wait-ForLanguageSetting {
    param(
        [Parameter(Mandatory = $true)][string]$Expected,
        [int]$TimeoutMilliseconds = 5000)

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        try {
            $settings = Get-Content -LiteralPath $SettingsPath -Raw |
                ConvertFrom-Json
            if ([string]$settings.languageMode -eq $Expected) {
                return
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "LanguageMode was not persisted as '$Expected'."
}

if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw "Executable not found: $Executable"
}

if (-not (Test-Path -LiteralPath $SettingsPath -PathType Leaf)) {
    throw "Settings file not found: $SettingsPath"
}

if (-not (Test-Path -LiteralPath $englishResourcePath -PathType Leaf)) {
    throw "English resource file not found: $englishResourcePath"
}

if (-not (Test-Path -LiteralPath $chineseResourcePath -PathType Leaf)) {
    throw "Chinese resource file not found: $chineseResourcePath"
}

if (Get-Process -Name 'CodexUsageWidget' -ErrorAction SilentlyContinue) {
    throw 'Close CodexUsageWidget before auditing localization.'
}

$english = Read-ResourceStrings -Path $englishResourcePath
$chinese = Read-ResourceStrings -Path $chineseResourcePath
$mainKeys = @(
    'Loc.RemainingQuota',
    'Loc.PeriodToday',
    'Loc.PeriodSevenDays',
    'Loc.PeriodMonth',
    'Loc.PeriodAll')
$settingsKeys = @(
    'Loc.SettingsHeader',
    'Loc.Appearance',
    'Loc.Language',
    'Loc.CollapsedMode',
    'Loc.SaveSettings')
$englishMainNames = @(
    foreach ($key in $mainKeys) {
        Get-ResourceString -Resources $english -Key $key
    })
$chineseMainNames = @(
    foreach ($key in $mainKeys) {
        Get-ResourceString -Resources $chinese -Key $key
    })
$englishSettingsNames = @(
    foreach ($key in $settingsKeys) {
        Get-ResourceString -Resources $english -Key $key
    })

$originalSettings = [IO.File]::ReadAllBytes($SettingsPath)
$process = $null
$completed = $false
$result = $null

try {
    $settings = Get-Content -LiteralPath $SettingsPath -Raw |
        ConvertFrom-Json
    $settings |
        Add-Member `
            -NotePropertyName 'languageMode' `
            -NotePropertyValue 'zh-Hans' `
            -Force
    $settings |
        Add-Member `
            -NotePropertyName 'isPinned' `
            -NotePropertyValue $true `
            -Force
    $settings |
        Add-Member `
            -NotePropertyName 'autoCollapse' `
            -NotePropertyValue $false `
            -Force
    $settings |
        Add-Member `
            -NotePropertyName 'collapsedMode' `
            -NotePropertyValue 'Circle' `
            -Force
    $settingsJson = $settings | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText(
        $SettingsPath,
        $settingsJson,
        [Text.UTF8Encoding]::new($false))

    $process = Start-Process -FilePath $Executable -PassThru
    $mainWindow = Wait-ForApplicationWindowByElementId `
        -Process $process `
        -AutomationId 'SettingsButton'
    $mainHandle = [IntPtr]$mainWindow.Handle

    Wait-ForVisibleNames `
        -Process $process `
        -WindowHandle $mainHandle `
        -Names $chineseMainNames

    $settingsWindow = Open-Settings `
        -Process $process `
        -MainHandle $mainHandle
    if ($null -eq (
            Get-AutomationElementById `
                -WindowHandle $settingsWindow.Handle `
                -AutomationId 'CollapsedModeComboBox')) {
        throw 'CollapsedModeComboBox was not found.'
    }
    Select-Language `
        -Process $process `
        -SettingsHandle $settingsWindow.Handle `
        -TargetIndex 2 `
        -ItemName (
            Get-ResourceString `
                -Resources $chinese `
                -Key 'Loc.English')
    Save-Settings `
        -Process $process `
        -SettingsHandle $settingsWindow.Handle `
        -SaveButtonName (
            Get-ResourceString `
                -Resources $chinese `
                -Key 'Loc.SaveSettings')
    Wait-ForLanguageSetting -Expected 'en'
    Wait-ForVisibleNames `
        -Process $process `
        -WindowHandle $mainHandle `
        -Names $englishMainNames

    $settingsWindow = Open-Settings `
        -Process $process `
        -MainHandle $mainHandle
    if ($null -eq (
            Get-AutomationElementById `
                -WindowHandle $settingsWindow.Handle `
                -AutomationId 'CollapsedModeComboBox')) {
        throw 'CollapsedModeComboBox was not found after language switch.'
    }
    Wait-ForVisibleNames `
        -Process $process `
        -WindowHandle $settingsWindow.Handle `
        -Names $englishSettingsNames
    Select-Language `
        -Process $process `
        -SettingsHandle $settingsWindow.Handle `
        -TargetIndex 1 `
        -ItemName (
            Get-ResourceString `
                -Resources $english `
                -Key 'Loc.SimplifiedChinese')
    Save-Settings `
        -Process $process `
        -SettingsHandle $settingsWindow.Handle `
        -SaveButtonName (
            Get-ResourceString `
                -Resources $english `
                -Key 'Loc.SaveSettings')
    Wait-ForLanguageSetting -Expected 'zh-Hans'
    Wait-ForVisibleNames `
        -Process $process `
        -WindowHandle $mainHandle `
        -Names $chineseMainNames

    $result = [ordered]@{
        capturedAt = [DateTimeOffset]::Now.ToString('O')
        status = 'PASS'
        executable = $Executable
        executableSha256 = (
            Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
        executableBytes = (Get-Item -LiteralPath $Executable).Length
        selectors = [ordered]@{
            settingsButton = 'SettingsButton'
            themeModeComboBox = 'ThemeModeComboBox'
            languageModeComboBox = 'LanguageModeComboBox'
            collapsedModeComboBox = 'CollapsedModeComboBox'
        }
        transitions = @(
            [ordered]@{
                from = 'zh-Hans'
                to = 'English'
                persistedValue = 'en'
                visibleMainText = $englishMainNames
                visibleSettingsText = $englishSettingsNames
            },
            [ordered]@{
                from = 'English'
                to = 'zh-Hans'
                persistedValue = 'zh-Hans'
                visibleMainText = $chineseMainNames
            })
        interaction = 'UI Automation focus plus standard keyboard messages; no coordinate-based selectors'
        originalSettingsRestored = $true
    }
    $completed = $true
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

    [IO.File]::WriteAllBytes($SettingsPath, $originalSettings)
}

if ($completed) {
    $outputDirectory = Split-Path -Parent $OutputPath
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    $resultJson = $result | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText(
        $OutputPath,
        $resultJson,
        [Text.UTF8Encoding]::new($false))
    Write-Host "Localization UI audit passed: $OutputPath"
}
