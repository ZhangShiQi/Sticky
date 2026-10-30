using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sticky.App;
using Sticky.Capture;
using Sticky.Capture.Services;

internal static partial class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr handle);

    private static void CaptureDialogInteraction()
    {
        var target = new Window { Width = 260, Height = 160 };
        var controller = new Window { Width = 260, Height = 160 };
        target.Show(); controller.Show();
        var preview = new Window { Owner = controller, Width = 200, Height = 120 };
        Exception? error = null;
        preview.Loaded += (_, _) =>
        {
            try
            {
                Check(IsWindowEnabled(new System.Windows.Interop.WindowInteropHelper(target).Handle), "Screenshot tools disabled the Sticky target window.");
                Check(!controller.IsEnabled, "The tool owner remained editable while its child was open.");
            }
            catch (Exception ex) { error = ex; }
            finally { preview.Dispatcher.BeginInvoke(new Action(preview.Close)); }
        };
        try
        {
            CaptureDialogHost.Show(preview);
            if (error is not null) throw error;
            Check(controller.IsEnabled, "Closing a tool did not restore its owner.");
        }
        finally { preview.Close(); controller.Close(); target.Close(); }
    }
    private static void CaptureSettingsIntegration()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "capture-settings"));
        var editorsBefore = Application.Current.Windows.OfType<AiSettingsWindow>().Count();
        try
        {
            workspace.ShowSettings(SettingsSection.Capture); Pump(30);
            var settings = workspace.Settings!;
            var tabs = (TabControl)settings.FindName("SettingsTabs");
            Check(ReferenceEquals(tabs.SelectedItem, settings.FindName("CaptureTab")), "Capture navigation selected the wrong tab.");
            Check(settings.CanStartCapture, "Capture settings blocked the screenshot entry.");
            var view = (CaptureSettingsView)((ContentControl)settings.FindName("CaptureHost")).Content;
            Check(((ComboBox)view.FindName("FpsChoice")).Items.Count == 5, "Recording frame rates were lost.");
            ((ComboBox)view.FindName("FpsChoice")).SelectedItem = 15;
            ((CheckBox)view.FindName("EnabledCheck")).IsChecked = false;
            var save = FindVisual<Button>(view).Single(b => Equals(b.Content, "保存并应用"));
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(workspace.Capture.Preferences.RecordingFps == 15 && !CapturePreferences.Load().Enabled, "Capture preferences did not persist.");
            foreach (var section in new[] { CaptureSettingsSection.Capture, CaptureSettingsSection.Translation, CaptureSettingsSection.TextAi, CaptureSettingsSection.VisionAi })
            {
                settings.SelectCaptureSection(section); Pump(20); settings.UpdateLayout();
                Check(settings.CanStartCapture == (section == CaptureSettingsSection.Capture), "Credential screenshot guard is incorrect.");
                var bitmap = new RenderTargetBitmap((int)settings.ActualWidth, (int)settings.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(settings); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(_root, "settings-capture-" + section + ".png")); encoder.Save(output);
            }
            settings.Close();
            Check(Application.Current.Windows.OfType<AiSettingsWindow>().Count() == editorsBefore, "Embedded editors leaked after settings closed.");
        }
        finally { workspace.PrepareExit(); }
    }

    private static void CaptureImportIntegration()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "capture-import"));
        var legacy = Path.Combine(_root, "legacy-cut"); Directory.CreateDirectory(legacy);
        try
        {
            const string fakeKey = "synthetic-capture-key";
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"Modifiers\":6,\"Key\":66}");
            new AiSettingsStore(Path.Combine(legacy, "ai.json")).Save(new AiSettings { Endpoint = "http://localhost:1234/v1/chat/completions", Model = "text-fixture", ApiKey = fakeKey });
            new AiSettingsStore(Path.Combine(legacy, "vision.json")).Save(new AiSettings { Model = "vision-fixture", UseTextService = true });
            new TranslationSettingsStore(Path.Combine(legacy, "translation.json")).Save(new TranslationSettings { ApiKey = fakeKey });
            var original = File.ReadAllBytes(Path.Combine(legacy, "ai.json"));
            var report = CaptureSettingsImport.Import(legacy, false, workspace.Capture.SavePreferences);
            Check(report.All(r => r.Contains("已导入")), "A legacy profile was not imported: " + string.Join(";", report));
            Check(workspace.Capture.Preferences.Modifiers == 6 && workspace.Capture.Preferences.Key == 66, "Legacy hotkey was not imported.");
            Check(new AiSettingsStore().Load().ApiKey == fakeKey && AiSettingsStore.Vision().Load().UseTextService, "Credential decryption or vision inheritance was lost.");
            Check(!File.ReadAllText(Path.Combine(CaptureEnvironment.DataDirectory, "ai.json")).Contains(fakeKey), "Imported key was written in plaintext.");
            Check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(legacy, "ai.json"))), "Import modified the source profile.");
            Check(CaptureSettingsImport.Import(legacy, false, workspace.Capture.SavePreferences).All(r => r.Contains("保留")), "Repeated import overwrote existing profiles.");
            File.WriteAllText(Path.Combine(legacy, "ai.json"), "corrupt");
            var partial = CaptureSettingsImport.Import(legacy, true, workspace.Capture.SavePreferences);
            Check(partial.Any(r => r.StartsWith("ai.json：未导入")) && new AiSettingsStore().Load().ApiKey == fakeKey, "Invalid import damaged a working profile.");
            var previous = File.ReadAllBytes(Path.Combine(CaptureEnvironment.DataDirectory, "preferences.json"));
            try { workspace.Capture.SavePreferences(workspace.Capture.Preferences with { Modifiers = 0 }); throw new Exception("Invalid hotkey accepted."); } catch (InvalidDataException) { }
            Check(previous.SequenceEqual(File.ReadAllBytes(Path.Combine(CaptureEnvironment.DataDirectory, "preferences.json"))), "Invalid preference save replaced the good file.");
        }
        finally { workspace.PrepareExit(); }
    }

    private static IEnumerable<T> FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisual<T>(child)) yield return descendant;
        }
    }

    private static void CaptureSessionIntegration()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "capture-session"));
        var host = new Window { Width = 300, Height = 180, Content = new TextBlock { Text = "Synthetic Sticky capture host" } };
        host.Show();
        try
        {
            var snapshot = new DesktopSnapshot(new[] { new MonitorSnapshot(new Sticky.Capture.Core.PixelRect(0, 0, 600, 400),
                BitmapSource.Create(600, 400, 96, 96, PixelFormats.Bgr32, null, new byte[600 * 400 * 4], 600 * 4)) });
            workspace.Capture.AcquireSnapshot = () => snapshot;
            workspace.Capture.AcquireTargets = () => [];
            var start = workspace.Capture.StartCaptureAsync(host);
            for (var i = 0; !start.IsCompleted && i < 100; i++) Pump(20);
            start.GetAwaiter().GetResult();
            Check(workspace.Capture.IsActive && !host.IsVisible, "Capture did not hide the initiating host.");
            var count = Application.Current.Windows.OfType<OverlayWindow>().Count();
            workspace.Capture.StartCaptureAsync(host).GetAwaiter().GetResult();
            Check(count == Application.Current.Windows.OfType<OverlayWindow>().Count(), "Capture allowed concurrent overlays.");
            Check(workspace.Capture.TryPrepareExit(false) && !workspace.Capture.IsActive && host.IsVisible, "Cancelling capture did not restore the host.");
        }
        finally { workspace.Capture.Dispose(); host.Close(); workspace.PrepareExit(); }
    }
}
