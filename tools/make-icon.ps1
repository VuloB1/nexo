# Draws the Nexo logo (rounded dark square + 4 blue tiles, diagonal pair faded) and writes src\nexo.ico with PNG frames.
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    function RR($x, $y, $w, $r) {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath; $d = 2 * $r
        $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $p.AddArc($x + $w - $d, $y + $w - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $w - $d, $d, $d, 90, 90); $p.CloseFigure(); $p
    }
    $k = $s / 120.0   # the concept is drawn on a 120 grid
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 31, 36))), (RR 0 0 $s (28 * $k)))
    $t = 30 * $k; $r = 9 * $k
    foreach ($c in @(@(26, 26, 255), @(64, 26, 140), @(26, 64, 140), @(64, 64, 255))) {
        $br = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($c[2], 79, 140, 255))
        $g.FillPath($br, (RR ($c[0] * $k) ($c[1] * $k) $t $r))
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , @($s, $ms.ToArray())
}
$out = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$off = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $s = $f[0]; $d = $f[1]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$d.Length); $w.Write([uint32]$off); $off += $d.Length
}
foreach ($f in $frames) { $w.Write($f[1]) }
[System.IO.File]::WriteAllBytes("$PSScriptRoot\..\src\nexo.ico", $out.ToArray())
