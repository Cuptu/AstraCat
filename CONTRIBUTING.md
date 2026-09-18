# 参与 AstraCat 开发

提交代码前，请先阅读 [README.md](README.md) 和 [SECURITY.md](SECURITY.md)。安全问题不要直接公开在 Issue 中。

## 开发环境

- Windows x64 / macOS / Linux
- .NET 10 SDK（版本见 `global.json`）
- 播放器开发需要运行 `scripts/prepare-native-deps.ps1`

```powershell
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test
```

## 修改要求

- 长任务必须传播 `CancellationToken`，页面或项目切换后不得让旧结果覆盖当前状态。
- Avalonia 控件只在 UI 线程更新；列表保持虚拟化，绘制和播放热路径不得访问磁盘或网络。
- 语音识别与媒体处理采用纯托管 C# 与原生 C ABI 引擎架构。
- 注释说明约束、原因或失败模式，不复述代码，不写参考产品名称和临时设计备注。
- 不提交模型、CUDA 运行库、用户项目、API Key、日志、构建产物或第三方大体积二进制。

## 提交前检查

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\audit-repository.ps1
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test
```

修改播放器、时间轴、部署或导出逻辑时，还应使用真实媒体完成对应的手动验证。
