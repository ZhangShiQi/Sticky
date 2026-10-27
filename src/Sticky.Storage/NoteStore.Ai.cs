using System.Text.Json;
using System.Text.Json.Serialization;
using Sticky.Core;

namespace Sticky.Storage;

public sealed partial class NoteStore
{
    public AiCatalog? LoadAiCatalog()
    {
        var path = Path.Combine(Root, "ai.json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("AI 配置文件过大。");
        return ParseAiCatalog(File.ReadAllBytes(path));
    }
    public static AiCatalog ParseAiCatalog(byte[] bytes)
    {
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("AI 配置文件过大。");
        var options = new JsonSerializerOptions(JsonOptions) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var catalog = JsonSerializer.Deserialize<AiCatalog>(bytes, options) ?? throw new InvalidDataException("AI 配置为空。");
        AiCatalogRules.Validate(catalog);
        catalog.Connections = catalog.Connections.OrderBy(c => c.Id).ToList(); catalog.Actions = catalog.Actions.OrderBy(a => a.Id).ToList();
        return catalog;
    }
    public void SaveAiCatalog(AiCatalog catalog)
    {
        var normalized = ParseAiCatalog(JsonSerializer.SerializeToUtf8Bytes(catalog, JsonOptions));
        WorkspaceFiles.WriteAtomic(Root, Path.Combine(Root, "ai.json"), JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions));
    }
}
