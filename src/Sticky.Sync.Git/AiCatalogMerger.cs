using Sticky.Core;

namespace Sticky.Sync.Git;

public static class AiCatalogMerger
{
    public static (AiCatalog? Catalog, int Conflicts) Merge(AiCatalog? baseline, AiCatalog? local, AiCatalog? remote)
    {
        if (baseline is null && local is null && remote is null) return (null, 0);
        var conflicts = 0;
        List<T> MergeRecords<T>(IEnumerable<T> b, IEnumerable<T> l, IEnumerable<T> r, Func<T, Guid> id, Action<T, Guid> setId,
            Action<T, Guid> mark, Func<T, string> name, Action<T, string> setName, string kind)
        {
            var bs = b.ToDictionary(id); var ls = l.ToDictionary(id); var rs = r.ToDictionary(id); var result = new Dictionary<Guid, T>();
            foreach (var key in bs.Keys.Union(ls.Keys).Union(rs.Keys).Order())
            {
                bs.TryGetValue(key, out var before); ls.TryGetValue(key, out var left); rs.TryGetValue(key, out var right);
                // Missing files/records do not delete data. Deletions are explicit tombstones.
                if (left is null) { result[key] = right is not null ? right : before!; continue; }
                if (right is null) { result[key] = left; continue; }
                if (SyncJson.Equal(left, right) || SyncJson.Equal(left, before)) { result[key] = right; continue; }
                if (SyncJson.Equal(right, before)) { result[key] = left; continue; }
                result[key] = right;
                var copy = SyncJson.Read<T>(SyncJson.Bytes(left)); mark(copy, key);
                var label = name(copy); setName(copy, label[..Math.Min(label.Length, 52)] + "（冲突副本）");
                var seed = "ai-" + kind + ":" + key + ":" + SyncJson.Hash(SyncJson.Bytes(left)); var copyId = SyncJson.StableId(seed); setId(copy, copyId);
                T? occupied;
                while ((rs.TryGetValue(copyId, out occupied) || ls.TryGetValue(copyId, out occupied)) && !SyncJson.Equal(copy, occupied))
                { seed += ":" + SyncJson.Hash(SyncJson.Bytes(occupied)); copyId = SyncJson.StableId(seed); setId(copy, copyId); }
                if (occupied is null) { result[copyId] = copy; conflicts++; }
            }
            return result.OrderBy(p => p.Key).Select(p => p.Value).ToList();
        }
        var result = new AiCatalog
        {
            Connections = MergeRecords(baseline?.Connections ?? [], local?.Connections ?? [], remote?.Connections ?? [], c => c.Id, (c, value) => c.Id = value,
                (c, value) => c.ConflictOf ??= value, c => c.Name, (c, value) => c.Name = value, "connection"),
            Actions = MergeRecords(baseline?.Actions ?? [], local?.Actions ?? [], remote?.Actions ?? [], a => a.Id, (a, value) => a.Id = value,
                (a, value) => a.ConflictOf ??= value, a => a.Name, (a, value) => a.Name = value, "action")
        };
        AiCatalogRules.Validate(result); return (result, conflicts);
    }
}
