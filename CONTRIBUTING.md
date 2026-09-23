# 开发指南

本文档是这个仓库的开发入口，面向人类贡献者和 AI 编码 Agent。产品使用说明见 [README.md](README.md)，本地纯净版的网络审计与实机验证记录见 [docs/local-only-validation.md](docs/local-only-validation.md)。

## 项目定位与红线

- 本仓库是 qiyh99/BnetSwitch 的 fork（ixchitl/BnetSwitch），定位**纯本地开源版**：账号、快照、设置全部在本机；无广告、无埋点、无激活码、无云同步/支付、不依赖作者私服。
- 上游 `upstream`（qiyh99/BnetSwitch）只读参考，**禁止 push**；只推 `origin`（fork）。上游 2.4.x 已闭源，公开源码止于 2.1.6，后续演进以本 fork 为主。
- 许可证 GPLv3：可自由改/发，对外分发修改版必须同样 GPLv3 开源，并保留原作者版权与许可声明（`LICENSE`、「联系」窗口署名）。
- 不新增生产依赖。当前唯一第三方包是 `Microsoft.Data.Sqlite 8.0.7`；确需新增（含测试框架）时先说明理由并征得维护者同意。
- 免密切换核心约束不可破坏：只备份/还原 `%APPDATA%\Battle.net` 文件快照，**不删改注册表令牌槽**（只写回账号自己的槽）、**不强杀战网进程**（优雅关闭）、**不触发登出**。历史事故与依据见 `Services/TokenStore.cs` 顶部注释。
- 启动与本地操作零联网；战绩/段位仅用户主动查询才发起请求，不引入后台自动刷新。

## 环境与 SDK 约束

- **.NET 8 SDK**，目标框架 `net8.0-windows`（WPF + 少量 WinForms 托盘），产品项目文件 `BnetSwitch.csproj`（仓库根目录，无 solution 文件）；回归测试工程 `tests/BnetSwitch.Tests/`（可移植 `net8.0`，仅 Linux/WSL 运行，见「测试约定」）。
- WSL/Linux 侧 SDK 惯例安装在 `~/.dotnet/dotnet`（可能不在 PATH）。`build.sh` 自动探测，也可用 `DOTNET` 环境变量显式指定。
- NuGet 还原需要能访问 nuget.org（或本地已有 `~/.nuget/packages` 缓存）。

平台矩阵（✅ 可用 / ❌ 不可用）：

| 操作 | WSL/Linux | Windows |
| --- | --- | --- |
| `./build.sh build` / `publish`（交叉编译出 exe） | ✅ 需 `-p:EnableWindowsTargeting=true`（脚本已带） | ✅ 直接 `dotnet build` / `dotnet publish`，无需该参数 |
| 运行 `BnetSwitch.exe`（GUI 与全部命令行参数） | ❌ WPF 只能在 Windows 运行 | ✅ 需 .NET 8 Desktop Runtime（框架依赖发布） |
| `./build.sh test` / `validate` | ✅ | ❌ 两者都依赖 XDG/TMPDIR 目录隔离，**不要在 Windows 上跑**（test 套件有守卫，非 Linux 如实报错返回 2） |
| `build.ps1`（发布 + Inno Setup 打安装包） | ❌ | ✅ 需 Inno Setup 6 |

**WSL 编译通过 ≠ Windows 验收通过**：GUI 行为、免密切换、注册表/进程相关逻辑必须在 Windows 实机验证；环境不具备时如实标记「未验证」，不得以编译成功冒充。

## 一键构建 / 验证

WSL/Linux 用仓库根目录的 `build.sh`（约定与退出码见脚本头部注释）：

```bash
./build.sh            # 等价于 ./build.sh all：build → test → validate
./build.sh build      # Debug 交叉编译 → bin/Debug/net8.0-windows/
./build.sh publish    # 清空并重建 publish/local-only/（Release win-x64 框架依赖）
./build.sh test       # 运行 tests/ 下的测试项目
./build.sh validate   # 运行 validation/*.sh 合成兼容性检查
```

- 退出码：`0` 成功；非 0 为对应步骤失败；`2` 为参数错误或未找到 .NET SDK。
- **test 现状**：`./build.sh test` 运行 `tests/` 下的回归测试工程（零依赖控制台运行器，任一用例失败即非零退出；「失败→非零」契约本身有用例自检）。若 `tests/` 为空会如实打印「无测试」并返回 0，**不会谎称测试通过**。约定见下方「测试约定」。
- `validate` 跑的是合成数据兼容性检查（如 `validation/local-compatibility.sh`），属验证脚本，不等同于单元测试套件。
- Windows 安装包打包仍走 `build.ps1`（清理 → 发布 → 可选签名 → Inno Setup 打包），本脚本不重复实现。

## 测试约定

回归测试工程在 `tests/BnetSwitch.Tests/`（阶段3 / DIEM-140 引入），设计原则：

- **零第三方测试框架**：运行器是自研的最小控制台断言（`TestRunner.cs`），全部通过退出 0、任一失败退出非 0。仓库约定新增依赖（含测试框架）需维护者同意；如将来迁移 xunit 等，先征得同意，用例本身是普通静态方法，迁移只需替换运行器。
- **测的是真实产品逻辑**：测试工程用 `Compile Include` 直接链接 `Services/`、`Models/` 下的产品源文件（同一份实现既进 WPF 产物也进测试，不存在会漂移的第二套逻辑）；不 mock 产品核心判断和文件恢复逻辑。根 `BnetSwitch.csproj` 以 `Compile Remove="tests/**"` 反向排除测试目录，避免 WPF 默认递归收集把测试源码编进产品。
- **数据隔离**：每个用例换全新临时沙箱 —— Linux 上 `XDG_DATA_HOME`/`XDG_CONFIG_HOME`/`TMPDIR` 重定向 `Environment.GetFolderPath` 与 `Path.GetTempPath`（同 `validation/*.sh` 的机制），产品代码的 `%LOCALAPPDATA%\BnetSwitch`、`%APPDATA%\Battle.net` 全部落进临时目录；沙箱自检失效即炸响，整轮结束对比个人目录指纹证明无副作用。**仅 WSL/Linux 可跑**（非 Linux 返回 2）。
- **合成数据**：登录名一律 `example.invalid`，BattleTag/令牌字节全部编造；真实账号、令牌绝不进测试（见「数据与凭据安全约定」）。注意 Microsoft.Data.Sqlite 默认连接池会握住已删文件的 inode，沙箱 `Reset` 时统一 `ClearAllPools()`。
- **覆盖范围（当前）**：令牌槽变化判定（`TokenStore.ChangedSince`/LastSeen 持久化）、配置账号/区服解析（`AccountReader.ResolveCurrentAccountFromConfig` 及合成库读取）、快照文件存取/还原成败边界（`AppDataStore`）、切号事务失败恢复与活跃指针写回（`SwitchJournal`/`WriteActivePointer`）。部分用例是「现行为记录」（注释里标明），钉住历史行为供阶段4重构对照，不代表该行为是理想设计。
- **不覆盖 / 需 Windows 实机**：注册表真实读写、进程控制（`BattleNetController`）、WPF UI、真实免密切换。测试里只验证注册表边界在非 Windows 平台安全退化（`TokenStore.ReadAll` 空字典 / `Write` false / 不抛）。
- **加用例**：新建或续写 `tests/BnetSwitch.Tests/` 下的 `*Tests.cs`（一个领域一个静态类，内部 `T.Test(...)`），在 `Program.cs` 注册 `Run()`；跑 `./build.sh test` 验证。

## 模块导航

```text
App.xaml(.cs)              入口:单实例互斥、全局异常、全部命令行参数分发(见下方分级表)
Views/                     主窗口与全部对话框:MainWindow、SettingsWindow、ContactWindow、
                           CloseChoiceWindow、DeleteConfirmWindow、LoginNewWindow、NoteWindow、
                           SnapshotConfirmWindow
ViewModels/MainViewModel   UI 侧编排:账号列表刷新/保存快照/切换/删除/刷新段位
Models/BattleAccount       账号数据模型
Services/                  全部非 UI 逻辑:
  BattleNetPaths           解析战网各存储位置,自动定位 Battle.net.exe
  AccountReader            从战网 CachedData.db(SQLite) 读账号列表与当前登录号
  AppDataStore             ★ 切换核心:%APPDATA%\Battle.net 快照的存/还原/删
  BattleNetController      ★ 战网客户端优雅关闭(WM_ENDSESSION,不强杀)与启动
  TokenStore               注册表 UnifiedAuth 令牌槽读/写回(只写自己的槽,绝不删槽)
  SwitchJournal            进行中危险操作的现场记录(事务备份与失败回滚)
  SwitchLog                切号日志 %LOCALAPPDATA%\BnetSwitch\switch.log
  LoginProbe               核验「这次真的免密登录成功了」
  GameStateStore           跨区服游戏文件状态清单(.build.info 区服 + 内容哈希)
  AppSettings              settings.json 读写;商业版遗留字段一律丢弃、不可复活
  RankFetcher / RankStore  段位查询编排与本地缓存(ranks.json)
  DiagnosticExport         一键导出诊断包(zip 到桌面)
  Avatar / ThemeManager / TrayMenuFactory / LinkOpener / StartupService
                           本地头像配色 / 亮暗主题 / 托盘菜单 / 外部链接(仅反馈与项目主页) / 开机自启(HKCU Run)
  Overwatch/               战绩查询(仅用户主动触发):DashenAuth/DashenClient(国服大神扫码授权)、
                           BlizzardCareerClient/CareerParser(国际服生涯)、CareerProbe/OwProbe(CLI 探针实现)、
                           OwMappings/OwEnNames/OwImageCache(映射与图片缓存)
Stats/                     战绩窗口族:StatsWindow、CareerWindow、BillboardWindow、FriendsWindow、
                           HeroDetailWindow、MatchDetailWindow、QrLoginDialog,
                           与 StatsService/CareerService/StatsModels;Stats/Theme 为战绩窗独立样式
Themes/                    主窗口亮/暗资源字典(Palette.Light/Dark、Controls、Icons)
validation/                合成数据兼容性检查脚本(Linux 可跑,mktemp + XDG 隔离)
tests/BnetSwitch.Tests/    核心回归测试(零依赖控制台运行器,链接产品源码,XDG 沙箱 + 合成数据,约定见「测试约定」)
docs/                      验证报告等文档
installer/                 Inno Setup 脚本(Windows 打包用)
build.sh / build.ps1       WSL 一键构建验证 / Windows 安装包打包
```

## 命令行参数分级（Windows 专属）

所有参数都只在 Windows 上可运行；诊断类结果写到 `%LOCALAPPDATA%\BnetSwitch\*.txt`。改动或调用前先看风险级别：

| 级别 | 参数 | 行为 |
| --- | --- | --- |
| 只读诊断 | `--selftest` | 读账号库自检 → `selftest.txt`。不关客户端、不写快照 |
| 主动联网诊断 | `--friendtest`、`--matchtest`、`--careerprobe <战网Tag>`、`--owtest` | 用缓存登录态发真实请求（网易大神/暴雪）。输出可能含账号信息，**不得提交入库或贴进 issue** |
| UI 演示 | `--statsdemo`、`--matchdemo` | 免登录、纯演示数据开窗口，查布局，不联网 |
| UI 演示 + 主动联网 | `--careerdemo [战网Tag]` | 开独立生涯窗；不带 Tag 显示空态，**带 Tag 会发起真实查询** |
| ⚠️ 关闭客户端 / 写数据 | `--switch <账号ID>`、`--save`、`--addaccount` | 真实执行：优雅关闭战网 → 改写 `%APPDATA%\Battle.net` 快照/令牌槽写回 →（`--save`/`--addaccount`）重启客户端。自动化脚本里**绝不对真实用户数据跑这一级** |

## 数据与凭据安全约定

- 真实账号、令牌、手机号、战网 Tag、邮箱**一律不得**写入仓库、测试、日志、CI 或 issue 评论。
- 自动化测试/验证只用临时目录 + 合成数据，范例见 `tests/BnetSwitch.Tests/Sandbox.cs` 与 `validation/local-compatibility.sh`：mktemp 目录、XDG 变量重定向真实路径 API、合成域名用 `example.invalid`；不读真实令牌、不控制进程、不写注册表。
- 注册表与进程控制逻辑只能通过隔离边界测试，合成测试通过也不代表 Windows 实机行为，两者分开记录。
- 抓包文件（`*.har`、`*.saz`、`*.pcap*`）与私钥（`*.pfx`）已在 `.gitignore` 全量拦截，不要绕过。

## 分支与提交约定

- 从 `main` 的准确提交切话题分支（`feat/xxx`、`fix/xxx`、`docs/xxx`）；动手前先 `git status` 检查工作区，不覆盖他人未提交改动。
- remote 布局：`origin` = fork（唯一可推送），`upstream` = 原作者（push URL 已禁用）。**禁止 force push，禁止改写已推送历史**；打 tag / 发 Release 仅在维护者明确要求时做。
- commit 跟随仓库既有风格：中文 conventional-commits 标题（`feat:` / `fix:` / `docs:`），正文先说 why；一次提交只解决一个问题；只 `git add` 本次相关文件，不用 `git add -A`。
- 验证通过后合并回 `main` 并推 `origin`，让后续工作基于准确提交继续。

## 验证证据约定

- 改完必须在真实环境跑起来：交付时附实际执行的命令与原样输出（不编造、不美化），以及可复现步骤。
- 验证失败如实报告；连续 3 轮不通过就停下，带真实报错和判断来讨论。**绝不通过修改断言、放宽校验、mock 掉真实逻辑或跳过用例来让验证「通过」。**
- 平台不可用的验证项（Windows GUI、真实免密切换、注册表/进程行为）明确标记「未验证」及原因，不用「逻辑上应该没问题」代替。

## AI 编码 Agent 注意事项

- 仓库级 Agent 说明就是本文件；工作前先读本文档与 README。
- 工作目录里可能出现的 `CLAUDE.md`、`AGENTS.md`、`.multica/`、`.opencode/`、`.claude/` 是各 Agent 工具**本地自动管理**的运行时文件（含运行时身份与平台信息），已加入 `.gitignore`：**不要提交、不要把它们的内容抄进任何受版本管理的文件**。
- 遵循仓库既有结构、命名与错误处理风格；不擅自引入新库、新模式或脚手架。
