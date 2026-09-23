using System.Text;
using BnetSwitch.Services;
using Microsoft.Data.Sqlite;
using static BnetSwitch.Tests.SyntheticWorld;

namespace BnetSwitch.Tests;

/// <summary>
/// 配置账号/区服解析(AccountReader):切号前「当前登录的是谁」判定错误会把状态灌进
/// 错误号的快照(快照污染,切换后掉登录页),解析规则必须钉死。
/// 全部走真实产品逻辑 + 沙箱里的合成 SQLite / 合成 Battle.net.config。
/// </summary>
public static class AccountResolveTests
{
    public static void Run()
    {
        // ===== 合成数据生成器的独立性:公开 FNV-1a-64 测试向量 =====

        T.Test("解析: FNV 生成器对公开测试向量(保证合成 name 列可信)", () =>
        {
            // 期望值来自 Landon Curt Noll 公布的 FNV-1a-64 标准向量,独立于产品实现
            T.Equal("cbf29ce484222325", FnvCore(""), "空串向量");
            T.Equal("af63dc4c8601ec8c", FnvCore("a"), "'a' 向量");
            T.Equal("85944171f73967e8", FnvCore("foobar"), "'foobar' 向量");
        });

        // ===== ResolveCurrentAccountFromConfig:live config → 唯一账号 =====

        T.Test("解析: 唯一登录名解析出账号 id(端到端核对产品 FNV 口径)", () =>
        {
            var p = NewPaths();
            WriteConfig(p, Login, region: null);
            BuildDb(p, new[] { new CacheRow(FnvName(Login), UsEnv, "Player#1234", 42) });
            T.Equal(42L, new AccountReader(p).ResolveCurrentAccountFromConfig(), "应解析出唯一候选");
        });

        T.Test("解析: 同邮箱国服+亚服两张卡,LastLoginRegion=CN 判给国服卡", () =>
        {
            var p = NewPaths();
            WriteConfig(p, Login, region: "CN");
            BuildDb(p, new[]
            {
                new CacheRow(FnvName(Login), CnEnv, "Player#1234", 42, "CN"),
                new CacheRow(FnvName(Login), UsEnv, "Player#1234", 43, "EU,KR,US"),
            });
            T.Equal(42L, new AccountReader(p).ResolveCurrentAccountFromConfig(), "CN 区服应判给国服卡");
        });

        T.Test("解析: 同邮箱国服+亚服两张卡,LastLoginRegion=KR 判给国际卡", () =>
        {
            var p = NewPaths();
            WriteConfig(p, Login, region: "KR");
            BuildDb(p, new[]
            {
                new CacheRow(FnvName(Login), CnEnv, "Player#1234", 42, "CN"),
                new CacheRow(FnvName(Login), UsEnv, "Player#1234", 43, "EU,KR,US"),
            });
            T.Equal(43L, new AccountReader(p).ResolveCurrentAccountFromConfig(), "KR 区服应判给国际卡");
        });

        T.Test("解析: LastLoginRegion 缺失时按国际服处理(现行为记录)", () =>
        {
            // region=null → liveIsCn=false → 只剩国际卡候选。国服用户 config 若缺该节点会判给国际卡;
            // 现状即如此(缺区服信息时国际卡是唯一非 CN 候选),钉住,阶段4如调整需同步改。
            var p = NewPaths();
            WriteConfig(p, Login, region: null);
            BuildDb(p, new[]
            {
                new CacheRow(FnvName(Login), CnEnv, "Player#1234", 42, "CN"),
                new CacheRow(FnvName(Login), UsEnv, "Player#1234", 43, "EU,KR,US"),
            });
            T.Equal(43L, new AccountReader(p).ResolveCurrentAccountFromConfig(), "缺区服应判给国际卡");
        });

        T.Test("解析: 同登录名两张都是国际卡时判不出,返回 null", () =>
        {
            var p = NewPaths();
            WriteConfig(p, Login, region: "KR");
            BuildDb(p, new[]
            {
                new CacheRow(FnvName(Login), UsEnv, "Player#1234", 43, "EU,KR,US"),
                new CacheRow(FnvName(Login), KrEnv, "Player#1234", 44, "EU,KR,US"),
            });
            T.Null(new AccountReader(p).ResolveCurrentAccountFromConfig(), "两张同域卡应判不出唯一账号");
        });

        T.Test("解析: login_cache 里没有该登录名返回 null", () =>
        {
            var p = NewPaths();
            WriteConfig(p, Login, region: "CN");
            BuildDb(p, new[] { new CacheRow(FnvName(OtherLogin), UsEnv, "Smurf#5678", 99) });
            T.Null(new AccountReader(p).ResolveCurrentAccountFromConfig(), "无匹配候选应返回 null");
        });

        // ===== 缺失/损坏 config =====

        T.Test("解析: config 文件缺失返回 null", () =>
        {
            var p = NewPaths();
            BuildDb(p, new[] { new CacheRow(FnvName(Login), UsEnv, "Player#1234", 42) });
            T.Null(new AccountReader(p).ResolveCurrentAccountFromConfig(), "缺 config 应返回 null");
        });

        T.Test("解析: config 损坏 JSON 返回 null 不抛异常", () =>
        {
            var p = NewPaths();
            File.WriteAllText(p.RoamingConfig, "{broken");
            BuildDb(p, new[] { new CacheRow(FnvName(Login), UsEnv, "Player#1234", 42) });
            T.Null(new AccountReader(p).ResolveCurrentAccountFromConfig(), "损坏 config 应返回 null");
        });

        T.Test("解析: config 没有 Client 节点返回 null", () =>
        {
            var p = NewPaths();
            File.WriteAllText(p.RoamingConfig, "{\"Login\":{\"LastLoginRegion\":\"CN\"}}");
            BuildDb(p, new[] { new CacheRow(FnvName(Login), UsEnv, "Player#1234", 42) });
            T.Null(new AccountReader(p).ResolveCurrentAccountFromConfig(), "缺 Client 节点应返回 null");
        });

        T.Test("解析: SavedAccountNames 为空串返回 null(登录页状态)", () =>
        {
            var p = NewPaths();
            WriteConfig(p, "", region: "CN");
            BuildDb(p, new[] { new CacheRow(FnvName(Login), UsEnv, "Player#1234", 42) });
            T.Null(new AccountReader(p).ResolveCurrentAccountFromConfig(), "空登录名应返回 null");
        });

        T.Test("解析: SavedAccountNames 多名取第一个", () =>
        {
            var p = NewPaths();
            WriteConfig(p, $"{Login},{OtherLogin}", region: "CN");
            BuildDb(p, new[]
            {
                new CacheRow(FnvName(Login), CnEnv, "Player#1234", 42, "CN"),
                new CacheRow(FnvName(OtherLogin), CnEnv, "Smurf#5678", 43, "CN"),
            });
            T.Equal(42L, new AccountReader(p).ResolveCurrentAccountFromConfig(), "逗号分隔应取第一项");
        });

        T.Test("解析: SavedAccountNames 用分号/空格分隔同样取第一个", () =>
        {
            var p = NewPaths();
            WriteConfig(p, $"{OtherLogin} {Login}", region: "CN");
            BuildDb(p, new[]
            {
                new CacheRow(FnvName(Login), CnEnv, "Player#1234", 42, "CN"),
                new CacheRow(FnvName(OtherLogin), CnEnv, "Smurf#5678", 43, "CN"),
            });
            T.Equal(43L, new AccountReader(p).ResolveCurrentAccountFromConfig(), "空格分隔应取第一项(Smurf)");
        });

        T.Test("解析: CachedData.db 缺失时即便 config 有效也返回 null", () =>
        {
            var p = NewPaths();
            WriteConfig(p, Login, region: "CN");
            T.Null(new AccountReader(p).ResolveCurrentAccountFromConfig(), "缺库应返回 null");
        });

        // ===== ReadAccounts / ReadActiveAccountId:合成库读取 =====

        T.Test("读库: 账号字段与活跃指针完整读回", () =>
        {
            var p = NewPaths();
            BuildDb(p, new[]
            {
                new CacheRow(FnvName(Login), CnEnv, "Player#1234", 42, "CN"),
                new CacheRow(FnvName(OtherLogin), UsEnv, "Smurf#5678", 43, "EU,KR,US"),
            }, pointerJson: Pointer(43));
            var accounts = new AccountReader(p).ReadAccounts(out var active);
            T.Equal(2, accounts.Count, "账号数");
            T.Equal(42L, accounts[0].AccountId, "第一个账号 id");
            T.Equal("Player#1234", accounts[0].BattleTag, "第一个账号 Tag");
            T.Equal(CnEnv, accounts[0].Environment, "第一个账号环境");
            T.Equal("CN", accounts[0].ConnectedEnvironments, "第一个账号真实归属");
            T.Equal(43L, active ?? -1, "活跃指针 id");
        });

        T.Test("读库: 老库没有 connected_environments 列仍可读,归属为空串", () =>
        {
            var p = NewPaths();
            BuildDb(p, new[] { new CacheRow(FnvName(Login), UsEnv, "Player#1234", 42, null) },
                withConnectedColumn: false);
            var accounts = new AccountReader(p).ReadAccounts(out _);
            T.Equal(1, accounts.Count, "老库账号数");
            T.Equal("", accounts[0].ConnectedEnvironments, "老库归属应为空串兜底");
        });

        T.Test("读库: 数据库文件损坏时 ReadAccounts 向上抛(现行为记录,调用方负责兜)", () =>
        {
            var p = NewPaths();
            File.WriteAllBytes(p.CachedDataDb, Encoding.ASCII.GetBytes("this is not a sqlite database"));
            var reader = new AccountReader(p);
            T.Throws<SqliteException>(() => reader.ReadAccounts(out _), "损坏库应抛 SqliteException");
            // 对照: 只读活跃指针的路径自己吞异常,退化为「未知」
            T.Null(reader.ReadActiveAccountId(), "损坏库读活跃指针应退化为 null");
        });

        T.Test("读库: 活跃指针缺失/损坏一律退化为 null", () =>
        {
            var p = NewPaths();
            BuildDb(p, new[] { new CacheRow(FnvName(Login), UsEnv, "Player#1234", 42) }, pointerJson: null);
            var reader = new AccountReader(p);
            T.Null(reader.ReadActiveAccountId(), "没有指针键应为 null");

            File.Delete(p.CachedDataDb);
            BuildDb(p, Array.Empty<CacheRow>(), pointerJson: "{broken");
            T.Null(new AccountReader(p).ReadActiveAccountId(), "损坏指针值应为 null");
        });
    }
}
