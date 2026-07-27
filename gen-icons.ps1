# Generates all pixel-art icon assets from a 16x16 grid:
#   Assets\Square150x150Logo.png, Square44x44Logo.png, StoreLogo.png, icon.ico
# Re-run after editing the grid; integer scaling keeps pixels crisp.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Padlock with an eye as the keyhole. '.' = transparent.
$grid = @(
    '................',
    '.....dddddd.....',
    '....dSSSSSSd....',
    '...dSd....dSd...',
    '...dS......Sd...',
    '...dS......Sd...',
    '..BBBBBBBBBBBB..',
    '..BBBBBBBBBBBB..',
    '..BBBKKKKKKBBB..',
    '..BBKKKWWKKKBB..',
    '..BBBKKKKKKBBB..',
    '..BBBBBBBBBBBB..',
    '..BBBBBBBBBBBB..',
    '..hhhhhhhhhhhh..',
    '................',
    '................'
)

# Note: keys must differ case-insensitively (PowerShell hashtables).
$palette = @{
    'd' = [System.Drawing.Color]::FromArgb(255, 0x7A, 0x84, 0x94)  # shackle shadow
    'S' = [System.Drawing.Color]::FromArgb(255, 0xB8, 0xC0, 0xCC)  # shackle
    'B' = [System.Drawing.Color]::FromArgb(255, 0xF2, 0xB4, 0x41)  # body
    'h' = [System.Drawing.Color]::FromArgb(255, 0xC6, 0x8C, 0x1E)  # body shadow
    'K' = [System.Drawing.Color]::FromArgb(255, 0x1E, 0x29, 0x33)  # eye outline/pupil
    'W' = [System.Drawing.Color]::FromArgb(255, 0xE8, 0xF0, 0xF8)  # eye glint
}

function New-PixelBitmap([int]$scale) {
    $bmp = New-Object System.Drawing.Bitmap (16 * $scale), (16 * $scale)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    for ($y = 0; $y -lt 16; $y++) {
        for ($x = 0; $x -lt 16; $x++) {
            $c = $grid[$y][$x]
            if ($c -ne '.') {
                $brush = New-Object System.Drawing.SolidBrush $palette[[string]$c]
                $g.FillRectangle($brush, $x * $scale, $y * $scale, $scale, $scale)
                $brush.Dispose()
            }
        }
    }
    $g.Dispose()
    return $bmp
}

function Save-Centered([System.Drawing.Bitmap]$art, [int]$canvas, [string]$path) {
    $bmp = New-Object System.Drawing.Bitmap $canvas, $canvas
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $off = [int](($canvas - $art.Width) / 2)
    $g.DrawImageUnscaled($art, $off, $off)
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# ICO with classic 32bpp BMP entries (BITMAPINFOHEADER + BGRA rows bottom-up + AND mask).
function Get-IcoEntryBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
    $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write($c.B); $bw.Write($c.G); $bw.Write($c.R); $bw.Write($c.A)
        }
    }
    $maskRow = [Math]::Ceiling($w / 32.0) * 4
    $bw.Write((New-Object byte[] ($maskRow * $h)))
    $bw.Flush()
    return ,$ms.ToArray()   # comma prevents pipeline from unrolling the byte[]
}

function Save-Ico([System.Drawing.Bitmap[]]$bitmaps, [string]$path) {
    $entries = @()
    foreach ($b in $bitmaps) { $entries += ,([byte[]](Get-IcoEntryBytes $b)) }
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$bitmaps.Count)
    $offset = 6 + 16 * $bitmaps.Count
    for ($i = 0; $i -lt $bitmaps.Count; $i++) {
        $b = $bitmaps[$i]
        $bw.Write([byte]($b.Width % 256)); $bw.Write([byte]($b.Height % 256))
        $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([int16]1); $bw.Write([int16]32)
        $bw.Write([int]$entries[$i].Length); $bw.Write([int]$offset)
        $offset += $entries[$i].Length
    }
    foreach ($e in $entries) { $bw.Write([byte[]]$e) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
}

$assets = Join-Path $PSScriptRoot 'Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

$x8 = New-PixelBitmap 8   # 128px
$x3 = New-PixelBitmap 3   # 48px
$x2 = New-PixelBitmap 2   # 32px
$x1 = New-PixelBitmap 1   # 16px

Save-Centered $x8 150 (Join-Path $assets 'Square150x150Logo.png')
Save-Centered $x2 44  (Join-Path $assets 'Square44x44Logo.png')
Save-Centered $x3 50  (Join-Path $assets 'StoreLogo.png')
Save-Ico @($x1, $x2, $x3) (Join-Path $PSScriptRoot 'icon.ico')

$x8.Dispose(); $x3.Dispose(); $x2.Dispose(); $x1.Dispose()
Write-Host 'Icons generated: Assets\*.png, icon.ico'
