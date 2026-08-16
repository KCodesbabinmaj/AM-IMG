
# Crops the circular AM monogram from the provided logo and builds AM-IMG.ico
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$srcPath = "C:\Users\USHA GUPTA\Downloads\soft\USBImager\design\logo\screen.png"
$img = [System.Drawing.Bitmap]::new($srcPath)

# find bounding box of non-white pixels in the circle badge area only
# (the wordmark "AM-IMG" starts around x=410 in the 1024px logo)
$maxX = [int]($img.Width * 0.365)
$minBx = $img.Width; $minBy = $img.Height; $maxBx = 0; $maxBy = 0
for ($y = 0; $y -lt $img.Height; $y += 2) {
    for ($x = 0; $x -lt $maxX; $x += 2) {
        $c = $img.GetPixel($x, $y)
        if (($c.R -lt 240) -or ($c.G -lt 240) -or ($c.B -lt 240)) {
            if ($x -lt $minBx) { $minBx = $x }
            if ($x -gt $maxBx) { $maxBx = $x }
            if ($y -lt $minBy) { $minBy = $y }
            if ($y -gt $maxBy) { $maxBy = $y }
        }
    }
}
Write-Host "circle bbox: ($minBx,$minBy) - ($maxBx,$maxBy)"
$w = $maxBx - $minBx; $h = $maxBy - $minBy
$side = [Math]::Max($w, $h) + 8
$cx = ($minBx + $maxBx) / 2; $cy = ($minBy + $maxBy) / 2
$x0 = [int]($cx - $side/2); $y0 = [int]($cy - $side/2)

# crop square
$crop = [System.Drawing.Bitmap]::new($side, $side)
$g = [System.Drawing.Graphics]::FromImage($crop)
$g.InterpolationMode = 'HighQualityBicubic'
$srcRect = [System.Drawing.Rectangle]::new($x0, $y0, $side, $side)
$dstRect = [System.Drawing.Rectangle]::new(0, 0, $side, $side)
$g.DrawImage($img, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()

# mask: transparent outside the circle
$r = $side / 2.0 - 1
for ($y = 0; $y -lt $side; $y++) {
    for ($x = 0; $x -lt $side; $x++) {
        $dx = $x - $side/2.0; $dy = $y - $side/2.0
        if ([Math]::Sqrt($dx*$dx + $dy*$dy) -gt $r) {
            $crop.SetPixel($x, $y, [System.Drawing.Color]::Transparent)
        }
    }
}

function Frame([int]$size, [System.Drawing.Bitmap]$source) {
    $b = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($source, 0, 0, $size, $size)
    $g.Dispose()
    $ms = [System.IO.MemoryStream]::new()
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
    return ,$ms.ToArray()
}

$sizes = 256, 48, 32, 16
$frames = @()
foreach ($s in $sizes) { $frames += ,(Frame $s $crop) }

$out = [System.IO.MemoryStream]::new()
$bw = [System.IO.BinaryWriter]::new($out)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim)
    $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]($frames[$i].Length))
    $bw.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $bw.Write([byte[]]$f) }
$bw.Flush()
[System.IO.File]::WriteAllBytes("C:\Users\USHA GUPTA\Downloads\soft\USBImager\src\AM-IMG.ico", $out.ToArray())

# also save a 256px png of the badge for the README / GitHub
$png256 = Frame 256 $crop
[System.IO.File]::WriteAllBytes("C:\Users\USHA GUPTA\Downloads\soft\USBImager\design\logo\badge-256.png", [byte[]]$png256)
$crop.Dispose(); $img.Dispose()
Write-Host "AM-IMG.ico rebuilt from logo ($($out.Length) bytes)"
