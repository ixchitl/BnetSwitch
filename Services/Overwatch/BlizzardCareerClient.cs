using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace BnetSwitch.Services.Overwatch;

// 国际服/亚服 OW 战绩 —— 暴雪官方生涯页(overwatch.blizzard.com)。
// 免 key、免登录、免 Cloudflare,拼 URL 直接拿 1.2MB 服务端渲染 HTML。
// (tracker.gg 那条路走不通:页面和它的内部 API 都被 Cloudflare 挡死,一律 403。)
//
// 两个实测坑:
//   1) URL 大小写敏感:/career/ruyuzhilin-1958/ → 404,/career/Ruyuzhilin-1958/ → 200
//   2) 语言前缀会被丢:/zh-tw/career/<名字-编号>/ 会 302 到 /en-us/career/<哈希>/。
//      想要别的语言得先拿到哈希永久链接,再请求 /<lang>/career/<哈希>/。
//      我们统一抓 en-us(英文键最稳),简体交给 OwEnNames 本地翻译。
public sealed class BlizzardCareerClient
{
    private const string Base = "https://overwatch.blizzard.com";
    private const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
    private const string SecChUa = "\"Chromium\";v=\"140\", \"Not=A?Brand\";v=\"24\", \"Google Chrome\";v=\"140\"";

    /// <summary>
    /// 相邻两次请求的最小间隔(毫秒)+ 随机抖动。批量刷段位时一个号一个请求,
    /// 连着打过去在对方看来就是脚本 —— 拉开间隔并让它不规则,是最基本的礼貌,也最不容易被拦。
    /// </summary>
    private const int MinIntervalMs = 1200;
    private const int JitterMs = 900;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastReq = DateTime.MinValue;
    private static readonly Random Rng = new();

    private static async Task PaceAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int jitter;
            lock (Rng) jitter = Rng.Next(0, JitterMs);
            var wait = MinIntervalMs + jitter - (int)(DateTime.UtcNow - _lastReq).TotalMilliseconds;
            if (wait > 0) await Task.Delay(wait, ct).ConfigureAwait(false);
            _lastReq = DateTime.UtcNow;
        }
        finally { Gate.Release(); }
    }

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(60) };   // 实测整页 ~1.26MB,慢的时候要 20s 上下

    public enum Status
    {
        Ok,
        /// <summary>暴雪侧查无此人 —— 国服号就是这个下场(cn.actual.battlenet.com.cn 一律 404)。</summary>
        NotFound,
        /// <summary>网络/服务端故障,和"没这个号"要分开报。</summary>
        Failed,
    }

    /// <summary>Permalink 是生涯页 URL 里的那段哈希(或 名字-编号),用于二次请求和缓存键。</summary>
    public sealed record Result(Status Status, string? Permalink, string? Html, string? Error);

    /// <summary>"Ruyuzhilin#1958" → "Ruyuzhilin-1958"(URL 片段,已转义)。</summary>
    public static string TagToSlug(string battleTag)
        => Uri.EscapeDataString(battleTag.Trim().Replace('#', '-'));

    /// <summary>一次拿到生涯页:先解析永久链接,再抓 HTML。</summary>
    public async Task<Result> LoadAsync(string battleTag, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(battleTag) || !battleTag.Contains('#'))
            return new Result(Status.NotFound, null, null, "战网 ID 格式不对(应形如 Name#1234)");

        try
        {
            // 第一跳:直接按 名字-编号 请求。命中就顺带把整页 HTML 拿到手,不用再打一次。
            var url = $"{Base}/en-us/career/{TagToSlug(battleTag)}/";
            await PaceAsync(ct).ConfigureAwait(false);
            using var req = New(url);
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return new Result(Status.Ok, PermalinkOf(resp.RequestMessage?.RequestUri, battleTag), html, null);
            }
            if (resp.StatusCode != HttpStatusCode.NotFound)
                return new Result(Status.Failed, null, null, $"暴雪返回 HTTP {(int)resp.StatusCode}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new Result(Status.Failed, null, null, Friendly(ex)); }

        // 第二跳:404 多半是大小写写错了(URL 区分大小写)。走搜索接口拿哈希永久链接。
        string? permalink;
        try { permalink = await SearchPermalinkAsync(battleTag, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new Result(Status.Failed, null, null, Friendly(ex)); }

        if (permalink == null)
            return new Result(Status.NotFound, null, null, "该账号在国际服查不到(国服账号在暴雪侧不存在)");

        try
        {
            var html = await FetchHtmlAsync(permalink, ct).ConfigureAwait(false);
            return html == null
                ? new Result(Status.Failed, permalink, null, "生涯页抓取失败")
                : new Result(Status.Ok, permalink, html, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new Result(Status.Failed, permalink, null, Friendly(ex)); }
    }

    /// <summary>按名字搜索,返回哈希永久链接。GET /en-us/search/account-by-name/{name}/ → JSON 数组。</summary>
    public async Task<string?> SearchPermalinkAsync(string battleTag, CancellationToken ct = default)
    {
        var name = battleTag.Split('#')[0].Trim();
        await PaceAsync(ct).ConfigureAwait(false);
        using var req = New($"{Base}/en-us/search/account-by-name/{Uri.EscapeDataString(name)}/", navigation: false);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        var want = battleTag.Trim().Replace('#', '-');
        string? loose = null;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var n = e.TryGetProperty("name", out var pn) ? pn.GetString() : null;
            var u = e.TryGetProperty("url", out var pu) ? pu.GetString() : null;
            if (string.IsNullOrEmpty(u)) continue;
            if (string.Equals(n?.Replace('#', '-'), want, StringComparison.OrdinalIgnoreCase)) return u;
            loose ??= u;   // 名字对不上就先记着:同名不同编号的情况下宁可不猜,只有独苗时才用
        }
        return doc.RootElement.GetArrayLength() == 1 ? loose : null;
    }

    /// <summary>GET /en-us/career/{permalink}/ —— 整页 HTML(~1.2MB)。</summary>
    public async Task<string?> FetchHtmlAsync(string permalink, CancellationToken ct = default)
    {
        await PaceAsync(ct).ConfigureAwait(false);
        using var req = New($"{Base}/en-us/career/{permalink.Trim('/')}/");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
    }

    // socket 层的原话("由于目标计算机积极拒绝…")不该直接怼到界面上,翻成人话。
    private static string Friendly(Exception ex) => ex switch
    {
        TaskCanceledException or TimeoutException => "连接暴雪超时,网络不太稳",
        HttpRequestException => "连不上暴雪服务器,检查一下网络或代理",
        _ => ex.Message,
    };

    /// <summary>
    /// 构造请求。头要凑齐【真实浏览器会发的那一整套】—— 只发 UA + Accept 两三个头,
    /// 在对方的风控看来和脚本没区别;sec-ch-ua / sec-fetch-* 这些缺一个都显眼。
    /// </summary>
    private static HttpRequestMessage New(string url, bool navigation = true)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        var h = r.Headers;
        h.TryAddWithoutValidation("User-Agent", UA);
        h.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        h.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        h.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
        h.TryAddWithoutValidation("sec-ch-ua", SecChUa);
        h.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        h.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        h.TryAddWithoutValidation("Sec-Fetch-Dest", navigation ? "document" : "empty");
        h.TryAddWithoutValidation("Sec-Fetch-Mode", navigation ? "navigate" : "cors");
        h.TryAddWithoutValidation("Sec-Fetch-Site", navigation ? "none" : "same-origin");
        if (navigation)
        {
            h.TryAddWithoutValidation("Sec-Fetch-User", "?1");
            h.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        }
        h.TryAddWithoutValidation("Cache-Control", "max-age=0");
        return r;
    }

    // 跟完 302 后的最终 URL 形如 https://overwatch.blizzard.com/en-us/career/<哈希>/
    private static string PermalinkOf(Uri? final, string battleTag)
    {
        var seg = final?.Segments;
        if (seg is { Length: > 0 })
        {
            var last = seg[^1].Trim('/');
            // 保持转义原样:这段要能直接拼回 /career/{permalink}/ 再请求一次(哈希里带 %7C)
            if (!string.IsNullOrEmpty(last) && !last.Equals("career", StringComparison.OrdinalIgnoreCase))
                return last;
        }
        return battleTag.Replace('#', '-');
    }
}
