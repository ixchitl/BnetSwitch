using BnetSwitch.Services;
using static BnetSwitch.Tests.SyntheticWorld;

namespace BnetSwitch.Tests;

/// <summary>
/// 快照文件还原(AppDataStore):切换核心 = 只换 %APPDATA%\Battle.net 文件、绝不碰注册表。
/// 这里跑真实文件 I/O(沙箱临时目录 + 合成内容),不 mock 任何恢复逻辑。
/// 令牌字节全部编造,与真实 DPAPI 数据无关。
/// </summary>
public static class SnapshotRestoreTests
{
    private const long IdA = 42;
    private const long IdB = 43;
    private const string SlotA = "4EB0C645";

    private static byte[] B(params int[] v) => v.Select(x => (byte)x).ToArray();

    public static void Run()
    {
        T.Test("快照: Save→改动 live→Restore 完整还原文件内容", () =>
        {
            var (p, store) = NewStore();
            WriteConfig(p, Login, region: "CN");
            File.WriteAllText(Path.Combine(p.RoamingDir, "extra.dat"), "before");
            store.Save(IdA, "Player#1234");

            // live 被「下一个号」覆盖
            WriteConfig(p, OtherLogin, region: "KR");
            File.WriteAllText(Path.Combine(p.RoamingDir, "extra.dat"), "after");

            store.Restore(IdA);
            var cfg = File.ReadAllText(p.RoamingConfig);
            T.True(cfg.Contains(Login) && !cfg.Contains(OtherLogin), "config 应还原为快照内容");
            T.Equal("before", File.ReadAllText(Path.Combine(p.RoamingDir, "extra.dat")), "附带文件应还原");
        });

        T.Test("快照: meta.json 记录 id/BattleTag/时间,老 meta 无 Expired 字段默认 false", () =>
        {
            var (p, store) = NewStore();
            WriteConfig(p, Login);
            store.Save(IdA, "Player#1234");

            var meta = store.ReadMeta(IdA);
            T.NotNull(meta, "meta 应可读");
            T.Equal(IdA, meta!.AccountId, "meta 账号 id");
            T.Equal("Player#1234", meta.BattleTag, "meta BattleTag");
            T.False(meta.Expired, "新存的快照不应带过期标记");
            T.True((DateTime.UtcNow - meta.SavedAtUtc).Duration() < TimeSpan.FromMinutes(1), "SavedAtUtc 应为当前时间");
            T.True(store.HasProfile(IdA), "HasProfile 应为 true");

            // 手工构造老版本 meta(没有 Expired 字段),验证向后兼容
            var legacyDir = Path.Combine(store.Root, IdB.ToString());
            Directory.CreateDirectory(legacyDir);
            File.WriteAllText(Path.Combine(legacyDir, "meta.json"),
                """{"AccountId":43,"BattleTag":"Smurf#5678","SavedAtUtc":"2026-01-01T00:00:00Z"}""");
            var legacy = store.ReadMeta(IdB);
            T.NotNull(legacy, "老 meta 应可读");
            T.False(legacy!.Expired, "老 meta 无 Expired 字段应默认 false");
            T.Equal("Smurf#5678", legacy.BattleTag, "老 meta BattleTag");
        });

        T.Test("快照: 重复 Save 替换旧快照,不残留已删文件", () =>
        {
            var (p, store) = NewStore();
            File.WriteAllText(Path.Combine(p.RoamingDir, "old.dat"), "1");
            store.Save(IdA, "Player#1234");
            File.Delete(Path.Combine(p.RoamingDir, "old.dat"));
            File.WriteAllText(Path.Combine(p.RoamingDir, "new.dat"), "2");
            store.Save(IdA, "Player#1234");

            var dataDir = Path.Combine(store.Root, IdA.ToString(), "BattleNet");
            T.False(File.Exists(Path.Combine(dataDir, "old.dat")), "旧文件不应残留在快照里");
            T.True(File.Exists(Path.Combine(dataDir, "new.dat")), "新文件应入快照");
        });

        T.Test("快照: 目标快照缺失时 Restore 抛 DirectoryNotFoundException", () =>
        {
            var (p, store) = NewStore();
            T.Throws<DirectoryNotFoundException>(() => store.Restore(999), "还原不存在的快照");
        });

        T.Test("快照: RoamingDir 不存在时 Restore 自动创建再还原", () =>
        {
            var (p, store) = NewStore();
            WriteConfig(p, Login);
            store.Save(IdA, "Player#1234");
            Directory.Delete(p.RoamingDir, recursive: true);

            store.Restore(IdA);
            T.True(File.Exists(p.RoamingConfig), "还原后 live config 应存在");
        });

        T.Test("快照: 复制失败向上抛,不留下静默半成品(Restore 无吞异常路径)", () =>
        {
            // 把快照文件 chmod 成不可读,模拟磁盘/权限故障:ForceCopy 必须把异常抛给调用方,
            // 让切号流程走事务回滚,而不是当作成功。
            var (p, store) = NewStore();
            WriteConfig(p, Login);
            store.Save(IdA, "Player#1234");
            var snapCfg = Path.Combine(store.Root, IdA.ToString(), "BattleNet", "Battle.net.config");
            File.SetUnixFileMode(snapCfg, UnixFileMode.None);
            try
            {
                T.Throws<UnauthorizedAccessException>(() => store.Restore(IdA), "不可读快照文件还原");
            }
            finally
            {
                File.SetUnixFileMode(snapCfg, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        });

        T.Test("快照: 只读 live 文件也能被覆盖(还原前清只读属性)", () =>
        {
            var (p, store) = NewStore();
            WriteConfig(p, Login);
            store.Save(IdA, "Player#1234");
            WriteConfig(p, OtherLogin);
            File.SetAttributes(p.RoamingConfig, FileAttributes.ReadOnly);
            try
            {
                store.Restore(IdA);
                T.True(File.ReadAllText(p.RoamingConfig).Contains(Login), "只读文件应被成功覆盖");
            }
            finally
            {
                File.SetAttributes(p.RoamingConfig, FileAttributes.Normal);
            }
        });

        T.Test("快照: 令牌槽存取回环,槽名大小写不敏感", () =>
        {
            var (p, store) = NewStore();
            store.SaveTokens(IdA, new Dictionary<string, byte[]> { [SlotA] = B(1, 2, 3) });
            var tokens = store.ReadTokens(IdA);
            T.Equal(1, tokens.Count, "令牌槽数");
            T.EqualBytes(B(1, 2, 3), tokens[SlotA.ToLowerInvariant()], "小写槽名也应读到(OrdinalIgnoreCase)");
        });

        T.Test("快照: 空令牌字典不落盘,保留旧值", () =>
        {
            var (p, store) = NewStore();
            store.SaveTokens(IdA, new Dictionary<string, byte[]> { [SlotA] = B(1) });
            store.SaveTokens(IdA, new Dictionary<string, byte[]>());
            T.Equal(1, store.ReadTokens(IdA).Count, "空存不应抹掉旧令牌快照");
        });

        T.Test("快照: 损坏的 uauth.json 退化为空字典,不抛异常", () =>
        {
            var (p, store) = NewStore();
            Directory.CreateDirectory(Path.Combine(store.Root, IdA.ToString()));
            File.WriteAllText(Path.Combine(store.Root, IdA.ToString(), "uauth.json"), "{broken");
            T.Equal(0, store.ReadTokens(IdA).Count, "损坏令牌快照应读出空字典");
        });

        T.Test("快照: FindNewestToken 跨账号取最新一份,坏文件跳过", () =>
        {
            var (p, store) = NewStore();
            // 旧快照存旧值,新快照存新值 —— 写回应取【最新】那份(目标号自己的可能是过期的)
            store.SaveTokens(IdA, new Dictionary<string, byte[]> { [SlotA] = B(1) });
            var fileA = Path.Combine(store.Root, IdA.ToString(), "uauth.json");
            File.SetLastWriteTimeUtc(fileA, DateTime.UtcNow.AddHours(-2));
            store.SaveTokens(IdB, new Dictionary<string, byte[]> { [SlotA] = B(9) });

            // 再造一个更新的损坏文件:应被跳过而不是让整次查找失败
            var dirC = Path.Combine(store.Root, "44");
            Directory.CreateDirectory(dirC);
            File.WriteAllText(Path.Combine(dirC, "uauth.json"), "{broken");

            T.EqualBytes(B(9), store.FindNewestToken(SlotA), "应取最新的有效令牌");
            T.Null(store.FindNewestToken("FFFFFFFF"), "没有该槽应返回 null");
        });

        T.Test("快照: 学到的槽名存取回环,空白槽名不落盘", () =>
        {
            var (p, store) = NewStore();
            store.SaveOwnSlot(IdA, $" {SlotA} ");
            T.Equal(SlotA, store.ReadOwnSlot(IdA), "槽名应 trim 后回读");
            T.Null(store.ReadOwnSlot(IdB), "没存过的账号应为 null");

            var before = store.ReadOwnSlot(IdA);
            store.SaveOwnSlot(IdA, "   ");
            T.Equal(before, store.ReadOwnSlot(IdA), "空白槽名不应覆盖已学到的值");
        });

        T.Test("快照: 活跃指针存取回环,空白指针不落盘", () =>
        {
            var (p, store) = NewStore();
            store.SavePointer(IdA, Pointer(IdA));
            T.Equal(Pointer(IdA), store.ReadPointer(IdA), "指针 JSON 应原样回读");
            T.Null(store.ReadPointer(IdB), "没存过的账号应为 null");
            store.SavePointer(IdB, "  ");
            T.Null(store.ReadPointer(IdB), "空白指针不应写文件");
        });

        T.Test("快照: 登录名提取取 SavedAccountNames 第一项", () =>
        {
            var (p, store) = NewStore();
            WriteConfig(p, $"{Login},{OtherLogin}");
            store.Save(IdA, "Player#1234");
            T.Equal(Login, store.ReadLoginName(IdA), "快照内登录名");
            T.Equal(Login, store.ReadLiveLoginName(), "live 登录名");
        });

        T.Test("快照: config 缺失/损坏/空名单时登录名读数为 null", () =>
        {
            var (p, store) = NewStore();
            T.Null(store.ReadLiveLoginName(), "缺 config 应为 null");
            File.WriteAllText(p.RoamingConfig, "{broken");
            T.Null(store.ReadLiveLoginName(), "损坏 config 应为 null");
            WriteConfig(p, "");
            T.Null(store.ReadLiveLoginName(), "空名单应为 null");
            WriteConfig(p, " , ");
            T.Null(store.ReadLiveLoginName(), "名单全是空白项应为 null");
        });

        T.Test("快照: ClearCurrentPointer 只清登录名单,文件缺失不抛", () =>
        {
            var (p, store) = NewStore();
            store.ClearCurrentPointer();   // 没有 config 也不能炸

            WriteConfig(p, $"{Login},{OtherLogin}", region: "CN");
            store.ClearCurrentPointer();
            var cfg = File.ReadAllText(p.RoamingConfig);
            T.True(cfg.Contains("\"SavedAccountNames\": \"\"") || cfg.Contains("\"SavedAccountNames\":\"\""),
                $"登录名单应被清空: {cfg}");
            T.True(cfg.Contains("LastLoginRegion"), "其他字段不应被波及");
            T.Null(store.ReadLiveLoginName(), "清空后登录名应读为 null");
        });

        T.Test("快照: 过期标记可来回设置,无 meta 时不抛", () =>
        {
            var (p, store) = NewStore();
            WriteConfig(p, Login);
            store.Save(IdA, "Player#1234");
            store.SetExpired(IdA, true);
            T.True(store.ReadMeta(IdA)!.Expired, "应标记过期");
            store.SetExpired(IdA, false);
            T.False(store.ReadMeta(IdA)!.Expired, "应可取消过期");
            store.SetExpired(999, true);   // 没有 meta 的账号: no-op 不抛
            T.False(store.HasProfile(999), "不应凭空创建 meta");
        });

        T.Test("快照: Delete 移除整个快照目录,不影响其他账号", () =>
        {
            var (p, store) = NewStore();
            WriteConfig(p, Login);
            store.Save(IdA, "Player#1234");
            store.Save(IdB, "Smurf#5678");
            store.Delete(IdA);
            T.False(store.HasProfile(IdA), "被删账号不应再有 meta");
            T.True(store.HasProfile(IdB), "其他账号不受影响");
            store.Delete(999);   // 删不存在的账号: no-op 不抛
        });

        T.Test("快照: Restore 只换文件,live 多余文件保留(与事务回滚语义不同,现行为记录)", () =>
        {
            // AppDataStore.Restore 覆盖同名文件但不清理快照里没有的多余文件;
            // 「清残留」由 SwitchJournal.Rollback 负责(见事务用例)。钉住这个分工边界。
            var (p, store) = NewStore();
            WriteConfig(p, Login);
            store.Save(IdA, "Player#1234");
            File.WriteAllText(Path.Combine(p.RoamingDir, "stray.dat"), "x");
            store.Restore(IdA);
            T.True(File.Exists(Path.Combine(p.RoamingDir, "stray.dat")), "Restore 不应删除快照外文件");
        });

        T.Test("快照: Save 时 live 目录缺失只存 meta,空快照还原不动 live(现行为记录)", () =>
        {
            var (p, store) = NewStore();
            Directory.Delete(p.RoamingDir, recursive: true);
            store.Save(IdA, "Player#1234");   // 没有 live 文件也应能存(只写 meta)
            T.True(store.HasProfile(IdA), "meta 应写入");
            Directory.CreateDirectory(p.RoamingDir);
            File.WriteAllText(p.RoamingConfig, "dirty");
            store.Restore(IdA);
            T.Equal("dirty", File.ReadAllText(p.RoamingConfig),
                "空快照还原只复制快照内文件,不动 live 残留(清残留是事务回滚的职责)");
        });
    }

    private static (BattleNetPaths Paths, AppDataStore Store) NewStore()
    {
        var p = NewPaths();
        return (p, new AppDataStore(p));
    }
}
