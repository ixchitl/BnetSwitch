namespace BnetSwitch.Models;

/// <summary>
/// 从战网 CachedData.db 的 login_cache 表读出的一个账号。
/// </summary>
public sealed class BattleAccount
{
    /// <summary>account_id_lo,同时也是 Account\ 与 BrowserCaches\ 下的目录名。</summary>
    public long AccountId { get; init; }

    /// <summary>展示用的 BattleTag(UTF-8),例如「给我激素是我爹#5314」。</summary>
    public string BattleTag { get; init; } = "";

    /// <summary>登录环境,例如 cn.actual.battlenet.com.cn。这是【末次登录端点】,跨区服切号有可能被写串。</summary>
    public string Environment { get; init; } = "";

    /// <summary>
    /// login_cache.connected_environments,账号能连的区服集合:国服号=「CN」,国际服号=「EU,KR,US,XX」之类。
    /// 这是账号的【真实归属】,比 Environment 可靠 —— 判国服/国际服优先看它。空串表示战网没写(旧库/兜底)。
    /// </summary>
    public string ConnectedEnvironments { get; init; } = "";

    /// <summary>login_cache.name,16 位十六进制的内部标识(非邮箱)。</summary>
    public string InternalName { get; init; } = "";
}
