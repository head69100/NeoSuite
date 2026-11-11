<#
    Transforme un logo (ex. image générée, tuile sombre sur fond uni) en icônes d'une appli Neo :
      <App>\src\<App>\Assets\AppIcon.png  (512 px, coins transparents)
      <App>\src\<App>\Assets\AppIcon.ico  (256, 64, 48, 32, 24, 16 px)
      NeoSuite\Assets\Apps\<App>.png      (512 px, tuile de NeoSuite)

    Le fond autour de la tuile est détecté et retiré, puis les coins sont arrondis.

    Utilisation :  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1 -App NeoBoost -Logo NeoBoost\logo.jpg
#>
param(
    [Parameter(Mandatory)][string]$App,
    [Parameter(Mandatory)][string]$Logo
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$source = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Logo))

# ---- Bords de la tuile : premier pixel nettement différent du fond, sur la ligne et la colonne centrales ----
$bg = $source.GetPixel(2, 2)
function Differs($c) { ([math]::Abs($c.R - $bg.R) + [math]::Abs($c.G - $bg.G) + [math]::Abs($c.B - $bg.B)) -gt 24 }
$midY = [int]($source.Height / 2); $midX = [int]($source.Width / 2)
$left = 0;   while ($left -lt $midX -and -not (Differs $source.GetPixel($left, $midY))) { $left++ }
$right = $source.Width - 1;  while ($right -gt $midX -and -not (Differs $source.GetPixel($right, $midY))) { $right-- }
$top = 0;    while ($top -lt $midY -and -not (Differs $source.GetPixel($midX, $top))) { $top++ }
$bottom = $source.Height - 1; while ($bottom -gt $midY -and -not (Differs $source.GetPixel($midX, $bottom))) { $bottom-- }
# Tuile carrée centrée sur la zone détectée
$size = [math]::Max($right - $left, $bottom - $top) + 1
$crop = New-Object System.Drawing.Rectangle ([int](($left + $right - $size) / 2)), ([int](($top + $bottom - $size) / 2)), $size, $size
Write-Host "Tuile détectée : $crop"

function New-RoundedIcon([int]$px) {
    $bmp = New-Object System.Drawing.Bitmap $px, $px, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Image recadrée mise à l'échelle, peinte à travers un rectangle arrondi (bords lissés)
    $scaled = New-Object System.Drawing.Bitmap $px, $px
    $gs = [System.Drawing.Graphics]::FromImage($scaled)
    $gs.InterpolationMode = 'HighQualityBicubic'; $gs.PixelOffsetMode = 'HighQuality'
    $gs.DrawImage($source, (New-Object System.Drawing.Rectangle 0, 0, $px, $px), $crop, 'Pixel')
    $gs.Dispose()

    $r = [single]($px * 0.2)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($px - 2 * $r - 1, 0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($px - 2 * $r - 1, $px - 2 * $r - 1, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc(0, $px - 2 * $r - 1, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.TextureBrush $scaled
    $g.FillPath($brush, $path)
    $brush.Dispose(); $path.Dispose(); $scaled.Dispose(); $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray() # la virgule évite que PowerShell déroule le tableau d'octets
}

$assets = Join-Path $root "$App\src\$App\Assets"
New-Item -ItemType Directory -Force $assets | Out-Null

$big = New-RoundedIcon 512
$big.Save((Join-Path $assets 'AppIcon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$big.Save((Join-Path $root "NeoSuite\Assets\Apps\$App.png"), [System.Drawing.Imaging.ImageFormat]::Png)
# Applis proposées dans le catalogue de NeoSoft (ex. NeoRar) : l'icône est créée si l'appli y figure,
# même la première fois (avant, seule une icône déjà présente était mise à jour : NeoTorrent n'en avait pas)
$neoSoftIcon = Join-Path $root "NeoSoft\src\NeoSoft\Assets\Apps\$App.png"
$neoSoftCatalog = [System.IO.File]::ReadAllText((Join-Path $root 'NeoSoft\src\NeoSoft\Services\SoftwareCatalog.cs'), [System.Text.Encoding]::UTF8)
if ((Test-Path $neoSoftIcon) -or $neoSoftCatalog.Contains("Id = `"$App`"")) { $big.Save($neoSoftIcon, [System.Drawing.Imaging.ImageFormat]::Png) }
$big.Dispose()

# ---- .ico multi-tailles (images PNG intégrées, format accepté depuis Windows Vista) ----
$sizes = 256, 64, 48, 32, 24, 16
$images = @()
foreach ($s in $sizes) { $b = New-RoundedIcon $s; $images += , (Get-PngBytes $b); $b.Dispose() }
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
[System.IO.File]::WriteAllBytes((Join-Path $assets 'AppIcon.ico'), $ico.ToArray())
$w.Dispose(); $source.Dispose()

Write-Host "Icônes de $App créées dans $assets et NeoSuite\Assets\Apps\$App.png" -ForegroundColor Green
