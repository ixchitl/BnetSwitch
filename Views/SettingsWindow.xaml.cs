using System.Windows;
using System.Windows.Input;
using BnetSwitch.Services;
using BnetSwitch.Services.Overwatch;
using BnetSwitch.ViewModels;

namespace BnetSwitch;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _loading;

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        _loading = true;
        SwAutoStart.IsChecked = StartupService.IsEnabled();
        SwCloseToTray.IsChecked = vm.Settings.CloseToTray;
        SwKillAgent.IsChecked = vm.Settings.ForceKillAgentOnSwitch;
        SwDark.IsChecked = vm.Settings.DarkMode;
        PathBox.Text = string.IsNullOrWhiteSpace(vm.Settings.ClientExe) ? "(自动探测)" : vm.Settings.ClientExe;
        VerText.Text = $"当前版本 {vm.AppVersion}";
        _loading = false;
        RefreshCacheInfo();
    }

    // ===== 战绩缓存 =====
    private void RefreshCacheInfo()
    {
        CacheSize.Text = $"已用 {FormatSize(OwImageCache.CacheSizeBytes())}(英雄/地图/头像/技能图标 + 配置)";
        CacheDir.Text = OwImageCache.CacheRoot;
    }

    private static string FormatSize(long b)
        => b >= 1 << 20 ? $"{b / 1024.0 / 1024:0.0} MB" : b >= 1024 ? $"{b / 1024.0:0.0} KB" : $"{b} B";

    private void OnClearCache(object sender, RoutedEventArgs e)
    {
        long freed = OwImageCache.ClearCache();
        CacheSize.Text = $"已清除 {FormatSize(freed)},下次查战绩自动重新下载";
        CacheDir.Text = OwImageCache.CacheRoot;
    }

    private void OnOpenCacheDir(object sender, MouseButtonEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(OwImageCache.CacheRoot);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = OwImageCache.CacheRoot, UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnAutoStart(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        StartupService.SetEnabled(SwAutoStart.IsChecked == true);
    }

    private void OnCloseToTray(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _vm.Settings.CloseToTray = SwCloseToTray.IsChecked == true;
        _vm.Settings.CloseChoiceMade = true;   // 手动设过就不再弹首次二选一
        _vm.Settings.Save();
    }

    private void OnKillAgent(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _vm.Settings.ForceKillAgentOnSwitch = SwKillAgent.IsChecked == true;
        _vm.Settings.Save();
    }

    private void OnDark(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var dark = SwDark.IsChecked == true;
        _vm.Settings.DarkMode = dark;
        _vm.Settings.Save();
        ThemeManager.Apply(dark);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        _vm.SetExePath();
        PathBox.Text = string.IsNullOrWhiteSpace(_vm.Settings.ClientExe) ? "(自动探测)" : _vm.Settings.ClientExe;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
