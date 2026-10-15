<#
.SYNOPSIS
    构建 LN-Mirror 的 Windows x64 便携包（自包含），并打成 ZIP。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # 自包含：目标电脑不需要另外安装 .NET 运行时，代价是包约 100 MB
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot 'src\LN-Mirror\LN-Mirror.csproj'
$distRoot = Join-Path $repoRoot 'dist'

$version = (Select-String -Path $project -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
$packageName = "LN-Mirror-$version-win-x64"
$publishDir = Join-Path $distRoot $packageName

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $distRoot | Out-Null

$selfContained = -not $FrameworkDependent
Write-Output "构建 LN-Mirror $version（$Configuration，self-contained=$selfContained）"

$arguments = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', $selfContained.ToString().ToLowerInvariant(),
    '--nologo',
    '-p:DebugType=none',
    '-o', $publishDir
)

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    if ($selfContained) {
        Write-Warning '自包含发布失败（多半是运行时包无法还原，例如没有可用的 NuGet 源）。'
        Write-Warning '自动改为免依赖框架的发布方式：目标电脑需要安装 .NET 10 桌面运行时。'

        $selfContained = $false
        $arguments = @(
            'publish', $project,
            '-c', $Configuration,
            '-r', 'win-x64',
            '--self-contained', 'false',
            '--nologo',
            '-p:DebugType=none',
            '-o', $publishDir
        )
        & dotnet @arguments
    }

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }
}

# 随包附带用户文档与引擎版本记录
Copy-Item (Join-Path $repoRoot 'docs\使用说明.md') $publishDir -Force
Copy-Item (Join-Path $repoRoot 'docs\M0验证清单.md') $publishDir -Force
Copy-Item (Join-Path $repoRoot 'tools\SCRCPY-VERSION.txt') $publishDir -Force

$checklist = Join-Path $repoRoot "docs\测试清单-LN-Mirror-$version.xlsx"
if (Test-Path $checklist) { Copy-Item $checklist $publishDir -Force }

# 发布结果自检：引擎必须完整
$engineDir = Get-ChildItem (Join-Path $publishDir 'tools') -Directory -ErrorAction SilentlyContinue |
    Where-Object { Test-Path (Join-Path $_.FullName 'scrcpy.exe') } |
    Select-Object -First 1

if (-not $engineDir) { throw '发布结果里没有找到内置 scrcpy 引擎，终止打包。' }

foreach ($required in 'scrcpy.exe', 'adb.exe', 'scrcpy-server') {
    if (-not (Test-Path (Join-Path $engineDir.FullName $required))) {
        throw "内置引擎缺少 $required，终止打包。"
    }
}

Write-Output "内置引擎：$($engineDir.Name)"

# 无界面自检：确认包内引擎、adb 与 Win32 透明调用链可用
$reportPath = Join-Path $publishDir 'selftest.txt'
$exe = Join-Path $publishDir 'LN-Mirror.exe'
Write-Output '运行包内环境自检 …'
$process = Start-Process -FilePath $exe -ArgumentList '--self-test', '--report', $reportPath -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "包内自检失败，退出码 $($process.ExitCode)" }
Get-Content $reportPath | ForEach-Object { "  $_" }

$zipPath = Join-Path $distRoot "$packageName.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)

Write-Output ''
Write-Output "交付目录：$publishDir"
Write-Output "交付包：  $zipPath"
Write-Output "包大小：  $zipSize MB"
Write-Output "SHA256：  $zipHash"