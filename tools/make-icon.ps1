# Generates src/DesktopReplay/Assets/app.ico (PNG-compressed multi-size ICO).
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\DesktopReplay\Assets\app.ico'
$sizes = 16, 24, 32, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = [single]($s * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = [single]($s - 1)
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($w - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($w - $r * 2, $w - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $w - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new($s, $s)), ([System.Drawing.Color]::FromArgb(255, 40, 44, 52)), ([System.Drawing.Color]::FromArgb(255, 18, 20, 24))
    $g.FillPath($bg, $path)
    # circular "replay" arc
    $penW = [single]([Math]::Max(1.5, $s * 0.09))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 235, 235, 240)), $penW
    $m = [single]($s * 0.2)
    $g.DrawArc($pen, $m, $m, $s - 2 * $m, $s - 2 * $m, -60, 300)
    # arrow head at arc start
    $cx = $s / 2.0; $cy = $s / 2.0; $rad = ($s - 2 * $m) / 2.0
    $a = -60 * [Math]::PI / 180
    $ax = $cx + $rad * [Math]::Cos($a); $ay = $cy + $rad * [Math]::Sin($a)
    $hs = $s * 0.16
    $pts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($ax + $hs * 0.9, $ay - $hs * 0.2),
        [System.Drawing.PointF]::new($ax - $hs * 0.3, $ay - $hs * 0.9),
        [System.Drawing.PointF]::new($ax - $hs * 0.1, $ay + $hs * 0.6))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 235, 235, 240))), $pts)
    # red record dot
    $d = [single]($s * 0.3)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 239, 68, 68))), [single]($cx - $d / 2), [single]($cy - $d / 2), $d, $d)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
Write-Output "Wrote $out"
