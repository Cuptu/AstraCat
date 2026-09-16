[CmdletBinding()]
param([string]$Destination = "artifacts/astracore-sources")

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$destinationRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $Destination))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
$artifactsPrefix = $artifactsRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $destinationRoot.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "第三方源码只能准备到 artifacts：$destinationRoot"
}

function Get-PinnedSource([string]$Name, [string]$Repository, [string]$Commit, [string]$FallbackRepository = "") {
    $directory = Join-Path $destinationRoot $Name
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null
        $cloned = $false
        try {
            & git clone --filter=blob:none $Repository $directory
            if ($LASTEXITCODE -eq 0) { $cloned = $true }
        } catch { }
        if (-not $cloned -and -not [string]::IsNullOrWhiteSpace($FallbackRepository)) {
            Write-Host "主源克隆失败，尝试镜像源 $FallbackRepository..." -ForegroundColor Yellow
            if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
            & git clone --filter=blob:none $FallbackRepository $directory
            if ($LASTEXITCODE -eq 0) { $cloned = $true }
        }
        if (-not $cloned) { throw "$Name 克隆失败。" }
    }
    & git -C $directory fetch --depth 1 origin $Commit
    if ($LASTEXITCODE -ne 0) { throw "$Name 固定提交下载失败。" }
    & git -C $directory checkout --detach $Commit
    if ($LASTEXITCODE -ne 0) { throw "$Name 固定提交检出失败。" }
    $actual = (& git -C $directory rev-parse HEAD).Trim()
    if ($actual -ne $Commit) { throw "$Name 提交不匹配：$actual" }
    Write-Host "$Name 已固定到 $actual" -ForegroundColor Green
}

Get-PinnedSource "ffmpeg" "https://github.com/FFmpeg/FFmpeg.git" "bf1b838f2ab88b4f8fd83443325c782ea0e0f7fa"
Get-PinnedSource "mpv" "https://github.com/mpv-player/mpv.git" "41f6a645068483470267271e1d09966ca3b9f413"
Get-PinnedSource "libass" "https://github.com/libass/libass.git" "4a05d8127f525943ebf45fdc6497c9e665947f0d"
Get-PinnedSource "libplacebo" "https://code.videolan.org/videolan/libplacebo.git" "cee9b076f2c63104ccfd497fa79c39a867293ec4"
Get-PinnedSource "x265" "https://bitbucket.org/multicoreware/x265_git.git" "e444744c03978c1fb4e037168967020cf2648427"
Get-PinnedSource "harfbuzz" "https://github.com/harfbuzz/harfbuzz.git" "36cb489cb02ce4b92099669ba9f9bea348eff93f"
Get-PinnedSource "dav1d" "https://code.videolan.org/videolan/dav1d.git" "54706fc6bc0cdecab7e9593974a4039cc038fca7"
