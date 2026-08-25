using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BnetSwitch.Services;

/// <summary>
/// 判断「战网这次真的免密登录成功了吗」。
///
/// 【为什么不能用 CachedData 的活跃指针判断】那个指针是切换时【我们自己写进去的】,
/// 读回来必然等于目标号 —— 拿它当"登录成功"的证据等于自证,什么都没验到。
/// 2026-08-14 就栽在这:登录被服务端拒绝、令牌已被客户端删掉,工具却判定成功,
/// 把坏令牌存进快照,下次写回去又被拒又被删,雪崩且不可逆。
///
/// 这里改用两个【客户端自己产生、我们绝不写】的证据:
/// 1. 客户端日志里有没有 "rejected by BGS" / "DeleteToken" —— 有就是失败,一票否决;
/// 2. 该账号的 account.db 有没有被刷新 —— 登录成功时客户端会写它。
///
/// 【2026-08-25 关键修复:必须按「行时间戳」过滤,不能按文件 mtime】
/// 战网每次启动写一个新日志文件,但一个文件里有整段会话的行。原来只按【文件最后修改时间】
/// 粗筛、再全文搜 "rejected by BGS",结果【快速连切】时:上一次切号失败的那份日志 mtime 仍很新,
/// 会被这一次的核对读到 → 把上一次的旧拒绝当成这一次的 → 明明这次登录成功却判失败,
/// 触发 2.1.5 的回滚把【刚生效的好令牌】覆盖回旧令牌 → 反而真把免密弄没了(实测 08-25 复现)。
/// 修法:解析每一行的 UTC 时间戳,只认【这次切号 sinceUtc 之后】写的那一行。
/// </summary>
public static class LoginProbe
{
    private static string LocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Battle.net");

    private static string LogsDir => Path.Combine(LocalRoot, "Logs");

    /// <summary>本次切号(sinceUtc)之后,客户端日志里是否出现了「令牌被拒 / 删令牌」。出现过就判失败。</summary>
    public static bool SawTokenRejected(DateTime sinceUtc)
    {
        try
        {
            var dir = new DirectoryInfo(LogsDir);
            if (!dir.Exists) return false;

            foreach (var f in dir.GetFiles("battle.net-*.log"))
            {
                // 粗筛:整份文件在这次切号之前 5 秒就没再写过 → 里面不可能有本次的证据,跳过
                if (f.LastWriteTimeUtc < sinceUtc.AddSeconds(-5)) continue;
                foreach (var line in ReadLinesShared(f.FullName))
                {
                    if (!(line.Contains("rejected by BGS", StringComparison.Ordinal) ||
                          line.Contains("DeleteToken", StringComparison.Ordinal)))
                        continue;
                    // 【只认本次切号之后写的行】—— 解析不出时间戳的行一律不算,宁可漏报也不误报,
                    // 因为误报会触发回滚、把刚成功的好令牌降级,代价远大于漏报。
                    var t = LineTimeUtc(line);
                    if (t is { } lt && lt >= sinceUtc) return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>该账号的 account.db 在本次启动之后被刷新过 —— 客户端成功登进这个号才会写它。</summary>
    public static bool AccountTouched(long accountId, DateTime sinceUtc)
    {
        try
        {
            var f = new FileInfo(Path.Combine(LocalRoot, "Account", accountId.ToString(), "account.db"));
            return f.Exists && f.LastWriteTimeUtc >= sinceUtc.AddSeconds(-5);
        }
        catch { return false; }
    }

    /// <summary>
    /// 本次切号之后客户端报过的错误码(BLZBNT…)。用户报障时直接写进我们自己的日志,
    /// 省得再让他去翻战网的日志目录 —— 那一步大多数人做不到。
    /// 同样【只取 sinceUtc 之后的行】,免得把上一次切号的旧错误码算进来。
    /// </summary>
    public static IReadOnlyList<string> ClientErrorCodes(DateTime sinceUtc)
    {
        var codes = new List<string>();
        try
        {
            var dir = new DirectoryInfo(LogsDir);
            if (!dir.Exists) return codes;
            foreach (var f in dir.GetFiles("battle.net-*.log"))
            {
                if (f.LastWriteTimeUtc < sinceUtc.AddSeconds(-5)) continue;
                foreach (var line in ReadLinesShared(f.FullName))
                {
                    if (!line.Contains("BLZBNT", StringComparison.Ordinal)) continue;
                    var t = LineTimeUtc(line);
                    if (t is not { } lt || lt < sinceUtc) continue;   // 旧行/无时间戳的行不算
                    foreach (Match m in Regex.Matches(line, @"BLZBNT[A-Z0-9]+"))
                        if (!codes.Contains(m.Value)) codes.Add(m.Value);
                }
            }
        }
        catch { }
        return codes;
    }

    /// <summary>
    /// 综合判定:没有被拒/删令牌的迹象,且这个号的 account.db 被刷新过,才算真的登进去了。
    /// 拿不准一律算【没成功】—— 存错令牌的代价(雪崩)远大于少存一次。
    /// </summary>
    public static bool LoginConfirmed(long accountId, DateTime sinceUtc) =>
        !SawTokenRejected(sinceUtc) && AccountTouched(accountId, sinceUtc);

    /// <summary>
    /// 解析战网日志行开头的 UTC 时间戳。行形如:「W 2026-08-25 02:47:21.482079 [..] ..」。
    /// 战网这个时间是 UTC(与文件名里的 T 时间一致),而 sinceUtc 也是 UtcNow,可直接比。
    /// 解析不出返回 null。
    /// </summary>
    private static DateTime? LineTimeUtc(string line)
    {
        var m = Regex.Match(line, @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(\.\d+)?");
        if (!m.Success) return null;
        if (DateTime.TryParse(m.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return dt;
        return null;
    }

    /// <summary>客户端还开着时日志是占用状态,必须共享读;按行返回,读失败返回空。</summary>
    private static IEnumerable<string> ReadLinesShared(string path)
    {
        string text;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            text = sr.ReadToEnd();
        }
        catch { yield break; }
        foreach (var line in text.Split('\n'))
            yield return line;
    }
}
