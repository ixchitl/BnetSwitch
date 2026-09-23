namespace BnetSwitch.Tests;

public static class Program
{
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine(
                "本套件只能在 WSL/Linux 上运行: 隔离依赖 XDG/TMPDIR 目录重定向(同 validation/*.sh 的约定)。" +
                " Windows 专用适配(注册表/进程/WPF)需在 Windows 实机单独验证,见 CONTRIBUTING.md。");
            return 2;
        }

        // 供 RunnerContractTests 子进程调用: 跑一个故意失败的用例,验证「失败 → 非零退出码」契约
        if (args.Contains("--selfcheck-fail"))
        {
            Sandbox.Install();
            try
            {
                T.Test("自检: 故意失败的用例", () => throw new InvalidOperationException("故意失败"));
            }
            finally
            {
                Sandbox.Uninstall();
            }
            return T.Summarize();
        }

        Console.WriteLine("BnetSwitch 核心回归测试: 真实产品逻辑 + 临时沙箱 + 合成数据");
        Console.WriteLine("(不触碰个人 AppData / 注册表 / 真实 Battle.net 进程;仅 WSL/Linux)");
        Sandbox.Install();
        try
        {
            TokenSlotTests.Run();
            AccountResolveTests.Run();
            SnapshotRestoreTests.Run();
            SwitchTransactionTests.Run();
            RunnerContractTests.Run();
            T.Test("无副作用: 个人数据目录运行前后完好", Sandbox.VerifyNoPersonalSideEffects);
        }
        finally
        {
            Sandbox.Uninstall();
        }
        return T.Summarize();
    }
}
