using System.Windows;
using System.Windows.Input;

namespace BnetSwitch;

public partial class ContactWindow : Window
{
    private readonly string _qqUrl;
    private readonly string _githubUrl;

    public ContactWindow(string qqUrl, string githubUrl)
    {
        InitializeComponent();
        _qqUrl = qqUrl;
        _githubUrl = githubUrl;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnQQ(object sender, MouseButtonEventArgs e) => BnetSwitch.Services.LinkOpener.Open(_qqUrl);
    private void OnGithub(object sender, MouseButtonEventArgs e) => BnetSwitch.Services.LinkOpener.Open(_githubUrl);

    /// <summary>把日志和状态打成 zip 放桌面,并在资源管理器里选中它 —— 用户接着拖进群里就行。</summary>
    private void OnExportDiag(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var ver = System.Reflection.Assembly.GetExecutingAssembly()
                          .GetName().Version?.ToString() ?? "?";
            var zip = BnetSwitch.Services.DiagnosticExport.Create(ver);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{zip}\"",
                UseShellExecute = true,
            });
            MessageBox.Show(
                "诊断包已生成:\n" + zip +
                "\n\n把这个文件发给开发者即可。里面只有运行日志和账号的区服/快照状态," +
                "不包含密码,也不包含免密登录凭据。",
                "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出失败:" + ex.Message, "导出诊断包",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
