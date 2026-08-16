
# Generates AM-IMG.ico (blue rounded tile, white "download-to-drive" glyph)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-FramePng([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(1, [int]($size * 0.03))
    $r = [Math]::Max(2, [int]($size * 0.22))
    $side = $size - (2 * $pad)
    $rect = [System.Drawing.Rectangle]::new($pad, $pad, $side, $side)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = 2 * $r
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc(($rect.Right - $d), $rect.Y, $d, $d, 270, 90)
    $path.AddArc(($rect.Right - $d), ($rect.Bottom - $d), $d, $d, 0, 90)
    $path.AddArc($rect.X, ($rect.Bottom - $d), $d, $d, 90, 90)
    $path.CloseFigure()

    $c1 = [System.Drawing.Color]::FromArgb(255, 25, 118, 210)
    $c2 = [System.Drawing.Color]::FromArgb(255, 10, 50, 120)
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect, $c1, $c2, [single]90)
    $g.FillPath($brush, $path)

    # white glyph: arrow pointing down into a tray (imaging / backup)
    $white = [System.Drawing.Brushes]::White
    $cx = $size / 2.0
    $shaftW = $size * 0.14
    $headW = $size * 0.34
    $topY = $size * 0.20
    $headTopY = $size * 0.44
    $tipY = $size * 0.62
    $g.FillRectangle($white, [single]($cx - ($shaftW / 2)), [single]$topY, [single]$shaftW, [single]($headTopY - $topY + 1))
    $p1 = [System.Drawing.PointF]::new([single]($cx - ($headW / 2)), [single]$headTopY)
    $p2 = [System.Drawing.PointF]::new([single]($cx + ($headW / 2)), [single]$headTopY)
    $p3 = [System.Drawing.PointF]::new([single]$cx, [single]$tipY)
    $g.FillPolygon($white, [System.Drawing.PointF[]]@($p1, $p2, $p3))
    # tray
    $trayY = $size * 0.70
    $trayH = [Math]::Max(1.0, $size * 0.09)
    $trayW = $size * 0.52
    $g.FillRectangle($white, [single]($cx - ($trayW / 2)), [single]$trayY, [single]$trayW, [single]$trayH)

    $g.Dispose()
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 256, 48, 32, 16
$frames = @()
foreach ($s in $sizes) { $frames += ,(New-FramePng $s) }

$out = [System.IO.MemoryStream]::new()
$w = [System.IO.BinaryWriter]::new($out)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([Byte]$dim); $w.Write([Byte]$dim)
    $w.Write([Byte]0); $w.Write([Byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]($frames[$i].Length))
    $w.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write([byte[]]$f) }
$w.Flush()
[System.IO.File]::WriteAllBytes("$PSScriptRoot\AM-IMG.ico", $out.ToArray())
Write-Host "Icon written: $PSScriptRoot\AM-IMG.ico ($($out.Length) bytes)"
