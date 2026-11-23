# Draws the MonitorDial app icon (a monitor whose screen is a brightness slider)
# and writes Assets\MonitorDial.ico plus a 256px PNG for the README.
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot "..\Assets"
New-Item -ItemType Directory -Force -Path $assets | Out-Null

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-Mark([int]$size) {
    # Draw at 4x and scale down so the small sizes stay crisp.
    $s = $size * 4
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $g.Clear([System.Drawing.Color]::Transparent)
    $u = $s / 64.0

    # Background tile.
    $tile = New-RoundedPath (3 * $u) (3 * $u) (58 * $u) (58 * $u) (13 * $u)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
        (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $s), `
        ([System.Drawing.Color]::FromArgb(255, 30, 41, 59)), ([System.Drawing.Color]::FromArgb(255, 11, 17, 28))
    $g.FillPath($bg, $tile)

    $white = [System.Drawing.Color]::FromArgb(255, 241, 245, 249)
    $amber = [System.Drawing.Color]::FromArgb(255, 251, 191, 36)
    $whiteBrush = New-Object System.Drawing.SolidBrush $white
    $amberBrush = New-Object System.Drawing.SolidBrush $amber

    # Screen: the lit part is amber, like a slider filled to about 60%.
    $sx = 12 * $u; $sy = 16 * $u; $sw = 40 * $u; $sh = 25 * $u
    $screen = New-RoundedPath $sx $sy $sw $sh (4 * $u)
    $g.SetClip($screen)
    $g.FillRectangle($amberBrush, $sx, $sy, $sw * 0.6, $sh)
    $g.ResetClip()
    $pen = New-Object System.Drawing.Pen $white, (3.5 * $u)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($pen, $screen)

    # Slider knob on the boundary (skipped at 16px, where it only adds noise).
    if ($size -ge 32) {
        $kx = $sx + $sw * 0.6; $ky = $sy + $sh / 2; $kr = 5.2 * $u
        $knobRing = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 17, 24, 39))
        $g.FillEllipse($knobRing, $kx - $kr - 1.6 * $u, $ky - $kr - 1.6 * $u, 2 * ($kr + 1.6 * $u), 2 * ($kr + 1.6 * $u))
        $g.FillEllipse($whiteBrush, $kx - $kr, $ky - $kr, 2 * $kr, 2 * $kr)
        $knobRing.Dispose()
    }

    # Stand.
    $g.FillRectangle($whiteBrush, 30 * $u, 41 * $u, 4 * $u, 6 * $u)
    $foot = New-RoundedPath (21 * $u) (46 * $u) (22 * $u) (4 * $u) (2 * $u)
    $g.FillPath($whiteBrush, $foot)

    $g.Dispose(); $tile.Dispose(); $bg.Dispose(); $screen.Dispose(); $pen.Dispose()
    $foot.Dispose(); $whiteBrush.Dispose(); $amberBrush.Dispose()

    $out = New-Object System.Drawing.Bitmap $size, $size
    $g2 = [System.Drawing.Graphics]::FromImage($out)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g2.DrawImage($bmp, (New-Object System.Drawing.Rectangle 0, 0, $size, $size))
    $g2.Dispose(); $bmp.Dispose()
    return $out
}

function Write-Ico($path, [System.Drawing.Bitmap[]]$images) {
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([uint16]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]$images.Count)

    $payloads = @()
    foreach ($img in $images) {
        $png = New-Object System.IO.MemoryStream
        $img.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        $payloads += , $png.ToArray()
        $png.Dispose()
    }

    $offset = 6 + (16 * $images.Count)
    for ($i = 0; $i -lt $images.Count; $i++) {
        $img = $images[$i]
        $w = if ($img.Width -ge 256) { 0 } else { [byte]$img.Width }
        $h = if ($img.Height -ge 256) { 0 } else { [byte]$img.Height }
        $bw.Write([byte]$w)
        $bw.Write([byte]$h)
        $bw.Write([byte]0)
        $bw.Write([byte]0)
        $bw.Write([uint16]1)
        $bw.Write([uint16]32)
        $bw.Write([uint32]$payloads[$i].Length)
        $bw.Write([uint32]$offset)
        $offset += $payloads[$i].Length
    }

    foreach ($bytes in $payloads) { $bw.Write($bytes) }
    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
    $bw.Dispose()
    $ms.Dispose()
}

$sizes = 16, 20, 24, 32, 48, 64, 256
$bitmaps = @()
foreach ($size in $sizes) { $bitmaps += New-Mark $size }

$ico = Join-Path $assets "MonitorDial.ico"
Write-Ico $ico $bitmaps
$bitmaps[-1].Save((Join-Path $assets "MonitorDial.png"), [System.Drawing.Imaging.ImageFormat]::Png)
foreach ($bmp in $bitmaps) { $bmp.Dispose() }
Write-Output "Wrote $ico"
