using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Sticky.Core;

namespace Sticky.Storage;

public sealed partial class NoteStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        IgnoreReadOnlyProperties = true,
        MaxDepth = 48
    };

    public string Root { get; }
    public string NotesDirectory => Path.Combine(Root, "notes");
    public string AssetsDirectory => Path.Combine(Root, "assets");
    public List<string> Warnings { get; } = [];

    public NoteStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(NotesDirectory);
        Directory.CreateDirectory(AssetsDirectory);
        Directory.CreateDirectory(Path.Combine(Root, "device"));
        var manifest = Path.Combine(Root, "manifest.json");
        if (!File.Exists(manifest))
            AtomicWrite(manifest, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["schemaVersion"] = 1, ["app"] = "Sticky" }, JsonOptions));
    }

    public List<Note> LoadNotes()
    {
        Warnings.Clear();
        var notes = new List<Note>();
        // Include backup-only notes, e.g. after an interrupted external file operation.
        var paths = Directory.EnumerateFiles(NotesDirectory, "*.json")
            .Concat(Directory.EnumerateFiles(NotesDirectory, "*.json.bak").Select(p => p[..^4]))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var id)) continue;
            try { notes.Add(ReadNote(path, id)); }
            catch (UnsupportedVersionException ex)
            {
                Warnings.Add($"{Path.GetFileName(path)}：{ex.Message}，原始文件已保留。");
            }
            catch (Exception ex) when (IsDataError(ex))
            {
                try
                {
                    var note = ReadNote(path + ".bak", id);
                    // Preserve the damaged source for diagnosis before restoring the backup.
                    if (File.Exists(path)) File.Copy(path, path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}", false);
                    File.Copy(path + ".bak", path, true);
                    notes.Add(note);
                    Warnings.Add($"便签「{note.DisplayTitle}」已从上次备份恢复。");
                }
                catch (Exception backupEx) when (IsDataError(backupEx) || backupEx is UnsupportedVersionException)
                {
                    Warnings.Add($"无法读取 {Path.GetFileName(path)}，原始文件已保留。原因：{ex.Message}");
                }
            }
        }
        return notes.OrderByDescending(n => n.ModifiedAt).ToList();
    }

    private static Note ReadNote(string path, Guid expectedId)
    {
        if (new FileInfo(path).Length > 20 * 1024 * 1024) throw new InvalidDataException("便签文件过大。");
        return ParseNote(File.ReadAllBytes(path), expectedId);
    }

    public static Note ParseNote(byte[] bytes, Guid expectedId)
    {
        var note = JsonSerializer.Deserialize<Note>(bytes, JsonOptions)
                   ?? throw new InvalidDataException("便签内容为空。");
        if (note.SchemaVersion != 1) throw new UnsupportedVersionException("此便签需要其他版本的 Sticky 才能读取");
        if (note.Id != expectedId) throw new InvalidDataException("便签标识不匹配。");
        Validate(note);
        return note;
    }

    public void Save(Note note)
    {
        Validate(note);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(note, JsonOptions);
        if (bytes.Length > 20 * 1024 * 1024) throw new InvalidDataException("便签正文超过 20 MB，请拆分为多张便签。");
        AtomicWrite(Path.Combine(NotesDirectory, note.Id + ".json"), bytes);
    }

    private static void Validate(Note note)
    {
        if (note.SchemaVersion != 1 || note.Id == Guid.Empty || note.Title is null || note.Title.Length > 500 || note.Blocks is null)
            throw new InvalidDataException("便签格式无效。");
        ValidateBlocks(note.Blocks, 0);
    }

    private static void ValidateBlocks(List<NoteBlock> blocks, int depth)
    {
        if (depth > 16) throw new InvalidDataException("正文嵌套层数过多。");
        foreach (var block in blocks)
        {
            if (block is null || block.Kind is not ("paragraph" or "list" or "listItem") || block.Inlines is null || block.Children is null)
                throw new InvalidDataException("正文结构无效。");
            if ((depth == 0 && block.Kind == "listItem") ||
                (block.Kind == "paragraph" && block.Children.Count != 0) ||
                (block.Kind != "paragraph" && block.Inlines.Count != 0) ||
                (block.Kind == "list" && block.Children.Any(c => c is null || c.Kind != "listItem")) ||
                (block.Kind == "listItem" && block.Children.Any(c => c is null || c.Kind == "listItem")))
                throw new InvalidDataException("正文元素层级无效。");
            foreach (var inline in block.Inlines)
            {
                if (inline is null || inline.Kind is not ("text" or "image") || inline.Text is null)
                    throw new InvalidDataException("正文内容无效。");
                if (inline.Kind == "image" && (inline.AssetId is null || !AssetName().IsMatch(inline.AssetId)))
                    throw new InvalidDataException("图片引用无效。");
            }
            ValidateBlocks(block.Children, depth + 1);
        }
    }

    public string AddImage(byte[] pngBytes)
    {
        var id = Convert.ToHexStringLower(SHA256.HashData(pngBytes)) + ".png";
        var path = GetAssetPath(id);
        if (!File.Exists(path)) AtomicWrite(path, pngBytes, keepBackup: false);
        return id;
    }

    public string GetAssetPath(string id)
    {
        if (!AssetName().IsMatch(id)) throw new InvalidDataException("图片引用无效。");
        return Path.Combine(AssetsDirectory, id);
    }

    public DeviceState LoadDeviceState()
    {
        var path = Path.Combine(Root, "device", "windows.json");
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    var state = JsonSerializer.Deserialize<DeviceState>(File.ReadAllBytes(candidate), JsonOptions);
                    if (state?.Windows is not null && state.Windows.Values.All(w => w is not null)) return state;
                }
            }
            catch (Exception ex) when (IsDataError(ex)) { }
        }
        return new DeviceState();
    }

    public void SaveDeviceState(DeviceState state) => AtomicWrite(
        Path.Combine(Root, "device", "windows.json"), JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions));

    private static bool IsDataError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException;

    private sealed class UnsupportedVersionException(string message) : Exception(message);

    private static void AtomicWrite(string path, byte[] bytes, bool keepBackup = true)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, keepBackup ? path + ".bak" : null, true);
            else
            {
                File.Move(temporary, path);
                if (keepBackup) File.Copy(path, path + ".bak", true);
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    [GeneratedRegex("^[a-f0-9]{64}\\.png$", RegexOptions.CultureInvariant)]
    private static partial Regex AssetName();
}
