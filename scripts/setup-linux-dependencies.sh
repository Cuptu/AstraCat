#!/usr/bin/env bash
set -e

echo "=== AstraCat Linux (Ubuntu/WSLg) 调试依赖安装 ==="

echo "[1/4] 更新软件源并安装基础多媒体与 OpenGL 依赖..."
sudo apt update
sudo apt install -y curl wget git build-essential \
    libmpv-dev libmpv2 ffmpeg \
    libx11-dev libice6 libsm6 libfontconfig1 \
    libgl1-mesa-glx libgl1-mesa-dri mesa-utils \
    fonts-noto-cjk

echo "[2/4] 检查/安装 .NET 10 SDK..."
if ! command -v dotnet &> /dev/null || ! dotnet --list-sdks | grep -q "^10\."; then
    echo "正在下载并安装 .NET 10 SDK..."
    curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$PATH:$DOTNET_ROOT"
    
    # 写入 ~/.bashrc
    if ! grep -q "DOTNET_ROOT" ~/.bashrc; then
        echo 'export DOTNET_ROOT="$HOME/.dotnet"' >> ~/.bashrc
        echo 'export PATH="$PATH:$DOTNET_ROOT"' >> ~/.bashrc
    fi
fi

echo "[3/4] 验证 Linux GPU 与 OpenGL 驱动就绪度..."
glxinfo -B || echo "提示: glxinfo 未能获取完整信息，WSLg 将在启动 GUI 时动态初始化。"

echo "[4/4] 验证 AstraCat 构建..."
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_DIR"

dotnet build AstraCat.csproj -c Debug

echo "=============================================="
echo "✓ Linux 调试环境准备就绪！"
echo "运行调试命令: dotnet run -c Debug"
echo "=============================================="
