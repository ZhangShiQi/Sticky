using System.Windows;
using System.Windows.Controls;

namespace Sticky.App.Synchronization;

public partial class SyncSettingsView : UserControl
{
    private readonly SyncCoordinator _sync;
    public SyncSettingsView(StickyWorkspace workspace)
    {
        _sync = workspace.Sync;
        InitializeComponent();
        DataContext = _sync;
        EnabledBox.IsChecked = _sync.Settings.Enabled;
        RemoteBox.Text = _sync.Settings.RemoteUrl;
        BranchBox.Text = _sync.Settings.Branch;
    }
    private bool SaveForm()
    {
        try
        {
            _sync.SaveSettings(RemoteBox.Text, BranchBox.Text, EnabledBox.IsChecked == true);
            FormError.Text = ""; RemoteBox.Text = _sync.Settings.RemoteUrl; BranchBox.Text = _sync.Settings.Branch;
            return true;
        }
        catch (Exception ex) { FormError.Text = ex.Message; return false; }
    }
    private void SaveConfig(object sender, RoutedEventArgs e) => SaveForm();
    private async void SyncNow(object sender, RoutedEventArgs e)
    {
        if (!SaveForm()) return;
        if (!_sync.Settings.Enabled) { FormError.Text = "请先勾选启用 GitHub 同步。"; return; }
        await _sync.RunAsync(interactive: true);
    }
    private void CancelSync(object sender, RoutedEventArgs e) => _sync.Cancel();
}
