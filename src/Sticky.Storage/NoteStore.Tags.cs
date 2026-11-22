using System.Text.Json;
using Sticky.Core;

namespace Sticky.Storage;

public sealed partial class NoteStore
{
    public const string TagsFile = "tags.json";

    public List<NoteTag> LoadTags()
    {
        var path = Path.Combine(Root, TagsFile);
        var backups = WorkspaceFiles.BackupCandidates(Root, path);
        if (!File.Exists(path) && !backups.Any(File.Exists)) return [];
        try { return ParseTags(File.ReadAllBytes(path)); }
        catch (Exception ex) when (IsDataError(ex))
        {
            foreach (var backup in backups.Where(File.Exists))
            {
                try
                {
                    var bytes = File.ReadAllBytes(backup);
                    var tags = ParseTags(bytes);
                    if (File.Exists(path)) WorkspaceFiles.PreserveCorrupt(Root, path);
                    AtomicWrite(path, bytes, keepBackup: false);
                    Warnings.Add("标签已从上次备份恢复。");
                    return tags;
                }
                catch (Exception backupError) when (IsDataError(backupError)) { }
            }
            throw new InvalidDataException("无法读取标签，原始文件已保留。", ex);
        }
    }

    public static List<NoteTag> ParseTags(byte[] bytes)
    {
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("标签数据过大。");
        var tags = JsonSerializer.Deserialize<List<NoteTag>>(bytes, JsonOptions);
        ValidateTags(tags);
        return tags!;
    }

    public static void ValidateTags(List<NoteTag>? tags)
    {
        if (tags is null || tags.Any(t => t is null || t.Id == Guid.Empty || t.Name is null ||
                !NoteTagNames.IsValid(t.Name) || t.Name != NoteTagNames.Normalize(t.Name)) ||
            tags.Select(t => t.Id).Distinct().Count() != tags.Count ||
            tags.Where(t => !t.Deleted).GroupBy(t => t.Name, NoteTagNames.Comparer).Any(g => g.Skip(1).Any()))
            throw new InvalidDataException("标签数据无效或包含重复名称。");
    }

    public static byte[] SerializeTags(List<NoteTag> tags)
    {
        ValidateTags(tags);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(tags.OrderBy(t => t.Id), JsonOptions);
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("标签数据过大。");
        return bytes;
    }

    public void SaveTags(List<NoteTag> tags)
    {
        var path = Path.Combine(Root, TagsFile);
        if (tags.Count == 0 && !File.Exists(path)) return;
        var bytes = SerializeTags(tags);
        History.WriteDocument(TagsFile, bytes, () => AtomicWrite(path, bytes));
    }
}
