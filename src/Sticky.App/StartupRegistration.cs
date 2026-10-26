using System.IO;
using Microsoft.Win32;

namespace Sticky.App;

public interface IStartupEntry
{
    string? Read();
    void Write(string command);
    void Delete();
}

// The OS registration is local to this Windows user, not part of portable/synchronized data.
public sealed class StartupRegistration
{
    private readonly IStartupEntry _entry;
    private readonly string[] _launchFiles;
    public string Command { get; }
    public bool IsEnabled => string.Equals(_entry.Read(), Command, StringComparison.OrdinalIgnoreCase);
    public bool HasRegistration => !string.IsNullOrWhiteSpace(_entry.Read());

    public StartupRegistration(string processPath, string applicationDirectory, string dataDirectory, IStartupEntry? entry = null)
    {
        var executable = Path.GetFullPath(processPath);
        var data = Path.GetFullPath(dataDirectory);
        var hosted = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var assembly = Path.Combine(Path.GetFullPath(applicationDirectory), "Sticky.dll");
        _launchFiles = hosted ? [executable, assembly] : [executable];
        Command = string.Join(" ", _launchFiles.Select(Quote)) + " --data-dir " + Quote(data) + " --startup";
        _entry = entry ?? new CurrentUserRunEntry();
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (Command.Length > 260) throw new IOException("启动命令超过 Windows 的 260 字符限制，请将程序和数据放到较短的目录后重试。");
            if (_launchFiles.Any(path => !File.Exists(path))) throw new FileNotFoundException("程序文件已移动，请从新目录启动 Sticky 后再启用开机运行。");
            _entry.Write(Command);
        }
        // A copied/moved build must not silently delete another workspace's startup entry.
        else if (IsEnabled) _entry.Delete();
    }

    private static string Quote(string value)
    {
        if (value.IndexOfAny(['"', '\r', '\n', '\0']) >= 0) throw new ArgumentException("启动路径包含无效字符。");
        return "\"" + value + new string('\\', value.Reverse().TakeWhile(c => c == '\\').Count()) + "\"";
    }

    private sealed class CurrentUserRunEntry : IStartupEntry
    {
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Sticky";
        public string? Read()
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        public void Write(string command)
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        public void Delete()
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
