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
        result.Directories = MergeDirectories(baseline, local, remote, ref conflicts);
        var tags = baseline.Tags.Count + local.Tags.Count + remote.Tags.Count > 0 ? new TagMerger(baseline, local, remote, result) : null;

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
            if (b is not null && (b.DirectoryId != l.DirectoryId || b.DirectoryId != r.DirectoryId))
            {
                var originalContent = NoteContent(b);
                var localContent = NoteContent(l);
                var remoteContent = NoteContent(r);
                if (localContent.AsSpan().SequenceEqual(remoteContent) || localContent.AsSpan().SequenceEqual(originalContent) || remoteContent.AsSpan().SequenceEqual(originalContent))
                {
                    var merged = remoteContent.AsSpan().SequenceEqual(originalContent) ? l : r;
                    if (l.DirectoryId != r.DirectoryId && l.DirectoryId != b.DirectoryId && r.DirectoryId != b.DirectoryId) conflicts++;
                    merged.DirectoryId = r.DirectoryId == b.DirectoryId ? l.DirectoryId : r.DirectoryId;
                    merged.ModifiedAt = l.ModifiedAt > r.ModifiedAt ? l.ModifiedAt : r.ModifiedAt;
                    result.Notes[id] = merged;
                    continue;
                }
            }
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
                tags?.RecordConflict(id, copyId);
                conflicts++;
            }
        }
        tags?.Apply(result.Notes.Values);
        DirectoryHierarchy.PreserveContentsUnderRemovedDirectories(result.Directories, result.Notes.Values);
        foreach (var (id, bytes) in local.Assets) result.Assets[id] = bytes;
        foreach (var (id, bytes) in remote.Assets) result.Assets[id] = bytes;
        return new MergeResult(result, conflicts);
    }

    private static byte[] NoteContent(Note note)
    {
        var copy = SyncJson.Read<Note>(SyncJson.Bytes(note));
        copy.DirectoryId = null;
        copy.ModifiedAt = default;
        return SyncJson.Bytes(copy);
    }

    private static List<NoteDirectory> MergeDirectories(SyncSnapshot baseline, SyncSnapshot local, SyncSnapshot remote, ref int conflicts)
    {
        var previous = baseline.Directories.ToDictionary(d => d.Id);
        var onDevice = local.Directories.ToDictionary(d => d.Id);
        var upstream = remote.Directories.ToDictionary(d => d.Id);
        var merged = new Dictionary<Guid, NoteDirectory>();
        foreach (var id in onDevice.Keys.Union(upstream.Keys))
        {
            var b = previous.GetValueOrDefault(id);
            var l = onDevice.GetValueOrDefault(id);
            var r = upstream.GetValueOrDefault(id);
            var chosen = r ?? l!;
            var name = chosen.Name;
            var parent = chosen.ParentId;
            var state = chosen.State;
            if (l is not null && r is not null)
            {
                var priorState = b?.State ?? DirectoryState.Active;
                if (r.State == priorState || l.State != priorState && (int)l.State > (int)r.State) state = l.State;
                if (r.ParentId == b?.ParentId) parent = l.ParentId;
                else if (l.ParentId != b?.ParentId && l.ParentId != r.ParentId) conflicts++;

                if (r.Name == b?.Name) name = l.Name;
                else if (l.Name != b?.Name && l.Name != r.Name && state == DirectoryState.Active)
                {
                    var copyId = SyncJson.StableId($"group:{id}:{l.Name}");
                    if (!previous.ContainsKey(copyId) && !onDevice.ContainsKey(copyId) && !upstream.ContainsKey(copyId) && !merged.ContainsKey(copyId))
                    {
                        merged.Add(copyId, new NoteDirectory { Id = copyId, Name = l.Name, ParentId = l.ParentId });
                        conflicts++;
                    }
                }
            }
            merged.Add(id, new NoteDirectory { Id = id, Name = name, ParentId = parent, State = state });
        }

        // Merging equal siblings can expose another pair of equal siblings in the next level.
        var aliases = new Dictionary<Guid, Guid>();
        while (true)
        {
            conflicts += BreakDirectoryCycles(merged);
            var previousCount = merged.Count;
            foreach (var siblings in merged.Values.Where(d => d.State == DirectoryState.Active).GroupBy(directory => directory.ParentId).ToList())
            {
                var usedNames = siblings.Select(directory => directory.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var matching in siblings.GroupBy(directory => directory.Name.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    var matches = matching.OrderBy(directory => directory.Id).ToList();
                    var established = matches.Where(d => previous.ContainsKey(d.Id) && onDevice.ContainsKey(d.Id) && upstream.ContainsKey(d.Id)).ToList();
                    var canonical = established.Count > 1 ? established[0] : matches[0];
                    var containsRestoredDirectory = matches.Any(d => previous.GetValueOrDefault(d.Id)?.State == DirectoryState.Trashed);
                    foreach (var duplicate in matches.Where(d => d.Id != canonical.Id))
                    {
                        if (containsRestoredDirectory || established.Count > 1 && established.Contains(duplicate))
                        {
                            // A concurrent move may bring two established directories together. Keep both memberships.
                            var number = 2;
                            string name;
                            do
                            {
                                var suffix = $"（{number++}）";
                                var stem = duplicate.Name.Trim();
                                name = stem[..Math.Min(stem.Length, 50 - suffix.Length)] + suffix;
                            } while (!usedNames.Add(name));
                            duplicate.Name = name;
                            conflicts++;
                        }
                        else
                        {
                            aliases.Add(duplicate.Id, canonical.Id);
                            merged.Remove(duplicate.Id);
                        }
                    }
                }
            }
            if (merged.Count == previousCount) break;
            foreach (var directory in merged.Values) directory.ParentId = CanonicalId(directory.ParentId);
        }

        foreach (var snapshot in new[] { baseline, local, remote })
            foreach (var note in snapshot.Notes.Values) note.DirectoryId = CanonicalId(note.DirectoryId);
        return merged.Values.OrderBy(directory => directory.Id).ToList();

        Guid? CanonicalId(Guid? id)
        {
            while (id is { } key && aliases.TryGetValue(key, out var target)) id = target;
            return id;
        }
    }

    private static int BreakDirectoryCycles(Dictionary<Guid, NoteDirectory> directories)
    {
        var checkedIds = new HashSet<Guid>();
        var repairs = 0;
        foreach (var directory in directories.Values)
        {
            var path = new List<Guid>();
            var visited = new HashSet<Guid>();
            for (Guid? id = directory.Id; id is { } key && !checkedIds.Contains(key); id = directories[key].ParentId)
            {
                if (!visited.Add(key))
                {
                    // Concurrent moves can form a cycle even when both source catalogs are valid.
                    var root = path.Skip(path.IndexOf(key)).Min();
                    directories[root].ParentId = null;
                    repairs++;
                    break;
                }
                path.Add(key);
            }
            checkedIds.UnionWith(path);
        }
        return repairs;
    }
}
