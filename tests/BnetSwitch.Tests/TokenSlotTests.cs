using System.Text;
using BnetSwitch.Services;

namespace BnetSwitch.Tests;

/// <summary>
/// 令牌槽变化判定(TokenStore 的纯逻辑部分):「哪个槽属于哪个号」靠 ChangedSince + LastSeen 学出来,
/// 学错会把别的号的有效令牌覆盖成过期的(见 TokenStore 顶部 2026-08-14 事故注释),必须钉死。
/// 注册表读写本身是 Windows 专用适配:这里只验证它在非 Windows 平台安全退化(不读不写不炸),
/// Windows 实机行为另行验证。
/// </summary>
public static class TokenSlotTests
{
    private const string SlotA = "4EB0C645";
    private const string SlotB = "1A2B3C4D";

    private static byte[] B(params int[] v) => v.Select(x => (byte)x).ToArray();

    public static void Run()
    {
        T.Test("ChangedSince: 槽值无变化返回空", () =>
        {
            var lastSeen = new Dictionary<string, string> { [SlotA] = TokenStore.Hash(B(1, 2, 3)) };
            var current = new Dictionary<string, byte[]> { [SlotA] = B(1, 2, 3) };
            T.Equal(0, TokenStore.ChangedSince(lastSeen, current).Count, "无变化应为空列表");
        });

        T.Test("ChangedSince: 单槽值变化被识别", () =>
        {
            var lastSeen = new Dictionary<string, string> { [SlotA] = TokenStore.Hash(B(1, 2, 3)) };
            var current = new Dictionary<string, byte[]> { [SlotA] = B(9, 9, 9) };
            var changed = TokenStore.ChangedSince(lastSeen, current);
            T.Equal(1, changed.Count, "应恰好识别一个变化槽");
            T.Equal(SlotA, changed[0], "变化的槽名");
        });

        T.Test("ChangedSince: 新增槽被识别", () =>
        {
            var changed = TokenStore.ChangedSince(
                new Dictionary<string, string>(),
                new Dictionary<string, byte[]> { [SlotA] = B(1) });
            T.Equal(1, changed.Count, "新增槽应被列出");
            T.Equal(SlotA, changed[0], "新增槽名");
        });

        T.Test("ChangedSince: 多槽同时变化全部列出(调用方据此放弃学习)", () =>
        {
            var lastSeen = new Dictionary<string, string>
            {
                [SlotA] = TokenStore.Hash(B(1)),
                [SlotB] = TokenStore.Hash(B(2)),
            };
            var current = new Dictionary<string, byte[]>
            {
                [SlotA] = B(11),          // 变了
                [SlotB] = B(22),          // 变了
                ["77777777"] = B(33),     // 新增
            };
            var changed = TokenStore.ChangedSince(lastSeen, current);
            T.Equal(3, changed.Count, "两个变化 + 一个新增应全部列出");
            T.True(changed.Contains(SlotA) && changed.Contains(SlotB) && changed.Contains("77777777"),
                "三个槽都应列出");
        });

        T.Test("ChangedSince: 槽从注册表消失不上报(现行为记录)", () =>
        {
            // 铁律「绝不删槽」之下,消失意味着外部(客户端/用户)动了注册表;
            // ChangedSince 只对比 current 里存在的槽,消失的槽不在其职责内 —— 记录该边界。
            var lastSeen = new Dictionary<string, string>
            {
                [SlotA] = TokenStore.Hash(B(1)),
                [SlotB] = TokenStore.Hash(B(2)),
            };
            var current = new Dictionary<string, byte[]> { [SlotA] = B(1) };
            T.Equal(0, TokenStore.ChangedSince(lastSeen, current).Count, "被删的槽不应出现在结果里");
        });

        T.Test("ChangedSince: 槽名大小写不同按变化处理(现行为记录)", () =>
        {
            // ReadAll 的字典是 OrdinalIgnoreCase,而 ReadLastSeen 反序列化出的字典区分大小写;
            // 若注册表返回的槽名大小写与存盘时不同,会被当成「变化」。实际槽名大小写稳定,
            // 该测试钉住现状,阶段4重构若统一比较器需同步改这里。
            var lastSeen = new Dictionary<string, string> { ["4eb0c645"] = TokenStore.Hash(B(1, 2, 3)) };
            var current = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { ["4EB0C645"] = B(1, 2, 3) };
            T.Equal(1, TokenStore.ChangedSince(lastSeen, current).Count, "大小写不同应被视为变化");
        });

        T.Test("Hash: SHA-256 公开测试向量(期望值来自独立 published vectors)", () =>
        {
            // 期望值是公开发布的 SHA-256 标准向量,不是用产品代码算出来的
            T.Equal(
                "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
                TokenStore.Hash(Array.Empty<byte>()), "空输入的 SHA-256");
            T.Equal(
                "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
                TokenStore.Hash(Encoding.ASCII.GetBytes("abc")), "\"abc\" 的 SHA-256");
        });

        T.Test("LastSeen: 写读回环,存的是槽值哈希", () =>
        {
            TokenStore.WriteLastSeen(new Dictionary<string, byte[]> { [SlotA] = B(1, 2, 3), [SlotB] = B(4) });
            var seen = TokenStore.ReadLastSeen();
            T.Equal(2, seen.Count, "槽数");
            T.Equal(TokenStore.Hash(B(1, 2, 3)), seen[SlotA], "槽 A 哈希");
            T.Equal(TokenStore.Hash(B(4)), seen[SlotB], "槽 B 哈希");

            // 落盘位置必须在沙箱内(%LOCALAPPDATA%\BnetSwitch\uauth_lastseen.json)
            var file = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BnetSwitch", "uauth_lastseen.json");
            T.True(file.StartsWith(Sandbox.Root), $"LastSeen 文件应落在沙箱内: {file}");
            T.True(File.Exists(file), "LastSeen 文件应已写入");
        });

        T.Test("LastSeen: 文件缺失返回空字典", () =>
        {
            T.Equal(0, TokenStore.ReadLastSeen().Count, "全新沙箱应读出空字典");
        });

        T.Test("LastSeen: 损坏 JSON 返回空字典不抛异常", () =>
        {
            var file = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BnetSwitch", "uauth_lastseen.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{broken");
            T.Equal(0, TokenStore.ReadLastSeen().Count, "损坏文件应退化为空字典");
        });

        T.Test("学习闭环: 登录一次后恰好识别出该号自己的槽", () =>
        {
            // MainViewModel 学槽的真实链路: 存基线 → 登录 → 对比。跨持久化走一遍。
            var before = new Dictionary<string, byte[]> { [SlotA] = B(1), [SlotB] = B(2) };
            TokenStore.WriteLastSeen(before);

            var afterLogin = new Dictionary<string, byte[]> { [SlotA] = B(1), [SlotB] = B(77) };
            var changed = TokenStore.ChangedSince(TokenStore.ReadLastSeen(), afterLogin);
            T.Equal(1, changed.Count, "应恰好学到一个槽");
            T.Equal(SlotB, changed[0], "学到的槽");
        });

        T.Test("注册表边界: 非 Windows 平台安全退化(不读不写不炸)", () =>
        {
            // Windows 专用适配的隔离验证: 本套件只在 Linux 跑(Program 已守卫),
            // 这里钉住「注册表不可用时 ReadAll 退化为空、Write 退化为 false、绝不抛异常」——
            // 即测试与产品在任何非 Windows 环境都不可能触碰真实注册表。
            var store = new TokenStore();
            T.Equal(0, store.ReadAll().Count, "ReadAll 应退化为空字典");
            T.False(store.Write(SlotA, B(1, 2, 3)), "Write 应退化为 false");
        });
    }
}
