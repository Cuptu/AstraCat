[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$excludedDirectoryNames = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase
)
foreach ($name in @(".git", ".vs", ".idea", ".vscode", "bin", "obj", "dist", "artifacts", "TestResults", "runtime", "runtimes", "__pycache__")) {
    [void]$excludedDirectoryNames.Add($name)
}

function Get-RepositoryFiles {
    $pending = [Collections.Generic.Queue[IO.DirectoryInfo]]::new()
    $pending.Enqueue([IO.DirectoryInfo]::new($repositoryRoot))
    while ($pending.Count -gt 0) {
        $directory = $pending.Dequeue()
        foreach ($item in $directory.EnumerateFileSystemInfos()) {
            if ($item -is [IO.DirectoryInfo]) {
                if (-not $excludedDirectoryNames.Contains($item.Name)) { $pending.Enqueue($item) }
            }
            elseif ($item -is [IO.FileInfo]) {
                $item
            }
        }
    }
}

$findings = [Collections.Generic.List[string]]::new()
$files = @(Get-RepositoryFiles)
$auditScriptPath = [IO.Path]::GetFullPath($MyInvocation.MyCommand.Path)
$relativePathOffset = $repositoryRoot.TrimEnd('\').Length + 1

function Get-RelativeRepositoryPath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if ($resolved.Length -lt $relativePathOffset) { return $resolved }
    return $resolved.Substring($relativePathOffset)
}

foreach ($file in $files) {
    $relativePath = Get-RelativeRepositoryPath $file.FullName
    if ($file.Length -gt 50MB) {
        $findings.Add("大文件超过 50 MB：$relativePath ($([math]::Round($file.Length / 1MB, 2)) MB)")
    }
    if ($file.Name -match '(?i)(^Video$|stagedtest|\.log$|\.trace$|\.bak$|\.backup$|\.orig$)') {
        $findings.Add("发现不应提交的日志或临时文件：$relativePath")
    }
}

$textExtensions = [Collections.Generic.HashSet[string]]::new(
    [string[]]@(".cs", ".axaml", ".csproj", ".props", ".targets", ".json", ".yml", ".yaml", ".ps1", ".iss", ".py", ".md", ".txt", ".manifest"),
    [StringComparer]::OrdinalIgnoreCase
)
$contentRules = @(
    @{ Name = "旧项目或无关项目名"; Pattern = '(?i)(?<![A-Za-z0-9_])PCL(?![A-Za-z0-9_])|Plain Craft Launcher|yomi[-_ ]?bot|VideoCaptioner|SmartSub|WEIFENG2333|buxuku' },
    @{ Name = "常见密钥格式"; Pattern = '(?i)sk-[A-Za-z0-9_-]{16,}|AIza[0-9A-Za-z_-]{20,}|ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}' },
    @{ Name = "硬编码凭据"; Pattern = '(?i)(api[_-]?key|secret|token|password)\s*[:=]\s*["''][^"'']{8,}["'']' },
    @{ Name = "本机绝对路径"; Pattern = '(?i)[A-Z]:\\Users\\|C:/Users/|/Users/[^/]+/|/home/[^/]+' },
    @{ Name = "未清理的代码标记"; Pattern = '(?i)(?<![A-Za-z0-9_])(TODO|FIXME|HACK|XXX)(?![A-Za-z0-9_])' },
    @{ Name = "临时设计注释"; Pattern = '(?i)modeled after|image\s*[0-9]|NO aggressive|//\s*(ignore|fall back)\s*$' }
)

foreach ($file in $files) {
    if ($file.FullName -eq $auditScriptPath -or -not $textExtensions.Contains($file.Extension)) { continue }
    $lineNumber = 0
    foreach ($line in [IO.File]::ReadLines($file.FullName)) {
        $lineNumber++
        foreach ($rule in $contentRules) {
            if ($line -match $rule.Pattern) {
                $relativePath = Get-RelativeRepositoryPath $file.FullName
                $findings.Add("$($rule.Name)：${relativePath}:$lineNumber")
            }
        }
    }
}

if ($findings.Count -gt 0) {
    $findings | Sort-Object -Unique | ForEach-Object { Write-Error $_ }
    throw "仓库审计失败，共发现 $($findings.Count) 项问题。"
}

Write-Host "仓库审计通过：$($files.Count) 个候选提交文件，未发现大文件、密钥、旧项目名、本机路径或调试残留。" -ForegroundColor Green
