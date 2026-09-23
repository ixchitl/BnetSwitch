namespace BnetSwitch.Tests;

/// <summary>
/// 最小测试运行器:登记用例、汇总结果。任一用例失败 → <see cref="Summarize"/> 返回 1,
/// `./build.sh test` 随之非零退出(该契约本身有用例覆盖,见 RunnerContractTests)。
/// </summary>
public static class T
{
    private static int _pass, _fail;
    private static readonly List<string> _failures = new();

    /// <summary>跑一个用例:先换全新临时沙箱再执行,任何异常记为失败并继续后面的用例。</summary>
    public static void Test(string name, Action body)
    {
        Sandbox.Reset();
        try
        {
            body();
            Sandbox.CheckNoTempLeftovers();
            _pass++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception ex)
        {
            _fail++;
            _failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"FAIL {name}: {ex.Message}");
        }
    }

    public static int Summarize()
    {
        Console.WriteLine();
        Console.WriteLine($"结果: 通过 {_pass}, 失败 {_fail}, 共 {_pass + _fail}");
        foreach (var f in _failures)
            Console.WriteLine($"  失败 → {f}");
        return _fail == 0 ? 0 : 1;
    }

    // ===== 断言:最小集合,失败信息要能直接定位 =====

    public static void True(bool cond, string what)
    {
        if (!cond) throw new Exception(what);
    }

    public static void False(bool cond, string what) => True(!cond, what);

    public static void Equal(string? expected, string? actual, string what) =>
        True(expected == actual, $"{what}: 期望 '{expected ?? "(null)"}', 实际 '{actual ?? "(null)"}'");

    public static void Equal(long expected, long actual, string what) =>
        True(expected == actual, $"{what}: 期望 {expected}, 实际 {actual}");

    public static void Equal(long? expected, long? actual, string what) =>
        True(expected == actual, $"{what}: 期望 {expected?.ToString() ?? "null"}, 实际 {actual?.ToString() ?? "null"}");

    public static void EqualBytes(byte[]? expected, byte[]? actual, string what) =>
        True(expected is not null && actual is not null && expected.SequenceEqual(actual), what);

    public static void Null(object? v, string what) => True(v is null, what);

    public static void NotNull(object? v, string what) => True(v is not null, what);

    public static TEx Throws<TEx>(Action action, string what) where TEx : Exception
    {
        try { action(); }
        catch (TEx ex) { return ex; }
        catch (Exception ex)
        {
            throw new Exception($"{what}: 期望 {typeof(TEx).Name}, 实际 {ex.GetType().Name}({ex.Message})");
        }
        throw new Exception($"{what}: 期望 {typeof(TEx).Name}, 但没有抛异常");
    }
}
