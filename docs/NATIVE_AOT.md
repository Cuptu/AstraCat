# Avalonia 原版 Native AOT 开发候选

日期：2026-09-14。范围是 AstraCat-main 的 .NET 10 / Avalonia 12.1.1，Windows x64。Native AOT 候选不是正式发行版。

## 构建

需要项目 global.json 指定的 .NET SDK，以及 Visual Studio C++ 链接工具和 Windows SDK。

在仓库运行：

```powershell
./scripts/publish-native-aot.ps1
```

脚本创建新的 artifacts/native-aot-<随机ID>，保留 publish.log、独立 NuGet 锁、生成目录、publish 产物和 smoke.txt。自定义 OutputDirectory 必须是尚不存在的目录；脚本自动识别并内置 AstraCore 运行时且执行 Native AOT 契约验证。现有 package-release.ps1 支持通过 `-NativeAot` 参数生成 Native AOT 免安装压缩包与 Inno Setup 安装程序。

底层发布参数是 `dotnet publish AstraCat.csproj -c Release -r win-x64 -p:PublishProfile=NativeAot -p:PublishAot=true`；脚本额外设置独立 ArtifactsPath、NuGetLockFilePath 和输出目录。重复实验不要与正在运行的发布产物共用输出位置。

## 适配内容

- 新增 NativeAot.pubxml；不默认改变普通 JIT 开发模式，不启用全局反射 JSON 回退，不压制 AOT/裁剪告警。
- 配置、项目、字幕缓存、术语、模型缓存及 AstraCore manifest 使用 System.Text.Json 编译期元数据。保留原字段名称、大小写选项、缩进以及 long 时间戳。
- 网络/ASR 请求中的匿名对象改为显式 JSON 字典，注册其嵌套容器及基础值类型；没有新增联网请求。
- 字幕编辑框改为 CompiledBinding；颜色选择器和样式窗口的命名控件绑定改为编译绑定。
- 排除独立 UI 基准项目及生成目录，避免多个 Main 入口。AOT 候选不包含旧 benchmarks 窗口；原有诊断模式继续在普通构建中使用。
- 新增离线产物检查以及可选原生窗口检查。窗口检查使用新的独立 runtime，避免读取或保存原项目目录。

## 本机验证记录

环境：Windows x64，.NET SDK 10.0.400，Avalonia 12.1.1。

本次实际执行的构建使用 `artifacts/native-aot`；日志在父目录 `../native-aot-build.log`。最终发布成功，日志未出现编译、裁剪或 AOT 告警。该结论仅限此次构建及依赖版本。

对实际生成的 EXE 执行：

```powershell
./AstraCat.exe --native-aot-smoke C:/absolute/path/smoke.txt
./AstraCat.exe --native-aot-ui-smoke C:/absolute/path/ui-smoke.txt
```

报告目录必须存在。两种模式均拒绝把支持动态代码的普通 JIT EXE 当作 Native AOT。UI 模式包含离线检查，并短暂显示窗口后关闭。

已执行 UI 模式，退出码 0，覆盖：

- 项目 JSON 往返；中文、多行文本及超过 JavaScript 安全整数范围的字幕时间戳。
- 翻译与断句响应大小写兼容、术语、字幕缓存、工作区状态、配置备份。
- 五类供应商协议请求 JSON 的构造/序列化，不发送请求，不代表 22 家接口联网验证。
- 模型目录缓存的异步读写、AstraCore manifest 流读取。
- 实际 libmpv DLL 加载、原生委托绑定、无音视频设备初始化与释放；不代表解码/播放通过。
- 字幕编辑框的初始值、双向写回及属性通知；主窗口启动和关闭；样式/颜色控件构造。

报告为 `artifacts/native-aot/ui-smoke.txt`。内部 RenderTargetBitmap 图片出现变换/布局异常，**不能作为视觉验收证据**；随后用 computer-use 检查独立预览的实际窗口，首页文字、卡片、图标和布局显示正常，辅助功能树可读。只确认首页，不宣称全页面视觉验收。

普通 Release JIT 构建也在独立目录 `artifacts/aot-jit-regression` 回归通过，0 警告、0 错误；日志 `../native-aot-jit-build.log`。新增发布脚本已做 PowerShell 语法检查；上述发布是直接执行相同的 dotnet 参数，未另跑一轮脚本的全流程构建。

## 产物与体积

本次独立预览位置：`../AstraCat-AOT-Preview/AstraCat.exe`。源产物为 `artifacts/native-aot/publish`，PDB 保留在那里；预览目录没有复制 PDB。

- 主 EXE：36,441,600 字节，约 **34.75 MiB**。
- 初始预览共 20 个文件，含许可证及现有媒体 DLL/CLI，约 **337.25 MiB**；首次运行后新增的 runtime 数据不计入此数字。
- 语音识别采用基于 sherpa-onnx 的进程内 ONNX C ABI 推理，AOT 产物开箱即具备原生推理与编译绑定契约，不自动裁剪 FFmpeg/libmpv。

未在本次建立同源码普通自包含包基线，因此不报告包体减少百分比或 RAM/CPU 提升。

## 尚未验收

真实项目导入编辑保存重开、全部页面交互、视频与字幕 Render API、实际导出、模型推理、各供应商联网、长时间稳定性、干净机和签名安装包仍需验证。主窗口启动与离线契约通过不代替这些步骤。

以后新增序列化类型，需要加入相应 JsonSerializerContext；放入 object 字典的运行时值也必须有生成元数据。禁止重新加入匿名反射序列化，或用静默异常吞掉缺失的元数据。

参考：[Microsoft Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)、[Avalonia 12.1.1 CompiledBinding 实现](https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/src/Avalonia.Base/Data/CompiledBinding.cs)。
