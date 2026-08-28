# 构建与发布

本文说明源码仓库应包含什么、如何在本机编译，以及 GitHub Actions 如何生成 Windows 安装包。

## 仓库内容

应提交：

- C#、Avalonia XAML 和 Python Worker 源码；
- `Assets`、`docs/images`、安装脚本和项目文档；
- `AstraCat.csproj`、`packages.lock.json` 与 `global.json`；
- `.github/workflows` 和 `scripts`；
- `runtime/tools/mpv/LIBMPV_SOURCE.md`，仅用于记录固定依赖来源和哈希。

不应提交：

- `bin`、`obj`、`dist`、`artifacts`；
- `runtime` 中的模型、Python 环境、CUDA 运行库、配置、缓存和项目数据；
- FFmpeg、libmpv 等大体积二进制；
- API Key、`.env`、日志、播放器诊断结果和本机绝对路径；
- `runtimes` 中的重复原生库。

提交前运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\audit-repository.ps1
```

该脚本会检查旧项目名、常见密钥格式、本机路径、临时文件、调试注释和超过 50 MB 的候选提交文件。

## 本机构建

环境要求：Windows x64、.NET 10 SDK 以及 Python 3.12 或更高版本。SDK 版本由 `global.json` 固定。

```powershell
dotnet restore --locked-mode
dotnet build -c Release --no-restore
python -m py_compile engines\asr_worker.py
```

普通源码构建不需要下载模型、FFmpeg 或 libmpv。要启动播放器功能，先准备固定版本的原生依赖：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\prepare-native-deps.ps1
dotnet run -c Debug
```

依赖脚本只从已记录的 GitHub Release 下载文件，并校验压缩包及 libmpv DLL 的 SHA-256。目前固定为：

- shinchiro libmpv `v0.41.0-1011-g182fa6ca4`；
- BtbN FFmpeg `n8.1.2-44-g7c533d0f86` GPL Shared。

## 本机打包

安装 [Inno Setup 6](https://jrsoftware.org/isinfo.php)，然后执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\prepare-native-deps.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\package-release.ps1 -Version 0.1.0-DEV -FfmpegDir .\runtime\tools\ffmpeg
```

产物位于 `dist`：

```text
AstraCat-v0.1.0-DEV-Setup.exe
AstraCat-v0.1.0-DEV-win-x64.zip
AstraCat-v0.1.0-DEV-SHA256.txt
```

## GitHub Actions

`CI` 工作流在推送到 `main` 和 Pull Request 时执行：

1. 仓库内容审计；
2. 按 `packages.lock.json` 还原 NuGet；
3. Release 编译；
4. Python Worker 语法检查。

`Build Windows release` 工作流有两种用法：

- 在 Actions 页面手动运行：生成可下载的工作流产物，不创建 Release；
- 推送 `v*` 标签：构建安装包并创建 GitHub Prerelease。

Release 顶部的固定介绍来自 [`.github/RELEASE_TEMPLATE.md`](../.github/RELEASE_TEMPLATE.md)，本次提交和合并记录由 GitHub 按 [`.github/release.yml`](../.github/release.yml) 自动分类并追加。发布前如需调整兼容性、已知问题或下载说明，先修改模板并提交到 `main`，再创建版本标签。

DEV 阶段固定使用 `主版本.次版本.补丁版本-DEV`。每发布一次，把补丁号加一：`0.1.0-DEV`、`0.1.1-DEV`、`0.1.2-DEV`。

```powershell
git switch main
git pull --ff-only
git status --short
git tag -a v0.1.0-DEV -m "AstraCat 0.1.0-DEV"
git push origin v0.1.0-DEV
```

标签必须创建在已经通过 CI 的 `main` 提交上。工作流会把标签中的版本写入程序集、安装包文件名和 SHA-256 清单，随后上传 Actions Artifact，并创建带有安装包、便携包和校验文件的 Prerelease。DEV 结束前不要使用正式 `v1.0.0` 标签。

如果只想验证打包，不准备公开发布：进入 GitHub 仓库的 **Actions → Build Windows release → Run workflow**，填写版本号后运行。手动运行只保留 Actions Artifact，不创建 Release。

发布完成后检查：

1. `Build Windows release` 的所有步骤为绿色；
2. Release 标记为 **Pre-release**；
3. `Setup.exe`、`win-x64.zip` 和 `SHA256.txt` 三个文件都已出现；
4. 下载页中的版本号、DEV 提示和系统要求正确；
5. 从安装包或便携版至少启动一次，确认主页、播放器和模型页可以打开。

## 第一次上传 GitHub

`Cuptu/AstraCat` 的 `main` 分支已经有一个 LICENSE 提交。当前开发目录还不是 Git 仓库，可以把远端提交设为本地历史起点，再加入本地源码：

```powershell
git init -b main
git remote add origin https://github.com/Cuptu/AstraCat.git
git fetch origin main
git reset --mixed origin/main
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\audit-repository.ps1
git add .
git status --short
git diff --cached --stat
git commit -m "Initial AstraCat DEV release"
git push -u origin main
```

这里的 `git reset --mixed` 只把 Git 的提交起点和暂存区对齐到远端 `main`，不会改写当前工作目录中的源码。

执行 `git add .` 后必须检查暂存区。若看到 `runtime/models`、`runtime/python`、`runtime/config`、`bin`、`obj`、`dist`、DLL、EXE、ZIP、日志或用户项目文件，先停止提交并检查 `.gitignore`。

不要使用强制推送覆盖远端历史。首次推送后，先在 GitHub 的 Actions 页面确认 `CI` 通过，再创建 DEV 标签。
