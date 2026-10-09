# 開発版(Debugビルド)用アイコン UI/app-dev.ico を、UI/app.ico から生成する。
# 下部にオレンジ色の帯と "DEV" の文字を重ね、インストール版とタスクバー等で見分けられるようにする。
# 生成物(app-dev.ico)はリポジトリにコミット済み。元のapp.icoを更新したときだけ再実行する。
#   使い方: powershell -File ./scripts/New-DevIcon.ps1
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\UI\app.ico'),
    [string]$Output = (Join-Path $PSScriptRoot '..\UI\app-dev.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Source = (Resolve-Path -LiteralPath $Source).ProviderPath
$Output = [IO.Path]::GetFullPath($Output)

# 元アイコンの最大サイズのフレームを取り出す。PNG格納のフレームはSystem.Drawing.Iconでは
# 読めないことがあるため、ICOのディレクトリを自前で読んでPNGならそのまま使う。
function Get-LargestFrame([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $count = [BitConverter]::ToUInt16($bytes, 4)
    $best = $null
    for ($i = 0; $i -lt $count; $i++) {
        $e = 6 + 16 * $i
        $dim = if ($bytes[$e] -eq 0) { 256 } else { [int]$bytes[$e] }
        $len = [BitConverter]::ToUInt32($bytes, $e + 8)
        $off = [BitConverter]::ToUInt32($bytes, $e + 12)
        if ($null -eq $best -or $dim -gt $best.Dim) { $best = [pscustomobject]@{ Dim = $dim; Len = $len; Off = $off } }
    }
    $isPng = $bytes[$best.Off] -eq 0x89 -and $bytes[$best.Off + 1] -eq 0x50
    if ($isPng) {
        $ms = New-Object System.IO.MemoryStream (, $bytes[$best.Off..($best.Off + $best.Len - 1)])
        return New-Object System.Drawing.Bitmap $ms
    }
    $ico = New-Object System.Drawing.Icon($path, $best.Dim, $best.Dim)
    return $ico.ToBitmap()
}
$srcBitmap = Get-LargestFrame $Source
$base = New-Object System.Drawing.Bitmap 256, 256, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($base)
$g.SmoothingMode = 'AntiAlias'
$g.InterpolationMode = 'HighQualityBicubic'
$g.TextRenderingHint = 'AntiAliasGridFit'
$g.DrawImage($srcBitmap, 0, 0, 256, 256)

# 下部の帯 + DEV文字
$bandTop = 168
$band = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 255, 140, 0))
$g.FillRectangle($band, 0, $bandTop, 256, 256 - $bandTop)
$font = New-Object System.Drawing.Font('Segoe UI', 54, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$fmt = New-Object System.Drawing.StringFormat
$fmt.Alignment = 'Center'
$fmt.LineAlignment = 'Center'
$rect = New-Object System.Drawing.RectangleF 0, $bandTop, 256, (256 - $bandTop)
$g.DrawString('DEV', $font, [System.Drawing.Brushes]::White, $rect, $fmt)
$g.Dispose()

# 帯が元アイコンの角丸の外(透明部分)にはみ出さないよう、元アイコンのアルファでマスクする。
$mask = New-Object System.Drawing.Bitmap 256, 256, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$mg = [System.Drawing.Graphics]::FromImage($mask)
$mg.DrawImage($srcBitmap, 0, 0, 256, 256)
$mg.Dispose()
for ($y = $bandTop; $y -lt 256; $y++) {
    for ($x = 0; $x -lt 256; $x++) {
        $ma = $mask.GetPixel($x, $y).A
        $c = $base.GetPixel($x, $y)
        if ($c.A -gt $ma) { $base.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($ma, $c.R, $c.G, $c.B)) }
    }
}

# 各サイズへ縮小してPNG化し、ICOコンテナに詰める。
$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $gg = [System.Drawing.Graphics]::FromImage($bmp)
    $gg.InterpolationMode = 'HighQualityBicubic'
    $gg.PixelOffsetMode = 'HighQuality'
    $gg.DrawImage($base, 0, 0, $s, $s)
    $gg.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [pscustomobject]@{ Size = $s; Data = $ms.ToArray() }
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$f.Data.Length); $w.Write([uint32]$offset)
    $offset += $f.Data.Length
}
foreach ($f in $frames) { $w.Write($f.Data) }
$w.Flush()
[IO.File]::WriteAllBytes($Output, $out.ToArray())
Write-Host "Generated: $Output ($($frames.Count) sizes)"
