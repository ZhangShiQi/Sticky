using System.IO.Compression;
using System.Security.Cryptography;
using Sticky.Core;

namespace Sticky.Storage;

public static class BackupService
{
    public static void Export(NoteStore store, string destination)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(Path.Combine(store.Root, "manifest.json"), "manifest.json");
                var groupsPath = Path.Combine(store.Root, "groups.json");
                if (File.Exists(groupsPath)) archive.CreateEntryFromFile(groupsPath, "groups.json");
                foreach (var path in Directory.EnumerateFiles(store.NotesDirectory, "*.json"))
                    archive.CreateEntryFromFile(path, "notes/" + Path.GetFileName(path));
                foreach (var path in Directory.EnumerateFiles(store.AssetsDirectory, "*.png"))
                    archive.CreateEntryFromFile(path, "assets/" + Path.GetFileName(path));
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static List<Note> Import(NoteStore store, string source)
    {
        using var archive = ZipFile.OpenRead(source);
        if (archive.Entries.Count > 10000) throw new InvalidDataException("备份包含过多文件。");
        var notes = new List<Note>();
        var importedGroups = new List<NoteGroup>();
        var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (!seen.Add(name)) throw new InvalidDataException("备份包含重复文件。");
            if (name is "manifest.json" or "notes/" or "assets/") continue;
            total += entry.Length;
            if (entry.Length > 20 * 1024 * 1024 || total > 256 * 1024 * 1024)
                throw new InvalidDataException("备份过大（单文件上限 20 MB，总计上限 256 MB）。");
            var parts = name.Split('/');
            if (parts.Length != 2 && name != "groups.json") throw new InvalidDataException("备份包含无效路径。");
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            if (name == "groups.json") importedGroups = NoteStore.ParseGroups(bytes);
            else if (parts[0] == "notes" && parts[1].EndsWith(".json", StringComparison.Ordinal) && Guid.TryParse(parts[1][..^5], out var id))
                notes.Add(NoteStore.ParseNote(bytes, id));
            else if (parts[0] == "assets")
            {
                _ = store.GetAssetPath(parts[1]); // Validate the filename before any write.
                if (Convert.ToHexStringLower(SHA256.HashData(bytes)) + ".png" != parts[1])
                    throw new InvalidDataException("图片校验失败：" + parts[1]);
                assets.Add(parts[1], bytes);
            }
            else throw new InvalidDataException("备份包含不支持的文件。");
        }
        if (notes.Count == 0 && importedGroups.Count == 0) throw new InvalidDataException("备份中没有便签或分组。");
        var originalGroups = store.LoadGroups();
        var mergedGroups = originalGroups.Select(g => new NoteGroup { Id = g.Id, Name = g.Name }).ToList();
        var groupMap = new Dictionary<Guid, Guid>();
        foreach (var group in importedGroups)
        {
            var same = mergedGroups.FirstOrDefault(g => g.Name.Equals(group.Name, StringComparison.OrdinalIgnoreCase));
            if (same is null)
            {
                same = new NoteGroup { Id = mergedGroups.Any(g => g.Id == group.Id) ? Guid.NewGuid() : group.Id, Name = group.Name };
                mergedGroups.Add(same);
            }
            groupMap[group.Id] = same.Id;
        }
        foreach (var note in notes)
        {
            if (note.GroupId is { } groupId)
            {
                // A backup cannot attach a note to an unrelated local group with a colliding ID.
                note.GroupId = groupMap.TryGetValue(groupId, out var mapped) ? mapped : null;
            }
            foreach (var id in ImageIds(note.Blocks))
                if (!assets.ContainsKey(id) && !File.Exists(store.GetAssetPath(id)))
                    throw new InvalidDataException("备份缺少图片：" + id);
            if (File.Exists(Path.Combine(store.NotesDirectory, note.Id + ".json")))
            {
                note.Id = Guid.NewGuid();
                note.Title = note.Title[..Math.Min(note.Title.Length, 490)] + "（导入副本）";
            }
        }
        // All archive paths, models and hashes have been checked before changing the workspace.
        foreach (var bytes in assets.Values) store.AddImage(bytes);
        var written = new List<string>();
        try
        {
            store.SaveGroups(mergedGroups);
            foreach (var note in notes)
            {
                var path = Path.Combine(store.NotesDirectory, note.Id + ".json");
                // New identities only; an existing local note is never overwritten.
                if (File.Exists(path)) throw new IOException("导入标识冲突。");
                written.Add(path);
                store.Save(note);
            }
        }
        catch
        {
            foreach (var path in written)
            {
                if (File.Exists(path)) File.Delete(path);
                foreach (var backup in WorkspaceFiles.BackupCandidates(store.Root, path))
                    if (File.Exists(backup)) File.Delete(backup);
            }
            store.SaveGroups(originalGroups);
            throw;
        }
        return notes;
    }

    private static IEnumerable<string> ImageIds(IEnumerable<NoteBlock> blocks)
    {
        foreach (var block in blocks)
        {
            foreach (var inline in block.Inlines) if (inline.Kind == "image" && inline.AssetId is { } id) yield return id;
            foreach (var id in ImageIds(block.Children)) yield return id;
        }
    }
}
