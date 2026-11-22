using System.Text.Json;
using Sticky.Core;

namespace Sticky.Storage;

public sealed partial class NoteStore
{
    public List<NoteDirectory> LoadDirectories()
    {
        var path = Path.Combine(Root, "groups.json");
        var backups = WorkspaceFiles.BackupCandidates(Root, path);
        if (!File.Exists(path) && !backups.Any(File.Exists)) return [];
        try { return ParseDirectories(File.ReadAllBytes(path)); }
        catch (UnsupportedVersionException ex) { throw new InvalidDataException("目录数据版本过新，请升级 Sticky。", ex); }
        catch (Exception ex) when (IsDataError(ex))
        {
            foreach (var backup in backups)
            {
                try
                {
                    var directories = ParseDirectories(File.ReadAllBytes(backup));
                    if (File.Exists(path)) WorkspaceFiles.PreserveCorrupt(Root, path);
                    AtomicWrite(path, File.ReadAllBytes(backup), keepBackup: false);
                    Warnings.Add("目录已从上次备份恢复。");
                    return directories;
                }
                catch (Exception backupEx) when (IsDataError(backupEx) || backupEx is UnsupportedVersionException) { }
            }
            throw new InvalidDataException("无法读取目录，原始文件已保留。请从 backups/groups.json.bak 恢复。", ex);
        }
    }

    public static List<NoteDirectory> ParseDirectories(byte[] bytes)
    {
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("目录数据过大。");
        var catalog = JsonSerializer.Deserialize<DirectoryCatalog>(bytes, JsonOptions) ?? throw new InvalidDataException("目录数据无效。");
        if (catalog.SchemaVersion is not (1 or 2 or 3)) throw new UnsupportedVersionException("目录数据版本过新，请升级 Sticky。");
        ValidateDirectories(catalog.Directories);
        if (catalog.SchemaVersion == 1 && catalog.Directories.Any(directory => directory.ParentId.HasValue))
            throw new InvalidDataException("旧版目录数据不能包含子目录。");
        if (catalog.SchemaVersion < 3 && catalog.Directories.Any(d => d.State != DirectoryState.Active))
            throw new InvalidDataException("旧版目录数据不能包含删除记录。");
        return catalog.Directories;
    }

    public void SaveDirectories(List<NoteDirectory> directories)
    {
        var bytes = SerializeDirectories(directories);
        History.WriteDocument("groups.json", bytes, () => AtomicWrite(Path.Combine(Root, "groups.json"), bytes));
    }

    public static byte[] SerializeDirectories(List<NoteDirectory> directories)
    {
        ValidateDirectories(directories);
        var version = directories.Any(d => d.State != DirectoryState.Active) ? 3 : directories.Any(d => d.ParentId.HasValue) ? 2 : 1;
        var catalog = new DirectoryCatalog { Directories = directories, SchemaVersion = version };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(catalog, JsonOptions);
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("目录数据过大。");
        return bytes;
    }

    public static void ValidateDirectories(List<NoteDirectory>? directories)
    {
        if (directories is null || directories.Count > 10000
            || directories.Any(d => d is null || d.Id == Guid.Empty || string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 50 || !Enum.IsDefined(d.State))
            || directories.Select(d => d.Id).Distinct().Count() != directories.Count)
            throw new InvalidDataException("目录数据无效，目录名称须为 1–50 字。");
        if (directories.Where(d => d.State == DirectoryState.Active).GroupBy(d => d.ParentId)
            .Any(siblings => siblings.Select(d => d.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != siblings.Count()))
            throw new InvalidDataException("同一个目录下不能有同名子目录。");

        var removed = directories.Where(d => d.State == DirectoryState.Removed).Select(d => d.Id).ToHashSet();
        if (directories.Any(d => d.State != DirectoryState.Removed && d.ParentId is { } id && removed.Contains(id)))
            throw new InvalidDataException("已删除的空目录不能包含可恢复的子目录。");

        var parents = directories.ToDictionary(d => d.Id, d => d.ParentId);
        var checkedIds = new HashSet<Guid>();
        foreach (var directory in directories)
        {
            var path = new HashSet<Guid>();
            for (Guid? id = directory.Id; id is { } current && !checkedIds.Contains(current);)
            {
                if (!parents.TryGetValue(current, out id)) throw new InvalidDataException("目录缺少上级目录。");
                if (!path.Add(current)) throw new InvalidDataException("不能把目录移动到自身或子目录中。");
            }
            checkedIds.UnionWith(path);
        }
    }
}
