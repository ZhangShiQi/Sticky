using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;

namespace Sticky.Capture;

public enum CaptureSettingsSection { Capture, Translation, TextAi, VisionAi }

// The host owns configuration location and navigation; capture has no application singleton.
public static class CaptureEnvironment
{
    public static string DataDirectory { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sticky", "device", "capture");
    public static Action<CaptureSettingsSection, Window?>? OpenSettings { get; set; }
    public static Func<Services.AiSettings>? SharedTextSettings { get; set; }
    public static void Configure(string directory) => DataDirectory = Path.GetFullPath(directory);
    public static void ApplyStyles(Window window) => window.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Sticky.Capture;component/CaptureResources.xaml") });
    public static bool IsToolWindow(IntPtr handle) => Application.Current is not null && Application.Current.Windows.OfType<Window>()
        .Any(w => w.GetType().Assembly == typeof(CaptureSession).Assembly && new WindowInteropHelper(w).Handle == handle);
    public static bool IsEditingCredentials => Application.Current is not null && Application.Current.Windows.OfType<Window>()
        .Any(w => w.IsVisible && w is AiSettingsWindow or TranslationSettingsWindow);
}
