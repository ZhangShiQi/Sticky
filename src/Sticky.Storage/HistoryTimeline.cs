using Sticky.Core;

namespace Sticky.Storage;

public sealed class HistoryTimeline
{
    private readonly HistoryStore _store;
    private readonly HistoryCheckpoint _baseline;
    private readonly string _checkpoints;
    public IReadOnlyList<HistoryEvent> Events { get; }
    public DateTimeOffset StartedAt => _baseline.Time;
    internal HistoryTimeline(HistoryStore store, HistoryCheckpoint baseline, List<HistoryEvent> entries, string checkpoints)
    { _store = store; _baseline = baseline; Events = entries; _checkpoints = checkpoints; }
    public List<HistoryRow> Rows(Guid? noteId = null)
    {
        var result = new List<HistoryRow>();
        foreach (var entry in Events.Where(e => noteId is null || e.NoteIds.Contains(noteId.Value)))
        {
            var last = result.LastOrDefault();
            if (last is not null && entry.Kind == "noteEdited" && last.Last.Kind == entry.Kind && entry.Source == "local"
                && entry.Target == last.Last.Target && entry.BatchId is not null && entry.BatchId == last.Last.BatchId
                && entry.Time - last.Last.Time is var gap && gap >= TimeSpan.Zero && gap <= TimeSpan.FromSeconds(30))
                result[^1] = new HistoryRow(last.First, entry, last.Count + 1);
            else result.Add(new(entry, entry, 1));
        }
        return result;
    }
    /// <summary>Content history for the UI. Device layout stays in the durable log only.</summary>
    public List<HistoryRow> UserRows()
    {
        var result = new List<HistoryRow>();
        var pending = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in Events.Where(e => e.Target != "device/windows.json"))
        {
            if (pending.TryGetValue(entry.Target, out var index))
            {
                var prior = result[index];
                if (entry.Kind == "noteEdited" && prior.Last.Kind == "noteEdited" && entry.Source == "local"
                    && prior.Last.Source == "local" && entry.BatchId is not null && entry.BatchId == prior.Last.BatchId
                    && entry.Time - prior.Last.Time is var gap && gap >= TimeSpan.Zero && gap <= TimeSpan.FromSeconds(30))
                {
                    result[index] = new(prior.First, entry, prior.Count + 1);
                    continue;
                }
            }
            // Group operations can change the meaning of a note's group between two edits.
            if (entry.Target == "groups.json") pending.Clear();
            pending[entry.Target] = result.Count;
            result.Add(new(entry, entry, 1));
        }
        return result.OrderBy(r => r.Sequence).ToList();
    }
    internal HistoryCheckpoint ProjectReferences(long sequence)
    {
        if (sequence < 0 || sequence > (Events.LastOrDefault()?.Sequence ?? 0)) throw new ArgumentOutOfRangeException(nameof(sequence));
        var source = _baseline;
        if (Directory.Exists(_checkpoints))
            foreach (var path in Directory.EnumerateFiles(_checkpoints, "*.json").OrderDescending(StringComparer.Ordinal))
                if (long.TryParse(Path.GetFileNameWithoutExtension(path), out var number) && number <= sequence)
                {
                    try
                    {
                        var candidate = HistoryStore.Decode<HistoryCheckpoint>(File.ReadAllBytes(path));
                        if (HistoryStore.ValidCheckpoint(candidate) && candidate.Sequence == number) { source = candidate; break; }
                    }
                    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
                }
        var state = new HistoryCheckpoint { Sequence = sequence, Time = source.Time, Documents = new(source.Documents, StringComparer.Ordinal) };
        foreach (var entry in Events.Where(e => e.Sequence > source.Sequence && e.Sequence <= sequence))
        { if (entry.After is null) state.Documents.Remove(entry.Target); else state.Documents[entry.Target] = entry.After; state.Time = entry.Time; }
        return state;
    }
    public HistoryFrame Frame(long sequence)
    {
        var refs = ProjectReferences(sequence);
        var notes = new Dictionary<Guid, Note>(); var groups = new List<NoteGroup>(); var layout = new DeviceState();
        foreach (var (target, hash) in refs.Documents)
        {
            var bytes = _store.ReadObject(hash);
            if (target.StartsWith("notes/", StringComparison.Ordinal)) { var id = Guid.Parse(Path.GetFileNameWithoutExtension(target)); notes.Add(id, NoteStore.ParseNote(bytes, id)); }
            else if (target == "groups.json") groups = NoteStore.ParseGroups(bytes);
            else if (target == "device/windows.json") layout = HistoryStore.Decode<DeviceState>(bytes);
        }
        return new HistoryFrame { Sequence = sequence, Notes = notes, Groups = groups, Layout = layout };
    }
    public (double Left, double Top, double Right, double Bottom) DesktopBounds(Guid? noteId = null)
    {
        double left = -100, top = -100, right = 1180, bottom = 700;
        var found = false;
        var hashes = Events.Where(e => e.Target == "device/windows.json" && e.After is not null).Select(e => e.After!)
            .Concat(_baseline.Documents.TryGetValue("device/windows.json", out var initial) ? [initial] : []).Distinct();
        foreach (var hash in hashes)
            foreach (var pair in HistoryStore.Decode<DeviceState>(_store.ReadObject(hash)).Windows.Where(p => noteId is null || p.Key == noteId))
            {
                var state = pair.Value;
                if (double.IsFinite(state.Left) && double.IsFinite(state.Top) && Math.Abs(state.Left) < 1_000_000 && Math.Abs(state.Top) < 1_000_000)
                {
                    var nextRight = state.Left + (double.IsFinite(state.Width) ? Math.Clamp(state.Width, 280, 650) : 390) + 50;
                    var nextBottom = state.Top + (double.IsFinite(state.Height) ? Math.Clamp(state.Height, 180, 600) : 320) + 50;
                    if (noteId is not null && !found) { left = state.Left - 50; top = state.Top - 50; right = nextRight; bottom = nextBottom; }
                    else { left = Math.Min(left, state.Left - 50); top = Math.Min(top, state.Top - 50); right = Math.Max(right, nextRight); bottom = Math.Max(bottom, nextBottom); }
                    found = true;
                }
            }
        if (noteId is not null) return found ? (left, top, right, bottom) : (30, 30, 520, 450);
        var count = _baseline.Documents.Keys.Concat(Events.Select(e => e.Target)).Where(t => t.StartsWith("notes/", StringComparison.Ordinal)).Distinct().Count();
        right = Math.Max(right, 80 + Math.Min(2, Math.Max(0, count - 1)) * 410 + 450);
        bottom = Math.Max(bottom, 80 + Math.Max(0, count - 1) / 3 * 350 + 350);
        return (left, top, right, bottom);
    }
}
