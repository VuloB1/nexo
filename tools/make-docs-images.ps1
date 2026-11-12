# Builds the images used by README.md from the raw screenshots in docs\screenshots (made with NEXO_DEMO=1, see README "Screenshots").
# Look: one flat ink-tinted backdrop, the product window is the only bright thing, soft neutral shadow, no gradients or glow.
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$shots = "$root\docs\screenshots"; $out = "$root\docs\img"
New-Item -ItemType Directory -Force $out | Out-Null
$ink = [System.Drawing.Color]::FromArgb(255, 11, 13, 20)

function RR($x, $y, $w, $h, $r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath; $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90); $p.CloseFigure(); $p
}

# Draws one screenshot as a rounded window with a soft shadow at (x, y), width w.
function Window($g, $img, $x, $y, $w, $radius) {
    $h = [int]($img.Height * $w / $img.Width)
    for ($i = 12; $i -ge 1; $i--) {   # shadow: stacked translucent rounded rects, neutral black
        $b = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb([int](5 + (12 - $i) * 0), 0, 0, 0))
        $g.FillPath($b, (RR ($x - $i) ($y - $i + 12) ($w + 2 * $i) ($h + 2 * $i) ($radius + $i)))
    }
    $clip = RR $x $y $w $h $radius
    $g.SetClip($clip)
    $g.DrawImage($img, $x, $y, $w, $h)
    $g.ResetClip()
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(40, 255, 255, 255), 1)
    $g.DrawPath($pen, $clip)
    $h
}

function Canvas($w, $h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear($ink)
    , @($bmp, $g)
}

function Load($name, $cropH) {
    $src = New-Object System.Drawing.Bitmap "$shots\$name"
    if ($cropH) { $rect = New-Object System.Drawing.Rectangle(0, 0, $src.Width, [Math]::Min($cropH, $src.Height)); $c = $src.Clone($rect, $src.PixelFormat); $src.Dispose(); return $c }
    $src
}

# One window centred on the backdrop.
function Single($name, $file, $winW, $pad, $cropH) {
    $img = Load $name $cropH
    $h = [int]($img.Height * $winW / $img.Width)
    $c = Canvas ($winW + 2 * $pad) ($h + 2 * $pad); $bmp = $c[0]; $g = $c[1]
    Window $g $img $pad $pad $winW 20 | Out-Null
    $g.Dispose(); $bmp.Save("$out\$file", [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); $img.Dispose()
}

Single 'main.png' 'hero.png' 1200 80 $null
Single 'cfg.png' 'customize.png' 900 64 $null
Single 'drop.png' 'reorder.png' 900 64 $null

# search: two cropped windows side by side (results, calculator)
$a = Load 'search.png' 400; $b = Load 'calc.png' 400
$w = 640; $ha = [int]($a.Height * $w / $a.Width); $hb = [int]($b.Height * $w / $b.Width)
$pad = 64; $gap = 48
$c = Canvas (2 * $w + $gap + 2 * $pad) ([Math]::Max($ha, $hb) + 2 * $pad); $bmp = $c[0]; $g = $c[1]
Window $g $a $pad $pad $w 20 | Out-Null; Window $g $b ($pad + $w + $gap) $pad $w 20 | Out-Null
$g.Dispose(); $bmp.Save("$out\search.png", [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); $a.Dispose(); $b.Dispose()

# themes: dark and light side by side
$a = Load 'main.png' $null; $b = Load 'light.png' $null
$w = 720; $h = [int]($a.Height * $w / $a.Width)
$c = Canvas (2 * $w + $gap + 2 * $pad) ($h + 2 * $pad); $bmp = $c[0]; $g = $c[1]
Window $g $a $pad $pad $w 20 | Out-Null; Window $g $b ($pad + $w + $gap) $pad $w 20 | Out-Null
$g.Dispose(); $bmp.Save("$out\themes.png", [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); $a.Dispose(); $b.Dispose()

# installer: welcome and result
foreach ($pair in @(@('installer-welcome.png', 'installer.png'))) { }
$i1 = Load 'installer-welcome.png' $null; $i2 = Load 'installer-done.png' $null
$w = 480; $h = [int]($i1.Height * $w / $i1.Width)
$c = Canvas (2 * $w + $gap + 2 * $pad) ($h + 2 * $pad); $bmp = $c[0]; $g = $c[1]
Window $g $i1 $pad $pad $w 12 | Out-Null; Window $g $i2 ($pad + $w + $gap) $pad $w 12 | Out-Null
$g.Dispose(); $bmp.Save("$out\installer.png", [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); $i1.Dispose(); $i2.Dispose()

# logo: the 256 px frame of the icon
$ico = [System.IO.File]::ReadAllBytes("$root\src\nexo.ico"); $n = [BitConverter]::ToUInt16($ico, 4)
for ($k = 0; $k -lt $n; $k++) {
    $e = 6 + 16 * $k
    if ($ico[$e] -eq 0) { $len = [BitConverter]::ToInt32($ico, $e + 8); $off = [BitConverter]::ToInt32($ico, $e + 12); [System.IO.File]::WriteAllBytes("$out\logo.png", $ico[$off..($off + $len - 1)]) }
}
