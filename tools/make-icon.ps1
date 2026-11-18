# Draws the CaptureFix icon (a screen with a play mark on a warm-to-cool "HDR" gradient tile) and
# writes a multi-size .ico with PNG entries. Usage: make-icon.ps1 [-Out src\CaptureFix\Assets\CaptureFix.ico]
param([string]$Out = (Join-Path $PSScriptRoot '..\src\CaptureFix\Assets\CaptureFix.ico'))
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
function Rounded($x, $y, $w, $h, $r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.AddArc($x, $y, 2*$r, 2*$r, 180, 90); $p.AddArc($x+$w-2*$r, $y, 2*$r, 2*$r, 270, 90)
  $p.AddArc($x+$w-2*$r, $y+$h-2*$r, 2*$r, 2*$r, 0, 90); $p.AddArc($x, $y+$h-2*$r, 2*$r, 2*$r, 90, 90); $p.CloseFigure(); $p
}
$pngs = foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
  $k = $s / 256.0
  # Tile: deep blue to violet, with a warm glow in the corner (HDR highlight)
  $tile = Rounded 0 0 $s $s (56*$k)
  $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0,0), (New-Object System.Drawing.PointF $s,$s), ([System.Drawing.Color]::FromArgb(0x25,0x63,0xEB)), ([System.Drawing.Color]::FromArgb(0x7C,0x3A,0xED))
  $g.FillPath($grad, $tile)
  $g.SetClip($tile)
  $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
  $glow.AddEllipse([single](120*$k), [single](-60*$k), [single](220*$k), [single](220*$k))
  $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
  $pgb.CenterColor = [System.Drawing.Color]::FromArgb(200, 0xFB, 0xBF, 0x24); $pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 0xFB, 0xBF, 0x24))
  $g.FillPath($pgb, $glow); $g.ResetClip()
  # Screen
  $pw = [Math]::Max(1.6, 16*$k)
  $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $pw
  $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
  $scr = Rounded (40*$k) (58*$k) (176*$k) (118*$k) (18*$k)
  $g.DrawPath($pen, $scr)
  # Stand
  $g.DrawLine($pen, [single](128*$k), [single](178*$k), [single](128*$k), [single](204*$k))
  if ($s -ge 24) { $g.DrawLine($pen, [single](92*$k), [single](206*$k), [single](164*$k), [single](206*$k)) }
  # Play mark
  $tri = New-Object System.Drawing.Drawing2D.GraphicsPath
  $tri.AddPolygon([System.Drawing.PointF[]]@((New-Object System.Drawing.PointF ([single](112*$k)), ([single](90*$k))), (New-Object System.Drawing.PointF ([single](112*$k)), ([single](144*$k))), (New-Object System.Drawing.PointF ([single](156*$k)), ([single](117*$k)))))
  $g.FillPath([System.Drawing.Brushes]::White, $tri)
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  ,$ms.ToArray()
}
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $d = $pngs[$i]
  $b = if ($s -ge 256) { 0 } else { $s }
  $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$d.Length); $w.Write([uint32]$offset); $offset += $d.Length
}
foreach ($d in $pngs) { $w.Write($d) }
$w.Close()
"wrote $Out ($((Get-Item $Out).Length) bytes)"
[System.IO.File]::WriteAllBytes([System.IO.Path]::ChangeExtension($Out, '.preview.png'), $pngs[-1])
