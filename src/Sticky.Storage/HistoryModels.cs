using Sticky.Core;

namespace Sticky.Storage;

public sealed record HistoryContext(string Source = "local", string? BatchId = null, string? Reason = null);
public sealed class HistoryOptions
{
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
    public Action<string>? CommitCheckpoint { get; init; }
}
public sealed class HistoryEvent
{
    public int Format { get; set; } = 1;
    public string Integrity { get; set; } = "";
    public long Sequence { get; set; }
    public DateTimeOffset Time { get; set; }
    public string Target { get; set; } = "";
    public string? Before { get; set; }
    public string? After { get; set; }
    public string Kind { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Changes { get; set; } = [];
    public string Source { get; set; } = "local";
    public string? BatchId { get; set; }
    public string? Reason { get; set; }
    public List<Guid> NoteIds { get; set; } = [];
    public string NoteTitle { get; set; } = "";
}
public sealed class HistoryCheckpoint
{
    public int Format { get; set; } = 1;
    public long Sequence { get; set; }
    public DateTimeOffset Time { get; set; }
    public string Integrity { get; set; } = "";
    public Dictionary<string, string> Documents { get; set; } = new(StringComparer.Ordinal);
}
public sealed record HistoryRow(HistoryEvent First, HistoryEvent Last, int Count)
{
    public long Sequence => Last.Sequence;
    public string Heading => Count > 1 ? $"{Last.Summary} · {Count} 次保存" : Last.Summary;
    public string Description => $"{First.Time.ToLocalTime():MM-dd HH:mm:ss}" + (Count > 1 ? $" — {Last.Time.ToLocalTime():HH:mm:ss}" : "")
        + " · " + HistoryDescriptions.SourceLabel(Last.Source);
}
public sealed class HistoryFrame
{
    public long Sequence { get; init; }
    public Dictionary<Guid, Note> Notes { get; init; } = [];
    public List<NoteGroup> Groups { get; init; } = [];
    public DeviceState Layout { get; init; } = new();
}

public static class HistoryDescriptions
{
    public static string SourceLabel(string source) => source switch
    { "sync" => "同步传入", "import" => "备份导入", "restore" => "历史恢复", "baseline" => "初始状态", "recovery" => "数据恢复", "external" => "外部或旧版变更", _ => "本机操作" };

    internal static HistoryEvent Describe(string target, byte[]? before, byte[] after, HistoryContext context)
    {
        var entry = new HistoryEvent { Target = target, Source = context.Source, BatchId = context.BatchId, Reason = context.Reason };
        if (target.StartsWith("notes/", StringComparison.Ordinal))
        {
            var id = Guid.Parse(Path.GetFileNameWithoutExtension(target));
            var next = NoteStore.ParseNote(after, id);
            Note? prior = null;
            if (before is not null) try { prior = NoteStore.ParseNote(before, id); } catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
            entry.NoteIds.Add(id); entry.NoteTitle = Short(next.DisplayTitle);
            entry.Kind = prior is null ? "noteCreated" : "noteEdited";
            if (prior is null) entry.Changes.Add("新建便签");
            else
            {
                if (prior.Title != next.Title) entry.Changes.Add($"标题：{Short(prior.DisplayTitle)} → {Short(next.DisplayTitle)}");
                if (prior.Color != next.Color) entry.Changes.Add($"颜色：{prior.Color} → {next.Color}");
                if (prior.GroupId != next.GroupId) { entry.Kind = "noteMoved"; entry.Changes.Add("移动到其他分组"); }
                if (prior.DeletedAt is null && next.DeletedAt is not null) { entry.Kind = "noteDeleted"; entry.Changes.Add("移入回收站"); }
                if (prior.DeletedAt is not null && next.DeletedAt is null) { entry.Kind = "noteRestored"; entry.Changes.Add("从回收站恢复"); }
                if (!HistoryStore.Equal(prior.Blocks, next.Blocks))
                {
                    var diff = TextDifference(prior.PlainText, next.PlainText);
                    if (diff.Removed.Length > 0) entry.Changes.Add("删除文字：" + diff.Removed);
                    if (diff.Added.Length > 0) entry.Changes.Add("新增文字：" + diff.Added);
                    if (prior.PlainText == next.PlainText) entry.Changes.Add("调整文字样式、段落或图片布局");
                    if (!ImageIds(prior.Blocks).SequenceEqual(ImageIds(next.Blocks))) entry.Changes.Add("变更图片");
                }
            }
            if (context.Source == "restore") { entry.Kind = "historyRestored"; entry.Changes.Insert(0, context.Reason ?? "恢复历史版本"); }
            entry.Summary = (entry.Kind switch { "noteCreated" => "新建", "noteDeleted" => "删除", "noteRestored" => "恢复", "noteMoved" => "移动分组", "historyRestored" => "恢复历史", _ => "修改" }) + " · " + entry.NoteTitle;
        }
        else if (target == "groups.json")
        {
            var old = before is null ? [] : NoteStore.ParseGroups(before);
            var next = NoteStore.ParseGroups(after);
            entry.Kind = "groupsChanged";
            foreach (var group in next)
            {
                var prior = old.FirstOrDefault(g => g.Id == group.Id);
                if (prior is null) entry.Changes.Add("新建分组：" + group.Name);
                else if (prior.Name != group.Name) entry.Changes.Add($"重命名分组：{prior.Name} → {group.Name}");
            }
            foreach (var removed in old.Where(g => !next.Any(n => n.Id == g.Id))) entry.Changes.Add("移除分组：" + removed.Name);
            entry.Summary = string.Join("；", entry.Changes);
        }
        else
        {
            var old = before is null ? new DeviceState() : HistoryStore.Decode<DeviceState>(before);
            var next = HistoryStore.Decode<DeviceState>(after);
            entry.Kind = "windowMoved";
            foreach (var (id, state) in next.Windows)
            {
                if (old.Windows.TryGetValue(id, out var prior) && HistoryStore.Equal(prior, state)) continue;
                entry.NoteIds.Add(id);
                if (prior is null || prior.IsOpen != state.IsOpen) entry.Changes.Add(state.IsOpen ? "打开便签窗口" : "关闭便签窗口");
                else
                {
                    if (Math.Abs(prior.Left - state.Left) > 0.1 || Math.Abs(prior.Top - state.Top) > 0.1) entry.Changes.Add($"位置：({prior.Left:0}, {prior.Top:0}) → ({state.Left:0}, {state.Top:0})");
                    if (Math.Abs(prior.Width - state.Width) > 0.1 || Math.Abs(prior.Height - state.Height) > 0.1) entry.Changes.Add($"大小：{state.Width:0} × {state.Height:0}");
                    if (prior.Topmost != state.Topmost) entry.Changes.Add(state.Topmost ? "窗口置顶" : "取消置顶");
                }
            }
            entry.Summary = "窗口位置与状态 · " + entry.NoteIds.Count + " 张便签";
        }
        return entry;
    }
    private static string Short(string value) => value.Length > 160 ? value[..160] + "…" : value;
    public static (string Removed, string Added) TextDifference(string before, string after)
    {
        var prefix = 0;
        while (prefix < Math.Min(before.Length, after.Length) && before[prefix] == after[prefix]) prefix++;
        // Keep a UTF-16 surrogate pair together at the change boundary.
        if (prefix > 0 && prefix < before.Length && char.IsLowSurrogate(before[prefix])) prefix--;
        var suffix = 0;
        while (suffix < Math.Min(before.Length, after.Length) - prefix && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix]) suffix++;
        if (suffix > 0 && char.IsLowSurrogate(before[before.Length - suffix])) suffix--;
        string Clip(string text) => text.Length > 4000 ? text[..4000] + "…" : text;
        return (Clip(before.Substring(prefix, before.Length - prefix - suffix)), Clip(after.Substring(prefix, after.Length - prefix - suffix)));
    }
    private static IEnumerable<string> ImageIds(IEnumerable<NoteBlock> blocks)
    {
        foreach (var block in blocks)
        {
            foreach (var inline in block.Inlines) if (inline.AssetId is { } id) yield return id;
            foreach (var id in ImageIds(block.Children)) yield return id;
        }
    }
}
