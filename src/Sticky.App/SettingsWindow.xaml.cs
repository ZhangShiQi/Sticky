using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using Sticky.App.Synchronization;
using Sticky.Storage;
using Sticky.Core;

namespace Sticky.App;

public enum SettingsSection { Sync, Data, Typography, Application }

public partial class SettingsWindow : Window
{
    private readonly StickyWorkspace _workspace;
    private bool _updatingStartup = true;
    public string DataPath => _workspace.Store.Root;
    public string BackupsPath => _workspace.Store.BackupsDirectory;
    public string ApplicationVersion => "Sticky " + typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3);
    public string StorageMode => AppPaths.IsPortableBuild || File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag")) ? "便携模式 · 数据保存在程序目录旁" : "本地模式 · 数据保存在此设备";

    public SettingsWindow(StickyWorkspace workspace)
    {
        _workspace = workspace;
        InitializeComponent();
        DataContext = this;
        SyncHost.Content = new SyncSettingsView(workspace);
        LoadTypographyFields(workspace.EditorPreferences);
        RefreshStartup();
    }
    public void SelectSection(SettingsSection section)
    {
        SettingsTabs.SelectedIndex = (int)section;
        if (section == SettingsSection.Application) RefreshStartup();
    }
    private void RefreshStartup()
    {
        _updatingStartup = true;
        try
        {
            StartupCheckBox.IsChecked = _workspace.Startup.IsEnabled;
            StartupStatus.Foreground = System.Windows.Media.Brushes.DarkGreen;
            StartupStatus.Text = StartupCheckBox.IsChecked == true ? "已启用当前工作空间的开机自动运行。"
                : _workspace.Startup.HasRegistration ? "已有其他位置的 Sticky 启动项。勾选后会切换到当前程序和工作空间。" : "未启用开机自动运行。";
        }
        catch (Exception ex)
        {
            StartupStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
            StartupStatus.Text = "无法读取启动设置：" + ex.Message;
        }
        finally { _updatingStartup = false; }
    }
    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingStartup) return;
        var enabled = StartupCheckBox.IsChecked == true;
        try
        {
            _workspace.Startup.SetEnabled(enabled);
            StartupStatus.Foreground = System.Windows.Media.Brushes.DarkGreen;
            StartupStatus.Text = enabled ? "已启用，下次登录 Windows 时自动运行。" : "已关闭开机自动运行。";
        }
        catch (Exception ex)
        {
            _updatingStartup = true;
            try { StartupCheckBox.IsChecked = !enabled; }
            finally { _updatingStartup = false; }
            StartupStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
            StartupStatus.Text = "未能更改启动设置：" + ex.Message;
        }
    }
    private void OpenDataFolder(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { DataPath }, UseShellExecute = true }); }
        catch (Exception ex) { StickyWorkspace.ShowError("无法打开数据目录", ex); }
    }
    private void OpenBackupsFolder(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { BackupsPath }, UseShellExecute = true }); }
        catch (Exception ex) { StickyWorkspace.ShowError("无法打开备份目录", ex); }
    }
    private void LoadTypographyFields(EditorPreferences preferences)
    {
        DefaultFontSizeBox.Text = preferences.DefaultFontSize.ToString("0.##", CultureInfo.CurrentCulture);
        ParagraphSpacingBox.Text = preferences.ParagraphSpacing.ToString("0.##", CultureInfo.CurrentCulture);
    }
    private void SaveTypography(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!double.TryParse(DefaultFontSizeBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var fontSize)
                || !double.TryParse(ParagraphSpacingBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var spacing))
                throw new InvalidDataException("请输入有效的字号和段落间距数值。");
            if (!_workspace.UpdateEditorPreferences(new EditorPreferences { DefaultFontSize = fontSize, ParagraphSpacing = spacing }))
                throw new IOException("存在未保存成功的便签，请先处理保存问题后重试。");
            TypographyStatus.Foreground = System.Windows.Media.Brushes.DarkGreen;
            TypographyStatus.Text = "已保存并应用到全部便签。";
        }
        catch (Exception ex)
        {
            TypographyStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
            TypographyStatus.Text = "未能应用：" + ex.Message;
        }
    }
    private void ResetTypography(object sender, RoutedEventArgs e)
    {
        LoadTypographyFields(new EditorPreferences());
        SaveTypography(sender, e);
    }
    private void ExportBackup(object sender, RoutedEventArgs e)
    {
        if (!_workspace.SaveAllNotes()) return;
        var dialog = new SaveFileDialog { Filter = "Sticky 备份 (*.zip)|*.zip", FileName = $"Sticky-{DateTime.Now:yyyyMMdd-HHmm}.zip" };
        if (dialog.ShowDialog(this) != true) return;
        try { BackupService.Export(_workspace.Store, dialog.FileName); DataStatus.Text = "备份已导出，包含全部便签、分组、回收站和图片。"; }
        catch (Exception ex) { StickyWorkspace.ShowError("导出失败", ex); }
    }
    private void ImportBackup(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Sticky 备份 (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var notes = BackupService.Import(_workspace.Store, dialog.FileName);
            _workspace.Groups.Clear(); _workspace.Groups.AddRange(_workspace.Store.LoadGroups());
            _workspace.Notes.AddRange(notes); _workspace.NotifyChanged();
            DataStatus.Text = $"已导入 {notes.Count} 张便签，相同 ID 的便签作为新副本保留。";
        }
        catch (Exception ex) { StickyWorkspace.ShowError("导入失败", ex); }
    }
    private void OpenManager(object sender, RoutedEventArgs e) => _workspace.ShowManager();
    private void ExitApplication(object sender, RoutedEventArgs e) { if (_workspace.PrepareExit()) System.Windows.Application.Current.Shutdown(); }
}
