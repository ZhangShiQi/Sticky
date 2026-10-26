using System.Text.Json;
using System.Text.RegularExpressions;
using Sticky.App.Updates;

namespace Sticky.UpdateEngine;

public static class VersionLayout
{
    public const int Protocol = 1;
    public const int DataSchema = 1;
    public const string Runtime = "tools/runtime/Sticky.exe";
    public static Version ParseVersion(string value)
    {
        if (!Regex.IsMatch(value, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant)
            || !Version.TryParse(value, out var version)) throw new InvalidDataException("无效的正式版本号。");
        return version;
    }
    public static string Slot(string root, string id)
    {
        if (id == "bootstrap") return Path.GetFullPath(root);
        if (Regex.IsMatch(id, @"^legacy-[a-f0-9]{32}$")) return DurableFiles.Child(root, ".sticky-update-backup-" + id[7..]);
        if (!Regex.IsMatch(id, @"^[0-9]+\.[0-9]+\.[0-9]+-[a-f0-9]{32}$")) throw new InvalidDataException("无效的版本目录。");
        return DurableFiles.Child(root, "versions/" + id);
    }
    public static string Validate(string directory, string? expectedVersion = null)
    {
        UpdatePackage.RejectReparseAncestors(directory);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "release-manifest.json")));
        var m = doc.RootElement;
        var version = m.GetProperty("version").GetString()!;
        ParseVersion(version);
        if (expectedVersion is not null && version != expectedVersion) throw new InvalidDataException("版本目录与发布清单不匹配。");
        if (m.GetProperty("application").GetString() != "Sticky" || m.GetProperty("runtimeIdentifier").GetString() != "win-x64"
            || !m.GetProperty("selfContained").GetBoolean() || !m.GetProperty("singleFile").GetBoolean()
            || m.GetProperty("formatVersion").GetInt32() != 2 || m.GetProperty("launcherProtocol").GetInt32() != Protocol
            || m.GetProperty("dataSchemaVersion").GetInt32() != DataSchema || m.GetProperty("runtimePath").GetString() != Runtime)
            throw new InvalidDataException("更新包与稳定启动器或数据格式不兼容，请保留 Data 并手动升级启动器。");
        var files = m.GetProperty("files");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.EnumerateObject())
        {
            if (!seen.Add(file.Name) || file.Name == "release-manifest.json"
                || !UpdatePackage.ManagedEntries.Contains(file.Name.Split('/')[0], StringComparer.Ordinal)) throw new InvalidDataException("无效的程序文件清单。");
            var expected = file.Value.GetString();
            if (expected is null || !Regex.IsMatch(expected, "^[a-f0-9]{64}$")
                || DurableFiles.Hash(DurableFiles.Child(directory, file.Name)) != expected) throw new InvalidDataException("程序组件校验失败：" + file.Name);
        }
        foreach (var required in new[] { "Sticky.exe", Runtime, "portable.flag", "tools/git/cmd/git.exe", "tools/gcm/git-credential-manager.exe" })
            if (!seen.Contains(required)) throw new InvalidDataException("更新包缺少受校验的组件：" + required);
        if (DurableFiles.Hash(Path.Combine(directory, "Sticky.exe")) != m.GetProperty("executableSha256").GetString()
            || DurableFiles.Hash(DurableFiles.Child(directory, Runtime)) != m.GetProperty("runtimeSha256").GetString())
            throw new InvalidDataException("启动器或运行程序校验失败。");
        return version;
    }
}
