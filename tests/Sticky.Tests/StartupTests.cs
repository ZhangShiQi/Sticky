using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sticky.App;

internal static partial class Program
{
    private sealed class FakeStartupEntry : IStartupEntry
    {
        public string? Value;
        public int Writes;
        public bool Fail;
        public string? Read() => Value;
        public void Write(string command) { if (Fail) throw new UnauthorizedAccessException("Access denied for test."); Value = command; Writes++; }
        public void Delete() { if (Fail) throw new UnauthorizedAccessException("Access denied for test."); Value = null; }
    }

    private static void StartupSettings()
    {
        var root = Path.Combine(_root, "startup-settings"); var entry = new FakeStartupEntry();
        // Exercise settings against a portable executable, independent of the test host/build path.
        // Dotnet command quoting and Windows' command-length guard are covered separately below.
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "Sticky.exe"); File.WriteAllBytes(executable, []);
        var startup = new StartupRegistration(executable, root, root, entry);
        var workspace = new StickyWorkspace(root, startup);
        try
        {
            workspace.ShowSettings(SettingsSection.Application); Pump(40);
            var settings = workspace.Settings!;
            var toggle = (CheckBox)settings.FindName("StartupCheckBox");
            Check(toggle.IsChecked == false && entry.Writes == 0, "Opening settings enabled startup implicitly.");
            toggle.IsChecked = true;
            Check(entry.Value == startup.Command && new StartupRegistration(executable, root, root, entry).IsEnabled, "Startup registration did not persist.");
            entry.Fail = true; toggle.IsChecked = false;
            Check(toggle.IsChecked == true && startup.IsEnabled && ((TextBlock)settings.FindName("StartupStatus")).Text.Contains("Access denied"), "Failed disable left the toggle in a misleading state.");
            entry.Fail = false; toggle.IsChecked = false;
            Check(entry.Value is null && !startup.IsEnabled, "Disabling startup did not remove registration.");
            entry.Fail = true; toggle.IsChecked = true;
            Check(toggle.IsChecked == false && !startup.IsEnabled, "Failed enable was reported as enabled.");
            entry.Fail = false;
            entry.Value = "\"C:\\Other Sticky\\Sticky.exe\" --data-dir \"C:\\Other Data\" --startup";
            startup.SetEnabled(false);
            Check(entry.Value.Contains("Other Sticky"), "An unrelated workspace's registration was removed.");
            toggle.IsChecked = true;
            Check(entry.Value == startup.Command, "Enabling the current workspace did not replace the previous location.");
            toggle.IsChecked = false;
            settings.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)settings.ActualWidth, (int)settings.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(settings);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(_root, "settings-application.png")); encoder.Save(output);
            Check(settings.Icon is not null && Application.Current.Resources["StickyLogo"] is DrawingImage, "The window/vector application icon did not load.");
        }
        finally { workspace.PrepareExit(); }
    }

    private static void StartupCommands()
    {
        var folder = Path.Combine(_root, "startup paths 'quoted' 中文"); Directory.CreateDirectory(folder);
        var executable = Path.Combine(folder, "Sticky.exe"); File.WriteAllBytes(executable, []);
        var data = Path.Combine(folder, "My Notes");
        var entry = new FakeStartupEntry();
        var startup = new StartupRegistration(executable, folder, data, entry);
        startup.SetEnabled(true);
        Check(ParseWindowsCommand(entry.Value!).SequenceEqual(new[] { executable, "--data-dir", data, "--startup" }), "Portable startup arguments were split or lost the workspace.");
        var dotnet = Path.Combine(folder, "dotnet.exe"); File.WriteAllBytes(dotnet, []);
        var assembly = Path.Combine(folder, "Sticky.dll"); File.WriteAllBytes(assembly, []);
        var hosted = new StartupRegistration(dotnet, folder, data, entry);
        Check(ParseWindowsCommand(hosted.Command).SequenceEqual(new[] { dotnet, assembly, "--data-dir", data, "--startup" }), "Development startup omitted the application assembly.");
        var driveRoot = Path.GetPathRoot(folder)!;
        var rootCommand = new StartupRegistration(executable, folder, driveRoot, entry);
        Check(ParseWindowsCommand(rootCommand.Command)[2] == driveRoot, "Trailing backslash escaped the data directory's closing quote.");
        File.Delete(executable);
        try { startup.SetEnabled(true); throw new Exception("Missing startup executable was registered."); } catch (FileNotFoundException) { }
        File.WriteAllBytes(executable, []);
        var longCommand = new StartupRegistration(executable, folder, Path.Combine(data, new string('a', 240)), entry);
        var before = entry.Value;
        try { longCommand.SetEnabled(true); throw new Exception("An unsupported long Run command was registered."); } catch (IOException) { }
        Check(entry.Value == before, "Invalid registration replaced a healthy startup entry.");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
    private static string[] ParseWindowsCommand(string command)
    {
        var pointer = CommandLineToArgvW(command, out var count);
        if (pointer == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(pointer); }
    }
}
