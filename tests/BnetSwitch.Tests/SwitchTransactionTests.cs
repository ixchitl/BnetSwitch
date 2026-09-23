using BnetSwitch.Services;
using static BnetSwitch.Tests.SyntheticWorld;

namespace BnetSwitch.Tests;

/// <summary>
/// 事务失败恢复(SwitchJournal + 活跃指针写回):2026-08-14 事故的兜底 ——
/// 动 %APPDATA%\Battle.net 之前先拍现场,中途出错原样放回,程序被杀下次启动也能收拾。
/// 真实文件 I/O + 沙箱内合成 SQLite,不 mock 回滚逻辑。
/// </summary>
public static class SwitchTransactionTests
{
    private const long TargetId = 43;

    public static void Run()
    {
        T.Test("事务: 初始状态无挂起,Rollback 返回 false 且不动回调", () =>
        {
            var (p, journal) = NewJournal();
            T.False(journal.HasPending, "全新沙箱不应有挂起事务");
            T.Null(journal.Read(), "无事务时 Read 应为 null");
            var called = false;
            T.False(journal.Rollback(p.RoamingDir, _ => called = true), "无现场可回滚");
            T.False(called, "指针回调不应被调用");
        });

        T.Test("事务: Begin 拍下完整现场(文件副本 + 事务元数据)", () =>
        {
            var (p, journal) = NewJournal();
            SeedLive(p, config: Login, other: "v1");
            journal.Begin("switch", TargetId, "Smurf#5678", p.RoamingDir, Pointer(42));

            T.True(journal.HasPending, "Begin 后应有挂起事务");
            var txn = journal.Read();
            T.NotNull(txn, "事务元数据应可读");
            T.Equal("switch", txn!.Op, "操作类型");
            T.Equal(TargetId, txn.TargetId, "目标账号 id");
            T.Equal("Smurf#5678", txn.TargetTag, "目标 Tag");
            T.Equal(Pointer(42), txn.PointerBefore, "切换前活跃指针");

            var before = Path.Combine(TxnRoot(), "before");
            T.Equal("v1", File.ReadAllText(Path.Combine(before, "other.dat")), "现场应含 live 文件副本");
            T.True(File.ReadAllText(Path.Combine(before, "Battle.net.config")).Contains(Login),
                "现场应含 config 副本");
        });

        T.Test("事务: 切换半途失败后 Rollback 完整还原现场", () =>
        {
            var (p, journal) = NewJournal();
            SeedLive(p, config: Login, other: "v1");
            journal.Begin("switch", TargetId, "Smurf#5678", p.RoamingDir, Pointer(42));

            // 模拟半途而废的切换: config 被换掉、附带文件被删、多了不该有的新文件
            WriteConfig(p, OtherLogin, region: "KR");
            File.Delete(Path.Combine(p.RoamingDir, "other.dat"));
            File.WriteAllText(Path.Combine(p.RoamingDir, "stray.dat"), "half-switched");

            string? rolledPointer = null;
            var rolled = journal.Rollback(p.RoamingDir, json => rolledPointer = json);

            T.True(rolled, "有现场时应回滚成功");
            T.True(File.ReadAllText(p.RoamingConfig).Contains(Login), "config 应还原");
            T.Equal("v1", File.ReadAllText(Path.Combine(p.RoamingDir, "other.dat")), "被删文件应找回");
            T.False(File.Exists(Path.Combine(p.RoamingDir, "stray.dat")), "切换中新增的残留文件应被清掉");
            T.Equal(Pointer(42), rolledPointer, "活跃指针应按 Before 值写回");
            T.False(journal.HasPending, "回滚后现场应清理");
            T.False(journal.Rollback(p.RoamingDir, _ => { }), "重复回滚应返回 false");
        });

        T.Test("事务: 回滚可覆盖只读 live 文件", () =>
        {
            var (p, journal) = NewJournal();
            SeedLive(p, config: Login, other: "v1");
            journal.Begin("switch", TargetId, "Smurf#5678", p.RoamingDir, null);
            WriteConfig(p, OtherLogin);
            File.SetAttributes(p.RoamingConfig, FileAttributes.ReadOnly);
            try
            {
                T.True(journal.Rollback(p.RoamingDir, null), "只读文件不应挡住回滚");
                T.True(File.ReadAllText(p.RoamingConfig).Contains(Login), "config 应还原");
            }
            finally
            {
                File.SetAttributes(p.RoamingConfig, FileAttributes.Normal);
            }
        });

        T.Test("事务: 切换前没有指针时回滚不调指针回调", () =>
        {
            var (p, journal) = NewJournal();
            SeedLive(p, config: Login, other: "v1");
            journal.Begin("switch", TargetId, "Smurf#5678", p.RoamingDir, pointerBefore: null);
            var called = false;
            T.True(journal.Rollback(p.RoamingDir, _ => called = true), "回滚应成功");
            T.False(called, "PointerBefore 为空不应触发写回");
        });

        T.Test("事务: journal.json 损坏仍回滚文件,指针写回安全跳过", () =>
        {
            var (p, journal) = NewJournal();
            SeedLive(p, config: Login, other: "v1");
            journal.Begin("switch", TargetId, "Smurf#5678", p.RoamingDir, Pointer(42));
            File.WriteAllText(Path.Combine(TxnRoot(), "journal.json"), "{broken");

            T.Null(journal.Read(), "损坏元数据应读为 null");
            var called = false;
            T.True(journal.Rollback(p.RoamingDir, _ => called = true), "文件现场仍在,应回滚成功");
            T.True(File.ReadAllText(p.RoamingConfig).Contains(Login), "config 应还原");
            T.False(called, "读不出 PointerBefore 时不应写指针");
        });

        T.Test("事务: 现场不完整(缺 before/)时不盲目回滚", () =>
        {
            var (p, journal) = NewJournal();
            SeedLive(p, config: Login, other: "v1");
            journal.Begin("switch", TargetId, "Smurf#5678", p.RoamingDir, Pointer(42));
            Directory.Delete(Path.Combine(TxnRoot(), "before"), recursive: true);

            T.False(journal.HasPending, "缺文件副本不算有完整现场");
            WriteConfig(p, OtherLogin);
            T.False(journal.Rollback(p.RoamingDir, _ => throw new Exception("不应触发")), "不应回滚");
            T.True(File.ReadAllText(p.RoamingConfig).Contains(OtherLogin), "live 不应被动过");
        });

        T.Test("事务: Commit 丢弃现场且不动 live 文件", () =>
        {
            var (p, journal) = NewJournal();
            SeedLive(p, config: Login, other: "v1");
            journal.Begin("switch", TargetId, "Smurf#5678", p.RoamingDir, Pointer(42));
            WriteConfig(p, OtherLogin);   // 切换成功的样子
            journal.Commit();

            T.False(journal.HasPending, "Commit 后应无挂起事务");
            T.False(journal.Rollback(p.RoamingDir, _ => { }), "Commit 后不应可回滚");
            T.True(File.ReadAllText(p.RoamingConfig).Contains(OtherLogin), "成功切换的结果应保留");
        });

        T.Test("事务: 再次 Begin 用最新现场整体替换旧现场", () =>
        {
            var (p, journal) = NewJournal();
            SeedLive(p, config: Login, other: "v1");
            journal.Begin("switch", TargetId, "Smurf#5678", p.RoamingDir, Pointer(42));

            WriteConfig(p, OtherLogin);   // live 又变了,第二次事务应拍新状态
            journal.Begin("relogin", 44, "Third#9999", p.RoamingDir, Pointer(43));

            var txn = journal.Read();
            T.Equal("relogin", txn!.Op, "应以最新事务为准");
            T.Equal(44L, txn.TargetId, "最新目标 id");
            var before = Path.Combine(TxnRoot(), "before");
            T.True(File.ReadAllText(Path.Combine(before, "Battle.net.config")).Contains(OtherLogin),
                "现场副本应是最新 live 状态");
        });

        T.Test("事务: Begin 时 live 目录不存在,回滚会重建目录并写回指针", () =>
        {
            var (p, journal) = NewJournal();
            Directory.Delete(p.RoamingDir, recursive: true);
            journal.Begin("addaccount", TargetId, "Smurf#5678", p.RoamingDir, Pointer(42));
            T.True(journal.HasPending, "空 live 也应建立现场");

            string? rolledPointer = null;
            T.True(journal.Rollback(p.RoamingDir, json => rolledPointer = json), "应回滚成功");
            T.True(Directory.Exists(p.RoamingDir), "回滚应重建 live 目录");
            T.Equal(Pointer(42), rolledPointer, "指针应写回");
        });

        // ===== 活跃指针写回(事务恢复的另一半: CachedData.db 在 %LOCALAPPDATA%,Restore 盖不到) =====

        T.Test("指针写回: 更新已有键并回读一致", () =>
        {
            var p = NewPaths();
            BuildDb(p, new[] { new CacheRow(FnvName(Login), CnEnv, "Player#1234", 42) },
                pointerJson: Pointer(42));
            var reader = new AccountReader(p);
            T.True(reader.WriteActivePointer(Pointer(43, "KR")), "写回应成功");
            T.Equal(Pointer(43, "KR"), reader.ReadActivePointerJson(), "回读应是新值");
        });

        T.Test("指针写回: 键不存在时插入", () =>
        {
            var p = NewPaths();
            BuildDb(p, new[] { new CacheRow(FnvName(Login), CnEnv, "Player#1234", 42) }, pointerJson: null);
            var reader = new AccountReader(p);
            T.True(reader.WriteActivePointer(Pointer(42)), "缺键应走插入路径");
            T.Equal(Pointer(42), reader.ReadActivePointerJson(), "回读应是插入值");
        });

        T.Test("指针写回: 库缺失或 JSON 空白一律拒绝", () =>
        {
            var p = NewPaths();
            var reader = new AccountReader(p);
            T.False(reader.WriteActivePointer(Pointer(42)), "库不存在应返回 false");
            T.Null(reader.ReadActivePointerJson(), "库不存在读数应为 null");

            BuildDb(p, Array.Empty<CacheRow>(), pointerJson: Pointer(42));
            T.False(reader.WriteActivePointer("  "), "空白 JSON 应拒绝写库");
            T.Equal(Pointer(42), reader.ReadActivePointerJson(), "拒绝后原值应保持不变");
        });
    }

    private static (BattleNetPaths Paths, SwitchJournal Journal) NewJournal()
    {
        var p = NewPaths();
        return (p, new SwitchJournal());
    }

    private static void SeedLive(BattleNetPaths p, string config, string other)
    {
        WriteConfig(p, config, region: "CN");
        File.WriteAllText(Path.Combine(p.RoamingDir, "other.dat"), other);
    }

    /// <summary>%LOCALAPPDATA%\BnetSwitch\txn —— 沙箱已把 LocalApplicationData 重定向进临时目录。</summary>
    private static string TxnRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BnetSwitch", "txn");
}
