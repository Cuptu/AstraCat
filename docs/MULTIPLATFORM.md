# AstraCat 多平台构建与运行指南 (Windows / Linux / macOS)

本文档记录 AstraCat 基于 .NET 10 + Avalonia 12 + Native AOT + AstraCore 原生媒体引擎的多平台支持架构、构建命令与运行规范。

---

## 1. 架构与平台对照表

| 目标平台 (RID) | 推荐环境 | UI / 图形渲染 | 视频解码 (libmpv) | 硬件编码 (AstraCore) | AI 推理运行时 (sherpa-onnx) |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Windows x64** (`win-x64`) | Windows 10/11 64-bit | Avalonia ANGLE (D3D11 EGL) | D3D11VA, D3D11VA-copy, 软件 | NVENC, QSV, AMF, x264, x265, SVT-AV1 | 进程内 sherpa-onnx (CPU / 可选 CUDA 12) |
| **Linux x64** (`linux-x64`) | Ubuntu 22.04+ / Debian 12+ / Arch | Avalonia X11 / Wayland (OpenGL) | VAAPI, NVDEC, 软件 | VAAPI, NVENC, QSV, x264, x265, SVT-AV1 | 进程内 sherpa-onnx (CPU / Linux CUDA) |
| **macOS Apple Silicon** (`osx-arm64`) | macOS 13 (Ventura)+ | Avalonia Metal / OpenGL | VideoToolbox, 软件 | VideoToolbox, x264, x265, SVT-AV1 | 进程内 sherpa-onnx (CPU / CoreML) |

---

## 2. 各平台本地构建说明

### 2.1 Windows x64

Windows 环境支持完整的端到端本地构建，包括 AstraCore 编译、Native AOT 生成、离线契约验证及 Inno Setup 安装包构建：

```powershell
# 1. 编译并验证 C# Release
dotnet build -c Release

# 2. 一键执行 Native AOT 编译与完整打包 (ZIP + 安装程序)
./package-release.ps1 -NativeAot -Version 0.1.2-DEV
```

产物位于 `dist/`：
- `AstraCat-v0.1.2-DEV-Setup.exe` (Windows 原生安装程序)
- `AstraCat-v0.1.2-DEV-win-x64.zip` (绿色免安装版)

---

### 2.2 Linux x64
 
在 Ubuntu / Debian 系统上构建：
 
```bash
# 1. 安装 Native AOT 编译依赖
sudo apt-get update
sudo apt-get install -y build-essential zlib1g-dev

# 2. 执行 Native AOT 编译
dotnet publish AstraCat.csproj -c Release -r linux-x64 \
  -p:PublishProfile=NativeAot -p:PublishAot=true \
  -p:DebugType=None -p:DebugSymbols=false \
  -o dist/AstraCat-linux-x64

# 3. 下载并部署 AstraCore Linux 原生引擎包
mkdir -p dist/AstraCat-linux-x64/runtime/tools/astracore/linux-x64
curl -sL https://github.com/Cuptu/AstraCore/releases/download/v0.1.0/AstraCore-linux-x64.tar.gz | tar -xz -C dist/AstraCat-linux-x64/runtime/tools/astracore/linux-x64/

# 4. 组装绿色运行包
cp LICENSE README.md THIRD_PARTY_NOTICES.md dist/AstraCat-linux-x64/
cd dist && tar -czvf AstraCat-linux-x64.tar.gz AstraCat-linux-x64
```

---

### 2.3 macOS Apple Silicon (`osx-arm64`)

在 macOS 终端中构建：

```bash
# 1. 执行 Native AOT 编译
dotnet publish AstraCat.csproj -c Release -r osx-arm64 \
  -p:PublishProfile=NativeAot -p:PublishAot=true \
  -p:DebugType=None -p:DebugSymbols=false \
  -o dist/AstraCat-osx-arm64

# 2. 下载并部署 AstraCore macOS 原生引擎包
mkdir -p dist/AstraCat-osx-arm64/runtime/tools/astracore/osx-arm64
curl -sL https://github.com/Cuptu/AstraCore/releases/download/v0.1.0/AstraCore-osx-arm64.tar.gz | tar -xz -C dist/AstraCat-osx-arm64/runtime/tools/astracore/osx-arm64/

# 3. 组装绿色运行包
cp LICENSE README.md THIRD_PARTY_NOTICES.md dist/AstraCat-osx-arm64/
cd dist && tar -czvf AstraCat-osx-arm64.tar.gz AstraCat-osx-arm64
```

---

## 3. GitHub Actions 自动化流水线

由于微软 .NET Native AOT 必须在目标宿主环境链接原生二进制，工程维护了统一的多平台 CI 配置文件：
- [`.github/workflows/multiplatform-build.yml`](../.github/workflows/multiplatform-build.yml)

该工作流在每次推送到 `main` 分支时并行触发：
1. `ubuntu-latest`：构建并打包 `AstraCat-linux-x64.tar.gz`
2. `macos-latest`：构建并打包 `AstraCat-osx-arm64.tar.gz`
3. `windows-latest`：构建并打包 `AstraCat-win-x64.zip` 与 `Setup.exe`
