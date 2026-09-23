#!/usr/bin/env bash
# 一键构建/验证入口(WSL/Linux 侧)。开发约定与平台限制见 CONTRIBUTING.md;Windows 安装包打包见 build.ps1。
#
# 用法: ./build.sh [命令]
#   all       (默认) 依次执行 build → test → validate
#   build     交叉编译 Windows 目标(Debug),产物在 bin/Debug/net8.0-windows/
#   test      运行 tests/ 下的测试工程(零依赖控制台运行器,任一用例失败即非零退出;仅 WSL/Linux)
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
    # 打印文件头部注释块(第 2 行起,到第一行非注释为止)作为用法说明,
    # 不硬编码结束行号,增删头部注释无需同步改这里
    awk 'NR < 2 {next} /^#/ {sub(/^#/, ""); print; next} {exit}' "$0"
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
    echo "== test: 运行 tests/ 下的测试工程 (临时沙箱 + 合成数据,不碰真实战网数据) =="
    shopt -s nullglob
    local projects=(tests/*.csproj tests/*/*.csproj)
    shopt -u nullglob
    if [ ${#projects[@]} -eq 0 ]; then
        # 如实报告: 没有测试 ≠ 测试通过。测试工程放进 tests/(或其一级子目录)即被本命令自动接入。
        echo "tests/ 下未找到测试工程 —— 当前状态为「无测试」,不是「测试通过」。"
        return 0
    fi
    # 测试工程是 net8.0 可移植目标(控制台运行器),Linux 上原生执行,不需要 win targeting;
    # 运行器约定: 全部通过退出 0,任一失败退出非 0(契约由 RunnerContractTests 自检)。
    local p
    for p in "${projects[@]}"; do
        "$dotnet_bin" run --project "$p" --nologo
    done
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
