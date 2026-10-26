# 从 Assets/app.svg 生成 PNG 与多尺寸 ICO。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File scripts\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\LN-Mirror\Assets'))
$svg = Join-Path $assets 'app.svg'
if (-not (Test-Path $svg)) { throw "找不到图标源文件：$svg" }

$edgeCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'),
    (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe')
) | Where-Object { $_ -and (Test-Path $_) }
$edge = $edgeCandidates | Select-Object -First 1
if (-not $edge) { throw '生成图标需要 Windows 自带的 Microsoft Edge。' }

function New-ResizedBitmap($source, [int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($source, 0, 0, $size, $size)
    $g.Dispose()
    return $bmp
}

function Get-IconBytes($bmp, [int]$size) {
    $stream = [System.IO.MemoryStream]::new()
    if ($size -eq 256) {
        $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    else {
        $writer = [System.IO.BinaryWriter]::new($stream)
        $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
        $pixelBytes = $size * $size * 4
        $writer.Write([uint32]40)
        $writer.Write([int32]$size)
        $writer.Write([int32]($size * 2))
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]0)
        $writer.Write([uint32]$pixelBytes)
        $writer.Write([int32]0)
        $writer.Write([int32]0)
        $writer.Write([uint32]0)
        $writer.Write([uint32]0)

        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $color = $bmp.GetPixel($x, $y)
                $writer.Write([byte]$color.B)
                $writer.Write([byte]$color.G)
                $writer.Write([byte]$color.R)
                $writer.Write([byte]$color.A)
            }
        }

        $writer.Write([byte[]]::new($maskStride * $size))
        $writer.Dispose()
    }

    $bytes = $stream.ToArray()
    $stream.Dispose()
    return ,$bytes
}

function Save-Icon([string]$path, [array]$images) {
    $stream = [System.IO.File]::Create($path)
    $writer = [System.IO.BinaryWriter]::new($stream)
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count

    foreach ($image in $images) {
        $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Bytes.Length
    }

    foreach ($image in $images) {
        $writer.Write([byte[]]$image.Bytes)
    }

    $writer.Dispose()
    $stream.Dispose()
}

$temp = Join-Path $assets '.icon-render'
New-Item -ItemType Directory -Force -Path $temp | Out-Null
$render = Join-Path $temp 'app-1024.png'

try {
    $arguments = @(
        '--headless', '--disable-gpu', '--disable-extensions', '--hide-scrollbars', '--no-first-run',
        '--force-device-scale-factor=1', '--default-background-color=00000000', '--window-size=1024,1024',
        "--user-data-dir=$(Join-Path $temp 'edge-profile')", "--screenshot=$render", [System.Uri]::new($svg).AbsoluteUri
    )
    $process = Start-Process -FilePath $edge -ArgumentList $arguments -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $temp 'edge-output.log') `
        -RedirectStandardError (Join-Path $temp 'edge-error.log')
    if ($process.ExitCode -ne 0 -or -not (Test-Path $render)) { throw 'Edge 未能渲染 SVG 图标。' }

    $source = [System.Drawing.Bitmap]::new($render)
    try {
        if ($source.Width -ne 1024 -or $source.Height -ne 1024) {
            throw "SVG 渲染尺寸异常：$($source.Width)x$($source.Height)"
        }

        $images = @()
        foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
            $bmp = New-ResizedBitmap $source $size
            $images += @{ Size = $size; Bytes = (Get-IconBytes $bmp $size) }
            if ($size -ne 128) {
                Save-Icon (Join-Path $assets "app-$size.ico") @($images[-1])
            }
            $bmp.Dispose()
        }

        Save-Icon (Join-Path $assets 'app.ico') $images
        $bmp = New-ResizedBitmap $source 512
        $bmp.Save((Join-Path $assets 'app.png'), [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
    }
    finally {
        $source.Dispose()
    }
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output "图标已生成：$(Join-Path $assets 'app.ico')"
Get-ChildItem $assets | Select-Object Name, Length | Format-Table -AutoSize
