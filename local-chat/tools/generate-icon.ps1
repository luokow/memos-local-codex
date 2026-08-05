[CmdletBinding()]
param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $PSScriptRoot '..\src\QwenLocalChat\Assets\qwen-local-chat.ico'
}

function New-RoundedRectanglePath {
    param(
        [single]$X,
        [single]$Y,
        [single]$Width,
        [single]$Height,
        [single]$Radius
    )

    $diameter = [single]($Radius * 2)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

$targetSizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$frames = [System.Collections.Generic.List[object]]::new()

foreach ($targetSize in $targetSizes) {
    $bitmap = [System.Drawing.Bitmap]::new($targetSize, $targetSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

        $outerInset = [single][Math]::Max(1, $targetSize * 0.04)
        $outerRadius = [single][Math]::Max(2, $targetSize * 0.20)
        $outerPath = New-RoundedRectanglePath $outerInset $outerInset ([single]($targetSize - 2 * $outerInset)) ([single]($targetSize - 2 * $outerInset)) $outerRadius
        try {
            $blackBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 8, 8, 9))
            $borderPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 241, 241, 238), [single][Math]::Max(1, $targetSize * 0.018))
            try {
                $graphics.FillPath($blackBrush, $outerPath)
                if ($targetSize -ge 32) { $graphics.DrawPath($borderPen, $outerPath) }
            }
            finally {
                $blackBrush.Dispose()
                $borderPen.Dispose()
            }
        }
        finally { $outerPath.Dispose() }

        $bubbleX = [single]($targetSize * 0.18)
        $bubbleY = [single]($targetSize * 0.25)
        $bubbleWidth = [single]($targetSize * 0.64)
        $bubbleHeight = [single]($targetSize * 0.43)
        $bubbleRadius = [single][Math]::Max(1.5, $targetSize * 0.10)
        $bubblePath = New-RoundedRectanglePath $bubbleX $bubbleY $bubbleWidth $bubbleHeight $bubbleRadius
        $whiteBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 241, 241, 238))
        try {
            $graphics.FillPath($whiteBrush, $bubblePath)
            $tail = [System.Drawing.PointF[]]@(
                [System.Drawing.PointF]::new([single]($targetSize * 0.36), [single]($targetSize * 0.64)),
                [System.Drawing.PointF]::new([single]($targetSize * 0.31), [single]($targetSize * 0.78)),
                [System.Drawing.PointF]::new([single]($targetSize * 0.49), [single]($targetSize * 0.65))
            )
            $graphics.FillPolygon($whiteBrush, $tail)
        }
        finally {
            $whiteBrush.Dispose()
            $bubblePath.Dispose()
        }

        $dotBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 17, 17, 18))
        try {
            $dotDiameter = [single][Math]::Max(1.6, $targetSize * 0.072)
            $dotY = [single]($targetSize * 0.43)
            foreach ($dotCenter in @(0.36, 0.50, 0.64)) {
                $dotX = [single]($targetSize * $dotCenter - $dotDiameter / 2)
                $graphics.FillEllipse($dotBrush, $dotX, $dotY, $dotDiameter, $dotDiameter)
            }
        }
        finally { $dotBrush.Dispose() }

        $memory = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames.Add([pscustomobject]@{ Size = $targetSize; Data = $memory.ToArray() })
        }
        finally { $memory.Dispose() }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutput
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$file = [System.IO.File]::Create($resolvedOutput)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $dataOffset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -ge 256) { [byte]0 } else { [byte]$frame.Size }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Data.Length)
        $writer.Write([uint32]$dataOffset)
        $dataOffset += $frame.Data.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Data) }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Host "Generated $resolvedOutput with $($frames.Count) frames."
