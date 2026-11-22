using Sticky.Core;

namespace Sticky.Sync.Git;

internal sealed class TagMerger
{
    private readonly Dictionary<Guid, Guid[]> _baseline;
    private readonly Dictionary<Guid, Guid[]> _local;
    private readonly Dictionary<Guid, Guid[]> _remote;
    private readonly Dictionary<Guid, Guid[]> _conflictCopies = [];

    internal TagMerger(SyncSnapshot baseline, SyncSnapshot local, SyncSnapshot remote, SyncSnapshot result)
    {
        var old = baseline.Tags.ToDictionary(t => t.Id);
        var left = local.Tags.ToDictionary(t => t.Id);
        var right = remote.Tags.ToDictionary(t => t.Id);
        var catalog = new List<NoteTag>();
        foreach (var id in left.Keys.Union(right.Keys))
        {
            var b = old.GetValueOrDefault(id);
            var l = left.GetValueOrDefault(id);
            var r = right.GetValueOrDefault(id);
            var tag = l is { Deleted: true } ? l : r is { Deleted: true } ? r : r == b ? l ?? r! : r ?? l!;
            catalog.Add(tag);
        }

        var aliases = new Dictionary<Guid, Guid>();
        var canonical = new List<NoteTag>();
        foreach (var group in catalog.Where(t => !t.Deleted).GroupBy(t => t.Name, NoteTagNames.Comparer))
        {
            var selected = group.MinBy(t => t.Id)!;
            canonical.Add(selected);
            foreach (var tag in group) aliases[tag.Id] = selected.Id;
        }
        result.Tags = canonical.Concat(catalog.Where(t => t.Deleted)).OrderBy(t => t.Id).ToList();

        // Compare note bodies and directory changes independently of tag edits.
        _baseline = Extract(baseline, aliases);
        _local = Extract(local, aliases);
        _remote = Extract(remote, aliases);
    }

    internal void RecordConflict(Guid original, Guid copy) => _conflictCopies[copy] = _local.GetValueOrDefault(original) ?? [];

    internal void Apply(IEnumerable<Note> notes)
    {
        foreach (var note in notes)
        {
            // A vault transfer publishes an empty, redacted tombstone; concurrent labels belong to the former ordinary note.
            if (note.DeletedAt is { } deleted && note.CreatedAt == deleted && note.ModifiedAt == deleted &&
                note.Title.Length == 0 && note.Blocks.Count == 0 && !note.IsMarkdown) continue;
            if (_conflictCopies.TryGetValue(note.Id, out var copy)) { note.SetTags(copy); continue; }
            if (!_local.TryGetValue(note.Id, out var local)) { note.SetTags(_remote.GetValueOrDefault(note.Id) ?? []); continue; }
            if (!_remote.TryGetValue(note.Id, out var remote)) { note.SetTags(local); continue; }
            var baseline = _baseline.GetValueOrDefault(note.Id) ?? [];
            note.SetTags(local.Intersect(remote).Union(local.Union(remote).Except(baseline)));
        }
    }

    private static Dictionary<Guid, Guid[]> Extract(SyncSnapshot snapshot, Dictionary<Guid, Guid> aliases)
    {
        var memberships = new Dictionary<Guid, Guid[]>();
        foreach (var note in snapshot.Notes.Values)
        {
            memberships.Add(note.Id, note.Tags.Where(aliases.ContainsKey).Select(id => aliases[id]).Distinct().ToArray());
            if (note.Tags.Count > 0) note.SetTags([]);
        }
        return memberships;
    }
}
