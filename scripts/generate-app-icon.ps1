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
        $lineWidth = [Math]::Max(1.7, 3.0 * $scale)
        $linePen = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::FromArgb(255, 16, 21, 18),
            [single]$lineWidth)
        try {
            $linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $linePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            $knot = New-Object System.Drawing.Drawing2D.GraphicsPath
            try {
                $previousEnd = $null
                $firstStart = $null
                for ($lobe = 0; $lobe -lt 6; $lobe++) {
                    $radians = (-90.0 + ($lobe * 60.0)) * [Math]::PI / 180.0
                    $radialX = [Math]::Cos($radians)
                    $radialY = [Math]::Sin($radians)
                    $tangentX = -$radialY
                    $tangentY = $radialX
                    $designPoints = @(
                        @(4.15, -2.45),
                        @(7.15, -4.25),
                        @(11.15, -3.75),
                        @(12.35, 0.0),
                        @(11.15, 3.75),
                        @(7.15, 4.25),
                        @(4.15, 2.45)
                    )
                    $points = @(
                        foreach ($designPoint in $designPoints) {
                            [System.Drawing.PointF]::new(
                                [single](
                                    $center +
                                    (($radialX * $designPoint[0] +
                                      $tangentX * $designPoint[1]) * $scale)),
                                [single](
                                    $center +
                                    (($radialY * $designPoint[0] +
                                      $tangentY * $designPoint[1]) * $scale)))
                        }
                    )

                    if ($null -eq $previousEnd) {
                        $knot.StartFigure()
                        $firstStart = $points[0]
                    }
                    else {
                        $knot.AddLine($previousEnd, $points[0])
                    }
                    $knot.AddBezier(
                        $points[0],
                        $points[1],
                        $points[2],
                        $points[3])
                    $knot.AddBezier(
                        $points[3],
                        $points[4],
                        $points[5],
                        $points[6])
                    $previousEnd = $points[6]
                }
                $knot.AddLine($previousEnd, $firstStart)
                $knot.CloseFigure()
                $graphics.DrawPath($linePen, $knot)
            }
            finally {
                $knot.Dispose()
            }
        }
        finally {
            $linePen.Dispose()
        }

        $statusBrush = New-Object System.Drawing.SolidBrush(
            [System.Drawing.Color]::FromArgb(255, 8, 122, 75))
        try {
            $dotRadius = [Math]::Max(0.75, 1.5 * $scale)
            $graphics.FillEllipse(
                $statusBrush,
                [single]($center + (11.0 * $scale) - $dotRadius),
                [single]($center - (11.0 * $scale) - $dotRadius),
                [single]($dotRadius * 2),
                [single]($dotRadius * 2))
        }
        finally {
            $statusBrush.Dispose()
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
