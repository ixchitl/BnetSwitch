using System.IO;
using System.Text;

namespace BnetSwitch.Services;

/// <summary>
/// 切号日志:<c>%LOCALAPPDATA%\BnetSwitch\switch.log</c>。
///
/// 【为什么需要它】切号失败几乎总是发生在【工具已经干完活、客户端启动之后】——
/// 用户看到的只是"又要输密码了",而工具这边一切正常。2026-08-14 那次丢免密的排查,
/// 全靠翻战网客户端自己的日志 + 现场看注册表才定位到,普通用户报障时这些都拿不到。
/// 有了这个文件,用户把它发过来就能还原整条链路。
///
/// 【绝不记录令牌内容】只记槽名和「写了 / 没写 / 原因」。令牌是登录凭据,
/// 日志是要发给开发者的,写进去等于让用户泄露账号。
/// </summary>
public static class SwitchLog
{
    private const long MaxBytes = 1024 * 1024;   // 1MB 封顶,超了砍掉前一半

    private static readonly object Gate = new();

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BnetSwitch");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "switch.log");
        }
    }

    /// <summary>目录路径,给设置里的「打开日志文件夹」用。</summary>
    public static string Directory_ => Path.GetDirectoryName(FilePath)!;

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                var path = FilePath;
                Rotate(path);
                File.AppendAllText(path,
                    $"{DateTime.Now:MM-dd HH:mm:ss}  {line}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { /* 记不了日志绝不能影响切号本身 */ }
    }

    /// <summary>每次启动记一行环境信息,报障时能一眼看出版本和系统。</summary>
    public static void WriteHeader(string appVersion)
    {
        Write($"===== 启动 {appVersion} · {Environment.OSVersion.VersionString} · " +
              $"{(Environment.Is64BitOperatingSystem ? "x64" : "x86")} =====");
    }

    private static void Rotate(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < MaxBytes) return;

            // 砍掉前一半,保留最近的记录 —— 出问题时要看的永远是最后那几十行
            var lines = File.ReadAllLines(path);
            File.WriteAllLines(path, lines.Skip(lines.Length / 2), Encoding.UTF8);
        }
        catch { }
    }
}
