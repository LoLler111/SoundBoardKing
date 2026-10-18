# Builds the Windows icon from the same artwork as the Android launcher icon
# (android/app/src/main/res/drawable/ic_launcher_foreground.xml): a gold crown over a bar
# on the app's dark background, as a rounded square. Writes a multi-size .ico (PNG entries)
# plus a 256 px PNG preview.
param(
    [string]$IcoPath = (Join-Path $PSScriptRoot '..\src\SoundBoardKing.App\Assets\SoundBoardKing.ico'),
    [string]$PreviewPath = (Join-Path ([IO.Path]::GetTempPath()) 'SoundBoardKing-icon-256.png')
)
Add-Type -AssemblyName System.Drawing

$background = [System.Drawing.Color]::FromArgb(255, 0x17, 0x17, 0x1B)
$gold = [System.Drawing.Color]::FromArgb(255, 0xF2, 0xB3, 0x3D)

# Android viewport is 108 units; the artwork sits around (54, 56). Show a 64-unit square of it.
$cropX = 22.0; $cropY = 24.0; $cropSize = 64.0
$crown = @(@(30,44), @(40,54), @(54,36), @(68,54), @(78,44), @(74,66), @(34,66))
$bar = @(@(34,70), @(74,70), @(74,76), @(34,76))

function Render([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded square background.
    $radius = [Math]::Max(2.0, $size * 0.2)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d - 0.01, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d - 0.01, $size - $d - 0.01, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d - 0.01, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $background), $path)

    $scale = $size / $cropSize
    $brush = New-Object System.Drawing.SolidBrush $gold
    foreach ($shape in @($crown, $bar)) {
        $points = $shape | ForEach-Object { New-Object System.Drawing.PointF ((($_[0] - $cropX) * $scale), (($_[1] - $cropY) * $scale)) }
        $g.FillPolygon($brush, [System.Drawing.PointF[]]$points)
    }
    $g.Dispose()
    return $bitmap
}

# Windows only reliably reads PNG-compressed frames at 256 px (the taskbar ignores small PNG
# frames), so smaller sizes are stored as classic 32-bit DIBs: BITMAPINFOHEADER with double
# height, bottom-up BGRA pixels, then an all-zero 1-bit AND mask (transparency comes from alpha).
function To-Dib([System.Drawing.Bitmap]$bitmap) {
    $size = $bitmap.Width
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = New-Object byte[] ($size * $size * 4)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bitmap.UnlockBits($data)

    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $stream = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $stream
    $w.Write([UInt32]40); $w.Write([Int32]$size); $w.Write([Int32]($size * 2))
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]0)
    $w.Write([UInt32]($pixels.Length + $maskStride * $size))
    $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($row = $size - 1; $row -ge 0; $row--) { $w.Write($pixels, $row * $size * 4, $size * 4) }
    $w.Write((New-Object byte[] ($maskStride * $size)))
    $w.Flush()
    return , $stream.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $bitmap = Render $size
    if ($size -ge 256) {
        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Save($PreviewPath, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $stream.ToArray()
    } else {
        $bytes = To-Dib $bitmap
    }
    $bitmap.Dispose()
    , $bytes
}

# ICO container: header, one 16-byte directory entry per image, then the PNG data.
$file = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $file
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$images[$i].Length); $writer.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write([byte[]]$image) }
$writer.Flush()
New-Item -ItemType Directory -Force (Split-Path $IcoPath) | Out-Null
[System.IO.File]::WriteAllBytes($IcoPath, $file.ToArray())
"{0}: {1} immagini, {2:N1} KB" -f $IcoPath, $sizes.Count, ($file.Length / 1KB)
