using System.IO;
using System.Text.Json;

namespace BnetSwitch.Services;

/// <summary>本工具自身的设置,存 %LOCALAPPDATA%\BnetSwitch\settings.json。</summary>
public sealed class AppSettings
{
    /// <summary>用户手动指定的 Battle.net.exe 路径(自动探测失败时使用)。</summary>
    public string? ClientExe { get; set; }

    // ---- 界面 / 行为 ----

    /// <summary>点关闭按钮时最小化到托盘(而不是退出)。默认开。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>是否已经问过「关闭时怎么处理」(首次关闭弹一次二选一)。</summary>
    public bool CloseChoiceMade { get; set; } = false;

    /// <summary>启动时直接最小化到托盘(配合开机自启用)。</summary>
    public bool StartMinimized { get; set; } = false;

    /// <summary>
    /// 跨区服切号时,等不及 Agent.exe 自己退出就直接结束它。
    ///
    /// 换游戏文件必须在 Agent 退干净之后做 —— 它内存里存着 product.db,活着时写进去会被它覆盖回来。
    /// 而它比客户端慢一拍才退,实测平均要等 15 秒,这是跨区服切号唯一的耗时来源
    /// (换文件本身是 0.0 秒)。结束它的风险很低:Agent 只管下载/安装,不持有任何登录状态
    /// (令牌在注册表、登录态在 %APPDATA%),战网启动时会自己把它拉起来。
    /// 【默认关】2026-08-14:开启它之后,跨区服切号开始出现「服务端拒绝令牌 → 客户端删掉免密」,
    /// 而在此之前用等待版切了很多次都没事。强杀这个常驻服务进程很可能破坏了客户端的会话状态。
    /// 在查清楚之前不要默认开 —— 省十几秒不值得拿账号的免密去换。
    /// </summary>
    public bool ForceKillAgentOnSwitch { get; set; } = false;

    /// <summary>深色模式。</summary>
    public bool DarkMode { get; set; } = false;

    /// <summary>上次窗口尺寸(逻辑像素)。0 = 未记录,首次启动用最小尺寸。</summary>
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }

    /// <summary>上次关闭时是否处于最大化。</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>用户在列表里手动隐藏的账号 id(战网本地检测到、但不想在本工具里看到的旧号)。
    /// 只影响本工具显示,绝不动战网 / 不登出;该号若重新登录会自动从这里移除并再次出现。</summary>
    public List<long> HiddenAccountIds { get; set; } = new();

    /// <summary>用户给账号写的备注,key = account_id 的字符串形式(JSON 对象键只能是字符串)。
    /// 存这儿而不是快照的 meta.json:没存过快照的号也要能写备注、也要能被搜到。</summary>
    public Dictionary<string, string> AccountNotes { get; set; } = new();

    /// <summary>置顶的账号 id。只影响排序,在各自分组内排到最前。</summary>
    public List<long> PinnedAccountIds { get; set; } = new();

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BnetSwitch");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = (File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) : null)
                ?? new AppSettings();
        }
        catch { s = new AppSettings(); }
        s.Save();   // 规范化:确保 settings.json 含所有(含新增的)字段,方便你直接编辑
        return s;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 存不了就算了 */ }
    }
}
