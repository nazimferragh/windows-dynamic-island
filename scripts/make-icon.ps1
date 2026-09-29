# Generates assets/icon.ico (multi-size, PNG-compressed entries).
# Run with Windows PowerShell: powershell -ExecutionPolicy Bypass -File scripts\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = New-Object 'System.Collections.Generic.List[byte[]]'

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded-square background with a vivid gradient so it reads on light and dark taskbars.
    $bg = New-RoundedPath 0 0 ($s - 1) ($s - 1) ($s * 0.24)
    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 94, 92, 230)), ([System.Drawing.Color]::FromArgb(255, 191, 90, 242)), 45.0
    $g.FillPath($grad, $bg)

    # The island: a black pill.
    $pw = $s * 0.78; $ph = $s * 0.34
    $px = ($s - $pw) / 2; $py = ($s - $ph) / 2
    $pill = New-RoundedPath $px $py $pw $ph ($ph / 2)
    $g.FillPath([System.Drawing.Brushes]::Black, $pill)

    # Green "live" dot on the left, white equalizer bars on the right.
    $dot = $ph * 0.42
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 48, 209, 88))), ($px + $ph * 0.32), ($py + ($ph - $dot) / 2), $dot, $dot)
    if ($s -ge 24) {
        $bw = [Math]::Max(1.0, $s * 0.035)
        $heights = 0.45, 0.8, 0.55
        for ($i = 0; $i -lt 3; $i++) {
            $bh = $ph * $heights[$i]
            $bx = $px + $pw - $ph * 0.45 - (2 - $i) * $bw * 2.1
            $g.FillRectangle([System.Drawing.Brushes]::White, [single]$bx, [single]($py + ($ph - $bh) / 2), [single]$bw, [single]$bh)
        }
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images.Add($ms.ToArray())
    $bmp.Dispose()
}

$out = Join-Path $PSScriptRoot '..\assets\icon.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
Write-Host "Wrote $out"
