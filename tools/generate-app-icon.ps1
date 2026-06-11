<#
.SYNOPSIS
  Generates src/ohSpy.App/Assets/AppIcon.ico — the ohSpy application/installer icon.

.DESCRIPTION
  Reproducible, dependency-free icon generation using GDI+ (System.Drawing), which ships
  with Windows PowerShell 5.1 — no ImageMagick/Inkscape needed.

  Design: a rounded emerald→teal tile (an OpenHome-flavoured green) bearing a bold white
  "Oh" wordmark over three faint concentric "discovery" waves — a nod to the SSDP
  multicast discovery that ohSpy inspects. The letters O + h satisfy the brief.

  Emits a multi-resolution .ico (16/24/32/48/64/128/256) with PNG-compressed entries
  (the 256px entry MUST be PNG per the ICO spec; PNG entries are supported on Win10/11).

.NOTES
  Run from the repo root:  powershell -ExecutionPolicy Bypass -File tools/generate-app-icon.ps1
#>

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'

# --- palette -----------------------------------------------------------------
$emerald = [System.Drawing.Color]::FromArgb(255, 31, 181, 143)  # #1FB58F  top
$teal    = [System.Drawing.Color]::FromArgb(255, 11,  92,  75)  # #0B5C4B  bottom
$white   = [System.Drawing.Color]::White

function New-OhIconBitmap {
    param([int]$S)

    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # rounded-square tile
    $pad    = [Math]::Max(1, [int]($S * 0.055))
    $x = $pad; $y = $pad; $w = $S - 2*$pad; $h = $S - 2*$pad
    $radius = $S * 0.225
    $d = $radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x,          $y,          $d, $d, 180, 90)
    $path.AddArc($x+$w-$d,    $y,          $d, $d, 270, 90)
    $path.AddArc($x+$w-$d,    $y+$h-$d,    $d, $d,   0, 90)
    $path.AddArc($x,          $y+$h-$d,    $d, $d,  90, 90)
    $path.CloseFigure()

    $rectF = New-Object System.Drawing.RectangleF($x, $y, $w, $h)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rectF, $emerald, $teal, 90.0)
    $g.FillPath($brush, $path)
    $brush.Dispose()

    # concentric "discovery" waves, anchored bottom-left, clipped to the tile.
    # Skip on the tiniest sizes where they'd just be noise.
    if ($S -ge 32) {
        $g.SetClip($path)
        $cx = $x + $w * 0.26
        $cy = $y + $h * 0.78
        for ($i = 1; $i -le 3; $i++) {
            $rr    = $w * (0.17 * $i)
            $penW  = [Math]::Max(1.0, $S * 0.020)
            $alpha = [int](110 - $i * 24)
            $pen   = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb($alpha, 255, 255, 255), $penW)
            $g.DrawArc($pen, ($cx-$rr), ($cy-$rr), ($rr*2), ($rr*2), -78, 76)
            $pen.Dispose()
        }
        $g.ResetClip()
    }

    # "Oh" wordmark, centred (nudged up a touch to sit above the waves' origin)
    $fontSize = $S * 0.46
    $font = New-Object System.Drawing.Font("Segoe UI", $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment     = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    [single]$trX = $x
    [single]$trY = $y - ($h * 0.04)
    [single]$trW = $w
    [single]$trH = $h
    $textRect = New-Object System.Drawing.RectangleF($trX, $trY, $trW, $trH)

    if ($S -ge 32) {
        $shadow = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(70, 0, 0, 0))
        [single]$shOff = $S * 0.012
        [single]$shX = $trX + $shOff
        [single]$shY = $trY + $shOff
        $shRect = New-Object System.Drawing.RectangleF($shX, $shY, $trW, $trH)
        $g.DrawString("Oh", $font, $shadow, $shRect, $sf)
        $shadow.Dispose()
    }
    $fg = New-Object System.Drawing.SolidBrush($white)
    $g.DrawString("Oh", $font, $fg, $textRect, $sf)
    $fg.Dispose()

    $font.Dispose(); $sf.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

function Get-PngBytes {
    param([System.Drawing.Bitmap]$Bmp)
    $ms = New-Object System.IO.MemoryStream
    $Bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

# --- assemble the .ico container --------------------------------------------
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs  = @()
foreach ($s in $sizes) {
    $b = New-OhIconBitmap -S $s
    $pngs += ,(Get-PngBytes -Bmp $b)
    $b.Dispose()
}

$outPath = Join-Path $PSScriptRoot '..\src\ohSpy.App\Assets\AppIcon.ico'
$outPath = [System.IO.Path]::GetFullPath($outPath)

$fs = [System.IO.File]::Create($outPath)
$bw = New-Object System.IO.BinaryWriter($fs)
try {
    # ICONDIR
    $bw.Write([UInt16]0)            # reserved
    $bw.Write([UInt16]1)            # type = icon
    $bw.Write([UInt16]$sizes.Count) # image count

    $headerSize = 6 + 16 * $sizes.Count
    $offset = $headerSize
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s   = $sizes[$i]
        $len = $pngs[$i].Length
        $dim = if ($s -ge 256) { 0 } else { $s }   # 0 means 256 in the ICO spec
        $bw.Write([byte]$dim)       # width
        $bw.Write([byte]$dim)       # height
        $bw.Write([byte]0)          # palette count
        $bw.Write([byte]0)          # reserved
        $bw.Write([UInt16]1)        # colour planes
        $bw.Write([UInt16]32)       # bits per pixel
        $bw.Write([UInt32]$len)     # bytes in resource
        $bw.Write([UInt32]$offset)  # offset from file start
        $offset += $len
    }
    foreach ($png in $pngs) { $bw.Write($png) }
}
finally {
    $bw.Flush(); $bw.Dispose(); $fs.Dispose()
}

$kb = [Math]::Round((Get-Item $outPath).Length / 1KB, 1)
Write-Host "Wrote $outPath ($kb KB, $($sizes.Count) sizes: $($sizes -join ', '))"
