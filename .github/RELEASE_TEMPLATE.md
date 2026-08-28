<div align="center">

<img src="https://raw.githubusercontent.com/Cuptu/AstraCat/main/Assets/Brand/AstraCatLogo.png" width="88" alt="AstraCat Logo" />

# AstraCat DEV

**在 Windows 上完成转录、校对、翻译和字幕时间轴编辑。**

</div>

> [!IMPORTANT]
> **AstraCat 目前处于 DEV 阶段。界面、项目格式和模型环境仍会调整，更新前请备份 `runtime/projects`。当前只发布 Windows x64 版本；macOS 和 Linux 会在 Windows 版稳定后继续适配和发布。**

AstraCat 把本地语音识别、字幕校对、翻译、播放器和时间轴放在同一个项目里。视频、音频和字幕文件可以直接拖进工作区；完成转录后，可以继续校时、翻译、调整样式，并导出字幕文件或带字幕的视频。

这个版本适合愿意提前体验新功能、并能接受界面和项目格式继续变化的用户。重要项目请先备份，再升级。

## 下载哪个文件

| 文件 | 用途 |
|---|---|
| `AstraCat-v*-Setup.exe` | Windows 安装包，推荐大多数用户使用 |
| `AstraCat-v*-win-x64.zip` | 便携版，解压后直接运行 |
| `AstraCat-v*-SHA256.txt` | 文件校验值，用来确认下载完整性 |

## 目前可以做什么

- 导入视频、纯音频、SRT 等字幕文件，并在工作区继续编辑；
- 使用 Qwen3-ASR、Whisper、FunASR、NVIDIA、MOSS 等本地语音模型；
- 在纯音频模式下播放、定位并预览字幕；
- 使用多轨时间轴校对文本、时间、说话人与字幕样式；
- 连接 DeepSeek、通义千问、Gemini、Claude、OpenAI 兼容接口或本地 Ollama；
- 使用 FFmpeg 导出字幕文件、软字幕视频和烧录字幕视频；
- 根据模型环境选择 CPU 或 NVIDIA CUDA 加速。

## 安装前说明

- 支持 Windows 10/11 x64；
- 安装包已包含 .NET 运行时、FFmpeg 和 libmpv；
- 语音模型与 CUDA 运行库不在安装包内，需要时在应用中单独下载；
- 当前安装包没有代码签名，Windows 可能显示“未知发布者”。请只从本仓库 Releases 页面下载，并核对 SHA-256；
- 更新前建议完整备份程序目录下的 `runtime/projects`。

## 反馈问题

如果遇到白屏、播放失败、模型部署失败或导出异常，请到 [Issues](https://github.com/Cuptu/AstraCat/issues) 提交问题，并附上：

- Windows 版本；
- CPU、显卡与显存信息；
- 使用的模型和媒体格式；
- 可以复现问题的操作步骤；
- 错误提示或截图。

项目说明、使用方法和构建文档见 [README](https://github.com/Cuptu/AstraCat#readme)。

---

下面是 GitHub 根据本次提交和合并记录生成的变更清单。
