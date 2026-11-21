using System.Text.Json;
using Sticky.Core;

namespace Sticky.Storage;

// A durable, content-free journal keeps a moved source from returning through recovery
// or sync, and retries local cleanup after a crash. Never clear it on vault lock.
public sealed class VaultMigration
{
    private readonly NoteStore _store;
    private string PathName => Path.Combine(_store.Root, "device", "vault-migrations.json");
    public List<Transfer> Transfers { get; }
    public HashSet<Guid> MovedIds => Transfers.Select(t => t.Id).ToHashSet();
    public static HashSet<Guid> ReadMovedIds(string root)
    {
        var path = Path.Combine(root, "device", "vault-migrations.json");
        if (!File.Exists(path)) return [];
        var transfers = JsonSerializer.Deserialize<List<Transfer>>(File.ReadAllBytes(path), NoteStore.JsonOptions)
            ?? throw new InvalidDataException("保险箱迁移记录无效。");
        return transfers.Select(t => t.Id).ToHashSet();
    }
    public VaultMigration(NoteStore store)
    {
        _store = store;
        Transfers = File.Exists(PathName) ? JsonSerializer.Deserialize<List<Transfer>>(File.ReadAllBytes(PathName), NoteStore.JsonOptions)
            ?? throw new InvalidDataException("保险箱迁移记录无效。") : [];
        if (Transfers.Any(t => t.Id == Guid.Empty || t.Assets is null)) throw new InvalidDataException("保险箱迁移记录无效。");
        foreach (var id in Transfers.SelectMany(t => t.Assets)) _ = store.GetAssetPath(id);
    }
    public void MoveIn(VaultStore vault, Note note)
    {
        if (MovedIds.Contains(note.Id)) throw new InvalidOperationException("此便签已移入保险箱。");
        foreach (var folder in new[] { _store.BackupsDirectory, _store.NotesDirectory, _store.AssetsDirectory }) HistoryStore.EnsureNoLinks(folder);
        var history = _store.History.NoteVersions(note.Id);
        var sourceBackups = new Dictionary<string, byte[]>();
        foreach (var folder in new[] { _store.BackupsDirectory, _store.NotesDirectory })
            if (Directory.Exists(folder))
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(p => Path.GetFileName(p).Contains(note.Id.ToString(), StringComparison.OrdinalIgnoreCase)))
                {
                    var bytes = File.ReadAllBytes(file);
                    sourceBackups[Path.GetRelativePath(_store.Root, file)] = bytes;
                    try { history.Add(NoteStore.ParseNote(bytes, note.Id)); }
                    catch (Exception ex) when (ex is JsonException or InvalidDataException or NoteStore.UnsupportedVersionException) { /* Preserve unreadable source inside the encrypted archive. */ }
                }
        var assets = history.Append(note).SelectMany(n => n.GetImageIds()).Distinct()
            .ToDictionary(id => id, id => File.ReadAllBytes(_store.GetAssetPath(id)));
        // Encrypted destination must be durable before marking or touching the source.
        vault.Save(note, assets, history, sourceBackups);
        var transfer = new Transfer { Id = note.Id, Assets = assets.Keys.ToList(), Time = DateTimeOffset.UtcNow };
        var next = Transfers.Append(transfer).ToList();
        WorkspaceFiles.WriteAtomic(_store.Root, PathName, JsonSerializer.SerializeToUtf8Bytes(next, NoteStore.JsonOptions), keepBackup: false);
        Transfers.Add(transfer);
        CompletePending();
    }
    public Note Tombstone(Transfer transfer) => new() { Id = transfer.Id, CreatedAt = transfer.Time, ModifiedAt = transfer.Time, DeletedAt = transfer.Time };
    public void CompletePending()
    {
        var pending = Transfers.Where(t => !t.Complete).ToList();
        if (pending.Count == 0) return;
        if (!File.Exists(Path.Combine(_store.Root, VaultStore.FileName))) throw new IOException("迁移中的加密保险箱丢失，已停止清理原便签。");
        foreach (var folder in new[] { _store.BackupsDirectory, _store.NotesDirectory, _store.AssetsDirectory }) HistoryStore.EnsureNoLinks(folder);
        var ids = MovedIds;
        foreach (var transfer in pending) _store.Save(Tombstone(transfer));
        _store.History.ForgetNotes(ids);
        var backups = _store.BackupsDirectory;
        if (Directory.Exists(backups))
            foreach (var path in Directory.EnumerateFiles(backups, "*", SearchOption.AllDirectories))
                if (ids.Any(id => Path.GetFileName(path).Contains(id.ToString(), StringComparison.OrdinalIgnoreCase))) File.Delete(path);
        // A legacy backup next to the current note must not resurrect its old body.
        foreach (var id in ids)
        {
            foreach (var extra in Directory.EnumerateFiles(_store.NotesDirectory, id + ".json.*")) File.Delete(extra);
            _store.RemoveNoteZoom(id);
        }
        // Retain images referenced by other ordinary notes, backups or historical objects.
        var retained = new HashSet<string>();
        foreach (var folder in new[] { _store.NotesDirectory, backups, _store.History.DirectoryPath })
            if (Directory.Exists(folder))
                foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || p.Contains(".json.", StringComparison.OrdinalIgnoreCase)))
                {
                    var text = File.ReadAllText(path);
                    foreach (var asset in pending.SelectMany(t => t.Assets)) if (text.Contains(asset, StringComparison.Ordinal)) retained.Add(asset);
                }
        foreach (var asset in pending.SelectMany(t => t.Assets).Distinct().Where(a => !retained.Contains(a)))
        {
            var path = _store.GetAssetPath(asset); if (File.Exists(path)) File.Delete(path);
            foreach (var backup in WorkspaceFiles.BackupCandidates(_store.Root, path)) if (File.Exists(backup)) File.Delete(backup);
            if (Directory.Exists(backups))
                foreach (var backup in Directory.EnumerateFiles(backups, "*", SearchOption.AllDirectories).Where(p => Path.GetFileName(p).Contains(asset, StringComparison.Ordinal))) File.Delete(backup);
        }
        foreach (var transfer in pending) transfer.Complete = true;
        WorkspaceFiles.WriteAtomic(_store.Root, PathName, JsonSerializer.SerializeToUtf8Bytes(Transfers, NoteStore.JsonOptions), keepBackup: false);
    }
    public sealed class Transfer
    {
        public Guid Id { get; set; }
        public DateTimeOffset Time { get; set; }
        public List<string> Assets { get; set; } = [];
        public bool Complete { get; set; }
    }
}
