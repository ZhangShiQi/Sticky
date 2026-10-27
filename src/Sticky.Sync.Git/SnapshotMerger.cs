using Sticky.Core;

namespace Sticky.Sync.Git;

public static class SnapshotMerger
{
    // remote owns the original identity; a competing local version always gets its own stable identity.
    // Swapping these inputs during UI reconciliation keeps edits made while the network was busy in the original editor.
    public static MergeResult Merge(SyncSnapshot baseline, SyncSnapshot local, SyncSnapshot remote)
    {
        baseline = baseline.Clone(); local = local.Clone(); remote = remote.Clone();
        var result = new SyncSnapshot();
        var conflicts = 0;
        var ai = AiCatalogMerger.Merge(baseline.Ai, local.Ai, remote.Ai);
        result.Ai = ai.Catalog; conflicts += ai.Conflicts;
        foreach (var id in baseline.Groups.Select(g => g.Id).Union(local.Groups.Select(g => g.Id)).Union(remote.Groups.Select(g => g.Id)))
        {
            var b = baseline.Groups.FirstOrDefault(g => g.Id == id);
            var l = local.Groups.FirstOrDefault(g => g.Id == id);
            var r = remote.Groups.FirstOrDefault(g => g.Id == id);
            var chosen = r ?? l ?? b!;
            if (l is not null && r is not null && l.Name != r.Name)
            {
                if (r.Name == b?.Name) chosen = l;
                else if (l.Name != b?.Name)
                {
                    result.Groups.Add(new NoteGroup { Id = SyncJson.StableId($"group:{id}:{l.Name}"), Name = l.Name });
                    conflicts++;
                }
            }
            result.Groups.Add(new NoteGroup { Id = chosen.Id, Name = chosen.Name });
        }
        // Concurrent creation of equally named groups converges on one stable ID.
        var aliases = new Dictionary<Guid, Guid>();
        result.Groups = result.Groups.GroupBy(g => g.Name.Trim(), StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var canonical = group.OrderBy(g => g.Id).First();
            foreach (var item in group) aliases[item.Id] = canonical.Id;
            return canonical;
        }).GroupBy(g => g.Id).Select(g => g.First()).OrderBy(g => g.Id).ToList();
        foreach (var snapshot in new[] { baseline, local, remote })
            foreach (var note in snapshot.Notes.Values)
                if (note.GroupId is { } groupId && aliases.TryGetValue(groupId, out var mapped)) note.GroupId = mapped;

        foreach (var id in local.Notes.Keys.Union(remote.Notes.Keys).Order())
        {
            baseline.Notes.TryGetValue(id, out var b);
            local.Notes.TryGetValue(id, out var l);
            remote.Notes.TryGetValue(id, out var r);
            // Deletion is represented only by DeletedAt. Missing files never mean delete.
            if (l is null) { result.Notes[id] = r!; continue; }
            if (r is null) { result.Notes[id] = l; continue; }
            if (SyncJson.Equal(l, r) || SyncJson.Equal(l, b)) { result.Notes[id] = r; continue; }
            if (SyncJson.Equal(r, b)) { result.Notes[id] = l; continue; }
            result.Notes[id] = r;
            var seed = $"conflict:{id}:{SyncJson.Hash(SyncJson.Bytes(l))}";
            var copyId = SyncJson.StableId(seed);
            var copy = SyncJson.Read<Note>(SyncJson.Bytes(l));
            copy.Id = copyId;
            copy.ConflictOf = l.ConflictOf ?? id;
            copy.Title = copy.DisplayTitle[..Math.Min(copy.DisplayTitle.Length, 480)] + "（冲突副本）";
            // A previous conflict copy may itself have been edited. Never reuse its identity for different content.
            Note? occupied;
            while ((remote.Notes.TryGetValue(copyId, out occupied) || local.Notes.TryGetValue(copyId, out occupied)) && !SyncJson.Equal(copy, occupied))
            {
                seed += ":" + SyncJson.Hash(SyncJson.Bytes(occupied));
                copyId = SyncJson.StableId(seed);
                copy.Id = copyId;
            }
            if (occupied is null)
            {
                result.Notes[copyId] = copy;
                conflicts++;
            }
        }
        foreach (var (id, bytes) in local.Assets) result.Assets[id] = bytes;
        foreach (var (id, bytes) in remote.Assets) result.Assets[id] = bytes;
        return new MergeResult(result, conflicts);
    }
}
