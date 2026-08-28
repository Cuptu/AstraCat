[CmdletBinding()]
param(
    [ValidateSet("all", "mpv", "ffmpeg")]
    [string]$Component = "all"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$toolsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "runtime\tools"))
$systemTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$workingDirectory = [IO.Path]::GetFullPath((Join-Path $systemTempRoot ("astracat-native-" + [Guid]::NewGuid().ToString("N"))))

if (-not $workingDirectory.StartsWith($systemTempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "临时目录不在系统临时目录内：$workingDirectory"
}

function Assert-PathInsideTools([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = $toolsRoot.TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝修改 runtime/tools 之外的路径：$resolved"
    }
}

function Get-VerifiedDownload(
    [string]$Uri,
    [string]$Destination,
    [string]$ExpectedSha256
) {
    Write-Host "下载 $([IO.Path]::GetFileName($Destination))..." -ForegroundColor Cyan
    Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $Destination
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Destination).Hash
    if ($actual -ne $ExpectedSha256) {
        throw "下载文件 SHA-256 不匹配。期望 $ExpectedSha256，实际 $actual"
    }
}

function Install-LibMpv {
    $archiveName = "mpv-dev-x86_64-20260828-git-182fa6ca49.7z"
    $archivePath = Join-Path $workingDirectory $archiveName
    $archiveUri = "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20260828/$archiveName"
    $archiveHash = "9EFD04D351E09ECA350D01DA1B8B0C406537C037537111BA65AB43C91905635B"
    $dllHash = "82BE8EDD8E61BD7A02458EFAF648D6414E262D59E9873D516A2E107579618FE2"

    Get-VerifiedDownload $archiveUri $archivePath $archiveHash

    $extractDirectory = Join-Path $workingDirectory "mpv"
    New-Item -ItemType Directory -Path $extractDirectory | Out-Null
    $tar = Get-Command tar.exe -ErrorAction SilentlyContinue
    if (-not $tar) { $tar = Get-Command tar -ErrorAction SilentlyContinue }
    if (-not $tar) { throw "未找到 bsdtar，无法解压 libmpv 的 7z 文件。" }
    & $tar.Source -xf $archivePath -C $extractDirectory
    if ($LASTEXITCODE -ne 0) { throw "libmpv 解压失败。" }

    $sourceDll = Get-ChildItem -LiteralPath $extractDirectory -Recurse -File -Filter "libmpv-2.dll" |
        Select-Object -First 1
    if (-not $sourceDll) { throw "libmpv 压缩包中没有 libmpv-2.dll。" }
    $actualDllHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourceDll.FullName).Hash
    if ($actualDllHash -ne $dllHash) {
        throw "libmpv-2.dll SHA-256 不匹配。期望 $dllHash，实际 $actualDllHash"
    }

    $targetDirectory = Join-Path $toolsRoot "mpv"
    Assert-PathInsideTools $targetDirectory
    New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null

    foreach ($obsoleteName in @("mpv.exe", "mpv.com", "libmpv.dll.a", "input.conf", "arctime.lua", "d3dcompiler_43.dll")) {
        $obsoletePath = Join-Path $targetDirectory $obsoleteName
        if (Test-Path -LiteralPath $obsoletePath) { Remove-Item -LiteralPath $obsoletePath -Force }
    }
    $obsoleteHeaders = Join-Path $targetDirectory "include"
    if (Test-Path -LiteralPath $obsoleteHeaders) {
        Assert-PathInsideTools $obsoleteHeaders
        Remove-Item -LiteralPath $obsoleteHeaders -Recurse -Force
    }

    Copy-Item -LiteralPath $sourceDll.FullName -Destination (Join-Path $targetDirectory "libmpv-2.dll") -Force
    Write-Host "libmpv 已安装并通过哈希校验。" -ForegroundColor Green
}

function Install-Ffmpeg {
    $archiveName = "ffmpeg-n8.1.2-44-g7c533d0f86-win64-gpl-shared-8.1.zip"
    $archivePath = Join-Path $workingDirectory $archiveName
    $archiveUri = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-08-20-13-45/$archiveName"
    $archiveHash = "A647DCD8E55323A6F9C367F73BF95E0624C2652F32B9E7A9766394377B84ECE8"

    Get-VerifiedDownload $archiveUri $archivePath $archiveHash

    $extractDirectory = Join-Path $workingDirectory "ffmpeg"
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractDirectory
    $sourceFfmpeg = Get-ChildItem -LiteralPath $extractDirectory -Recurse -File -Filter "ffmpeg.exe" |
        Select-Object -First 1
    if (-not $sourceFfmpeg) { throw "FFmpeg 压缩包中没有 ffmpeg.exe。" }
    $sourceDirectory = $sourceFfmpeg.Directory.FullName
    $sourceFfprobe = Join-Path $sourceDirectory "ffprobe.exe"
    if (-not (Test-Path -LiteralPath $sourceFfprobe)) { throw "FFmpeg 压缩包中没有 ffprobe.exe。" }

    $targetDirectory = Join-Path $toolsRoot "ffmpeg"
    Assert-PathInsideTools $targetDirectory
    if (Test-Path -LiteralPath $targetDirectory) {
        Remove-Item -LiteralPath $targetDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $targetDirectory | Out-Null

    Copy-Item -LiteralPath $sourceFfmpeg.FullName -Destination $targetDirectory
    Copy-Item -LiteralPath $sourceFfprobe -Destination $targetDirectory
    Get-ChildItem -LiteralPath $sourceDirectory -File -Filter "*.dll" |
        Copy-Item -Destination $targetDirectory

    $license = Get-ChildItem -LiteralPath $extractDirectory -Recurse -File |
        Where-Object { $_.Name -in @("LICENSE", "LICENSE.txt") } |
        Select-Object -First 1
    if (-not $license) { throw "FFmpeg 压缩包中没有许可证文件。" }
    Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $targetDirectory "LICENSE.txt")

    @(
        "Distributor: BtbN/FFmpeg-Builds",
        "Release: autobuild-2026-08-20-13-45",
        "Archive: $archiveName",
        "Archive SHA-256: $archiveHash",
        "Source: https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-08-20-13-45"
    ) | Set-Content -LiteralPath (Join-Path $targetDirectory "FFMPEG_SOURCE.txt") -Encoding UTF8

    $encoderList = (& (Join-Path $targetDirectory "ffmpeg.exe") -hide_banner -encoders 2>$null | Out-String)
    foreach ($requiredEncoder in @("libx264", "libx265", "libsvtav1", "aac")) {
        if (-not $encoderList.Contains($requiredEncoder)) {
            throw "固定 FFmpeg 构建缺少必要编码器 $requiredEncoder。"
        }
    }
    Write-Host "BtbN FFmpeg 8.1.2 Shared 已安装并通过校验。" -ForegroundColor Green
}

try {
    New-Item -ItemType Directory -Path $workingDirectory | Out-Null
    New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null

    if ($Component -in @("all", "mpv")) { Install-LibMpv }
    if ($Component -in @("all", "ffmpeg")) { Install-Ffmpeg }
}
finally {
    if (Test-Path -LiteralPath $workingDirectory) {
        Remove-Item -LiteralPath $workingDirectory -Recurse -Force
    }
}
