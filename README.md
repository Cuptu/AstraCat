<div align="center">

<img src="./Assets/Brand/AstraCatLogo.png" width="104" alt="AstraCat Logo" />

# AstraCat · 小猫做字幕

**跨平台智能字幕制作工具（Windows / macOS / Linux）—— 本地语音识别、AI校对、双语翻译与多轨时间轴编辑。**

[![Status](https://img.shields.io/badge/Status-DEV-F59E0B?style=flat-square&labelColor=1F2937)](https://github.com/Cuptu/AstraCat)
[![Release](https://img.shields.io/github/v/release/Cuptu/AstraCat?include_prereleases&style=flat-square&logo=github&label=Release&color=blue)](https://github.com/Cuptu/AstraCat/releases)
[![Downloads](https://img.shields.io/github/downloads/Cuptu/AstraCat/total?style=flat-square&logo=github&label=Downloads&color=brightgreen)](https://github.com/Cuptu/AstraCat/releases)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-0078D4?style=flat-square&logo=electron&logoColor=white)](https://github.com/Cuptu/AstraCat)
[![Language](https://img.shields.io/badge/Language-C%23-0078D4?style=flat-square)](https://dotnet.microsoft.com/languages/csharp)
[![Avalonia](https://img.shields.io/badge/Avalonia-12.1.1-0078D4?style=flat-square&logo=avaloniaui&logoColor=white)](https://avaloniaui.net/)
[![Acceleration](https://img.shields.io/badge/Acceleration-CUDA%20%7C%20CPU-76B900?style=flat-square&logo=nvidia&logoColor=white&labelColor=555)](https://developer.nvidia.com/cuda-toolkit)
[![License](https://img.shields.io/badge/License-GPL--3.0--only-green?style=flat-square)](./LICENSE)

[下载安装](#开始使用) · [功能介绍](#界面与功能) · [快速上手](#第一次使用) · [构建与测试](./docs/BUILDING.md) · [问题反馈](https://github.com/Cuptu/AstraCat/issues)

</div>

> [!IMPORTANT]
> **AstraCat 基于高性能 Avalonia 12.1.1 与 .NET 10 构建，支持 Native AOT 原生机器码编译与纯 C 媒体音频核心 AstraCore。Windows、macOS 与 Linux 各平台均提供一键安装原生交付包（.exe 安装程序、.dmg 镜像、.AppImage 独立运行包与 .deb 安装包），开箱即用。**

## AstraCat 是什么

AstraCat 是一款基于高性能 Avalonia 12.1.1 与 .NET 10 构建的跨平台智能字幕桌面工具。

它把本地语音识别、字幕校对、翻译、播放器和时间轴放进同一个项目里。导入一段视频或音频后，可以一路做到字幕文件或带字幕的视频；如果手里已经有 SRT，也可以直接拖进工作区继续修改。

语音识别在本机运行。翻译和字幕处理可以连接 DeepSeek、通义千问、OpenAI 兼容接口、Gemini、Claude 或本地 Ollama。云端接口不是必需项，不配置也能使用本地转录和字幕编辑。

## 它能帮你做什么

| 需要处理的内容 | 在 AstraCat 里的做法 |
|---|---|
| 视频或录音没有字幕 | 使用本地模型转录，生成带时间戳的字幕 |
| 识别结果有错字或断句不自然 | 校对原文、核对术语并重新断句 |
| 需要双语字幕 | 调用翻译接口生成译文，在双轨时间轴中继续调整 |
| 已经有媒体和 SRT | 直接拖进工作区，播放、定位并编辑字幕 |
| 字幕时间需要微调 | 在波形和多轨时间轴上拖动、裁剪、切分字幕块 |
| 需要交付字幕或成片 | 导出 SRT、ASS、TXT，或使用 FFmpeg 输出带字幕视频 |

```text
音视频 / 现有字幕 → 本地转录 → 原文处理 → 翻译 → 时间轴校对 → 字幕或视频导出
```

## 界面与功能

### 首页

首页保留最近任务、累计识别时长和本地模型状态。可以从这里新建任务，也可以直接回到上一次没有做完的项目。

### 本地语音识别

模型页统一管理本地语音识别模型。技术选型基于 **sherpa-onnx** 原生推理引擎，直接以 C ABI 集成 ONNX 运行时，涵盖 OpenAI Whisper、阿里通义 Qwen3-ASR 以及 NVIDIA NeMo Parakeet 等主流架构。

- **开发选择**：采用进程内原生推理架构取代外部脚本子进程。单一宿主进程统一部署与调度，天然适配 Native AOT 机器码编译，具备极高的运行稳定性和确定性响应。
- **性能开销**：
  - **低延迟与零磁盘 I/O**：通过 AstraCore 直接在应用内存中以 16kHz 16-bit 单声道 PCM 流式抽样并送入 Silero VAD 人声切片，全链路无临时中间音频文件读写，避免跨进程 IPC 协议的序列化性能瓶颈；
  - **轻量资源占用**：模型权重解压即用，免去预装庞大运行时依赖的存储与显存额外开销；
  - **灵活加速后端**：支持 CPU 多线程并行与专用 CUDA 运行库 GPU 硬件加速，Whisper 推理速率达 10x+ 实时率，Parakeet CTC 达 50x+ 极速吞吐，毫秒级流式出词。

<p align="center">
<img src="./docs/images/01-dashboard.webp" alt="AstraCat 首页" width="46%" />
<img src="./docs/images/02-asr-models.webp" alt="本地语音识别模型管理" width="46%" />
</p>
<p align="center"><sub>左：最近任务与模型状态　右：本地语音识别模型和运行环境</sub></p>

### 翻译接口

翻译服务在一个页面里统一管理。每个服务可以分别填写协议、地址、密钥、系统提示词和模型 ID，也可以添加自己的 OpenAI 兼容接口。

API Key 只保存在本机配置中。截图、日志和提交记录里仍然不要放真实密钥。

### 项目流程

一个项目分成语音转录、字幕处理、字幕翻译和工作区几个页面。流程图只是入口，不会把每一步绑死：可以完整跑一遍，也可以跳过字幕处理，或者直接载入现成字幕翻译。

### 字幕处理

这一页主要处理识别后的原文：

- 从字幕和文件名里找出可能的人名、地名和专业词；
- 通过 DeepSeek 联网搜索核对写法和来源；
- 只用已经确认的术语修正原文；
- 在不改原意的前提下重新断句。

联网研究失败不会覆盖原字幕。搜索结果、来源和失败原因会写进项目目录，也可以在右侧摘要中查看。该功能会使用 DeepSeek API 额度，不需要时可以关掉。

<p align="center">
<img src="./docs/images/05-workflow-pipeline.webp" alt="字幕项目流程" width="46%" />
<img src="./docs/images/06-subtitle-processing.webp" alt="字幕处理和术语研究" width="46%" />
</p>
<p align="center"><sub>左：项目处理流程　右：字幕修正、断句与术语研究</sub></p>

### 字幕工作区

工作区左侧是视频或音频播放器，右侧是字幕列表，下方是波形和多轨时间轴。播放位置、字幕列表和时间轴会互相跟随。

这里可以：

- 直接拖入视频、音频和 SRT；
- 播放纯音频并预览字幕；
- 修改字幕文字、开始时间和结束时间；
- 拖动、裁剪、切分、插入或删除字幕块；
- 使用多轨字幕制作双语字幕；
- 搜索、替换、撤销和重做；
- 调整字体、颜色、描边、背景和位置；
- 导出 SRT、ASS、TXT 或带字幕的视频。

<p align="center">
<img src="./docs/images/08-workspace-editor.webp" alt="视频预览、字幕列表和多轨时间轴" width="76%" />
</p>

### GPU 与运行环境

设置页显示显卡、驱动与 CUDA 运行库的实际状态。电脑即使已安装显卡驱动，ONNX Runtime 也需要配套的 CUDA 运行库（如 cuBLAS / cudart）方可启用 GPU 加速，AstraCat 会检测运行库就绪状态。

应用内下载的 CUDA 运行库仅存放在 AstraCat 的私有目录中，不污染系统环境变量。GPU 环境未就绪时会自动安全回退至 CPU 推理。

<p align="center">
<img src="./docs/images/03-llm-providers.webp" alt="翻译服务配置" width="46%" />
<img src="./docs/images/04-settings-cuda.webp" alt="GPU 和 CUDA 运行环境" width="46%" />
</p>
<p align="center"><sub>左：翻译与大模型接口　右：GPU、驱动和 CUDA 环境检测</sub></p>

## 开始使用

### 下载

在 [GitHub Releases](https://github.com/Cuptu/AstraCat/releases) 下载对应平台的原生交付包（全平台支持 Native AOT 机器码原生编译，免除 .NET 运行时依赖，极速启动）：

| 平台 | 安装包格式 | 说明 |
|---|---|---|
| **Windows (x64)** | `AstraCat-v*-Setup.exe` | 原生安装向导程序，自动创建快捷方式与文件关联 |
| **macOS (Apple Silicon)** | `AstraCat-v*-arm64.dmg` | 原生 Apple 磁盘镜像，挂载后拖拽至 Applications 即可运行 |
| **Linux (x86_64)** | `AstraCat-v*-x86_64.AppImage` | 独立免安装可执行单文件，赋予执行权限后直接双击运行 |
| **Linux (Debian / Ubuntu)** | `AstraCat-v*_amd64.deb` | 原生系统软件包，双击调起软件中心一键安装 |

所有安装程序均已内置 AstraCore 原生媒体引擎，无需用户预先安装 .NET SDK 或运行库。语音识别模型和可选 CUDA 运行库体积较大，首次使用时可在应用内按需下载。

### 第一次使用

1. 打开“模型配置”，下载一个识别模型。
2. 新建任务，导入视频或音频。
3. 在“语音转录”里选择模型、语言和设备，然后开始识别。
4. 在工作区检查文字和时间轴。
5. 导出字幕；需要成片时再选择视频导出。

如果只想编辑已有字幕，新建项目后把媒体和 SRT 直接拖进工作区即可。

## 导出格式

| 类型 | 当前支持 |
|---|---|
| 字幕 | SRT、ASS、TXT |
| 视频容器 | MP4、MOV、MKV |
| 视频编码 | H.264、HEVC、AV1 |
| 硬件编码 | NVIDIA NVENC、Intel QSV、AMD AMF |
| 分辨率 | 保持原始、常用预设或自定义 |
| 帧率 | 保持原始、24、25、30、60 FPS |

导出前会检查 FFmpeg 实际提供的编码器。硬件编码不可用或执行失败时，可以改用软件编码。

## 数据放在哪里

便携版和开发版的数据都在程序目录下的 `runtime`：

```text
runtime/
├─ cache/       模型下载与网络解析缓存
├─ config/      应用与翻译接口配置
├─ gpu/         可选 CUDA 运行库（Direct ONNX 加速）
├─ models/      本地 ONNX 语音识别与 VAD 模型权重
├─ projects/    项目、字幕和自动保存
└─ tools/       FFmpeg 与 libmpv
```

不要在没有备份的情况下删除 `runtime`。只想腾出构建空间时，可以删除 `bin`、`obj` 和 `dist`，它们不包含项目数据。

## 隐私和联网

- 本地转录完全在机内执行，不会上传音视频。
- 播放、时间轴编辑和本地字幕导出不需要联网。
- 模型下载访问官方模型源或高速镜像，下载后离线可用。
- 翻译、LLM 校对和术语研究会把相应文字发送给所选服务商。
- 联网术语研究会访问 DeepSeek 的 `web_search`，并消耗对应 API 额度。

使用云端接口前，请自行查看服务商的数据处理规则和计费方式。

## 开发选择与性能开销

AstraCat 采用 .NET 10 与 Avalonia 12.1.1。架构选型注重极低系统开销、确定性响应与开箱即用的跨平台能力：

```text
Avalonia 宿主进程 (.NET 10 / Native AOT)
├─ 渲染引擎: libmpv Render API      同进程 OpenGL 帧缓冲直接绘制，零额外窗口与上下文切换开销
├─ 语音推理: sherpa-onnx (C ABI)     进程内全内存 ONNX 推理（Whisper / Qwen / NeMo）
├─ 媒体下载: 内置 Web 视图嗅探      通过浏览器会话提取 Cookie，实现高带宽流式分块下载
├─ 媒体编辑: AstraCore (C ABI)      纯 C 高性能音频抽样、无损流裁剪与时间戳重基准计算
└─ 导出转码: FFmpeg CLI              按需拉起独立子进程，调用硬件编码器（NVENC / QSV / AMF）
```

### 为什么选择 Avalonia 与同进程渲染

- **开发选择**：选用 Avalonia 12.1.1 配合 XAML 编译绑定，保证 Windows、macOS 与 Linux 界面行为、样式和动效的高度一致；视频播放基于 `OpenGlControlBase` 通过 libmpv Render API 直接将视频帧绘制进 Avalonia 的 OpenGL 帧缓冲，无需创建独立的系统原生视频子窗口。
- **性能开销**：
  - 杜绝传统跨窗口嵌入（Airspace）带来的图层撕裂、遮挡穿透与 DPI 缩放不同步问题；
  - 字幕、控件浮层与视频处于同一合成树，GPU 硬件合成开销极小，窗口缩放流畅无闪烁。

### 时间轴海量字幕的性能优化

字幕时间轴没有为每条字幕创建单独的 UI 控件，而是在 `SubtitleTimelineControl` 中使用 `DrawingContext` 统一在画布上批处理绘制：
- **空间索引与二分查找**：数据变化时建立时间区间索引，视图滚动或缩放时仅检索视口范围内的字幕块，时间复杂度由 $O(N)$ 降至 $O(\log N + M)$（$M$ 为当前可见块数），即使面对数千条字幕的超长视频也能保持 60 FPS 跟手拖动；
- **渲染对象复用与布局缓存**：复用画刷、画笔及带上限的 `TextLayout` 文本排版缓存，避免每帧重复进行高开销的字体排版计算与 GC 压力。

### 为什么选择进程内原生语音推理

- **开发选择**：选择基于 C ABI 的 **sherpa-onnx** 作为语音推理引擎，而非将推理委托给外部脚本子进程。推理引擎与 .NET 共享相同的应用生命周期，原生支持跨平台编译与 Native AOT。
- **性能开销**：
  - **消除冷启动延迟**：避免每次调用拉起外部解释器与运行时（节省 2~5 秒进程初始化延迟），模型加载后常驻内存，随时响应；
  - **全内存零磁盘 I/O**：通过 AstraCore C ABI 直接在内存中抽取 16kHz PCM 单声道音频流送入推理，免去生成中间音频临时文件的磁盘读写开销，也避免了跨进程序列化的大块数据拷贝；
  - **确定性响应与取消粒度**：全流程深入接入 `CancellationToken`，用户点击取消时可在微秒级安全中断计算并即刻释放计算资源。

### 为什么选择内置 Web 视图辅助下载

- **开发选择**：对于流媒体下载，采用内置 Web 视图结合原生下载服务的模式，用户可直接在窗口中进行常规网页浏览与账号登录，系统自动嗅探并承接会话 Cookie，免去手动导出配置或配置外部爬虫工具的繁琐门槛。
- **性能开销**：流式直连下载，内存缓冲分块刷盘，CPU 占用率低于 2%，同时支持代理加速与断点续传。

## 开发构建

当前开发环境为 Windows x64，需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```powershell
git clone https://github.com/Cuptu/AstraCat.git
cd AstraCat
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test
```

Windows 开发可先下载并校验旧预编译依赖；正式发布使用统一 AstraCore：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\prepare-native-deps.ps1
dotnet run -c Debug

# 或从固定提交构建共享 FFmpeg/libmpv/libass 运行时
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\prepare-astracore-sources.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-astracore.ps1
$env:ASTRACAT_MEDIA_RUNTIME = (Resolve-Path .\artifacts\astracore\win-x64).Path
dotnet run -c Debug
```

源码仓库不保存构建产物、模型和 CUDA 运行库。AstraCore
脚本固定 FFmpeg、mpv、libass、libplacebo 和 8-bit x265 的源码提交，生成
一套共享 ABI，并检查软件编码器、三家硬件编码入口、D3D11VA 和运行时哈希。

### 构建检查

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\audit-repository.ps1
dotnet build -c Release
dotnet test
```

### 打包 Windows 版本

需要先安装 Inno Setup 6，然后执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\prepare-astracore-sources.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-astracore.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\package-release.ps1 -Version 0.1.2-DEV -AstraCoreDir .\artifacts\astracore\win-x64
```

脚本会生成：

```text
dist/AstraCat-v0.1.2-DEV-Setup.exe
dist/AstraCat-v0.1.2-DEV-win-x64.zip
dist/AstraCat-v0.1.2-DEV-SHA256.txt
```

它会检查 AstraCore 清单哈希、共享 ABI、依赖闭包、FFmpeg 功能白名单和
200 MiB 硬上限；缺少能力或混入旧版 FFmpeg/libmpv 时直接停止。

GitHub 上传范围、Actions 工作流和标签发布方法见 [构建与发布文档](./docs/BUILDING.md)。

<details>
<summary><b>项目结构</b></summary>

```text
AstraCat/
├─ Assets/                          应用图标、字体与界面视觉资产
├─ Controls/                        高性能 DrawingContext 字幕时间轴与自定义控件
├─ Models/                          字幕、样式、音频波形与工程数据模型
├─ Services/                        领域服务层架构
│  ├─ Animation/                    动效调度与 Easing 缓动曲线
│  ├─ Media/                        播放器、波形提取、PureMediaDownloadService 下载与媒体导出服务
│  ├─ Native/                       AstraCore C ABI 跨平台动态库载入桥
│  ├─ Speech/                       基于 sherpa-onnx 的本地离线 ASR 引擎、Silero VAD 与模型注册表
│  └─ Workspace/                    工程事务仓储与工作区会话管理
├─ Views/                           Avalonia 界面窗口与浮层组件
├─ tests/                           自动化单元测试 (MSTest / .NET 10)
├─ native/                          纯 C 语言高性能 AstraCore 媒体音频核心源码
├─ engines/                         原生语音模型规范与文档
├─ installer/                       Windows Inno Setup 原生安装程序脚本
├─ scripts/                         全平台构建、发布打包与安全审计脚本
├─ runtime/                         本地模型、CUDA 运行库与项目持久化目录
├─ App.axaml                        应用资源与主题配置
├─ Program.cs                       程序启动入口与平台渲染上下文初始化
├─ AstraCat.slnx                    现代跨平台解决方案
├─ AstraCat.csproj                  .NET 10 / Avalonia 项目配置
└─ packages.lock.json               NuGet 依赖锁定文件
```

界面使用 Avalonia 12.1.1 渲染，播放器通过 libmpv Render API 同步呈现，音视频抽样与裁剪由 AstraCore C ABI 原生完成，本地语音识别由 sherpa-onnx 进程内驱动。

</details>

## 常见问题

<details>
<summary><b>为什么显示 CPU，明明电脑有 NVIDIA 显卡？</b></summary>

AstraCat 检查系统是否安装了配套的 CUDA 运行库（cuBLAS / cudart DLL）。可在模型与环境页一键安装专用 CUDA 运行库，直接为 ONNX Runtime 提供 GPU 硬件加速。

</details>

<details>
<summary><b>模型下载完成后为什么还是不能使用？</b></summary>

sherpa-onnx 模型由程序自动校验完整性并放置在 `runtime/models` 中。如果网络中断导致权重未完成，可以在模型页重新点击下载，或检查目录下的 `.astracat_complete` 完成标记。

</details>

<details>
<summary><b>视频或纯音频无法播放怎么办？</b></summary>

先确认 `runtime/tools/mpv/libmpv-2.dll` 存在。若文件完整，更新显卡驱动后重试；仍然失败时请在 Issues 中附上系统版本、媒体格式和错误提示。

</details>

<details>
<summary><b>翻译或联网术语研究没有结果怎么办？</b></summary>

检查服务地址、API Key 和模型名称。DeepSeek 联网术语研究使用 Responses API 和 `deepseek-v4-flash`；接口超时、限流或没有返回完整 JSON 时会自动重试，并把最终原因写入术语摘要。

</details>

## 参与开发

发现问题请开 [Issue](https://github.com/Cuptu/AstraCat/issues)。如果准备提交代码，欢迎发 [Pull Request](https://github.com/Cuptu/AstraCat/pulls)。

提交前请至少完成：

1. `dotnet build -c Release`；
2. 运行 `dotnet test` 确保单元测试全数通过；
3. 检查取消、失败回退和重复执行；
4. 不提交 API Key、模型、CUDA 运行库、项目数据和构建产物。

## 鸣谢与支持

### 核心技术框架

<a href="https://avaloniaui.net/" target="_blank" rel="noopener noreferrer">
  <img src="./docs/images/avalonia-banner.png" alt="Avalonia UI" width="100%" />
</a>

AstraCat 深度基于 **[Avalonia UI](https://avaloniaui.net/)** 打造现代化高性能跨平台桌面体验，在此特别向 Avalonia 核心团队及全球开源社区致以崇高敬意！

### 开源许可证支持

<a href="https://jb.gg/OpenSourceSupport" target="_blank" rel="noopener noreferrer">
  <img src="./docs/images/jetbrains.svg" width="96" alt="JetBrains" />
</a>

特别感谢 **[JetBrains](https://www.jetbrains.com/?from=AstraCat)** 通过 [免费开源项目许可证计划 (Free Open Source License)](https://jb.gg/OpenSourceSupport) 为 AstraCat 核心开发团队提供专业 IDE 及全套开发工具链支持。

### 赞助商与支持者

感谢以下平台与赞助商对开源生态的大力支持：

<a href="https://www.digitalocean.com/" target="_blank" rel="noopener noreferrer">
  <img src="./docs/images/digitalocean.svg" width="190" alt="DigitalOcean" />
</a>

### 使用的开源项目

- [Avalonia](https://avaloniaui.net/)：跨平台桌面界面框架与现代化渲染引擎
- [FFmpeg](https://ffmpeg.org/)：音视频多媒体编解码、音频探测与字幕合成烧录
- [mpv / libmpv](https://mpv.io/)：硬件加速媒体播放核心引擎
- [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx)：跨平台下一代原生语音识别与 VAD 离线推理引擎
- [Hugging Face](https://huggingface.co/)：开放模型目录与权重分发托管

各组件和模型遵循各自的开源许可证。发布二进制文件时，均遵守相应的再分发与署名要求。

## 许可证

原生媒体运行时的统一构建、字幕接口边界、多平台策略和 FFmpeg CLI 迁移路线见 [AstraCore media runtime](docs/ASTRACORE.md)。

AstraCat 使用 [GNU General Public License v3.0](LICENSE)，SPDX 标识为 `GPL-3.0-only`。

```text
Copyright (C) 2026 Cuptu
SPDX-License-Identifier: GPL-3.0-only
```

项目地址：<https://github.com/Cuptu/AstraCat>

## Star History

[![Star History Chart](https://api.star-history.com/svg?repos=Cuptu/AstraCat&type=Date)](https://star-history.com/#Cuptu/AstraCat&Date)

