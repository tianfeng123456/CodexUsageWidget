[CmdletBinding()]
param(
    [string]$ReadyFile,
    [switch]$Topmost
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework

$window = [Windows.Window]::new()
$window.Title = 'Codex Usage Widget Demo Backdrop'
$window.WindowStyle = [Windows.WindowStyle]::None
$window.ResizeMode = [Windows.ResizeMode]::NoResize
$window.WindowState = [Windows.WindowState]::Maximized
$window.ShowInTaskbar = $false
$window.Topmost = $Topmost.IsPresent
$window.Focusable = $false

$brush = [Windows.Media.LinearGradientBrush]::new()
$brush.StartPoint = [Windows.Point]::new(0, 0)
$brush.EndPoint = [Windows.Point]::new(1, 1)
$brush.GradientStops.Add(
    [Windows.Media.GradientStop]::new(
        [Windows.Media.Color]::FromRgb(137, 155, 181),
        0.0))
$brush.GradientStops.Add(
    [Windows.Media.GradientStop]::new(
        [Windows.Media.Color]::FromRgb(143, 177, 183),
        0.48))
$brush.GradientStops.Add(
    [Windows.Media.GradientStop]::new(
        [Windows.Media.Color]::FromRgb(181, 147, 169),
        1.0))
$window.Background = $brush

$surface = [Windows.Controls.Grid]::new()
$surface.Background = $brush
$window.Content = $surface

$window.Add_Loaded({
    if (-not [string]::IsNullOrWhiteSpace($ReadyFile)) {
        $fullPath = [IO.Path]::GetFullPath($ReadyFile)
        [IO.Directory]::CreateDirectory(
            [IO.Path]::GetDirectoryName($fullPath)) | Out-Null
        [IO.File]::WriteAllText(
            $fullPath,
            $PID.ToString(),
            [Text.UTF8Encoding]::new($false))
    }
})

$null = $window.ShowDialog()
