# Writes src\Sakin\sakin.ico: same mark as Icons.Draw (ink tile, paper horizon, signal dot).
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "..\src\Sakin\sakin.ico"

function Draw-Mark([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = $s * 0.24; $d = $r * 2
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc(0, 0, $d, $d, 180, 90); $p.AddArc($s - $d, 0, $d, $d, 270, 90)
    $p.AddArc($s - $d, $s - $d, $d, $d, 0, 90); $p.AddArc(0, $s - $d, $d, $d, 90, 90); $p.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x1D,0x1C,0x1A))), $p)
    $th = [Math]::Max(1.6, $s * 0.075)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(0xEC,0xE8,0xDF)), $th
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawLine($pen, $s * 0.25, $s * 0.64, $s * 0.75, $s * 0.64)
    $dd = [Math]::Max(3.2, $s * 0.22)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0xE4,0x53,0x1F))), $s*0.5 - $dd/2, $s*0.40 - $dd/2, $dd, $dd)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = foreach ($s in $sizes) { ,(Draw-Mark $s) }
$fs = [System.IO.File]::Create($out); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $b = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$frames[$i].Length); $w.Write([int]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write([byte[]]$f) }
$w.Close()
# a preview png for docs
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot "..\docs\mark-256.png"), $frames[-1])
"wrote $out"
