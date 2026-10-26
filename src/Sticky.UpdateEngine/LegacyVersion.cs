using System.Text.Json;

namespace Sticky.UpdateEngine;

// One-time bridge from 0.2.2's in-place updater. Its retained backup is the previously
// running program; it does not speak the new startup protocol and is never a new trial.
public static class LegacyVersion
{
    public static string Validate(string folder)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(DurableFiles.Child(folder, "release-manifest.json")));
        var m = doc.RootElement;
        var version = m.GetProperty("version").GetString()!; VersionLayout.ParseVersion(version);
        if (m.GetProperty("application").GetString() != "Sticky" || m.GetProperty("runtimeIdentifier").GetString() != "win-x64"
            || !m.GetProperty("selfContained").GetBoolean() || !m.GetProperty("singleFile").GetBoolean()
            || (m.TryGetProperty("formatVersion", out var format) && format.GetInt32() >= 2)
            || DurableFiles.Hash(DurableFiles.Child(folder, "Sticky.exe")) != m.GetProperty("executableSha256").GetString())
            throw new InvalidDataException("旧程序备份无效。");
        return version;
    }
    public static string? Find(string root)
    {
        foreach (var folder in Directory.EnumerateDirectories(root, ".sticky-update-backup-*").OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            var id = "legacy-" + Path.GetFileName(folder)[".sticky-update-backup-".Length..];
            try { Validate(VersionLayout.Slot(root, id)); return id; }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException) { }
        }
        return null;
    }
}
