using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace BnetSwitch.Services;

/// <summary>
/// 一键导出诊断包:把排障需要的东西打成一个 zip 放桌面,用户直接发给开发者即可。
///
/// 【为什么要有】切号出问题时,真正有用的信息散在四个地方(本工具日志、战网客户端日志、
/// 注册表槽位、快照状态),让用户一个个去翻是不现实的 —— 大多数人做不到,
/// 最后只能靠开发者远程猜。一个按钮换一个文件,把来回沟通从半小时压到一次。
///
/// 【绝不包含的东西】
/// - 令牌内容(uauth.json / 注册表值):那是登录凭据,日志是要发出去的,等于泄露账号;
/// - 密码、cookie、激活码。
/// 只带槽位【名字】、账号 id、BattleTag、区服、文件是否存在这类判断故障必需的元信息。
/// </summary>
public static class DiagnosticExport
{
    private const string SlotKey = @"Software\Blizzard Entertainment\Battle.net\UnifiedAuth";

    /// <summary>生成诊断包,返回 zip 路径。失败抛异常,由调用方提示。</summary>
    public static string Create(string appVersion)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(local, "BnetSwitch");

        var tmp = Path.Combine(Path.GetTempPath(), "BnetSwitch-diag-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            // 1) 本工具的切号日志 + 崩溃日志
            CopyIfExists(Path.Combine(root, "switch.log"), Path.Combine(tmp, "switch.log"));
            CopyTail(Path.Combine(root, "crash.log"), Path.Combine(tmp, "crash.log"), 200);

            // 2) 战网客户端最近几份日志 —— 令牌被拒/删令牌这些关键证据只在它里面
            var bnetLogs = Path.Combine(local, "Battle.net", "Logs");
            if (Directory.Exists(bnetLogs))
            {
                var dst = Path.Combine(tmp, "battlenet-logs");
                Directory.CreateDirectory(dst);
                foreach (var f in new DirectoryInfo(bnetLogs)
                             .GetFiles("battle.net-*.log")
                             .OrderByDescending(f => f.LastWriteTimeUtc)
                             .Take(6))
                    CopyIfExists(f.FullName, Path.Combine(dst, f.Name));
            }

            // 3) 环境与状态汇总
            File.WriteAllText(Path.Combine(tmp, "summary.txt"), BuildSummary(appVersion, root), Encoding.UTF8);

            var zip = Path.Combine(DesktopOrFallback(root),
                $"BnetSwitch-诊断-{DateTime.Now:MMdd-HHmm}.zip");
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(tmp, zip, CompressionLevel.Optimal, includeBaseDirectory: false);
            return zip;
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    private static string DesktopOrFallback(string fallback)
    {
        try
        {
            var d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
        }
        catch { }
        return fallback;
    }

    private static string BuildSummary(string appVersion, string root)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BnetSwitch 诊断信息");
        sb.AppendLine($"版本      : {appVersion}");
        sb.AppendLine($"系统      : {Environment.OSVersion.VersionString} {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}");
        sb.AppendLine($"导出时间  : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        // 令牌槽:只列名字,绝不列值
        sb.AppendLine("== 免密令牌槽(仅名字,不含内容)==");
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SlotKey);
            var names = key?.GetValueNames() ?? Array.Empty<string>();
            sb.AppendLine(names.Length == 0 ? "  (空)" : "  " + string.Join(", ", names.OrderBy(n => n)));
        }
        catch (Exception e) { sb.AppendLine("  读取失败: " + e.Message); }
        sb.AppendLine();

        // 账号快照概览
        sb.AppendLine("== 账号快照 ==");
        var acc = Path.Combine(root, "accounts");
        if (Directory.Exists(acc))
        {
            foreach (var dir in Directory.EnumerateDirectories(acc).OrderBy(d => d))
            {
                var id = Path.GetFileName(dir);
                var cfg = Path.Combine(dir, "BattleNet", "Battle.net.config");
                var region = "?";
                try
                {
                    if (File.Exists(cfg))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(
                            File.ReadAllText(cfg), "\"LastLoginRegion\"\\s*:\\s*\"([^\"]*)\"");
                        if (m.Success) region = m.Groups[1].Value;
                    }
                }
                catch { }
                var slot = "-";
                try
                {
                    var sp = Path.Combine(dir, "uauth_slot.txt");
                    if (File.Exists(sp)) slot = File.ReadAllText(sp).Trim();
                }
                catch { }
                var hasToken = File.Exists(Path.Combine(dir, "uauth.json"));
                var hasPtr = File.Exists(Path.Combine(dir, "cacheddata_pointer.json"));
                sb.AppendLine($"  {id,-12} 区服={region,-4} 槽={slot,-10} 令牌文件={(hasToken ? "有" : "无")} 指针={(hasPtr ? "有" : "无")}");
            }
        }
        else sb.AppendLine("  (没有 accounts 目录)");
        sb.AppendLine();

        // 游戏状态快照
        sb.AppendLine("== 游戏状态快照(跨区服免下载)==");
        try
        {
            var gs = new GameStateStore();
            sb.AppendLine($"  游戏目录: {gs.GameRoot ?? "(没找到)"}");
            sb.AppendLine($"  当前区服: {gs.ReadCurrentRegion() ?? "?"}  版本: {gs.ReadCurrentVersion() ?? "?"}");
            var rd = Path.Combine(gs.Root, "regions");
            if (Directory.Exists(rd))
                foreach (var f in Directory.EnumerateFiles(rd, "*.json"))
                {
                    try
                    {
                        var man = JsonSerializer.Deserialize<GameStateManifest>(File.ReadAllText(f));
                        sb.AppendLine($"  {Path.GetFileNameWithoutExtension(f),-4} 文件={man?.Files.Count,-5} " +
                                      $"版本={man?.Version,-24} 记录于={man?.CapturedUtc.ToLocalTime():MM-dd HH:mm}");
                    }
                    catch { }
                }
            else sb.AppendLine("  (还没有记录过任何区服)");
        }
        catch (Exception e) { sb.AppendLine("  读取失败: " + e.Message); }

        return sb.ToString();
    }

    private static void CopyIfExists(string src, string dst)
    {
        try
        {
            if (!File.Exists(src)) return;
            // 日志可能正被占用,必须共享读
            using var fs = new FileStream(src, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var os = File.Create(dst);
            fs.CopyTo(os);
        }
        catch { }
    }

    /// <summary>只取末尾若干行 —— crash.log 可能几百 KB,全带上没必要。</summary>
    private static void CopyTail(string src, string dst, int lines)
    {
        try
        {
            if (!File.Exists(src)) return;
            using var fs = new FileStream(src, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var all = sr.ReadToEnd().Split('\n');
            File.WriteAllLines(dst, all.Skip(Math.Max(0, all.Length - lines)), Encoding.UTF8);
        }
        catch { }
    }
}
