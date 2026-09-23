using System.Diagnostics;
using BnetSwitch.Services;

namespace BnetSwitch.Tests;

/// <summary>
/// 运行器自身的契约:「任一用例失败 → 进程非零退出」不是口头承诺,
/// 用子进程真跑一遍 --selfcheck-fail 验证;沙箱重定向的有效性也在这里显式钉住。
/// </summary>
public static class RunnerContractTests
{
    public static void Run()
    {
        T.Test("运行器契约: 存在失败用例时进程非零退出", () =>
        {
            var exe = Environment.ProcessPath
                      ?? throw new Exception("无法定位自身可执行文件,不能自检退出码");
            var psi = new ProcessStartInfo(exe, "--selfcheck-fail")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi) ?? throw new Exception("自检子进程启动失败");
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            T.Equal(1, p.ExitCode, "故意失败的子进程退出码");
            T.True(stdout.Contains("FAIL"), "子进程输出应包含 FAIL 标记");
        });

        T.Test("运行器契约: 产品路径全部落在沙箱内", () =>
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            T.True(local.StartsWith(Sandbox.Root), $"LocalApplicationData 应在沙箱内: {local}");

            var paths = new BattleNetPaths();
            T.True(paths.LocalRoot.StartsWith(Sandbox.Root), $"LocalRoot 应在沙箱内: {paths.LocalRoot}");
            T.True(paths.RoamingDir.StartsWith(Sandbox.Root), $"RoamingDir 应在沙箱内: {paths.RoamingDir}");
            T.False(paths.Exists, "沙箱内不应凭空存在战网数据");
            T.Null(paths.ClientExe, "沙箱内不应解析出 Battle.net.exe");
        });
    }
}
