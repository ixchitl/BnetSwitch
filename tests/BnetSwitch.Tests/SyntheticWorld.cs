using System.Text;
using BnetSwitch.Services;
using Microsoft.Data.Sqlite;

namespace BnetSwitch.Tests;

/// <summary>
/// 合成测试世界:临时沙箱里的假战网数据(CachedData.db / Battle.net.config)。
/// 登录名一律 example.invalid,BattleTag/令牌字节全部编造 —— 绝不使用真实账号数据。
/// </summary>
public static class SyntheticWorld
{
    public const string Login = "player@example.invalid";
    public const string OtherLogin = "smurf@example.invalid";

    /// <summary>国服端点含 battlenet.com.cn(产品用它判国服/国际),国际服端点不含。</summary>
    public const string CnEnv = "cn.actual.battlenet.com.cn";
    public const string UsEnv = "us.actual.battle.net";
    public const string KrEnv = "kr.actual.battle.net";

    /// <summary>建目录骨架并返回产品路径对象(调用前沙箱环境已生效)。</summary>
    public static BattleNetPaths NewPaths()
    {
        var p = new BattleNetPaths();
        Directory.CreateDirectory(p.LocalRoot);
        Directory.CreateDirectory(p.RoamingDir);
        return p;
    }

    /// <summary>写 live Battle.net.config。region 传 null 则整个 LastLoginRegion 节点缺失。</summary>
    public static void WriteConfig(BattleNetPaths p, string savedAccountNames, string? region = "CN")
    {
        var loginNode = region is null ? "" : $",\"Login\":{{\"LastLoginRegion\":\"{region}\"}}";
        File.WriteAllText(p.RoamingConfig,
            $"{{\"Client\":{{\"SavedAccountNames\":\"{savedAccountNames}\"}}{loginNode}}}");
    }

    /// <summary>合成 login_cache 一行。Connected 为 null 时该列不建(模拟老版本战网库)。</summary>
    public sealed record CacheRow(string Name, string Env, string Tag, long Id, string? Connected = "CN");

    /// <summary>
    /// 建合成 CachedData.db:login_cache + key_value_store(活跃指针)。
    /// withConnectedColumn=false 模拟没有 connected_environments 列的老库。
    /// </summary>
    public static void BuildDb(
        BattleNetPaths p,
        IReadOnlyList<CacheRow> rows,
        string? pointerJson = null,
        bool withConnectedColumn = true)
    {
        // Pooling=false: 同一用例内可能删库重建,不能让池化句柄握住已删文件的旧 inode
        using var conn = new SqliteConnection($"Data Source={p.CachedDataDb};Pooling=false");
        conn.Open();
        using (var ddl = conn.CreateCommand())
        {
            var extra = withConnectedColumn ? ", connected_environments TEXT" : "";
            ddl.CommandText =
                "CREATE TABLE login_cache(name TEXT, environment TEXT, battle_tag TEXT, account_id_lo INTEGER" + extra + ");" +
                "CREATE TABLE key_value_store(key TEXT, value TEXT);";
            ddl.ExecuteNonQuery();
        }
        foreach (var r in rows)
        {
            using var ins = conn.CreateCommand();
            if (withConnectedColumn)
            {
                ins.CommandText = "INSERT INTO login_cache(name, environment, battle_tag, account_id_lo, connected_environments) VALUES($n,$e,$t,$i,$c)";
                ins.Parameters.AddWithValue("$c", (object?)r.Connected ?? DBNull.Value);
            }
            else
            {
                ins.CommandText = "INSERT INTO login_cache(name, environment, battle_tag, account_id_lo) VALUES($n,$e,$t,$i)";
            }
            ins.Parameters.AddWithValue("$n", r.Name);
            ins.Parameters.AddWithValue("$e", r.Env);
            ins.Parameters.AddWithValue("$t", r.Tag);
            ins.Parameters.AddWithValue("$i", r.Id);
            ins.ExecuteNonQuery();
        }
        if (pointerJson is not null)
        {
            using var kv = conn.CreateCommand();
            kv.CommandText = "INSERT INTO key_value_store(key, value) VALUES('features_cached_data_points', $v)";
            kv.Parameters.AddWithValue("$v", pointerJson);
            kv.ExecuteNonQuery();
        }
    }

    /// <summary>合成活跃指针 JSON(与战网同构:含 account_id / account_region)。</summary>
    public static string Pointer(long accountId, string region = "CN") =>
        $"{{\"account_id\":{accountId},\"account_region\":\"{region}\"}}";

    /// <summary>合成令牌/槽值字节(数值全部编造,与真实 DPAPI 数据无关)。</summary>
    public static byte[] Bytes(params int[] v) => v.Select(x => (byte)x).ToArray();

    /// <summary>
    /// FNV-1a-64,小写十六进制 —— 与 login_cache.name 同口径(输入先转大写)。
    /// 这是【造合成数据】用的独立实现,正确性由公开测试向量用例核对(见 AccountResolveTests),
    /// 产品侧匹配逻辑仍由被测代码自己执行,不存在拿同一实现自证。
    /// </summary>
    public static string FnvCore(string s)
    {
        ulong h = 0xcbf29ce484222325UL;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            h ^= b;
            h *= 0x100000001b3UL;
        }
        return h.ToString("x16");
    }

    public static string FnvName(string login) => FnvCore(login.ToUpperInvariant());
}
