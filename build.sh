#!/usr/bin/env bash
# 一键构建/验证入口(WSL/Linux 侧)。开发约定与平台限制见 CONTRIBUTING.md;Windows 安装包打包见 build.ps1。
#
# 用法: ./build.sh [命令]
#   all       (默认) 依次执行 build → test → validate
#   build     交叉编译 Windows 目标(Debug),产物在 bin/Debug/net8.0-windows/
#   test      运行 tests/ 下的测试项目;当前没有测试项目时如实报告,不算通过
#   validate  运行 validation/*.sh 合成兼容性检查(合成数据,不是单元测试)
#   publish   清空输出目录后产出 Release win-x64 框架依赖发布物,到 publish/local-only/
#
# 退出码: 0 = 成功;非 0 = 对应步骤失败(参数错误 / 缺 .NET SDK 时为 2)
# 注意: WPF(net8.0-windows)产物只能在 Windows 上运行,WSL 编译通过不代表 GUI 验收通过。
set -euo pipefail

root=$(cd "$(dirname "$0")" && pwd)
cd "$root"

# dotnet 探测顺序: DOTNET 环境变量 > PATH > ~/.dotnet/dotnet(WSL 常见安装位置)
dotnet_bin="${DOTNET:-}"
if [ -z "$dotnet_bin" ]; then
    if command -v dotnet >/dev/null 2>&1; then
        dotnet_bin=$(command -v dotnet)
    elif [ -x "$HOME/.dotnet/dotnet" ]; then
        dotnet_bin="$HOME/.dotnet/dotnet"
    else
        echo "错误: 未找到 .NET 8 SDK。请安装后用 DOTNET 环境变量指定路径,如: DOTNET=~/.dotnet/dotnet $0" >&2
        exit 2
    fi
fi

# Linux 上交叉编译 Windows 目标必须显式开启 Windows targeting
win_targeting=(-p:EnableWindowsTargeting=true)

usage() {
    # 打印文件头部注释块(第 2~12 行)作为用法说明
    sed -n '2,12p' "$0" | sed 's/^#//'
}

cmd_build() {
    echo "== build: 交叉编译 Windows 目标 (Debug) =="
    "$dotnet_bin" build BnetSwitch.csproj --nologo -v minimal "${win_targeting[@]}"
    echo "产物: bin/Debug/net8.0-windows/BnetSwitch.exe (只能在 Windows 上运行)"
}

cmd_publish() {
    local out=publish/local-only
    echo "== publish: 清空 $out 后产出 Release win-x64 (框架依赖) =="
    rm -rf "$out"
    "$dotnet_bin" publish BnetSwitch.csproj -c Release -r win-x64 --self-contained false \
        -o "$out" --nologo -v minimal "${win_targeting[@]}"
    rm -f "$out"/*.pdb
    echo "产物: $out/BnetSwitch.exe (目标机需 .NET 8 Desktop Runtime;只能在 Windows 上运行)"
}

cmd_test() {
    echo "== test: 运行测试项目 =="
    shopt -s nullglob
    local projects=(tests/*.csproj)
    shopt -u nullglob
    if [ ${#projects[@]} -eq 0 ]; then
        # 如实报告: 没有测试 ≠ 测试通过。后续测试项目放进 tests/ 即被本命令自动接入。
        echo "tests/ 下未找到测试项目 —— 当前状态为「无测试」,不是「测试通过」。"
        echo "自动化回归测试计划在阶段3 (DIEM-140) 引入,届时本项目约定测试工程放 tests/ 目录。"
        return 0
    fi
    "$dotnet_bin" test "${projects[@]}" --nologo "${win_targeting[@]}"
}

cmd_validate() {
    echo "== validate: 运行 validation/ 合成兼容性检查 (临时目录 + 合成数据,不碰真实战网数据) =="
    shopt -s nullglob
    local scripts=(validation/*.sh)
    shopt -u nullglob
    if [ ${#scripts[@]} -eq 0 ]; then
        echo "validation/ 下未找到脚本。"
        return 0
    fi
    local s
    for s in "${scripts[@]}"; do
        echo "-- $s --"
        DOTNET="$dotnet_bin" bash "$s"
    done
}

cmd_all() {
    cmd_build
    echo
    cmd_test
    echo
    cmd_validate
    echo
    echo "== all 完成: build 成功;test / validate 的实际状态见上方各自输出 =="
}

case "${1:-all}" in
    all)             cmd_all ;;
    build)           cmd_build ;;
    test)            cmd_test ;;
    validate)        cmd_validate ;;
    publish)         cmd_publish ;;
    -h|--help|help)  usage ;;
    *)
        echo "错误: 未知参数 '$1'。" >&2
        usage >&2
        exit 2
        ;;
esac
