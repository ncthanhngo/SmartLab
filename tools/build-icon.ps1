<#
    Generates src/SmartLab.App/Assets/app.ico.

    The Windows shell reads an executable's icon from a real .ico resource, so
    unlike the in-app wordmark this one cannot be vector geometry resolved at
    runtime. This script is therefore the source of truth for the artwork: the
    .ico is a build product, and regenerating it is one command rather than a
    trip through an image editor.

    Artwork: a flask on a rounded tile, with the EVSELab bolt added only at sizes
    where it still reads.

    It was a shield holding a USB stick, which described a tool that only triaged
    removable drives. Seventeen sections later most of them are not about USB at
    all, and a shield reads as antivirus - a claim this app deliberately does not
    make, since naming malware is delegated to Defender. A flask says instrument,
    which is what the interface was rebuilt to be.

    Usage:  pwsh tools/build-icon.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src\SmartLab.App\Assets'
$icoPath = Join-Path $assets 'app.ico'

if (-not (Test-Path $assets)) { New-Item -ItemType Directory -Path $assets | Out-Null }

# Brand colours, matching Themes/Palette.xaml.
$greenLight = [System.Drawing.Color]::FromArgb(255, 0x3B, 0xE8, 0x86)
$greenDark  = [System.Drawing.Color]::FromArgb(255, 0x1B, 0xA0, 0x55)
$plateDark  = [System.Drawing.Color]::FromArgb(255, 0x0A, 0x0F, 0x15)
$bolt       = [System.Drawing.Color]::FromArgb(255, 0xF5, 0xB9, 0x3B)

function New-FlaskPath([single]$s) {
    # A conical flask: a narrow neck that widens to a flat base. Drawn as one
    # polygon because at 16 px anything more detailed - a lip, a meniscus, a
    # rounded shoulder - turns into a smudge that reads as no shape at all.
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $x = { param($v) [single]($v * $s) }

    $p.AddPolygon(@(
        (New-Object System.Drawing.PointF((& $x 0.430), (& $x 0.235))),
        (New-Object System.Drawing.PointF((& $x 0.570), (& $x 0.235))),
        (New-Object System.Drawing.PointF((& $x 0.570), (& $x 0.430))),
        (New-Object System.Drawing.PointF((& $x 0.800), (& $x 0.775))),
        (New-Object System.Drawing.PointF((& $x 0.200), (& $x 0.775))),
        (New-Object System.Drawing.PointF((& $x 0.430), (& $x 0.430)))
    ))

    return $p
}

function New-ShieldPath([single]$s) {
    # Normalised control points scaled to the requested size, so every size is
    # the same drawing rather than a resampled bitmap.
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $x = { param($v) [single]($v * $s) }

    $p.AddLine((& $x 0.50), (& $x 0.055), (& $x 0.885), (& $x 0.195))
    $p.AddLine((& $x 0.885), (& $x 0.195), (& $x 0.885), (& $x 0.505))
    $p.AddBezier(
        (& $x 0.885), (& $x 0.505),
        (& $x 0.885), (& $x 0.735),
        (& $x 0.705), (& $x 0.885),
        (& $x 0.50),  (& $x 0.955))
    $p.AddBezier(
        (& $x 0.50),  (& $x 0.955),
        (& $x 0.295), (& $x 0.885),
        (& $x 0.115), (& $x 0.735),
        (& $x 0.115), (& $x 0.505))
    $p.AddLine((& $x 0.115), (& $x 0.505), (& $x 0.115), (& $x 0.195))
    $p.CloseFigure()
    return $p
}

function New-RoundedRect([single]$l, [single]$t, [single]$r, [single]$b, [single]$radius) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    if ($d -le 0) {
        $p.AddRectangle((New-Object System.Drawing.RectangleF($l, $t, ($r - $l), ($b - $t))))
        return $p
    }
    $p.AddArc($l, $t, $d, $d, 180, 90)
    $p.AddArc(($r - $d), $t, $d, $d, 270, 90)
    $p.AddArc(($r - $d), ($b - $d), $d, $d, 0, 90)
    $p.AddArc($l, ($b - $d), $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [single]$size

    # --- tile ---------------------------------------------------------------
    # A rounded square rather than a shield. A shield reads as antivirus, which is
    # a claim this app does not make: it identifies hiding behaviour and asks
    # Defender to name anything.
    $shield = New-RoundedRect ([single]($s * 0.055)) ([single]($s * 0.055)) `
                              ([single]($s * 0.945)) ([single]($s * 0.945)) `
                              ([single]($s * 0.225))
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF($s, $s)),
        $greenLight, $greenDark)
    $g.FillPath($grad, $shield)
    $grad.Dispose()

    # --- flask, cut out of the tile in the shell's dark ---------------------
    $plate = New-Object System.Drawing.SolidBrush($plateDark)

    $flask = New-FlaskPath $s
    $g.FillPath($plate, $flask)
    $flask.Dispose()

    # --- bolt, only where it still reads ------------------------------------
    # Below 48 px the bolt collapses into a smudge that muddies the flask's
    # silhouette, so small sizes are deliberately simpler. Sits inside the body,
    # where the contents of a flask would be.
    if ($size -ge 48) {
        $pts = @(
            (New-Object System.Drawing.PointF([single]($s * 0.545), [single]($s * 0.485))),
            (New-Object System.Drawing.PointF([single]($s * 0.445), [single]($s * 0.625))),
            (New-Object System.Drawing.PointF([single]($s * 0.505), [single]($s * 0.625))),
            (New-Object System.Drawing.PointF([single]($s * 0.455), [single]($s * 0.735))),
            (New-Object System.Drawing.PointF([single]($s * 0.565), [single]($s * 0.595))),
            (New-Object System.Drawing.PointF([single]($s * 0.500), [single]($s * 0.595)))
        )
        $boltBrush = New-Object System.Drawing.SolidBrush($bolt)
        $g.FillPolygon($boltBrush, $pts)
        $boltBrush.Dispose()
    }

    $plate.Dispose()
    $shield.Dispose()
    $g.Dispose()
    return $bmp
}

# ---- frame encoding ----------------------------------------------------------
# Sizes up to 64 are stored as uncompressed DIBs, larger ones as PNG.
#
# This split is not a preference. GDI+ cannot decode PNG-compressed icon frames,
# so an all-PNG .ico throws "Requested range extends past the end of the array"
# the moment System.Drawing touches it - which is exactly what NotifyIcon does for
# the tray. The Windows shell reads PNG frames happily and needs them for the
# large sizes, where a raw DIB would bloat the file. Hence both.
function ConvertTo-IcoDib([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width
    $h = $bmp.Height

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    # BITMAPINFOHEADER. Height is doubled: the format expects the colour image
    # followed by an AND mask, even when the alpha channel already carries it.
    $bw.Write([uint32]40)
    $bw.Write([int32]$w)
    $bw.Write([int32]($h * 2))
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]0)            # BI_RGB
    $bw.Write([uint32]($w * $h * 4))
    0..3 | ForEach-Object { $bw.Write([uint32]0) }

    # Pixel rows are stored bottom-up.
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B)
            $bw.Write([byte]$c.G)
            $bw.Write([byte]$c.R)
            $bw.Write([byte]$c.A)
        }
    }

    # AND mask: zeroed, since transparency comes from the alpha channel. Rows are
    # 1 bit per pixel padded to a 4-byte boundary.
    $maskStride = [math]::Floor(($w + 31) / 32) * 4
    $blank = New-Object byte[] $maskStride
    for ($y = 0; $y -lt $h; $y++) { $bw.Write($blank) }

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose()
    $ms.Dispose()

    # The leading comma is load-bearing. PowerShell unrolls an array returned from
    # a function into individual pipeline objects, so a plain `return $bytes` hands
    # the caller an Object[] of System.Byte rather than a byte[]. Its Length still
    # reads correctly, so the directory entries come out right while
    # BinaryWriter.Write picks an overload that writes a single byte - producing an
    # .ico whose headers promise data that is not there.
    return ,$bytes
}

# Windows picks the nearest size, so shipping the full ladder keeps the taskbar,
# Alt-Tab, Explorer tiles and the tray all sharp from one file.
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

$frames = foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size

    if ($size -le 64) {
        $bytes = ConvertTo-IcoDib $bmp
    }
    else {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
        $ms.Dispose()
    }

    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $bytes }
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)

$w.Write([uint16]0)                 # reserved
$w.Write([uint16]1)                 # type: icon
$w.Write([uint16]$frames.Count)

# Directory entries come first, so image data starts after all of them.
$offset = 6 + (16 * $frames.Count)

foreach ($f in $frames) {
    # 256 is encoded as 0 in the single-byte width and height fields.
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim)
    $w.Write([byte]$dim)
    $w.Write([byte]0)               # palette entries
    $w.Write([byte]0)               # reserved
    $w.Write([uint16]1)             # colour planes
    $w.Write([uint16]32)            # bits per pixel
    $w.Write([uint32]$f.Bytes.Length)
    $w.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}

foreach ($f in $frames) { $w.Write($f.Bytes) }

$w.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
$w.Dispose()
$out.Dispose()

$info = Get-Item $icoPath
Write-Output ("wrote {0} ({1:N0} bytes, {2} sizes: {3})" -f `
    $info.FullName, $info.Length, $frames.Count, ($sizes -join ', '))
