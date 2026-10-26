using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sticky.Core;
using Sticky.Storage;

namespace Sticky.Sync.Git;

public sealed class SyncSettings
{
    public bool Enabled { get; set; }
    public string RemoteUrl { get; set; } = "";
    public string Branch { get; set; } = "sticky-data";
    public DateTimeOffset? LastSuccess { get; set; }
}

public sealed class SyncSnapshot
{
    public Dictionary<Guid, Note> Notes { get; set; } = [];
    public List<NoteGroup> Groups { get; set; } = [];
    [JsonIgnore] public Dictionary<string, byte[]> Assets { get; } = new(StringComparer.Ordinal);

    public SyncSnapshot Clone()
    {
        var result = SyncJson.Read<SyncSnapshot>(SyncJson.Bytes(this));
        foreach (var (id, bytes) in Assets) result.Assets[id] = bytes;
        return result;
    }
}

public static class SyncJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, IgnoreReadOnlyProperties = true, MaxDepth = 48
    };
    public static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    public static T Read<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new InvalidDataException("同步数据为空。");
    public static bool Equal<T>(T? a, T? b) => Bytes(a).AsSpan().SequenceEqual(Bytes(b));
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    public static void WriteAtomic(string path, byte[] bytes, string dataRoot) => WorkspaceFiles.WriteAtomic(dataRoot, path, bytes);
}

public sealed record MergeResult(SyncSnapshot Snapshot, int ConflictCount);
public sealed record SyncResult(int ConflictCount, bool Uploaded);
