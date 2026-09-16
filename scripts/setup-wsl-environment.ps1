# AstraCat WSL2 / Linux 调试环境一键安装脚本
$ErrorActionPreference = 'Stop'

Write-Host "=== AstraCat Linux 调试环境检查与安装 ===" -ForegroundColor Cyan

# 1. 检查 CPU 硬件虚拟化 (VT-x / SVM)
$cpu = Get-CimInstance Win32_Processor
if (-not $cpu.VirtualizationFirmwareEnabled) {
    $board = Get-CimInstance Win32_BaseBoard
    Write-Host "`n[!] 检测到主板 BIOS 中未开启 CPU 硬件虚拟化！" -ForegroundColor Yellow
    Write-Host "当前处理器: $($cpu.Name)" -ForegroundColor Gray
    Write-Host "当前主板: $($board.Manufacturer) $($board.Product)" -ForegroundColor Gray
    Write-Host @"

由于 WSL2 与 WSLg（Linux GUI 窗口与 OpenGL 渲染）依赖硬件虚拟化支持，
请先按以下步骤在主板 BIOS 中开启 VT-x（仅需操作一次）：

1. 重启电脑，在开机出现 LOGO 时连续按 [Delete] 或 [F2] 键进入 BIOS 设置；
2. 切换到 [Advanced]（高级设置）页面；
3. 点击 [CPU Configuration]（CPU 配置）；
4. 找到 [Intel (VMX) Virtualization Technology]（或 Intel 虚拟化技术）；
5. 将其设置为 [Enabled]（开启）；
6. 按 [F10] 保存并退出，正常进入 Windows。

开启后重新运行本脚本即可自动完成后续全部安装！
"@ -ForegroundColor Yellow
    return
}

Write-Host "[✓] CPU 硬件虚拟化已在 BIOS 中开启。" -ForegroundColor Green

# 2. 检查管理员权限
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "`n[i] 正在请求管理员权限以启用 Windows 虚拟化和 WSL 组件..." -ForegroundColor Cyan
    Start-Process powershell -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -NoExit -File `"$PSCommandPath`""
    return
}

# 3. 安装 WSL2 与 Ubuntu
Write-Host "`n[i] 正在安装/更新 WSL2 及 Ubuntu..." -ForegroundColor Cyan
try {
    & wsl.exe --install -d Ubuntu --no-launch
    Write-Host "[✓] WSL2 与 Ubuntu 安装指令已下发。" -ForegroundColor Green
} catch {
    Write-Warning "执行 wsl.exe --install 失败：$_"
}

Write-Host @"

=== 安装阶段指引 ===
如果系统提示需要重启电脑，请先重启；
重启后在开始菜单打开 [Ubuntu]，根据提示设置用户名和密码即可就绪。
就绪后可执行 scripts/setup-linux-dependencies.sh 自动安装 .NET 10、libmpv 及渲染依赖。
"@ -ForegroundColor Green
