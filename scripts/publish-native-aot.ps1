<#
.SYNOPSIS
发布原版 Avalonia AstraCat 的 Windows x64 Native AOT 开发候选。
独立生成目录，不调用 package-release.ps1、不删除既有 dist 或用户 runtime。
#>
param(
    [string]$OutputDirectory = "",
    [string]$AstraCoreDir = $env:ASTRACAT_MEDIA_RUNTIME
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($AstraCoreDir)) {
    $defaultAstraCore = Join-Path $projectRoot 'artifacts/astracore/win-x64'
    if (Test-Path (Join-Path $defaultAstraCore 'astracore-runtime.json')) {
        $AstraCoreDir = $defaultAstraCore
    }
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot ('artifacts/native-aot-' + [Guid]::NewGuid().ToString('N'))
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) { throw '输出目录必须不存在，以免覆盖旧产物或数据。' }
New-Item -ItemType Directory -Path $outputRoot | Out-Null
$publishRoot = Join-Path $outputRoot 'publish'
$buildRoot = Join-Path $outputRoot 'build'
$lockPath = Join-Path $outputRoot 'packages.lock.json'
# 保留现有包版本；AOT 工具包使用独立锁文件，避免污染普通发布锁。
Copy-Item -LiteralPath (Join-Path $projectRoot 'packages.lock.json') -Destination $lockPath
Push-Location $projectRoot
try {
    & dotnet publish AstraCat.csproj -c Release -r win-x64 -p:PublishProfile=NativeAot `
        -p:PublishAot=true "-p:ArtifactsPath=$buildRoot" "-p:NuGetLockFilePath=$lockPath" `
        -o $publishRoot 2>&1 | Tee-Object -FilePath (Join-Path $outputRoot 'publish.log')
    if ($LASTEXITCODE -ne 0) { throw 'Native AOT 发布失败，请查看 publish.log。' }

    if (-not [string]::IsNullOrWhiteSpace($AstraCoreDir)) {
        $AstraCoreDir = [IO.Path]::GetFullPath($AstraCoreDir)
        & (Join-Path $projectRoot 'scripts/Test-AstraCoreRuntime.ps1') -RuntimeDirectory $AstraCoreDir
        $legacyMpv = Join-Path $publishRoot 'runtime/tools/mpv'
        $legacyFfmpeg = Join-Path $publishRoot 'runtime/tools/ffmpeg'
        if (Test-Path -LiteralPath $legacyMpv) { Remove-Item -LiteralPath $legacyMpv -Recurse -Force }
        if (Test-Path -LiteralPath $legacyFfmpeg) { Remove-Item -LiteralPath $legacyFfmpeg -Recurse -Force }
        $targetAstraCore = Join-Path $publishRoot 'runtime/tools/astracore/win-x64'
        New-Item -ItemType Directory -Path $targetAstraCore -Force | Out-Null
        Copy-Item -Path (Join-Path $AstraCoreDir '*') -Destination $targetAstraCore -Recurse -Force
        & (Join-Path $projectRoot 'scripts/Test-AstraCoreRuntime.ps1') -RuntimeDirectory $targetAstraCore -DistributionRoot $publishRoot
    }

    $report = Join-Path $outputRoot 'smoke.txt'
    $process = Start-Process -FilePath (Join-Path $publishRoot 'AstraCat.exe') `
        -ArgumentList @('--native-aot-smoke', ('"' + $report + '"')) `
        -WorkingDirectory $publishRoot -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) {
        $process.Kill()
        throw 'AOT 离线契约检查超时。'
    }
    if ($process.ExitCode -ne 0) { throw "AOT 离线契约检查失败：$report" }
    $files = Get-ChildItem -LiteralPath $publishRoot -File -Recurse
    $files | Select-Object FullName, Length | ConvertTo-Json -Depth 3 |
        Set-Content -LiteralPath (Join-Path $outputRoot 'files.json') -Encoding utf8
    Write-Host "AOT 开发候选：$publishRoot"
    Write-Host '尚需真实播放、导出、模型和干净机验收；PDB 保留用于诊断。'
} finally { Pop-Location }
