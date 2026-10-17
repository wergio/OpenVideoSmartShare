# Generates src/OpenVideoSmartShare/Assets/app.ico: a simple TV glyph with a play triangle on a
# white circle, so that it stands out on dark taskbars too.
# Run with Windows PowerShell 5.1: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\OpenVideoSmartShare\Assets\app.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 32.0

    # White circle background (inset half a pixel so the antialiased edge is not clipped)
    $g.FillEllipse([System.Drawing.Brushes]::White, [single]0.5, [single]0.5, [single]($size - 1), [single]($size - 1))

    # The TV is drawn on a 32x32 grid centred on (16, 16); shrink it so that the bezel
    # corners stay inside the circle.
    $half = [single]($size / 2.0)
    $g.TranslateTransform($half, $half)
    $g.ScaleTransform([single]0.78, [single]0.78)
    $g.TranslateTransform(-$half, -$half)

    # Screen bezel
    $bezel = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = [single](3 * $s); $x = [single](1.5 * $s); $y = [single](3 * $s); $w = [single](29 * $s); $h = [single](20 * $s)
    $bezel.AddArc($x, $y, $r * 2, $r * 2, 180, 90)
    $bezel.AddArc($x + $w - $r * 2, $y, $r * 2, $r * 2, 270, 90)
    $bezel.AddArc($x + $w - $r * 2, $y + $h - $r * 2, $r * 2, $r * 2, 0, 90)
    $bezel.AddArc($x, $y + $h - $r * 2, $r * 2, $r * 2, 90, 90)
    $bezel.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 32, 41, 56))), $bezel)

    # Screen
    $screen = New-Object System.Drawing.RectangleF ([single](4 * $s)), ([single](5.5 * $s)), ([single](24 * $s)), ([single](15 * $s))
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $screen, ([System.Drawing.Color]::FromArgb(255, 30, 136, 229)), ([System.Drawing.Color]::FromArgb(255, 13, 71, 161)), 90
    $g.FillRectangle($grad, $screen)

    # Play triangle
    $tri = @(
        (New-Object System.Drawing.PointF ([single](13 * $s)), ([single](8.5 * $s))),
        (New-Object System.Drawing.PointF ([single](13 * $s)), ([single](17.5 * $s))),
        (New-Object System.Drawing.PointF ([single](20.5 * $s)), ([single](13 * $s)))
    )
    $g.FillPolygon([System.Drawing.Brushes]::White, $tri)

    # Stand
    $stand = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 32, 41, 56))
    $g.FillRectangle($stand, [single](14 * $s), [single](23 * $s), [single](4 * $s), [single](3.5 * $s))
    $g.FillRectangle($stand, [single](9 * $s), [single](26.5 * $s), [single](14 * $s), [single](2.5 * $s))
    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
    $bmp = New-Frame $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container with PNG-compressed entries (supported since Windows Vista).
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
Write-Output "Written $out"
