using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Sticky.App.Synchronization;
using Sticky.Storage;

namespace Sticky.App;

public enum SettingsSection { Sync, Data, Application }

public partial class SettingsWindow : Window
{
    private readonly StickyWorkspace _workspace;
    public string DataPath => _workspace.Store.Root;
    public string ApplicationVersion => "Sticky " + typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3);
    public string StorageMode => AppPaths.IsPortableBuild || File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag")) ? "便携模式 · 数据保存在程序目录旁" : "本地模式 · 数据保存在此设备";

    public SettingsWindow(StickyWorkspace workspace)
    {
        _workspace = workspace;
        InitializeComponent();
        DataContext = this;
        SyncHost.Content = new SyncSettingsView(workspace);
    }
    public void SelectSection(SettingsSection section) => SettingsTabs.SelectedIndex = (int)section;
    private void OpenDataFolder(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { DataPath }, UseShellExecute = true }); }
        catch (Exception ex) { StickyWorkspace.ShowError("无法打开数据目录", ex); }
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
