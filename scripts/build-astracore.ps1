[CmdletBinding()]
param(
    [string]$RuntimeIdentifier = "win-x64",
    [string]$FfmpegSource = "artifacts/astracore-sources/ffmpeg",
    [string]$MpvSource = "artifacts/astracore-sources/mpv",
    [string]$LibassSource = "artifacts/astracore-sources/libass",
    [string]$LibplaceboSource = "artifacts/astracore-sources/libplacebo",
    [string]$X265Source = "artifacts/astracore-sources/x265",
    [string]$HarfbuzzSource = "artifacts/astracore-sources/harfbuzz",
    [string]$Dav1dSource = "artifacts/astracore-sources/dav1d",
    [string]$OutputDirectory = "artifacts/astracore/$RuntimeIdentifier"
)

$ErrorActionPreference = "Stop"
$supportedRids = @("win-x64", "linux-x64", "osx-arm64", "osx-x64")
if ($RuntimeIdentifier -notin $supportedRids) {
    throw "不支持的 RuntimeIdentifier：$RuntimeIdentifier。支持列表：$($supportedRids -join ', ')"
}

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$paths = @{
    Repo = $repositoryRoot
    Ffmpeg = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $FfmpegSource))
    Mpv = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $MpvSource))
    Libass = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $LibassSource))
    Libplacebo = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $LibplaceboSource))
    X265 = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $X265Source))
    Harfbuzz = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $HarfbuzzSource))
    Dav1d = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $Dav1dSource))
    Output = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
foreach ($name in @("Ffmpeg", "Mpv", "Libass", "Libplacebo", "X265", "Harfbuzz", "Dav1d")) {
    if (-not (Test-Path -LiteralPath $paths[$name] -PathType Container)) {
        throw "缺少 $name 源码：$($paths[$name])"
    }
}

if ($RuntimeIdentifier -eq "win-x64") {
    $bashCandidates = @(
        $env:ASTRACAT_MSYS2_BASH,
        "C:\msys64\usr\bin\bash.exe",
        "C:\tools\msys64\usr\bin\bash.exe"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_) }
    $bash = $bashCandidates | Select-Object -First 1
    if (-not $bash) { throw "未找到 MSYS2 bash；请安装 CLANG64 环境或设置 ASTRACAT_MSYS2_BASH。" }

    function Convert-ToMsysPath([string]$Path) {
        $env:ASTRACORE_CONVERT_PATH = $Path
        try {
            $converted = (& $bash -lc 'cygpath -u "$ASTRACORE_CONVERT_PATH"').Trim()
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($converted)) { throw "路径转换失败：$Path" }
            return $converted
        } finally {
            Remove-Item Env:ASTRACORE_CONVERT_PATH -ErrorAction SilentlyContinue
        }
    }

    $env:ASTRACORE_REPO = Convert-ToMsysPath $paths.Repo
    $env:ASTRACORE_FFMPEG_SOURCE = Convert-ToMsysPath $paths.Ffmpeg
    $env:ASTRACORE_MPV_SOURCE = Convert-ToMsysPath $paths.Mpv
    $env:ASTRACORE_LIBASS_SOURCE = Convert-ToMsysPath $paths.Libass
    $env:ASTRACORE_LIBPLACEBO_SOURCE = Convert-ToMsysPath $paths.Libplacebo
    $env:ASTRACORE_X265_SOURCE = Convert-ToMsysPath $paths.X265
    $env:ASTRACORE_HARFBUZZ_SOURCE = Convert-ToMsysPath $paths.Harfbuzz
    $env:ASTRACORE_DAV1D_SOURCE = Convert-ToMsysPath $paths.Dav1d
    $env:ASTRACORE_OUTPUT = Convert-ToMsysPath $paths.Output
    $env:MSYSTEM = "CLANG64"

    & $bash -lc 'exec /usr/bin/bash "$ASTRACORE_REPO/native/astracore/build-clang64.sh"'
    if ($LASTEXITCODE -ne 0) { throw "AstraCore CLANG64 构建失败，退出码 $LASTEXITCODE。" }
} elseif ($RuntimeIdentifier -eq "linux-x64") {
    $env:ASTRACORE_REPO = $paths.Repo
    $env:ASTRACORE_FFMPEG_SOURCE = $paths.Ffmpeg
    $env:ASTRACORE_MPV_SOURCE = $paths.Mpv
    $env:ASTRACORE_LIBASS_SOURCE = $paths.Libass
    $env:ASTRACORE_LIBPLACEBO_SOURCE = $paths.Libplacebo
    $env:ASTRACORE_X265_SOURCE = $paths.X265
    $env:ASTRACORE_HARFBUZZ_SOURCE = $paths.Harfbuzz
    $env:ASTRACORE_DAV1D_SOURCE = $paths.Dav1d
    $env:ASTRACORE_OUTPUT = $paths.Output

    & bash (Join-Path $paths.Repo "native/astracore/build-linux-x64.sh")
    if ($LASTEXITCODE -ne 0) { throw "AstraCore Linux 构建失败，退出码 $LASTEXITCODE。" }
} else {
    $env:ASTRACORE_REPO = $paths.Repo
    $env:ASTRACORE_FFMPEG_SOURCE = $paths.Ffmpeg
    $env:ASTRACORE_MPV_SOURCE = $paths.Mpv
    $env:ASTRACORE_LIBASS_SOURCE = $paths.Libass
    $env:ASTRACORE_LIBPLACEBO_SOURCE = $paths.Libplacebo
    $env:ASTRACORE_X265_SOURCE = $paths.X265
    $env:ASTRACORE_HARFBUZZ_SOURCE = $paths.Harfbuzz
    $env:ASTRACORE_DAV1D_SOURCE = $paths.Dav1d
    $env:ASTRACORE_OUTPUT = $paths.Output

    & bash (Join-Path $paths.Repo "native/astracore/build-macos.sh")
    if ($LASTEXITCODE -ne 0) { throw "AstraCore macOS 构建失败，退出码 $LASTEXITCODE。" }
}

& (Join-Path $PSScriptRoot "New-AstraCoreManifest.ps1") `
    -RuntimeDirectory $paths.Output -RuntimeIdentifier $RuntimeIdentifier `
    -MpvVersion "0.41.0" -LibassVersion "0.17.5"
& (Join-Path $PSScriptRoot "Test-AstraCoreRuntime.ps1") -RuntimeDirectory $paths.Output

