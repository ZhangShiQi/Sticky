using System.IO;
using System.Windows;
using System.Windows.Controls;
using Sticky.Capture;

namespace Sticky.App;

public partial class CaptureSettingsView : UserControl
{
    private readonly StickyWorkspace workspace;
    public CaptureSettingsView(StickyWorkspace workspace)
    {
        this.workspace = workspace; InitializeComponent();
        foreach (char key in Enumerable.Range('A', 26).Select(i => (char)i)) KeyChoice.Items.Add(key.ToString());
        foreach (var fps in new[] { 5, 10, 15, 30, 60 }) FpsChoice.Items.Add(fps);
        Reload();
        ComponentStatus.Text = "离线 OCR：" + (File.Exists(Path.Combine(AppContext.BaseDirectory, "Models", "ch_PP-OCRv5_rec_mobile.onnx")) ? "已内置" : "模型缺失，请重新解压完整包")
            + "\n录制编码器：" + (File.Exists(Path.Combine(AppContext.BaseDirectory, "MediaTools", "ffmpeg", "ffmpeg.exe")) ? "已内置" : "组件缺失，请重新解压完整包");
        Loaded += (_, _) => { workspace.Capture.Changed += RefreshStatus; RefreshStatus(); };
        Unloaded += (_, _) => workspace.Capture.Changed -= RefreshStatus;
    }
    internal void Reload()
    {
        var value = workspace.Capture.Preferences;
        EnabledCheck.IsChecked = value.Enabled; CtrlCheck.IsChecked = (value.Modifiers & 2) != 0;
        AltCheck.IsChecked = (value.Modifiers & 1) != 0; ShiftCheck.IsChecked = (value.Modifiers & 4) != 0;
        KeyChoice.SelectedItem = ((char)value.Key).ToString(); FpsChoice.SelectedItem = value.RecordingFps;
        CursorCheck.IsChecked = value.RecordingCursor; CountdownCheck.IsChecked = value.RecordingCountdown;
        ReuseAiCheck.IsChecked = value.UseStickyTextConnection;
        AiConnectionChoice.ItemsSource = workspace.AiSettings.EditingCatalog().Connections.Where(c => c.DeletedAt is null).ToList();
        AiConnectionChoice.SelectedValue = value.StickyTextConnectionId;
    }
    private void RefreshStatus() => HotkeyStatus.Text = workspace.Capture.Status;
    private async void StartCapture(object sender, RoutedEventArgs e) => await workspace.Capture.StartCaptureAsync(Window.GetWindow(this));
    private void SavePreferences(object sender, RoutedEventArgs e)
    {
        try
        {
            var value = workspace.Capture.Preferences with {
                Enabled = EnabledCheck.IsChecked == true,
                Modifiers = (CtrlCheck.IsChecked == true ? 2u : 0) | (AltCheck.IsChecked == true ? 1u : 0) | (ShiftCheck.IsChecked == true ? 4u : 0),
                Key = KeyChoice.SelectedItem is string key ? key[0] : 65u, RecordingFps = (int)(FpsChoice.SelectedItem ?? 30),
                RecordingCursor = CursorCheck.IsChecked == true, RecordingCountdown = CountdownCheck.IsChecked == true,
                UseStickyTextConnection = ReuseAiCheck.IsChecked == true, StickyTextConnectionId = AiConnectionChoice.SelectedValue as Guid? };
            if (value.UseStickyTextConnection && value.StickyTextConnectionId is null) throw new InvalidOperationException("请先选择一个便签 AI 连接。");
            workspace.Capture.SavePreferences(value); StatusText.Text = "设置已保存并应用。";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
}
