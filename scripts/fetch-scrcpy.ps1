<#
.SYNOPSIS
    从 scrcpy 官方 GitHub Release 下载指定版本的 Windows 包，校验 SHA256 后解压到 tools 目录。

.PARAMETER Version
    版本号，例如 v5.0.1。默认使用 tools/SCRCPY-VERSION.txt 中记录的版本。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\fetch-scrcpy.ps1 -Version v5.0.1
#>
[CmdletBinding()]
param(
    [string]$Version = 'v5.0.1',
    [string]$Arch = 'win64'
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolsRoot = Join-Path $repoRoot 'tools'
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("scrcpy-dl-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $temp | Out-Null

$fileName = "scrcpy-$Arch-$Version.zip"
$zipPath = Join-Path $temp $fileName
$sumsPath = Join-Path $temp 'SHA256SUMS.txt'

Write-Output "下载 $fileName …"
Invoke-WebRequest -UseBasicParsing -TimeoutSec 300 -Uri "https://github.com/Genymobile/scrcpy/releases/download/$Version/$fileName" -OutFile $zipPath
Invoke-WebRequest -UseBasicParsing -TimeoutSec 60 -Uri "https://github.com/Genymobile/scrcpy/releases/download/$Version/SHA256SUMS.txt" -OutFile $sumsPath

$expectedLine = Get-Content $sumsPath | Where-Object { $_ -match [regex]::Escape($fileName) } | Select-Object -First 1
if (-not $expectedLine) {
    throw "官方 SHA256SUMS.txt 中找不到 $fileName，终止。"
}

$expected = ($expectedLine -split '\s+')[0].ToLowerInvariant()
$actual = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Output "期望 SHA256：$expected"
Write-Output "实际 SHA256：$actual"

if ($expected -ne $actual) {
    throw "SHA256 不一致，已终止解压。"
}

Write-Output "校验通过，解压到 $toolsRoot"
New-Item -ItemType Directory -Force -Path $toolsRoot | Out-Null
Expand-Archive -Path $zipPath -DestinationPath $toolsRoot -Force

$extracted = Join-Path $toolsRoot "scrcpy-$Arch-$Version"
foreach ($required in 'scrcpy.exe', 'adb.exe', 'scrcpy-server') {
    if (-not (Test-Path (Join-Path $extracted $required))) {
        throw "解压后缺少 $required，请检查包内容。"
    }
}

& (Join-Path $extracted 'scrcpy.exe') --version

Remove-Item $temp -Recurse -Force
Write-Output "完成。请同步更新 tools\SCRCPY-VERSION.txt 中的版本、来源与 SHA256。"