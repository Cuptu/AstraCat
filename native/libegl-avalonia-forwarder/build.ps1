[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ZigPath
)

$ErrorActionPreference = "Stop"
$sourceDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $sourceDirectory "..\.."))
$outputDirectory = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "runtime\tools\mpv"))
$outputPath = Join-Path $outputDirectory "libEGL.dll"

if (-not (Test-Path -LiteralPath $ZigPath -PathType Leaf)) {
    throw "找不到 Zig 编译器：$ZigPath"
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
& $ZigPath cc -target x86_64-windows-gnu -shared `
    (Join-Path $sourceDirectory "forwarder.c") `
    (Join-Path $sourceDirectory "libEGL.def") `
    -O2 -s -o $outputPath
if ($LASTEXITCODE -ne 0) {
    throw "libEGL 转发层构建失败，退出码：$LASTEXITCODE"
}

Write-Host "已生成 $outputPath" -ForegroundColor Green
