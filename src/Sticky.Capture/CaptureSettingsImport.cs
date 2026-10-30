using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Sticky.Capture.Services;

namespace Sticky.Capture;

public static class CaptureSettingsImport
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cut");
    public static IReadOnlyList<string> Import(string sourceDirectory, bool replaceExisting, Action<CapturePreferences> savePreferences)
    {
        var source = Path.GetFullPath(sourceDirectory);
        if (source.Equals(Path.GetFullPath(CaptureEnvironment.DataDirectory), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择旧 cut 配置目录。");
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("未找到 cut 配置目录。");
        var report = new List<string>();
        foreach (var name in new[] { "settings.json", "ai.json", "vision.json", "translation.json" })
        {
            var path = Path.Combine(source, name);
            var target = Path.Combine(CaptureEnvironment.DataDirectory, name == "settings.json" ? "preferences.json" : name);
            if (!File.Exists(path)) { report.Add(name + "：未找到，跳过"); continue; }
            if (!replaceExisting && File.Exists(target)) { report.Add(name + "：保留 Sticky 已有配置"); continue; }
            try
            {
                if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("配置文件过大。");
                switch (name)
                {
                    case "settings.json":
                        var hotkey = JsonSerializer.Deserialize<HotkeySettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("配置为空。");
                        savePreferences(CapturePreferences.Load() with { Modifiers = hotkey.Modifiers, Key = hotkey.Key }); break;
                    case "ai.json": case "vision.json":
                        var ai = new AiSettingsStore(path).Load();
                        new AiSettingsStore(target).Save(ai); break;
                    case "translation.json": new TranslationSettingsStore(target).Save(new TranslationSettingsStore(path).Load()); break;
                }
                report.Add(name + "：已导入");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or AiException or TranslationException)
            { report.Add(name + "：未导入，" + ex.Message); }
        }
        CaptureConfiguration.Write("migration.json", JsonSerializer.Serialize(new { version = 1, importedAt = DateTimeOffset.UtcNow, report }));
        return report;
    }
}
