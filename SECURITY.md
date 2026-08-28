# 安全策略

## 支持范围

AstraCat 仍处于积极开发阶段。安全修复优先应用于默认分支和最新正式版本；旧版本可能需要先升级才能获得修复。

## 私下报告漏洞

不要通过公开 Issue、Pull Request、Discussion、日志或截图披露尚未修复的漏洞。请使用 GitHub 私有漏洞报告：

<https://github.com/Cuptu/AstraCat/security/advisories/new>

报告中建议包含：

- 受影响版本或提交；
- 操作系统、显卡和相关运行环境；
- 可复现步骤或最小验证样例；
- 实际影响和可能的攻击场景；
- 已知的缓解措施；
- 必要的日志，但请移除 API Key、访问令牌、私人文件路径和用户数据。

如果报告涉及恶意文件，请先描述文件类型、哈希和行为，不要直接提交可能自动执行的载荷。

> 仓库所有者需要在 GitHub 的 `Settings → Security → Private vulnerability reporting` 中启用私有漏洞报告。功能启用前，请保留报告，不要改用公开 Issue 提交敏感细节。

## 重点安全边界

AstraCat 会处理本地媒体、字幕、API 凭据、模型文件和多个外部进程。安全审查应特别关注：

- FFmpeg、FFprobe、libmpv 和 Python 可执行文件的查找与启动路径；
- 下载文件的来源、哈希校验、压缩包路径穿越和临时目录处理；
- 模型仓库、Python 包和 GPU 运行库的供应链完整性；
- 命令参数、字幕文本和文件路径的转义；
- API Key、翻译请求、日志和项目缓存中的敏感数据；
- Python ASR Worker 的逐行 JSON 协议及标准输出污染；
- 安装、卸载和取消操作中的目录竞争与不完整状态；
- 原生资源、子进程和 GPU 上下文的关闭顺序。

## 公开披露

请给予维护者合理时间确认、修复和发布安全更新。修复可用后，维护者可以通过 GitHub Security Advisory 和 Release Notes 协调公开披露，并在征得同意后注明报告者贡献。

