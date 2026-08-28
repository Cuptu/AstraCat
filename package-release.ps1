<#
.SYNOPSIS
    AstraCat 一键打包分发脚本 (.NET 10 + libmpv Render API + FFmpeg，去除独立 mpv.exe、Python 和 AI 模型权重)
    自动生成：
      1. dist/AstraCat-v<Version>-Setup.exe (Inno Setup 安装向导程序)
      2. dist/AstraCat-v<Version>-win-x64.zip (免安装绿色包)

.PARAMETER FfmpegDir
    包含 ffmpeg.exe、ffprobe.exe 和 FFmpeg 运行 DLL 的固定 Windows x64 shared 构建目录。
    未指定时依次使用 ASTRACAT_FFMPEG_DIR、仓库 runtime/tools/ffmpeg、
    WinGet 安装的 BtbN FFmpeg 8.1 Shared 和 PATH。
#>

param(
    [string]$Version = $env:ASTRACAT_VERSION,
    [string]$FfmpegDir = $env:ASTRACAT_FFMPEG_DIR,
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression

if ([string]::IsNullOrWhiteSpace($Version)) { $Version = "0.1.0-DEV" }
if ($Version.StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) { $Version = $Version.Substring(1) }
if ($Version -cnotmatch '^\d+\.\d+\.\d+-DEV$') {
    throw "发布失败：DEV 版本号必须是 0.1.0-DEV 这样的格式。当前值：$Version"
}

$rootDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $rootDir) { $rootDir = Get-Location }
$distDir = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $rootDir "dist"
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $rootDir $OutputDirectory))
}
if ([IO.Path]::GetFullPath($distDir).TrimEnd('\') -eq [IO.Path]::GetFullPath($rootDir).TrimEnd('\')) {
    throw "发布失败：输出目录不能是项目根目录。"
}

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "             AstraCat $Version 软件安装分发包构建工具             " -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan

if (Test-Path $distDir) {
    Write-Host "清理旧的 dist 目录..." -ForegroundColor Gray
    Remove-Item -Recurse -Force $distDir
}
New-Item -ItemType Directory -Path $distDir | Out-Null

Write-Host "1. 执行 .NET 10 (win-x64) 独立运行时编译发布..." -ForegroundColor Yellow
$publishDir = Join-Path $distDir "raw-publish"
dotnet restore (Join-Path $rootDir "AstraCat.csproj") -r win-x64 --locked-mode
if ($LASTEXITCODE -ne 0) { throw "发布失败：NuGet 锁定还原失败。" }
dotnet publish (Join-Path $rootDir "AstraCat.csproj") -c Release -r win-x64 --self-contained --no-restore `
    -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "发布失败：dotnet publish 执行失败。" }

# 去除 NuGet 附带的冗余 .pdb 调试符号文件 (节省约 100MB)
Get-ChildItem -Recurse $publishDir -Filter "*.pdb" | Remove-Item -Force

Write-Host "2. 组装标准精简版发布目录 (内置 libmpv Render API)..." -ForegroundColor Yellow
$layoutDir = Join-Path $distDir "AstraCat-win-x64"
New-Item -ItemType Directory -Path $layoutDir | Out-Null
Copy-Item -Recurse "$publishDir\*" $layoutDir

# Open-source notices must travel with both the portable ZIP and installer.
Copy-Item (Join-Path $rootDir "LICENSE") (Join-Path $layoutDir "LICENSE") -Force
Copy-Item (Join-Path $rootDir "README.md") (Join-Path $layoutDir "README.md") -Force
Copy-Item (Join-Path $rootDir "SECURITY.md") (Join-Path $layoutDir "SECURITY.md") -Force
Copy-Item (Join-Path $rootDir "THIRD_PARTY_NOTICES.md") (Join-Path $layoutDir "THIRD_PARTY_NOTICES.md") -Force

# Keep relative README images functional in the portable package and installer.
$readmeBrandTarget = Join-Path $layoutDir "Assets\Brand"
$readmeBadgesTarget = Join-Path $layoutDir "Assets\Badges"
$readmeImagesTarget = Join-Path $layoutDir "docs\images"
New-Item -ItemType Directory -Path $readmeBrandTarget, $readmeBadgesTarget, $readmeImagesTarget -Force | Out-Null
Copy-Item (Join-Path $rootDir "Assets\Brand\AstraCatLogo.png") $readmeBrandTarget -Force
Copy-Item (Join-Path $rootDir "Assets\Badges\*.svg") $readmeBadgesTarget -Force
Copy-Item (Join-Path $rootDir "docs\images\*") $readmeImagesTarget -Force

# 发布目录只保留应用实际加载的 libmpv-2.dll；csproj 已排除 mpv.exe、开发头文件和静态库。
$mpvDir = Join-Path $layoutDir "runtime\tools\mpv"
$libMpvPath = Join-Path $mpvDir "libmpv-2.dll"
if (-not (Test-Path $libMpvPath)) { throw "发布失败：缺少 runtime\tools\mpv\libmpv-2.dll" }
$expectedLibMpvHash = "82BE8EDD8E61BD7A02458EFAF648D6414E262D59E9873D516A2E107579618FE2"
$actualLibMpvHash = (Get-FileHash -Algorithm SHA256 $libMpvPath).Hash
if ($actualLibMpvHash -ne $expectedLibMpvHash) {
    throw "发布失败：libmpv-2.dll SHA-256 不符合固定供应链版本：$actualLibMpvHash"
}
$forbiddenMpvArtifacts = Get-ChildItem -Recurse $layoutDir -File | Where-Object {
    $_.Name -in @("mpv.exe", "mpv.com", "libmpv.dll.a", "input.conf") -or
    $_.Extension -in @(".h", ".a")
}
if ($forbiddenMpvArtifacts) {
    throw "发布失败：发现不应分发的 mpv 工具/开发文件：$($forbiddenMpvArtifacts.FullName -join ', ')"
}

# FFmpeg 作为独立子进程随应用发布。优先使用显式目录，避免发布包依赖用户 PATH；
# PATH 回退只用于本地构建，并将实际版本与哈希写入发布目录供审计。
$repoFfmpegDir = Join-Path $rootDir "runtime\tools\ffmpeg"
if ([string]::IsNullOrWhiteSpace($FfmpegDir) -and
    (Test-Path (Join-Path $repoFfmpegDir "ffmpeg.exe")) -and
    (Test-Path (Join-Path $repoFfmpegDir "ffprobe.exe"))) {
    $FfmpegDir = $repoFfmpegDir
}
if ([string]::IsNullOrWhiteSpace($FfmpegDir)) {
    # AstraCat 的导出能力只依赖 H.264/H.265/SVT-AV1/AAC。优先复用体积更小且
    # 已覆盖这些编码器的 BtbN 8.1 shared 构建，避免 PATH 中的 full build 膨胀安装包。
    $btbnWingetRoot = Join-Path $env:LOCALAPPDATA `
        "Microsoft\WinGet\Packages\BtbN.FFmpeg.GPL.Shared.8.1_Microsoft.Winget.Source_8wekyb3d8bbwe"
    if (Test-Path $btbnWingetRoot) {
        $btbnBin = Get-ChildItem $btbnWingetRoot -Recurse -Directory -Filter "bin" -ErrorAction SilentlyContinue |
            Where-Object {
                (Test-Path (Join-Path $_.FullName "ffmpeg.exe")) -and
                (Test-Path (Join-Path $_.FullName "ffprobe.exe"))
            } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($btbnBin) { $FfmpegDir = $btbnBin.FullName }
    }
}
if ([string]::IsNullOrWhiteSpace($FfmpegDir)) {
    $ffmpegCommand = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    $ffprobeCommand = Get-Command ffprobe.exe -ErrorAction SilentlyContinue
    if ($ffmpegCommand -and $ffprobeCommand -and
        (Split-Path -Parent $ffmpegCommand.Source) -eq (Split-Path -Parent $ffprobeCommand.Source)) {
        $FfmpegDir = Split-Path -Parent $ffmpegCommand.Source
    }
}
if ([string]::IsNullOrWhiteSpace($FfmpegDir)) {
    throw "发布失败：未找到成对的 ffmpeg.exe/ffprobe.exe。请传入 -FfmpegDir 或设置 ASTRACAT_FFMPEG_DIR。"
}
$sourceFfmpeg = Join-Path $FfmpegDir "ffmpeg.exe"
$sourceFfprobe = Join-Path $FfmpegDir "ffprobe.exe"
if (-not (Test-Path $sourceFfmpeg) -or -not (Test-Path $sourceFfprobe)) {
    throw "发布失败：FFmpeg 目录不完整：$FfmpegDir"
}
$sourceFfmpegDlls = @(Get-ChildItem $FfmpegDir -File -Filter "*.dll")
if ($sourceFfmpegDlls.Count -eq 0) {
    throw "发布失败：当前目录不是 FFmpeg shared 构建（未找到运行 DLL）：$FfmpegDir"
}

$ffmpegVersionOutput = @(& $sourceFfmpeg -hide_banner -version 2>&1)
$ffmpegVersionExitCode = $LASTEXITCODE
$ffprobeVersionOutput = @(& $sourceFfprobe -hide_banner -version 2>&1)
$ffprobeVersionExitCode = $LASTEXITCODE
$ffmpegVersionLine = $ffmpegVersionOutput | Select-Object -First 1
$ffprobeVersionLine = $ffprobeVersionOutput | Select-Object -First 1
if ($ffmpegVersionExitCode -ne 0 -or $ffprobeVersionExitCode -ne 0 -or
    [string]::IsNullOrWhiteSpace($ffmpegVersionLine) -or
    [string]::IsNullOrWhiteSpace($ffprobeVersionLine)) {
    throw "发布失败：FFmpeg/FFprobe 版本检查失败。"
}
$encoderList = (& $sourceFfmpeg -hide_banner -encoders 2>$null | Out-String)
foreach ($requiredEncoder in @("libx264", "libx265", "libsvtav1", "aac")) {
    if (-not $encoderList.Contains($requiredEncoder)) {
        throw "发布失败：当前 FFmpeg 缺少必要编码器 $requiredEncoder。"
    }
}

$ffmpegTarget = Join-Path $layoutDir "runtime\tools\ffmpeg"
New-Item -ItemType Directory -Path $ffmpegTarget -Force | Out-Null
Copy-Item $sourceFfmpeg (Join-Path $ffmpegTarget "ffmpeg.exe") -Force
Copy-Item $sourceFfprobe (Join-Path $ffmpegTarget "ffprobe.exe") -Force
Copy-Item $sourceFfmpegDlls.FullName $ffmpegTarget -Force
$ffmpegLicense = @(
    (Join-Path $FfmpegDir "LICENSE.txt"),
    (Join-Path $FfmpegDir "LICENSE"),
    (Join-Path (Split-Path -Parent $FfmpegDir) "LICENSE.txt"),
    (Join-Path (Split-Path -Parent $FfmpegDir) "LICENSE")
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $ffmpegLicense) {
    throw "发布失败：FFmpeg shared 构建缺少 LICENSE.txt 或 LICENSE。"
}
Copy-Item $ffmpegLicense (Join-Path $ffmpegTarget "LICENSE.txt") -Force
$ffmpegManifest = @(
    "FFmpeg: $ffmpegVersionLine",
    "FFprobe: $ffprobeVersionLine"
)
$ffmpegManifest += Get-ChildItem $ffmpegTarget -File |
    Where-Object { $_.Name -ne "FFMPEG_SOURCE.txt" } |
    Sort-Object Name |
    ForEach-Object { "$($_.Name) SHA-256: $((Get-FileHash -Algorithm SHA256 $_.FullName).Hash)" }
$ffmpegManifest | Set-Content (Join-Path $ffmpegTarget "FFMPEG_SOURCE.txt") -Encoding UTF8
Write-Host "   已内置 $ffmpegVersionLine" -ForegroundColor Gray

# 补充 engines 脚本与预设 runtime 目录结构
$enginesTarget = Join-Path $layoutDir "engines"
New-Item -ItemType Directory -Path $enginesTarget -Force | Out-Null
Copy-Item (Join-Path $rootDir "engines\asr_worker.py") $enginesTarget
if (Test-Path (Join-Path $rootDir "engines\README.md")) {
    Copy-Item (Join-Path $rootDir "engines\README.md") $enginesTarget
}
New-Item -ItemType Directory -Path (Join-Path $layoutDir "runtime\models") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $layoutDir "runtime\cache") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $layoutDir "runtime\config") -Force | Out-Null

Write-Host "3. 压缩为标准 ZIP 免安装绿色包..." -ForegroundColor Yellow
$zipPath = Join-Path $distDir "AstraCat-v$Version-win-x64.zip"
[System.IO.Compression.ZipFile]::CreateFromDirectory($layoutDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

# 清理临时 raw-publish
Remove-Item -Recurse -Force $publishDir

# 4. 编译 Inno Setup .exe 安装向导程序
$isccPaths = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$iscc = $isccPaths | Where-Object { Test-Path $_ } | Select-Object -First 1

$exeSetupPath = Join-Path $distDir "AstraCat-v$Version-Setup.exe"
if ($iscc -and (Test-Path (Join-Path $rootDir "installer\AstraCat-Setup.iss"))) {
    Write-Host "4. 正在编译 Windows 原生 .exe 安装程序 (Inno Setup Solid LZMA2)..." -ForegroundColor Yellow
    & $iscc "/DBuildDistDir=$distDir" "/DMyAppVersion=$Version" `
        "/DMyAppOutputBaseFilename=AstraCat-v$Version-Setup" `
        (Join-Path $rootDir "installer\AstraCat-Setup.iss") | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exeSetupPath)) {
        throw "发布失败：Inno Setup 未生成安装程序。"
    }
} else {
    throw "发布失败：未找到 Inno Setup 6 编译器或 installer/AstraCat-Setup.iss。"
}

$releaseHashPath = Join-Path $distDir "AstraCat-v$Version-SHA256.txt"
@($exeSetupPath, $zipPath) | ForEach-Object {
    $hash = (Get-FileHash -Algorithm SHA256 $_).Hash
    "$hash  $(Split-Path -Leaf $_)"
} | Set-Content $releaseHashPath -Encoding ASCII

function Get-FolderSize($path) {
    $files = Get-ChildItem -Recurse $path -File -ErrorAction SilentlyContinue
    return ($files | Measure-Object -Property Length -Sum).Sum
}

$uncompressedBytes = Get-FolderSize $layoutDir
$zipBytes = (Get-Item $zipPath).Length

Write-Host ""
Write-Host "=================================================================" -ForegroundColor Green
Write-Host "                      打包完成！体积统计                          " -ForegroundColor Green
Write-Host "=================================================================" -ForegroundColor Green
Write-Host ("  解压后运行所需物理空间:   " + [math]::Round($uncompressedBytes / 1MB, 2) + " MB (" + $uncompressedBytes + " 字节)")
Write-Host ("  ZIP 免安装压缩包:         " + [math]::Round($zipBytes / 1MB, 2) + " MB -> " + $zipPath)
if (Test-Path $exeSetupPath) {
    $exeBytes = (Get-Item $exeSetupPath).Length
    Write-Host ("  EXE 原生安装向导程序:     " + [math]::Round($exeBytes / 1MB, 2) + " MB -> " + $exeSetupPath)
}
Write-Host "=================================================================" -ForegroundColor Green
