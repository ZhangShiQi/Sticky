using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sticky.Core;

namespace Sticky.Storage;

// Immutable, content-addressed documents + a write-ahead record for each materialized JSON update.
// Git only sees current note files. History remains a local subsystem, including while offline.
public sealed class HistoryStore
{
    private readonly string _root;
    private readonly HistoryOptions _options;
    private readonly AsyncLocal<HistoryContext?> _context = new();
    public string DirectoryPath => Path.Combine(_root, "history");
    public List<string> Warnings { get; } = [];
    public event Action? Changed;
    public HistoryStore(string root, HistoryOptions? options = null) { _root = Path.GetFullPath(root); _options = options ?? new(); }
    internal static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, NoteStore.JsonOptions);
    internal static T Decode<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, NoteStore.JsonOptions) ?? throw new InvalidDataException("历史记录为空。");
    internal static bool Equal<T>(T left, T right) => Encode(left).AsSpan().SequenceEqual(Encode(right));
    private string FilePath(string relative) => Path.Combine(DirectoryPath, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static bool ValidCheckpoint(HistoryCheckpoint checkpoint) => checkpoint.Format == 1 && checkpoint.Documents is not null
        && checkpoint.Integrity == CheckpointHash(checkpoint);
    private static string CheckpointHash(HistoryCheckpoint checkpoint) => Hash(Encode(new HistoryCheckpoint
    { Format = checkpoint.Format, Sequence = checkpoint.Sequence, Time = checkpoint.Time, Documents = checkpoint.Documents.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value) }));
    private static string EventHash(HistoryEvent entry) => Hash(Encode(new HistoryEvent
    {
        Format = entry.Format, Sequence = entry.Sequence, Time = entry.Time, Target = entry.Target, Before = entry.Before, After = entry.After,
        Kind = entry.Kind, Summary = entry.Summary, Changes = entry.Changes, Source = entry.Source, BatchId = entry.BatchId, Reason = entry.Reason,
        NoteIds = entry.NoteIds, NoteTitle = entry.NoteTitle
    }));
    private void Write<T>(string relative, T value)
    {
        if (value is HistoryCheckpoint checkpoint) checkpoint.Integrity = CheckpointHash(checkpoint);
        if (value is HistoryEvent entry) entry.Integrity = EventHash(entry);
        WorkspaceFiles.WriteAtomic(_root, FilePath(relative), Encode(value), keepBackup: false);
    }
    private IDisposable Lock()
    {
        Directory.CreateDirectory(DirectoryPath);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(FilePath("writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (clock.Elapsed < TimeSpan.FromSeconds(3)) { Thread.Sleep(20); }
        }
    }
    public IDisposable WithContext(string source, string? batchId = null, string? reason = null)
    {
        var old = _context.Value; _context.Value = new(source, batchId, reason);
        return new ContextScope(() => _context.Value = old);
    }
    private sealed class ContextScope(Action close) : IDisposable { public void Dispose() => close(); }
    private string Object(byte[] bytes)
    {
        var hash = Hash(bytes); var path = FilePath("objects/" + hash + ".json");
        if (File.Exists(path)) { if (Hash(File.ReadAllBytes(path)) != hash) throw new InvalidDataException("历史对象校验失败，未覆盖：" + hash); }
        else WorkspaceFiles.WriteAtomic(_root, path, bytes, keepBackup: false);
        return hash;
    }
    internal byte[] ReadObject(string hash)
    {
        if (!Regex.IsMatch(hash, "^[a-f0-9]{64}$")) throw new InvalidDataException("历史对象标识无效。");
        var bytes = File.ReadAllBytes(FilePath("objects/" + hash + ".json"));
        if (Hash(bytes) != hash) throw new InvalidDataException("历史对象损坏，当前便签未被修改。");
        return bytes;
    }
    private static bool ValidTarget(string relative) => relative is "groups.json" or "device/windows.json"
        || Regex.IsMatch(relative, @"^notes/[a-fA-F0-9-]{36}\.json$") && Guid.TryParse(Path.GetFileNameWithoutExtension(relative), out _);
    private string DataFile(string target) => ValidTarget(target) ? Path.Combine(_root, target.Replace('/', Path.DirectorySeparatorChar)) : throw new InvalidDataException("历史目标路径无效。");
    private void Initialize()
    {
        if (File.Exists(FilePath("baseline.json")))
        {
            var existing = Decode<HistoryCheckpoint>(File.ReadAllBytes(FilePath("baseline.json")));
            if (!ValidCheckpoint(existing) || existing.Sequence != 0) throw new InvalidDataException("本地历史格式无法读取，原文件已保留。");
            return;
        }
        var baseline = new HistoryCheckpoint { Time = _options.Clock() };
        void Add(string relative)
        {
            var path = DataFile(relative);
            if (!File.Exists(path)) return;
            var bytes = File.ReadAllBytes(path);
            try
            {
                if (relative.StartsWith("notes/", StringComparison.Ordinal)) _ = NoteStore.ParseNote(bytes, Guid.Parse(Path.GetFileNameWithoutExtension(relative)));
                else if (relative == "groups.json") _ = NoteStore.ParseGroups(bytes);
                else _ = Decode<DeviceState>(bytes);
                baseline.Documents.Add(relative, Object(bytes));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or NoteStore.UnsupportedVersionException) { Warnings.Add("初始历史未包含无法读取的文件：" + relative); }
        }
        foreach (var path in Directory.EnumerateFiles(Path.Combine(_root, "notes"), "*.json"))
            if (Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var id)) Add("notes/" + id + ".json");
        Add("groups.json"); Add("device/windows.json");
        Write("baseline.json", baseline);
    }
    private static void ValidateEvent(HistoryEvent entry)
    {
        if (entry.Format != 1 || entry.Sequence <= 0 || !ValidTarget(entry.Target) || entry.NoteIds is null || entry.Changes is null || entry.Integrity != EventHash(entry)
            || entry.Before is null && entry.After is null || entry.After is not null && !Regex.IsMatch(entry.After, "^[a-f0-9]{64}$")
            || entry.Before is not null && !Regex.IsMatch(entry.Before, "^[a-f0-9]{64}$"))
            throw new InvalidDataException("不支持或损坏的历史事件。");
    }
    private long Head()
    {
        if (File.Exists(FilePath("head.json")))
        {
            try { var head = Decode<long>(File.ReadAllBytes(FilePath("head.json"))); if (head >= 0) return head; }
            catch (JsonException) { }
        }
        return Directory.Exists(FilePath("events")) ? Directory.EnumerateFiles(FilePath("events"), "*.json")
            .Select(p => long.TryParse(Path.GetFileNameWithoutExtension(p), out var number) ? number : 0).DefaultIfEmpty().Max() : 0;
    }
    private void Finalize(HistoryEvent entry)
    {
        ValidateEvent(entry);
        var name = $"events/{entry.Sequence:D20}.json";
        if (File.Exists(FilePath(name)))
        { if (!Equal(Decode<HistoryEvent>(File.ReadAllBytes(FilePath(name))), entry)) throw new IOException("历史序号冲突，原记录已保留。"); }
        else Write(name, entry);
        _options.CommitCheckpoint?.Invoke("event-written");
        Write("head.json", Math.Max(Head(), entry.Sequence));
        File.Delete(FilePath("pending.json"));
    }
    private void Recover()
    {
        if (!File.Exists(FilePath("pending.json"))) return;
        var pending = Decode<HistoryEvent>(File.ReadAllBytes(FilePath("pending.json"))); ValidateEvent(pending);
        var data = DataFile(pending.Target);
        var actual = File.Exists(data) ? Hash(File.ReadAllBytes(data)) : null;
        if (actual == pending.After) Finalize(pending);
        else if (actual == pending.Before) File.Delete(FilePath("pending.json"));
        else
        {
            Directory.CreateDirectory(FilePath("recovery"));
            File.Move(FilePath("pending.json"), FilePath("recovery/ambiguous-" + Guid.NewGuid().ToString("N") + ".json"));
            Warnings.Add("一条未完成记录与当前文件不一致，已隔离保留，当前文件未被覆盖。");
        }
    }
    private static bool SameState(string target, byte[]? before, byte[]? after)
    {
        if (after is null) return before is null;
        if (before is null)
        {
            if (target == "groups.json" && NoteStore.ParseGroups(after).Count == 0) return true;
            if (target == "device/windows.json" && Decode<DeviceState>(after).Windows.Count == 0) return true;
            return false;
        }
        if (target.StartsWith("notes/", StringComparison.Ordinal))
        {
            var id = Guid.Parse(Path.GetFileNameWithoutExtension(target));
            try
            {
                var old = NoteStore.ParseNote(before, id); var next = NoteStore.ParseNote(after, id);
                next.ModifiedAt = old.ModifiedAt;
                return Equal(old, next);
            }
            catch (Exception ex) when (ex is JsonException or IOException or NoteStore.UnsupportedVersionException) { return false; }
        }
        return before.AsSpan().SequenceEqual(after);
    }
    internal void WriteDocument(string target, byte[]? after, Action writeData)
    {
        using (Lock())
        {
            Initialize(); Recover();
            var file = DataFile(target);
            var before = File.Exists(file) ? File.ReadAllBytes(file) : null;
            if (SameState(target, before, after)) { writeData(); return; }
            var context = _context.Value ?? new();
            var entry = after is not null ? HistoryDescriptions.Describe(target, before, after, context)
                : new HistoryEvent { Target = target, Kind = "noteRemoved", Source = context.Source, BatchId = context.BatchId, Reason = context.Reason,
                    Summary = "撤回未完成导入", Changes = ["撤回未完成导入，原有便签未被修改"], NoteIds = [Guid.Parse(Path.GetFileNameWithoutExtension(target))] };
            entry.Sequence = checked(Head() + 1); entry.Time = _options.Clock();
            entry.Before = before is null ? null : Object(before); entry.After = after is null ? null : Object(after);
            if (target == "groups.json" && after is not null)
            {
                var old = before is null ? [] : NoteStore.ParseGroups(before); var next = NoteStore.ParseGroups(after);
                var changed = old.Concat(next).Select(g => g.Id).Distinct().Where(id => old.FirstOrDefault(g => g.Id == id)?.Name != next.FirstOrDefault(g => g.Id == id)?.Name).ToHashSet();
                foreach (var path in Directory.EnumerateFiles(Path.Combine(_root, "notes"), "*.json"))
                    try { var note = NoteStore.ParseNote(File.ReadAllBytes(path), Guid.Parse(Path.GetFileNameWithoutExtension(path))); if (note.GroupId is { } id && changed.Contains(id)) entry.NoteIds.Add(note.Id); }
                    catch (Exception ex) when (ex is IOException or JsonException or FormatException or NoteStore.UnsupportedVersionException) { }
            }
            Write("pending.json", entry);
            _options.CommitCheckpoint?.Invoke("prepared");
            try { writeData(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A failed write before atomic replacement must not become a historical modification.
                var actual = File.Exists(file) ? Hash(File.ReadAllBytes(file)) : null;
                if (actual != entry.After)
                {
                    if (actual == entry.Before) File.Delete(FilePath("pending.json"));
                    throw;
                }
                Warnings.Add("便签已保存，但附加备份未完成：" + error.Message);
            }
            _options.CommitCheckpoint?.Invoke("state-written");
            try { Finalize(entry); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warnings.Add("历史提交待完成；便签已保存，下次访问历史时将补齐记录。"); }
            if (entry.Sequence % 100 == 0)
                try { var state = ReadTimelineLocked().ProjectReferences(entry.Sequence); Write($"checkpoints/{entry.Sequence:D20}.json", state); }
                catch (Exception ex) when (ex is IOException or JsonException) { Warnings.Add("历史检查点未生成，仍可从事件重建。"); }
        }
        try { Changed?.Invoke(); } catch (Exception) { Warnings.Add("历史视图未能及时刷新；便签已保存。"); }
    }
    public HistoryTimeline ReadTimeline()
    {
        using (Lock()) { Initialize(); Recover(); return ReadTimelineLocked(); }
    }
    private HistoryTimeline ReadTimelineLocked()
    {
        var baseline = Decode<HistoryCheckpoint>(File.ReadAllBytes(FilePath("baseline.json")));
        if (!ValidCheckpoint(baseline)) throw new InvalidDataException("初始历史快照校验失败，原文件已保留。");
        var events = new List<HistoryEvent>();
        long expectedSequence = 1;
        if (Directory.Exists(FilePath("events")))
            foreach (var path in Directory.EnumerateFiles(FilePath("events"), "*.json").Order(StringComparer.Ordinal))
            {
                var entry = Decode<HistoryEvent>(File.ReadAllBytes(path)); ValidateEvent(entry);
                if (Path.GetFileNameWithoutExtension(path) != entry.Sequence.ToString("D20", System.Globalization.CultureInfo.InvariantCulture)) throw new InvalidDataException("历史事件序号与文件不匹配。");
                if (entry.Sequence != expectedSequence++) throw new InvalidDataException("历史事件有缺失，已停止重放以免展示不完整的状态。");
                events.Add(entry);
            }
        if (Head() != (events.LastOrDefault()?.Sequence ?? 0)) throw new InvalidDataException("历史索引与事件不一致，原文件已保留。");
        return new HistoryTimeline(this, baseline, events, FilePath("checkpoints"));
    }
    public void Flush() { using (Lock()) { Initialize(); Recover(); } }

    // Older app versions and manual backup restoration may change current JSON without history.
    // Observe the difference, never overwrite current data with an old historical projection.
    public void Reconcile()
    {
        var changed = false;
        using (Lock())
        {
            Initialize(); Recover();
            var timeline = ReadTimelineLocked();
            var refs = timeline.ProjectReferences(timeline.Events.LastOrDefault()?.Sequence ?? 0).Documents;
            var targets = refs.Keys.Concat(Directory.EnumerateFiles(Path.Combine(_root, "notes"), "*.json")
                .Where(p => Guid.TryParse(Path.GetFileNameWithoutExtension(p), out _)).Select(p => "notes/" + Guid.Parse(Path.GetFileNameWithoutExtension(p)) + ".json"))
                .Concat(new[] { "groups.json", "device/windows.json" }).Distinct().ToList();
            foreach (var target in targets)
            {
                var path = DataFile(target); var current = File.Exists(path) ? File.ReadAllBytes(path) : null;
                refs.TryGetValue(target, out var oldHash);
                if (current is null && oldHash is null || current is not null && Hash(current) == oldHash) continue;
                var before = oldHash is null ? null : ReadObject(oldHash);
                if (SameState(target, before, current)) continue;
                HistoryEvent entry;
                try
                {
                    entry = current is null ? new HistoryEvent { Target = target, Kind = "externalRemoved", Source = "external", Summary = "检测到文件移除 · " + target,
                        Changes = ["其他版本或外部工具移除了此文件；检测时记录，未修改当前内容"], NoteIds = target.StartsWith("notes/", StringComparison.Ordinal) ? [Guid.Parse(Path.GetFileNameWithoutExtension(target))] : [] }
                        : HistoryDescriptions.Describe(target, before, current, new HistoryContext("external", Reason: "启动或刷新时检测到，事件时间为本机检测时间"));
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or NoteStore.UnsupportedVersionException)
                { Warnings.Add("当前文件无法纳入历史，原文件已保留：" + target); continue; }
                entry.Sequence = checked(Head() + 1); entry.Time = _options.Clock(); entry.Before = oldHash; entry.After = current is null ? null : Object(current);
                Write("pending.json", entry); Finalize(entry); changed = true;
            }
        }
        if (changed) try { Changed?.Invoke(); } catch (Exception) { Warnings.Add("历史视图未能及时刷新。"); }
    }

    public void Export(string destination)
    {
        if (!Path.GetExtension(destination).Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || Path.GetFullPath(destination).StartsWith(Path.GetFullPath(DirectoryPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择历史目录以外的 ZIP 备份路径。");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (Lock())
        {
            Initialize(); Recover();
            try
            {
                using (var archive = System.IO.Compression.ZipFile.Open(temporary, System.IO.Compression.ZipArchiveMode.Create))
                {
                    void Add(string path, string name)
                    {
                        if (!File.Exists(path)) return;
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("历史备份不支持文件链接。");
                        System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(archive, path, name);
                    }
                    void Folder(string directory, string prefix, string pattern)
                    {
                        if (!Directory.Exists(directory)) return;
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("历史备份不支持目录链接。");
                        foreach (var path in Directory.EnumerateFiles(directory, pattern)) Add(path, prefix + Path.GetFileName(path));
                    }
                    Add(Path.Combine(_root, "manifest.json"), "manifest.json"); Add(Path.Combine(_root, "groups.json"), "groups.json");
                    Add(Path.Combine(_root, "device", "windows.json"), "device/windows.json");
                    Folder(Path.Combine(_root, "notes"), "notes/", "*.json"); Folder(Path.Combine(_root, "assets"), "assets/", "*.png");
                    Add(FilePath("baseline.json"), "history/baseline.json"); Add(FilePath("head.json"), "history/head.json");
                    foreach (var name in new[] { "objects", "events", "checkpoints", "recovery" }) Folder(FilePath(name), "history/" + name + "/", "*.json");
                    using var writer = new StreamWriter(archive.CreateEntry("history-backup-readme.txt").Open());
                    writer.Write("Sticky 便签与本地历史备份。恢复：退出 Sticky，将此 ZIP 完整解压到新的空 Data 目录，再从该工作空间启动 Sticky。不要与正在使用的 Data 合并，也不要通过普通便签导入入口导入此文件。同步配置、凭据和 Git 缓存不包含在备份中。复制图片只保存一份，历史版本通过内容标识共享图片。");
                }
                File.Move(temporary, destination, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
