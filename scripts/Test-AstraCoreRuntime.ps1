[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$RuntimeDirectory,
    [int]$MaximumSizeMiB = 200,
    [int]$MaximumLibMpvMiB = 25,
    [string]$DistributionRoot = ""
)

$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath($RuntimeDirectory)
$manifestPath = Join-Path $root "astracore-runtime.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "缺少 $manifestPath" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) { throw "不支持的 AstraCore 清单版本：$($manifest.schemaVersion)" }
if ($manifest.versions.mpvClientApi -ne "2.5") { throw "libmpv Client API 不符合预期：$($manifest.versions.mpvClientApi)" }
if (-not $manifest.features.preview -or -not $manifest.features.subtitles -or -not $manifest.features.export) {
    throw "AstraCore 清单缺少功能契约。"
}
if (-not $manifest.licenseSources -or $manifest.licenseSources.Count -lt 5) {
    throw "AstraCore 清单缺少许可证来源。"
}
$requiredBuildPatch = "native/astracore/patches/mpv-angle-device-query.patch"
if (-not $manifest.build -or -not $manifest.build.patches -or
    $requiredBuildPatch -notin @($manifest.build.patches)) {
    throw "AstraCore 清单未记录必需的 mpv ANGLE 互操作补丁：$requiredBuildPatch"
}

function Resolve-ManifestPath([string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath)) { throw "清单包含空文件路径。" }
    $resolved = [IO.Path]::GetFullPath((Join-Path $root $RelativePath))
    $prefix = $root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "清单路径越界：$RelativePath"
    }
    return $resolved
}

foreach ($file in $manifest.files) {
    $path = Resolve-ManifestPath $file.path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "清单文件缺失：$($file.path)" }
    $item = Get-Item -LiteralPath $path
    if ($item.Length -ne [long]$file.size) { throw "文件大小不符：$($file.path)" }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash
    if ($hash -ne $file.sha256) { throw "文件哈希不符：$($file.path)" }
}

$totalBytes = (Get-ChildItem -LiteralPath $root -File -Recurse | Measure-Object Length -Sum).Sum
$maximumBytes = [long]$MaximumSizeMiB * 1MB
if ($totalBytes -gt $maximumBytes) {
    throw "AstraCore 体积 $([Math]::Round($totalBytes / 1MB, 2)) MiB 超过 $MaximumSizeMiB MiB。"
}

$libMpv = Resolve-ManifestPath $manifest.components.libMpv
$libMpvBytes = (Get-Item -LiteralPath $libMpv).Length
if ($libMpvBytes -gt ([long]$MaximumLibMpvMiB * 1MB)) {
    throw "libmpv 体积 $([Math]::Round($libMpvBytes / 1MB, 2)) MiB，疑似仍静态包含 FFmpeg。"
}

function Get-NativeDependencies([string]$Binary) {
    if ($IsMacOS) {
        return (& otool -L $Binary 2>&1 | Out-String)
    }
    if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') {
        return (& readelf -d $Binary 2>&1 | Out-String)
    }

    $llvmReadObj = Get-Command llvm-readobj.exe -ErrorAction SilentlyContinue
    if (-not $llvmReadObj) {
        $llvmCandidate = "C:\msys64\clang64\bin\llvm-readobj.exe"
        if (Test-Path -LiteralPath $llvmCandidate) { $llvmReadObj = Get-Item -LiteralPath $llvmCandidate }
    }
    if ($llvmReadObj) {
        $command = if ($llvmReadObj.Source) { $llvmReadObj.Source } else { $llvmReadObj.FullName }
        return (& $command --coff-imports $Binary 2>&1 | Out-String)
    }

    $dumpbin = Get-Command dumpbin.exe -ErrorAction SilentlyContinue
    if (-not $dumpbin) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
        if (Test-Path -LiteralPath $vswhere) {
            $installation = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
            if ($installation) {
                $dumpbin = Get-ChildItem -LiteralPath (Join-Path $installation "VC\Tools\MSVC") -Filter dumpbin.exe -File -Recurse |
                    Sort-Object FullName -Descending | Select-Object -First 1
            }
        }
    }
    if (-not $dumpbin) { throw "无法检查 PE 依赖：未找到 Visual Studio dumpbin.exe。" }
    $command = if ($dumpbin.Source) { $dumpbin.Source } else { $dumpbin.FullName }
    return (& $command /nologo /dependents $Binary 2>&1 | Out-String)
}

function Get-ImportedLibraryNames([string]$Binary) {
    $dependencyText = Get-NativeDependencies $Binary
    $names = @([Regex]::Matches($dependencyText, '(?im)^\s*Name:\s*(\S+)\s*$') |
        ForEach-Object { $_.Groups[1].Value })
    if ($names.Count -eq 0) {
        $names = @([Regex]::Matches($dependencyText, '(?im)^\s+([A-Za-z0-9._+\-]+\.dll)\s*$') |
            ForEach-Object { $_.Groups[1].Value })
    }
    return @($names | Sort-Object -Unique)
}

$mpvDependencies = Get-NativeDependencies $libMpv
foreach ($requiredFamily in @('avcodec', 'avformat', 'avfilter', 'avutil', 'libass')) {
    if ($mpvDependencies -notmatch "(?i)$requiredFamily") {
        throw "libmpv 没有动态依赖 $requiredFamily，疑似仍包含重复静态实现。"
    }
}

if ($manifest.runtimeIdentifier -like 'win-*') {
    $runtimeImages = @(Get-ChildItem -LiteralPath $root -File |
        Where-Object { $_.Extension -in @('.dll', '.exe') })
    $packagedImages = @{}
    foreach ($image in $runtimeImages) { $packagedImages[$image.Name.ToLowerInvariant()] = $image.FullName }

    $systemImportPattern = '(?i)^(api-ms-win-|ext-ms-win-|advapi32\.dll$|avrt\.dll$|bcrypt\.dll$|crypt32\.dll$|dwmapi\.dll$|gdi32\.dll$|imm32\.dll$|kernel32\.dll$|ncrypt\.dll$|ntdll\.dll$|ole32\.dll$|opengl32\.dll$|secur32\.dll$|shcore\.dll$|shell32\.dll$|shlwapi\.dll$|user32\.dll$|uxtheme\.dll$|ws2_32\.dll$)'
    $forbiddenImportPattern = '(?i)^(libshaderc|libspirv|vulkan-1|libdovi|libva|libglib|libpcre2|libgraphite2|libfontconfig|libexpat|libintl|libjpeg)'
    $importsByImage = @{}
    foreach ($image in $runtimeImages) {
        $imports = @(Get-ImportedLibraryNames $image.FullName)
        $importsByImage[$image.Name.ToLowerInvariant()] = $imports
        foreach ($import in $imports) {
            if ($import -match $forbiddenImportPattern) {
                throw "$($image.Name) 仍导入已裁剪依赖：$import"
            }
            if (-not $packagedImages.ContainsKey($import.ToLowerInvariant()) -and
                $import -notmatch $systemImportPattern) {
                throw "$($image.Name) 的非系统依赖未进入运行时/清单：$import"
            }
        }
    }

    $reachable = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $pending = [Collections.Generic.Queue[string]]::new()
    foreach ($component in @($manifest.components.libMpv, $manifest.components.ffmpeg,
            $manifest.components.ffprobe, $manifest.components.native)) {
        if (-not [string]::IsNullOrWhiteSpace($component)) { $pending.Enqueue($component) }
    }
    while ($pending.Count -gt 0) {
        $name = $pending.Dequeue()
        if (-not $reachable.Add($name)) { continue }
        foreach ($import in @($importsByImage[$name.ToLowerInvariant()])) {
            if ($packagedImages.ContainsKey($import.ToLowerInvariant())) { $pending.Enqueue($import) }
        }
    }
    $orphanImages = @($runtimeImages | Where-Object { -not $reachable.Contains($_.Name) })
    if ($orphanImages.Count -gt 0) {
        throw "AstraCore 包含根组件不可达的原生文件：$($orphanImages.Name -join ', ')"
    }
}

$ffmpeg = Resolve-ManifestPath $manifest.components.ffmpeg
$ffprobe = Resolve-ManifestPath $manifest.components.ffprobe
$checks = @(
    @{ Arguments = @('-hide_banner', '-encoders'); Required = @('libx264', 'libx265', 'libsvtav1', 'h264_nvenc', 'h264_qsv', 'h264_amf', 'aac', 'pcm_s16le', 'png') },
    @{ Arguments = @('-hide_banner', '-decoders'); Required = @('h264', 'hevc', 'av1', 'libdav1d', 'vp9', 'aac', 'flac', 'opus') },
    @{ Arguments = @('-hide_banner', '-filters'); Required = @('subtitles', 'scale', 'format', 'fps', 'aresample') },
    @{ Arguments = @('-hide_banner', '-hwaccels'); Required = @('d3d11va', 'dxva2') },
    @{ Arguments = @('-hide_banner', '-demuxers'); Required = @('mov', 'matroska', 'avi', 'flv', 'mpegts', 'wav', 'ogg', 'flac', 'aac', 'hls', 'asf') },
    @{ Arguments = @('-hide_banner', '-muxers'); Required = @('mp4', 'mov', 'matroska', 'wav', 's16le') }
)
foreach ($check in $checks) {
    [string[]]$queryArguments = $check.Arguments
    $output = (& $ffmpeg @queryArguments 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg 功能查询失败：$($check.Arguments -join ' ')" }
    foreach ($required in $check.Required) {
        if ($output -notmatch "(?m)\b$([Regex]::Escape($required))\b") {
            throw "AstraCore 缺少必需 FFmpeg 功能：$required"
        }
    }
}

# The global hwaccel list is insufficient: a build can advertise d3d11va while
# individual decoders expose only the legacy DXVA2 configuration.
foreach ($decoder in @('h264', 'hevc', 'av1', 'vp9', 'mpeg2video')) {
    $decoderHelp = (& $ffmpeg -hide_banner -h "decoder=$decoder" 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or $decoderHelp -notmatch '(?i)Supported hardware devices:.*\bd3d11va\b') {
        throw "FFmpeg decoder $decoder 未暴露 D3D11VA device context。"
    }
}

& $ffprobe -hide_banner -version *> $null
if ($LASTEXITCODE -ne 0) { throw "ffprobe 无法启动。" }

$avPattern = '^(avcodec|avformat|avfilter|avutil|swscale|swresample)-?\d*\.(dll|so|dylib)'
$duplicateFamilies = @(Get-ChildItem -LiteralPath $root -File | Where-Object Name -Match $avPattern |
    Group-Object { [Regex]::Match($_.Name, $avPattern).Groups[1].Value } | Where-Object Count -GT 1)
if ($duplicateFamilies) { throw "AstraCore 内存在重复 FFmpeg ABI：$($duplicateFamilies.Name -join ', ')" }

foreach ($forbidden in @('libshaderc_shared.dll', 'libspirv-cross-c-shared.dll', 'vulkan-1.dll', 'libdovi.dll', 'libva.dll', 'libva_win32.dll', 'libglib-2.0-0.dll', 'libpcre2-8-0.dll', 'libgraphite2.dll', 'libfontconfig-1.dll', 'libexpat-1.dll', 'libintl-8.dll')) {
    if (Test-Path -LiteralPath (Join-Path $root $forbidden)) {
        throw "AstraCore 精简配置不应携带未使用依赖：$forbidden"
    }
}
if (Test-Path -LiteralPath (Join-Path $root 'libjpeg-8.dll')) {
    throw "AstraCore 截图已使用 PNG，不应再携带 mpv 的独立 libjpeg：libjpeg-8.dll"
}

if (-not [string]::IsNullOrWhiteSpace($DistributionRoot)) {
    $distribution = [IO.Path]::GetFullPath($DistributionRoot)
    $legacyAvLibraries = @(Get-ChildItem -LiteralPath $distribution -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { -not $_.FullName.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -and $_.Name -match $avPattern })
    if ($legacyAvLibraries.Count -gt 0) {
        throw "AstraCore 外仍存在重复 FFmpeg 库：$($legacyAvLibraries.FullName -join ', ')"
    }
}

Write-Host "AstraCore 校验通过：$([Math]::Round($totalBytes / 1MB, 2)) MiB，RID $($manifest.runtimeIdentifier)。" -ForegroundColor Green
