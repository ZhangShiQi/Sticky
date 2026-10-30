using System;
using System.IO;
using System.Text.Json;
using Sticky.Capture.Services;

namespace Sticky.Capture;

public sealed record CapturePreferences
{
    public int Version { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    public uint Modifiers { get; init; } = 3;
    public uint Key { get; init; } = 65;
    public int RecordingFps { get; init; } = 30;
    public bool RecordingCursor { get; init; } = true;
    public bool RecordingCountdown { get; init; } = true;
    public bool UseStickyTextConnection { get; init; }
    public Guid? StickyTextConnectionId { get; init; }
    public void Validate()
    {
        if (Version != 1 || Modifiers is 0 or > 7 || Key is < 65 or > 90 || !Array.Exists(new[] { 5, 10, 15, 30, 60 }, f => f == RecordingFps))
            throw new InvalidDataException("截图配置格式、快捷键或录制帧率无效。");
    }
    public static CapturePreferences Load()
    {
        var path = Path.Combine(CaptureEnvironment.DataDirectory, "preferences.json");
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("截图配置文件过大。");
        var value = JsonSerializer.Deserialize<CapturePreferences>(File.ReadAllText(path)) ?? throw new InvalidDataException("截图配置为空。");
        value.Validate(); return value;
    }
    public void Save() { Validate(); CaptureConfiguration.Write("preferences.json", JsonSerializer.Serialize(this)); }
    public override string ToString() => new HotkeySettings(Modifiers, Key).ToString();
}

public static class CaptureConfiguration
{
    internal static void Write(string name, string content)
    {
        Directory.CreateDirectory(CaptureEnvironment.DataDirectory);
        var path = Path.Combine(CaptureEnvironment.DataDirectory, name);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { using var writer = new StreamWriter(stream, leaveOpen: true); writer.Write(content); writer.Flush(); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static AiSettings LoadTextSettings() => CapturePreferences.Load().UseStickyTextConnection
        ? CaptureEnvironment.SharedTextSettings?.Invoke() ?? throw new AiException("Sticky 文字 AI 连接尚未就绪。") : new AiSettingsStore().Load();
}
