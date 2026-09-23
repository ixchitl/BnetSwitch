using Microsoft.Data.Sqlite;

namespace BnetSwitch.Tests;

/// <summary>
/// 临时沙箱。Linux 上 <c>Environment.GetFolderPath</c> 把 LocalApplicationData / ApplicationData
/// 映射到 XDG_DATA_HOME / XDG_CONFIG_HOME(目录必须已存在,否则返回空串 —— 已实测),
/// <c>Path.GetTempPath</c> 映射到 TMPDIR,且三者都是每次调用动态读环境变量。
/// 于是产品代码里 %LOCALAPPDATA%\BnetSwitch、%APPDATA%\Battle.net、临时库副本等
/// 全部真实路径 API 都被重定向进临时目录:测试跑的是真实产品逻辑,只是数据是合成的。
///
/// 每个用例前 <see cref="Reset"/> 换全新沙箱;整轮结束后 <see cref="Uninstall"/> 删除整棵树。
/// <see cref="VerifyNoPersonalSideEffects"/> 用运行前后的指纹对比证明个人数据目录未被触碰。
/// </summary>
public static class Sandbox
{
    public static string Root { get; private set; } = "";
    public static string DataDir => Path.Combine(Root, "data");     // → SpecialFolder.LocalApplicationData
    public static string ConfigDir => Path.Combine(Root, "config"); // → SpecialFolder.ApplicationData
    public static string TmpDir => Path.Combine(Root, "tmp");       // → Path.GetTempPath

    private static string _personalDataRoot = "";
    private static string _personalConfigRoot = "";
    private static string _personalFingerprintBefore = "";

    /// <summary>建沙箱根目录,并在重定向之前给本机个人数据目录拍指纹。</summary>
    public static void Install()
    {
        if (Root.Length > 0) return;
        _personalDataRoot = FirstNonEmpty(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"));
        _personalConfigRoot = FirstNonEmpty(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"));
        _personalFingerprintBefore = PersonalFingerprint();

        Root = Directory.CreateTempSubdirectory("bnetswitch-tests-").FullName;
        Reset();
    }

    /// <summary>换新沙箱:清空并重建三个子目录,重设环境变量,然后自检重定向确实生效。</summary>
    public static void Reset()
    {
        if (Root.Length == 0) { Install(); return; }
        // Microsoft.Data.Sqlite 默认按连接字符串池化连接:上一个用例的池化句柄仍握着
        // 已删除文件的 inode,同路径重开时会拿到旧库(实测 CREATE TABLE 撞「already exists」)。
        // 换沙箱必须连池一起清,产品代码里的真实连接同样受益。
        SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { DataDir, ConfigDir, TmpDir })
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
        }
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", DataDir);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", ConfigDir);
        Environment.SetEnvironmentVariable("TMPDIR", TmpDir);

        // 自检:重定向失效(如 .NET 行为变化)必须炸响,绝不带着真实个人路径跑测试
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var tmp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        if (local != DataDir || roaming != ConfigDir || tmp != TmpDir)
            throw new Exception(
                $"沙箱隔离失效: LocalApplicationData='{local}', ApplicationData='{roaming}', Temp='{tmp}';" +
                $" 期望 '{DataDir}' / '{ConfigDir}' / '{TmpDir}'");
    }

    /// <summary>用例结束后临时目录必须干净:产品代码(AccountReader)的临时库副本应已自清。</summary>
    public static void CheckNoTempLeftovers()
    {
        var left = Directory.GetFileSystemEntries(TmpDir);
        if (left.Length > 0)
            throw new Exception($"测试在临时目录留下 {left.Length} 个未清理条目,第一个: {Path.GetFileName(left[0])}");
    }

    /// <summary>整轮跑完:个人数据目录(BnetSwitch / Battle.net)指纹必须与运行前一致。</summary>
    public static void VerifyNoPersonalSideEffects()
    {
        var after = PersonalFingerprint();
        T.Equal(_personalFingerprintBefore, after, "个人数据目录运行前后指纹");
    }

    public static void Uninstall()
    {
        if (Root.Length == 0) return;
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception ex) { Console.Error.WriteLine($"警告: 沙箱目录 {Root} 清理失败: {ex.Message}"); }
    }

    /// <summary>个人目录指纹:各守护目录的存在性 + 文件数 + 最新写入时间。</summary>
    private static string PersonalFingerprint()
    {
        var parts = new List<string>();
        foreach (var dir in new[]
                 {
                     Path.Combine(_personalDataRoot, "BnetSwitch"),
                     Path.Combine(_personalDataRoot, "Battle.net"),
                     Path.Combine(_personalConfigRoot, "BnetSwitch"),
                     Path.Combine(_personalConfigRoot, "Battle.net"),
                 })
            parts.Add(FingerprintOf(dir));
        return string.Join("|", parts);
    }

    private static string FingerprintOf(string dir)
    {
        if (!Directory.Exists(dir)) return "absent";
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        var newest = files.Length == 0
            ? 0L
            : files.Max(f => File.GetLastWriteTimeUtc(f).Ticks);
        return $"{files.Length}:{newest}";
    }

    private static string FirstNonEmpty(string? a, string b) =>
        string.IsNullOrWhiteSpace(a) ? b : a!;
}
