# 本地版网络审计与验证（DIEM-138）

基线 `13fff09`。本 fork 保留 qiyh99 署名与 GPLv3，不更改账号、快照、令牌及切换算法。

## 网络入口审计

这是源码调用链审计，**不是运行时零联网证明**。

| 入口 | 触发条件与处理 |
| --- | --- |
| MainWindow.Loaded | 仅日志、本地刷新、启动本地账号轮询、托盘；已删除 ping、更新检查、授权复核、广告及图片预加载 |
| Analytics / LicenseService / UpdateService | 源文件和窗口已删除，不保留服务器配置或开关 |
| AppSettings.Load / Save | 旧广告、激活、私服地址及旧反馈地址均为未知 JSON 字段，被忽略并在保存时丢弃；保留本地设置、备注、置顶与隐藏列表 |
| 账号浏览、搜索、2 秒轮询 | 读取本地 SQLite、快照和 ranks.json；账号段位图标先检查本地文件存在，不回源下载 |
| 刷新段位 | 只由 OnRefreshRanks 点击发起；国服 DashenAuth → RankFetcher → StatsService，国际服 BlizzardCareerClient；查询状态与切号 Busy 分离，不因网络等待覆盖本地操作进度 |
| 国服战绩 | OnOpenStats → StatsWindow.ShowFor → 校验缓存大神会话/主动扫码 → StatsService；会话存在本身不会触发启动联网 |
| 国际服战绩 | OnOpenStats → CareerWindow.ShowFor → CareerService → BlizzardCareerClient；仅查询内使用 10 分钟 HTML 缓存与失败回退 |
| 扫码轮询 | 仅主动打开 QrLoginDialog 后每 2 秒查询扫码状态，120 秒过期，关闭窗口停止计时器；慢请求关闭后的生命周期仍需 Windows 观测 |
| 战绩分页、好友、榜单、详情、刷新 | 用户打开战绩后按对应交互加载；没有主窗口启动定时刷新 |
| 网易 HTTP | DashenClient：q.reg.163.com、api.cc.163.com、inf.ds.163.com、datamsapi.ds.163.com；仅战绩授权/查询。协议请求头保留，作者机器指纹遥测已删除 |
| 暴雪 HTTP | BlizzardCareerClient：overwatch.blizzard.com 及响应重定向；仅主动国际服查询 |
| 战绩配置与图片 | OwMappings 从 s.166.net/config/ds_ow/ 读映射；OwImageCache 下载响应中的图片 URL。StatsService 已删除全部英雄/地图后台预取，仅下载当前查询需要的资源；详情所需技能图片批量加载保留 |
| WPF 图片 | 主窗口头像为本地生成；段位 LoadIcon 检查 File.Exists；StatsModels 图片转换读取本地缓存；二维码从主动授权响应字节解码；广告远程 Image 绑定已删除 |
| CLI 显式战绩探针 | --owtest、--friendtest、--matchtest、--careerprobe、--careerdemo 保留，显式传参即主动查询；普通启动不走这些分支。探针可能包含账号信息，不用于真实账号日志提交 |
| 外部浏览器 | 点击反馈/项目主页才调用 LinkOpener，固定到 ixchitl/BnetSwitch；无自动更新下载或安装 |
| 战网进程 | 用户点击启动/切号会启动 Battle.net，其自身登录网络请求必须在抓包中按进程区分 |

HTTP 客户端原有超时：大神 20 秒、映射 20 秒、图片 25 秒；暴雪 60 秒并保留取消路径。请求失败走现有查询错误显示，不占用切号的忙碌锁。扫码窗口是主动授权步骤，用户可关闭。

## 已执行验证

WSL：

```sh
~/.dotnet/dotnet build -p:EnableWindowsTargeting=true --nologo
bash validation/local-compatibility.sh
~/.dotnet/dotnet publish -c Release -r win-x64 --self-contained false -p:EnableWindowsTargeting=true -o publish/local-only --nologo
```

兼容性脚本在 mktemp 目录设置 XDG_DATA_HOME/XDG_CONFIG_HOME，直接编译生产 AppSettings、AccountReader、AppDataStore 等源文件，不使用 mock，不新增测试框架；SQLite 沿用生产依赖版本。合成账号仅使用 example.invalid，不读真实令牌、不控制进程、不写注册表。临时目录保留供排错。

实测输出：

```text
PASS fresh settings defaults
PASS legacy local preferences retained
PASS legacy service configuration discarded
PASS corrupt settings recover
PASS legacy SQLite accounts without region column
PASS current SQLite region retained
PASS legacy snapshot metadata and login name
PASS synthetic snapshot token and pointer format
PASS legacy snapshot file restore
PASS snapshot save round trip
Synthetic checks only; no Windows GUI, registry, process control or real login exercised.
```

## Windows 验收待完成（阻塞验收通过）

本次可通过 WSL 调用 Windows PowerShell，但宿主已有 Battle.net 与 BnetSwitch 数据，未提供独立测试用户/虚拟机。没有运行真实用户的启动轮询、注册表恢复或切号，也没有抓取真实账号流量。**新旧配置 Windows 启动、断网启动、运行时网络观测、主动在线查询、真实免密切换均未验证**；WSL 构建与合成数据通过不代表这些通过。

在隔离 Windows 用户或虚拟机中复现：

1. 安装 .NET 8 Desktop Runtime，运行 publish/local-only/BnetSwitch.exe。准备新配置、旧版配置（广告启用、旧私服地址、授权缓存）两组，旧设置仅使用合成数据；保留副本。
2. 从进程启动前开启按 PID 过滤的网络捕获（含 DNS、连接尝试，避免只看瞬时 TCP 列表漏掉短连接）。启动后等待至少 30 秒，浏览/搜索/备注/置顶、刷新本地列表、打开设置、最小化恢复。应无作者服务、战绩或图片请求；断网重复，窗口应正常响应。
3. 使用有权限的测试账号点击「刷新段位」或「查战绩」，确认只在点击后出现对应暴雪/网易与资源请求；记录正常结果。断网、超时、失败期间验证主窗口本地操作仍可响应、切号按钮可用。扫码授权窗口可取消。
4. 对照旧版读取同一组账号与快照。真实免密切换单独由账号所有者测试，不把真实令牌、账号或完整抓包提交到仓库/任务。
5. 记录构建 commit、Windows 版本、测试矩阵、进程名与脱敏域名统计、界面结果。网络验收与真实登录验收分别签收。

## 双轴审查

审查范围 `git diff 13fff09...84f35ef`，两个独立子代理审查。

- Standards：0 项硬性违规，1 项过时更新注释清理建议，已删除。未发现具体正确性缺陷。
- Spec：1 项验收缺口，即上述 Windows 实测未完成；未发现确定的实现偏差或范围扩张。段位查询不再清除切号 Busy 或覆盖其 StatusText。

未增加生产依赖；没有修改 LICENSE、账号读写服务或切号算法。待 Windows 验收后再决定合并；后续开发须基于本分支准确提交，不以陈旧 main 为新基线。
