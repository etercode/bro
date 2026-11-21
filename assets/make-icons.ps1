# Makes Bro's icons from logo.png (in the project root):
#   assets\bro.ico        app / window / installer icon (16-256 px)
#   assets\logo-256.png   installer window
#   ui\logo.png, pages\logo.png   toolbar and page sidebar (128 px, shown at ~28 px)
#
#   powershell -ExecutionPolicy Bypass -File assets\make-icons.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$src = New-Object System.Drawing.Bitmap (Join-Path $root 'logo.png')

# 1. Clean up: drop nearly transparent specks, then trim to the visible artwork
$minAlpha = 48
$rect = New-Object System.Drawing.Rectangle 0, 0, $src.Width, $src.Height
$data = $src.LockBits($rect, 'ReadWrite', [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$bytes = New-Object byte[] ($data.Stride * $src.Height)
[Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
$minX = $src.Width; $minY = $src.Height; $maxX = -1; $maxY = -1
for ($y = 0; $y -lt $src.Height; $y++) {
    $row = $y * $data.Stride
    for ($x = 0; $x -lt $src.Width; $x++) {
        $a = $row + $x * 4 + 3
        if ($bytes[$a] -lt $minAlpha) { $bytes[$a - 3] = 0; $bytes[$a - 2] = 0; $bytes[$a - 1] = 0; $bytes[$a] = 0; continue }
        if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
        if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
    }
}
[Runtime.InteropServices.Marshal]::Copy($bytes, 0, $data.Scan0, $bytes.Length)
$src.UnlockBits($data)

# Square crop around the artwork with a small margin
$w = $maxX - $minX + 1; $h = $maxY - $minY + 1
$side = [int]([Math]::Max($w, $h) * 1.04)
$square = New-Object System.Drawing.Bitmap $side, $side
$g = [System.Drawing.Graphics]::FromImage($square)
$g.DrawImage($src, [int](($side - $w) / 2), [int](($side - $h) / 2), (New-Object System.Drawing.Rectangle $minX, $minY, $w, $h), 'Pixel')
$g.Dispose(); $src.Dispose()

function Resize([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $g.DrawImage($square, 0, 0, $size, $size)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return ,$ms.ToArray()
}

# 2. .ico with every size Windows asks for
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) { ,(Resize $s) }
$out = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
[IO.File]::WriteAllBytes((Join-Path $root 'assets\bro.ico'), $out.ToArray())

# 3. PNGs for the installer, toolbar and pages
[IO.File]::WriteAllBytes((Join-Path $root 'assets\logo-256.png'), (Resize 256))
$small = Resize 128
[IO.File]::WriteAllBytes((Join-Path $root 'ui\logo.png'), $small)
[IO.File]::WriteAllBytes((Join-Path $root 'pages\logo.png'), $small)
$square.Dispose()
Write-Host "Done (artwork $w x $h px, cropped to $side px square)"
