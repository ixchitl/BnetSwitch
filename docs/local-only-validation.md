# 本地版网络审计与验证（DIEM-138）

基线 `13fff09`。本 fork 保留 qiyh99 署名与 GPLv3，不更改账号、快照、令牌及切换算法。

## 网络入口审计

这是源码调用链审计；运行时零联网与按需联网的实测证据见「Windows 实机验证」。

| 入口 | 触发条件与处理 |
| --- | --- |
| MainWindow.Loaded | 仅日志、本地刷新、启动本地账号轮询、托盘；已删除 ping、更新检查、授权复核、广告及图片预加载 |
| Analytics / LicenseService / UpdateService | 源文件和窗口已删除，不保留服务器配置或开关 |
| AppSettings.Load / Save | 旧广告、激活、私服地址及旧反馈地址均为未知 JSON 字段，被忽略并在保存时丢弃；保留本地设置、备注、置顶与隐藏列表 |
| 账号浏览、搜索、2 秒轮询 | 读取本地 SQLite、快照和 ranks.json；账号段位图标先检查本地文件存在，不回源下载 |
| 刷新段位 | 只由 OnRefreshRanks 点击发起；国服 DashenAuth → RankFetcher → StatsService，国际服 BlizzardCareerClient；查询状态与切号 Busy 分离，不因网络等待覆盖本地操作进度 |
| 国服战绩 | OnOpenStats → StatsWindow.ShowFor → 校验缓存大神会话/主动扫码 → StatsService；会话存在本身不会触发启动联网 |
| 国际服战绩 | OnOpenStats → CareerWindow.ShowFor → CareerService → BlizzardCareerClient；仅查询内使用 10 分钟 HTML 缓存与失败回退 |
| 扫码轮询 | 仅主动打开 QrLoginDialog 后每 2 秒查询扫码状态，120 秒过期，关闭窗口停止计时器。Windows 实测：关闭窗口后国服号按未授权跳过，流程继续且应用回到零连接 |
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

## Windows 实机验证（2026-09-21，真实数据）

经数据所有者授权，直接在其日常 Windows 主机与真实账号数据上完成。环境：Windows 11 22631、.NET Desktop Runtime 8.0.10、系统级本地代理（FlClash，127.0.0.1:7890，全部 HTTP 流量经它转发）、BnetSwitch 数据目录含 7 个真实账号快照（5 国服 + 2 国际服，均由商业版 2.4.1 写入）、旧版 settings.json 含全部商业字段。被测构建 `2.1.6+3b4190f`（Release win-x64，安装于 D:\BnetSwitch-Local）。测试前完整备份数据目录、Battle.net 漫游/本地关键文件与 HKCU UnifiedAuth 注册表键（备份仅存本机，不入库）。

### 验证矩阵与结果

| 项 | 方法 | 结果 |
| --- | --- | --- |
| 二进制不含商业服务 | 对产物 dll 做 UTF-8/UTF-16 字符串扫描 | 无作者私服（api.qiyonghan.icu）、广告联盟、激活/云同步/遥测字段；仅剩暴雪/网易按需战绩端点与 ixchitl 反馈地址；qiyh99 仅存于「联系」窗口的版权署名文案（GPLv3 要求保留） |
| 2.4.x 旧配置迁移 | 真实 settings.json（含 SplashAd/BottomAd/CloudToken/TelemetryEnabled/LicenseCode/SponsorUrl 等）首启 | 全部商业字段被丢弃；窗口尺寸、托盘、置顶、备注、隐藏列表等本地偏好原样保留；账号/快照/令牌槽格式与 2.1.6 读取器完全兼容 |
| 启动+闲置零联网 | 按 PID（含子进程）300ms 轮询全部 TCP 连接（含环回代理口）75 秒 + DNS 缓存前后差 | 应用 0 条连接；同期代理进程自身的 5 条背景连接被采到，证明采集有效；DNS 新增仅系统级 CRL 检查，无任何应用域名 |
| 界面加载真实数据 | UIAutomation 遍历 | 7 个账号卡全部渲染，0 个广告类元素，窗口全程响应 |
| 主动查询才联网 | `--careerprobe` 真实战网 tag + 同法监控 | 点击前应用 0 连接；触发后约 0.5 秒出现 app→127.0.0.1:7890 及代理外联；国服账号在国际服接口返回 404，被正确解释为「查不到」，不阻塞、不弹窗 |
| 刷新段位（国服未授权） | UIAutomation 真实鼠标点击「刷新段位」 | 国服号无大神会话时弹出扫码授权窗（主动授权设计）；关窗后 5 个国服号按未授权跳过、2 个国际服号走 s.166.net 映射 + overwatch.blizzard.com 查询（真实账号无 OW 生涯，返回未定级）；流程结束后按钮恢复可用、ranks.json 无脏数据、应用回到零连接 |
| 查询不阻塞切号 | 段位查询进行中（按钮禁用态）点击账号卡 | 切换立即执行并登录成功：查询等待不占用切号忙碌锁，实测确认 |
| 真实免密切换 A→B→A | 同区服（国服↔国服）往返，UIAutomation 真实点击 | 两程 switch.log 完整：优雅关闭战网→离号令牌重存→文件还原→活跃指针写回→令牌槽核对（无需写回，槽已属目标号）→同区服跳过游戏文件→启动战网→「登录核对: 成功」；CachedData 指针与注册表槽哈希前后一致，机器回到测试前账号 |

### 观测方法说明

网络证据 = 按 PID 的 TCP 轮询（300ms，含环回，覆盖子进程）+ DNS 客户端缓存差值 + 系统代理进程（FlClash）出向连接时间对齐。本机开启系统代理，应用流量均经 127.0.0.1:7890 转发，故「应用零外联」以应用进程零连接（含代理口）为准，代理出向仅用于归属佐证。UI 触发采用 UIAutomation 定位 + Win32 真实鼠标点击，带前台窗口/坐标归属三重校验与用户空闲等待，防止误点其他窗口。

### 未验证项（如实记录）

- **断网启动**：数据所有者明确要求测试期间不人为断网，未执行。启动路径全部为本地 IO（源码审计 + 闲置零联网实测佐证），HTTP 仅存在于主动查询且有超时。
- **全新配置冷启动（Windows）**：未搬动真实数据目录做无配置首启；该场景由合成兼容性脚本覆盖（PASS fresh settings defaults / corrupt settings recover）。
- **国服大神扫码登录后的战绩全链路**：需人工扫码，未执行；扫码窗口的弹出/关闭/跳过后路径已实测。
- **国际服战绩阳性显示**：真实账号均无公开 OW 生涯（暴雪 404），查询链路、错误处理与不阻塞已实测，阳性渲染未覆盖。

## 双轴审查

审查范围 `git diff 13fff09...84f35ef`，两个独立子代理审查。

- Standards：0 项硬性违规，1 项过时更新注释清理建议，已删除。未发现具体正确性缺陷。
- Spec：1 项验收缺口，即当时的 Windows 实测未完成；未发现确定的实现偏差或范围扩张。段位查询不再清除切号 Busy 或覆盖其 StatusText。该缺口已由上文「Windows 实机验证」补齐（断网启动一项经数据所有者同意不执行）。

未增加生产依赖；没有修改 LICENSE、账号读写服务或切号算法。Windows 实机验证通过后，feat/local-only 已合并回 main 并推送 origin；后续开发以合并后的 main 为基线。
