[CmdletBinding()]
param(
    [string]$TargetDirectory = "",
    [string]$PythonVersion = "3.12.10",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($TargetDirectory)) {
    $TargetDirectory = Join-Path $repositoryRoot "runtime\python-embed"
}
$TargetDirectory = [IO.Path]::GetFullPath($TargetDirectory)

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "         准备 AstraCat 内置精简 Python + yt-dlp 运行时            " -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "目标目录: $TargetDirectory" -ForegroundColor Gray

if ((Test-Path $TargetDirectory) -and -not $Force) {
    if ((Test-Path (Join-Path $TargetDirectory "python.exe")) -and 
        (Test-Path (Join-Path $TargetDirectory "Lib\site-packages\yt_dlp"))) {
        Write-Host "内置 Python 运行时已就绪，跳过准备步骤（传入 -Force 重新准备）。" -ForegroundColor Green
        return
    }
}

$systemTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$workingDirectory = Join-Path $systemTempRoot ("astracat-embed-py-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $workingDirectory -Force | Out-Null

try {
    # 1. 下载 Windows 官方 Embeddable Python
    $pyZipName = "python-$PythonVersion-embed-amd64.zip"
    $pyZipPath = Join-Path $workingDirectory $pyZipName
    $pyUrl = "https://www.python.org/ftp/python/$PythonVersion/$pyZipName"
    
    Write-Host "1. 下载 Python $PythonVersion Embeddable Package..." -ForegroundColor Yellow
    Invoke-WebRequest -UseBasicParsing -Uri $pyUrl -OutFile $pyZipPath

    $pyExtractDir = Join-Path $workingDirectory "py-extract"
    [System.IO.Compression.ZipFile]::ExtractToDirectory($pyZipPath, $pyExtractDir)

    # 2. 启用 import site 与 Lib\site-packages
    Write-Host "2. 配置 python312._pth 支持 site-packages..." -ForegroundColor Yellow
    $pthFile = Get-ChildItem $pyExtractDir -Filter "*._pth" | Select-Object -First 1
    if ($pthFile) {
        $lines = Get-Content $pthFile.FullName
        $newLines = @()
        foreach ($line in $lines) {
            if ($line.Trim() -eq "#import site") {
                $newLines += "import site"
            } else {
                $newLines += $line
            }
        }
        $newLines += "Lib\site-packages"
        $newLines += "."
        $newLines | Set-Content $pthFile.FullName -Encoding ASCII
    }

    # 3. 创建 Lib\site-packages
    $sitePackagesDir = Join-Path $pyExtractDir "Lib\site-packages"
    New-Item -ItemType Directory -Path $sitePackagesDir -Force | Out-Null

    # 4. 下载最新 yt-dlp wheel 并解压
    Write-Host "3. 获取最新 yt-dlp wheel 包..." -ForegroundColor Yellow
    $pypiMeta = Invoke-RestMethod -Uri "https://pypi.org/pypi/yt-dlp/json"
    $wheelUrl = $null
    foreach ($file in $pypiMeta.urls) {
        if ($file.filename -like "*py3-none-any.whl") {
            $wheelUrl = $file.url
            break
        }
    }
    if (-not $wheelUrl) {
        throw "未找到 yt-dlp 的 wheel 安装包。"
    }

    $wheelPath = Join-Path $workingDirectory "yt_dlp.whl"
    Write-Host "   下载 $wheelUrl..." -ForegroundColor Gray
    Invoke-WebRequest -UseBasicParsing -Uri $wheelUrl -OutFile $wheelPath
    [System.IO.Compression.ZipFile]::ExtractToDirectory($wheelPath, $sitePackagesDir)

    # 5. 验证是否可被识别
    $extractedPython = Join-Path $pyExtractDir "python.exe"
    $testOut = & $extractedPython -c "import yt_dlp; print('yt_dlp version:', yt_dlp.version.__version__)" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "内置 Python 校验失败: $testOut"
    }
    Write-Host "   校验成功: $testOut" -ForegroundColor Green

    # 6. 部署到目标目录
    if (Test-Path $TargetDirectory) {
        Remove-Item -Recurse -Force $TargetDirectory
    }
    New-Item -ItemType Directory -Path $TargetDirectory -Force | Out-Null
    Copy-Item -Recurse "$pyExtractDir\*" $TargetDirectory

    # 生成校验元数据
    $manifestLines = @(
        "AstraCat Embedded Python & yt-dlp Runtime",
        "Python: $PythonVersion",
        "yt-dlp: $($pypiMeta.info.version)",
        "GeneratedAt: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
        "python.exe SHA-256: $((Get-FileHash -Algorithm SHA256 (Join-Path $TargetDirectory 'python.exe')).Hash)"
    )
    $manifestLines | Set-Content (Join-Path $TargetDirectory "EMBEDDED_RUNTIME.txt") -Encoding UTF8

    Write-Host "=================================================================" -ForegroundColor Green
    Write-Host "     内置 Python + yt-dlp 准备完成！目录：$TargetDirectory       " -ForegroundColor Green
    Write-Host "=================================================================" -ForegroundColor Green
}
finally {
    if (Test-Path $workingDirectory) {
        Remove-Item -Recurse -Force $workingDirectory -ErrorAction SilentlyContinue
    }
}
