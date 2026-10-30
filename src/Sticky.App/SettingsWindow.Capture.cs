using System.IO;
using System.Windows;
using System.Windows.Controls;
using Sticky.Capture;

namespace Sticky.App;

public partial class SettingsWindow
{
    private CaptureSettingsView _captureSettings = null!;
    private readonly List<Sticky.Capture.AiSettingsWindow> _captureAiEditors = [];
    private Sticky.Capture.TranslationSettingsWindow? _translationEditor;
    public bool CanStartCapture => !IsVisible || (SettingsTabs.SelectedItem != AiTab && SettingsTabs.SelectedItem != TranslationTab);
    private void InitializeCaptureSettings()
    {
        _captureSettings = new CaptureSettingsView(_workspace); CaptureHost.Content = _captureSettings;
        _translationEditor = new Sticky.Capture.TranslationSettingsWindow();
        TranslationHost.Content = _translationEditor.CreateEmbeddedView();
        var text = new Sticky.Capture.AiSettingsWindow(false); var vision = new Sticky.Capture.AiSettingsWindow(true);
        _captureAiEditors.Add(text); _captureAiEditors.Add(vision);
        CaptureTextAiHost.Content = text.CreateEmbeddedView(); CaptureVisionAiHost.Content = vision.CreateEmbeddedView();
        Closed += (_, _) => { foreach (var editor in _captureAiEditors) editor.ReleaseEmbeddedView(); _translationEditor.ReleaseEmbeddedView(); };
        CutImportPath.Text = CaptureSettingsImport.DefaultDirectory;
    }
    private void ImportCutSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ReplaceCutSettings.IsChecked == true && MessageBox.Show(this, "用 cut 配置替换已有的截图配置？便签 AI 配置保持不变。", "导入 cut 设置", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            var result = CaptureSettingsImport.Import(CutImportPath.Text.Trim(), ReplaceCutSettings.IsChecked == true, _workspace.Capture.SavePreferences);
            CutImportStatus.Text = string.Join("\n", result) + "\n原 cut 文件和启动项保持不变，设置页已刷新。";
            _captureSettings.Reload();
            foreach (var editor in _captureAiEditors) editor.ReloadSavedSettings();
            _translationEditor?.ReloadSavedSettings();
        }
        catch (Exception ex) { CutImportStatus.Text = "导入未完成：" + ex.Message; }
    }
}
