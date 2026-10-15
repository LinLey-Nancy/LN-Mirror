# 生成 LN-Mirror 的应用图标（自绘，避免引入来源不明的二进制资源）
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File scripts\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\LN-Mirror\Assets'))
New-Item -ItemType Directory -Force -Path $assets | Out-Null

function New-FillRect($g, $color, [float]$x, [float]$y, [float]$w, [float]$h) {
    $brush = New-Object System.Drawing.SolidBrush $color
    $g.FillRectangle($brush, [float]$x, [float]$y, [float]$w, [float]$h)
    $brush.Dispose()
}

function New-AppBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [float]($size / 256.0)

    # 深色外框（比机身大若干像素，用两层填充模拟描边，避开 PS 的 RectangleF 重载问题）
    $border = [float][Math]::Max(1.5, 7 * $s)
    New-FillRect $g ([System.Drawing.Color]::FromArgb(255, 48, 48, 48)) (18 * $s) (10 * $s) (220 * $s) (236 * $s)
    New-FillRect $g ([System.Drawing.Color]::FromArgb(255, 246, 246, 244)) (18 * $s + $border) (10 * $s + $border) (220 * $s - 2 * $border) (236 * $s - 2 * $border)

    # 屏幕
    New-FillRect $g ([System.Drawing.Color]::FromArgb(210, 74, 106, 160)) (38 * $s) (30 * $s) (180 * $s) (190 * $s)

    # 屏幕中部的高亮带，视觉上表示"半透明悬浮"
    New-FillRect $g ([System.Drawing.Color]::FromArgb(80, 255, 255, 255)) (38 * $s) (100 * $s) (180 * $s) (46 * $s)

    # 底部指示线
    New-FillRect $g ([System.Drawing.Color]::FromArgb(255, 90, 90, 90)) (14 * $s) (252 * $s) (228 * $s) ([float][Math]::Max(1.5, 5 * $s))

    $g.Dispose()
    return $bmp
}

$targets = @()
foreach ($size in @(16, 24, 32, 48, 64, 256)) {
    $bmp = New-AppBitmap $size
    $icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
    $path = Join-Path $assets "app-$size.ico"
    $stream = [System.IO.File]::Create($path)
    $icon.Save($stream)
    $stream.Close()
    $bmp.Dispose()
    $targets += $path
}

$icon = New-Object System.Drawing.Icon ($targets[3], 48, 48)
$main = Join-Path $assets 'app.ico'
$stream = [System.IO.File]::Create($main)
$icon.Save($stream)
$stream.Close()
$icon.Dispose()

$bmp = New-AppBitmap 512
$bmp.Save((Join-Path $assets 'app.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Output "图标已生成：$main"
Get-ChildItem $assets | Select-Object Name, Length | Format-Table -AutoSize