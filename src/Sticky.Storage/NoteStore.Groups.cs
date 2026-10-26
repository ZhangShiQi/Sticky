using System.Text.Json;
using Sticky.Core;

namespace Sticky.Storage;

public sealed partial class NoteStore
{
    public List<NoteGroup> LoadGroups()
    {
        var path = Path.Combine(Root, "groups.json");
        var backups = WorkspaceFiles.BackupCandidates(Root, path);
        if (!File.Exists(path) && !backups.Any(File.Exists)) return [];
        try { return ParseGroups(File.ReadAllBytes(path)); }
        catch (UnsupportedVersionException ex) { throw new InvalidDataException("分组数据版本过新，请升级 Sticky。", ex); }
        catch (Exception ex) when (IsDataError(ex))
        {
            foreach (var backup in backups)
            {
                try
                {
                    var groups = ParseGroups(File.ReadAllBytes(backup));
                    if (File.Exists(path)) WorkspaceFiles.PreserveCorrupt(Root, path);
                    AtomicWrite(path, File.ReadAllBytes(backup), keepBackup: false);
                    Warnings.Add("分组已从上次备份恢复。");
                    return groups;
                }
                catch (Exception backupEx) when (IsDataError(backupEx) || backupEx is UnsupportedVersionException) { }
            }
            throw new InvalidDataException("无法读取分组，原始文件已保留。请从 backups/groups.json.bak 恢复。", ex);
        }
    }

    public static List<NoteGroup> ParseGroups(byte[] bytes)
    {
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("分组数据过大。");
        var catalog = JsonSerializer.Deserialize<GroupCatalog>(bytes, JsonOptions) ?? throw new InvalidDataException("分组数据无效。");
        if (catalog.SchemaVersion != 1) throw new UnsupportedVersionException("不支持的分组版本。");
        ValidateGroups(catalog.Groups);
        return catalog.Groups;
    }

    public void SaveGroups(List<NoteGroup> groups)
    {
        ValidateGroups(groups);
        AtomicWrite(Path.Combine(Root, "groups.json"), JsonSerializer.SerializeToUtf8Bytes(new GroupCatalog { Groups = groups }, JsonOptions));
    }

    private static void ValidateGroups(List<NoteGroup>? groups)
    {
        if (groups is null || groups.Count > 1000 || groups.Any(g => g is null || g.Id == Guid.Empty || string.IsNullOrWhiteSpace(g.Name) || g.Name.Length > 50)
            || groups.Select(g => g.Id).Distinct().Count() != groups.Count
            || groups.Select(g => g.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != groups.Count)
            throw new InvalidDataException("分组名不能为空或重复，长度上限为 50 字。");
    }
}
