[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$RuntimeDirectory,
    [Parameter(Mandatory)] [string]$RuntimeIdentifier,
    [Parameter(Mandatory)] [string]$MpvVersion,
    [Parameter(Mandatory)] [string]$LibassVersion,
    [string]$MpvCommit = "41f6a645068483470267271e1d09966ca3b9f413",
    [string]$FfmpegCommit = "bf1b838f2ab88b4f8fd83443325c782ea0e0f7fa",
    [string]$LibassCommit = "4a05d8127f525943ebf45fdc6497c9e665947f0d",
    [string]$LibplaceboCommit = "cee9b076f2c63104ccfd497fa79c39a867293ec4",
    [string]$X265Commit = "e444744c03978c1fb4e037168967020cf2648427",
    [string]$HarfbuzzCommit = "36cb489cb02ce4b92099669ba9f9bea348eff93f",
    [string]$Dav1dCommit = "54706fc6bc0cdecab7e9593974a4039cc038fca7",
    [string]$BuildConfiguration = "native/astracore/build-clang64.sh"
)

$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath($RuntimeDirectory)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "AstraCore 目录不存在：$root"
}

function Find-Component([string[]]$Names) {
    foreach ($name in $Names) {
        $candidate = Join-Path $root $name
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $name }
    }
    throw "AstraCore 缺少组件：$($Names -join ', ')"
}

$libMpv = if ($RuntimeIdentifier.StartsWith("win-")) {
    Find-Component @("libmpv-2.dll")
} elseif ($RuntimeIdentifier.StartsWith("osx-")) {
    Find-Component @("libmpv.2.dylib", "libmpv.dylib")
} else {
    Find-Component @("libmpv.so.2", "libmpv.so")
}
$ffmpeg = Find-Component $(if ($RuntimeIdentifier.StartsWith("win-")) { @("ffmpeg.exe") } else { @("ffmpeg") })
$ffprobe = Find-Component $(if ($RuntimeIdentifier.StartsWith("win-")) { @("ffprobe.exe") } else { @("ffprobe") })
$nativeNames = if ($RuntimeIdentifier.StartsWith("win-")) { @("AstraCore.Native.dll") } elseif ($RuntimeIdentifier.StartsWith("osx-")) { @("libAstraCore.Native.dylib") } else { @("libAstraCore.Native.so") }
$native = $null
foreach ($name in $nativeNames) {
    if (Test-Path -LiteralPath (Join-Path $root $name) -PathType Leaf) { $native = $name; break }
}

$ffmpegVersionOutput = @(& (Join-Path $root $ffmpeg) -hide_banner -version 2>&1)
$ffmpegVersion = ($ffmpegVersionOutput | Select-Object -First 1).ToString()
if ($LASTEXITCODE -ne 0) { throw "ffmpeg 版本读取失败。" }
$ffprobeVersionOutput = @(& (Join-Path $root $ffprobe) -hide_banner -version 2>&1)
$ffprobeVersion = ($ffprobeVersionOutput | Select-Object -First 1).ToString()
if ($LASTEXITCODE -ne 0) { throw "ffprobe 版本读取失败。" }

$files = @(Get-ChildItem -LiteralPath $root -File -Recurse |
    Where-Object { $_.Name -ne "astracore-runtime.json" } |
    Sort-Object FullName |
    ForEach-Object {
        $relativePath = $_.FullName.Substring($root.TrimEnd('\', '/').Length + 1).Replace('\', '/')
        [ordered]@{
            path = $relativePath
            size = $_.Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
        }
    })

$manifest = [ordered]@{
    schemaVersion = 1
    runtimeIdentifier = $RuntimeIdentifier
    components = [ordered]@{
        libMpv = $libMpv
        ffmpeg = $ffmpeg
        ffprobe = $ffprobe
        native = $native
    }
    versions = [ordered]@{
        mpv = $MpvVersion
        mpvClientApi = "2.5"
        ffmpeg = $ffmpegVersion
        ffprobe = $ffprobeVersion
        libass = $LibassVersion
        libplacebo = "7.360.1"
        x265 = "4.2-8bit"
        harfbuzz = "14.4.0-minimal"
        dav1d = "1.5.4"
    }
    build = [ordered]@{
        configuration = $BuildConfiguration
        sourceCommits = [ordered]@{
            mpv = $MpvCommit
            ffmpeg = $FfmpegCommit
            libass = $LibassCommit
            libplacebo = $LibplaceboCommit
            x265 = $X265Commit
            harfbuzz = $HarfbuzzCommit
            dav1d = $Dav1dCommit
        }
        patches = @("native/astracore/patches/mpv-angle-device-query.patch")
        generatedUtc = [DateTime]::UtcNow.ToString("O")
    }
    features = [ordered]@{
        preview = @("libmpv-opengl-render-api", "d3d11va", "dxva2", "software-fallback")
        subtitles = @("ass", "ssa", "srt", "webvtt", "mov_text", "font-fallback", "hot-reload")
        export = @("libx264", "libx265", "libsvtav1", "nvenc", "qsv", "amf", "aac")
        tools = @("ffmpeg", "ffprobe", "native-libavformat-probe")
    }
    licenseSources = @(
        [ordered]@{ component = "AstraCat"; path = "LICENSES/AstraCat-GPL-3.0.txt"; source = "repository LICENSE" },
        [ordered]@{ component = "mpv"; path = "LICENSES/mpv-GPL.txt"; source = "mpv LICENSE.GPL" },
        [ordered]@{ component = "FFmpeg"; path = "LICENSES/FFmpeg-GPLv3.txt"; source = "FFmpeg COPYING.GPLv3" },
        [ordered]@{ component = "libass"; path = "LICENSES/libass-ISC.txt"; source = "libass COPYING" },
        [ordered]@{ component = "libplacebo"; path = "LICENSES/libplacebo-LGPL-2.1.txt"; source = "libplacebo LICENSE" },
        [ordered]@{ component = "x265"; path = "LICENSES/x265-GPL-2.0.txt"; source = "x265 COPYING" },
        [ordered]@{ component = "HarfBuzz"; path = "LICENSES/HarfBuzz-MIT.txt"; source = "HarfBuzz COPYING" },
        [ordered]@{ component = "dav1d"; path = "LICENSES/dav1d-BSD-2-Clause.txt"; source = "dav1d COPYING" },
        [ordered]@{ component = "runtime dependencies"; path = "LICENSES/dependencies"; source = "MSYS2 package license payloads" }
    )
    files = $files
}

$manifestPath = Join-Path $root "astracore-runtime.json"
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host "已生成 $manifestPath，共记录 $($files.Count) 个文件。" -ForegroundColor Green
