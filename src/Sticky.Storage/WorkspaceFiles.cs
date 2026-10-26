namespace Sticky.Storage;

public static class WorkspaceFiles
{
    public static string BackupDirectory(string root) => Path.Combine(Path.GetFullPath(root), "backups");

    private static string RelativeFile(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (Path.IsPathRooted(relative) || relative is "." or ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.Split(Path.DirectorySeparatorChar)[0].Equals("backups", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("文件必须位于数据目录中，且不能位于备份目录内。");
        return relative;
    }

    public static string BackupPath(string root, string originalPath) => Path.Combine(BackupDirectory(root), RelativeFile(root, originalPath) + ".bak");
    public static string[] BackupCandidates(string root, string originalPath) => [BackupPath(root, originalPath), originalPath + ".bak"];

    public static void WriteAtomic(string root, string path, byte[] bytes, bool keepBackup = true)
    {
        _ = RelativeFile(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var backup = keepBackup ? BackupPath(root, path) : null;
        if (backup is not null) Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, backup, true);
            else { File.Move(temporary, path); if (backup is not null) File.Copy(path, backup, true); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string PreserveCorrupt(string root, string original)
    {
        var destination = Path.Combine(BackupDirectory(root), "corrupt", RelativeFile(root, original) + $".{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.corrupt");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(original, destination);
        return destination;
    }

    public static List<string> MigrateLegacyBackups(string root)
    {
        var warnings = new List<string>();
        Directory.CreateDirectory(BackupDirectory(root));
        var legacy = new List<string>();
        void Add(string folder, string pattern)
        {
            if (Directory.Exists(folder)) legacy.AddRange(Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly));
        }
        Add(root, "*.json.bak"); Add(Path.Combine(root, "notes"), "*.json.bak");
        Add(Path.Combine(root, "assets"), "*.png.bak"); Add(Path.Combine(root, "device"), "*.json.bak");
        var sync = Path.Combine(root, "device", "sync");
        if (Directory.Exists(sync)) foreach (var target in Directory.EnumerateDirectories(sync)) Add(target, "baseline.json.bak");
        foreach (var source in legacy)
        {
            try
            {
                var destination = BackupPath(root, source[..^4]);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    var archive = Path.Combine(BackupDirectory(root), "legacy", RelativeFile(root, source) + $".{Guid.NewGuid():N}.bak");
                    Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
                    if (File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(destination)) File.Move(destination, archive);
                    else { File.Move(source, archive); continue; }
                }
                File.Move(source, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add($"旧备份 {Path.GetFileName(source)} 暂未迁移，原文件已保留：{ex.Message}"); }
        }
        return warnings;
    }
}
