[CmdletBinding()]
param(
    [string]$OutputPath,
    [string]$PreviewPath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $projectRoot 'src\CodexUsageWidget\Assets\CodexUsageWidget.ico'
}
if ([string]::IsNullOrWhiteSpace($PreviewPath)) {
    $PreviewPath = Join-Path $projectRoot 'docs\screenshots\app-icon-preview.png'
}

$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$PreviewPath = [IO.Path]::GetFullPath($PreviewPath)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $PreviewPath) | Out-Null

Add-Type -AssemblyName System.Drawing

function New-HollowLineIconPng {
    param([int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $scale = $Size / 32.0
        $center = $Size / 2.0
        $lineWidth = [Math]::Max(1.15, 2.05 * $scale)
        $linePen = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::FromArgb(245, 24, 29, 27),
            [single]$lineWidth)
        try {
            $linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $linePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            $loopWidth = 18.5 * $scale
            $loopHeight = 8.8 * $scale
            foreach ($angle in @(0.0, 60.0, 120.0)) {
                $state = $graphics.Save()
                $graphics.TranslateTransform([single]$center, [single]$center)
                $graphics.RotateTransform([single]$angle)
                $graphics.DrawEllipse(
                    $linePen,
                    [single](-$loopWidth / 2),
                    [single](-$loopHeight / 2),
                    [single]$loopWidth,
                    [single]$loopHeight)
                $graphics.Restore($state)
            }
        }
        finally {
            $linePen.Dispose()
        }

        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $aperture = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::Transparent)
        try {
            $apertureSize = [Math]::Max(2.2, 4.2 * $scale)
            $graphics.FillEllipse(
                $aperture,
                [single]($center - ($apertureSize / 2)),
                [single]($center - ($apertureSize / 2)),
                [single]$apertureSize,
                [single]$apertureSize)
        }
        finally {
            $aperture.Dispose()
        }

        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
        $progressWidth = [Math]::Max(1.1, 1.75 * $scale)
        $progressPen = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::FromArgb(255, 8, 122, 75),
            [single]$progressWidth)
        try {
            $progressPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $progressPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $inset = 2.4 * $scale
            $graphics.DrawArc(
                $progressPen,
                [single]$inset,
                [single]$inset,
                [single]($Size - (2 * $inset)),
                [single]($Size - (2 * $inset)),
                -82.0,
                58.0)
        }
        finally {
            $progressPen.Dispose()
        }

        $stream = New-Object IO.MemoryStream
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            return $stream.ToArray()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$entries = @()
foreach ($size in $sizes) {
    $entries += [pscustomobject]@{
        Size = $size
        Bytes = [byte[]](New-HollowLineIconPng -Size $size)
    }
}

$iconStream = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter($iconStream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$entries.Count)
    $offset = 6 + (16 * $entries.Count)
    foreach ($entry in $entries) {
        $dimension = if ($entry.Size -eq 256) { 0 } else { $entry.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$entry.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $entry.Bytes.Length
    }
    foreach ($entry in $entries) {
        $writer.Write([byte[]]$entry.Bytes)
    }
    $writer.Flush()
    [IO.File]::WriteAllBytes($OutputPath, $iconStream.ToArray())
}
finally {
    $writer.Dispose()
    $iconStream.Dispose()
}

[IO.File]::WriteAllBytes(
    $PreviewPath,
    [byte[]]($entries | Where-Object { $_.Size -eq 256 }).Bytes)
[IO.File]::WriteAllBytes(
    ([IO.Path]::ChangeExtension($PreviewPath, '.32.png')),
    [byte[]]($entries | Where-Object { $_.Size -eq 32 }).Bytes)
[IO.File]::WriteAllBytes(
    ([IO.Path]::ChangeExtension($PreviewPath, '.16.png')),
    [byte[]]($entries | Where-Object { $_.Size -eq 16 }).Bytes)

Write-Host ('Generated: {0}' -f $OutputPath)
Write-Host ('Preview: {0}' -f $PreviewPath)
