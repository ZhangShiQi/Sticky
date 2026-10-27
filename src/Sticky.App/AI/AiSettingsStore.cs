using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sticky.Core;
using Sticky.Storage;
using Sticky.Sync.Git;

namespace Sticky.App.AI;

public sealed class AiConfiguration
{
    public Guid ConnectionId { get; init; } = AiConnection.DefaultId;
    public string ConnectionName { get; init; } = "默认连接";
    public bool Enabled { get; init; }
    public string BaseUrl { get; init; } = "";
    public string Model { get; init; } = "";
    [JsonIgnore] public string ApiKey { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 60;
    public bool ThinkingEnabled { get; init; }
    public string ThinkingControl { get; init; } = "auto";
    [JsonIgnore] public string EffectiveThinkingControl => ThinkingControl == "auto"
        ? CompletionUri().Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase) ? "deepseek" : "none" : ThinkingControl;
    [JsonIgnore] public bool CanRun { get { try { Validate(true); return Enabled; } catch (AiException) { return false; } } }
    public Uri CompletionUri()
    { try { return AiCatalogRules.CompletionUri(BaseUrl); } catch (InvalidDataException ex) { throw new AiException(ex.Message); } }
    public AiConnection Connection() => new() { Id = ConnectionId, Name = ConnectionName, Enabled = Enabled, BaseUrl = BaseUrl.Trim(), Model = Model.Trim(),
        TimeoutSeconds = TimeoutSeconds, ThinkingEnabled = ThinkingEnabled, ThinkingControl = ThinkingControl };
    public void Validate(bool connectionRequired = false)
    {
        try { AiCatalogRules.Validate(new AiCatalog { Connections = [Connection()] }); }
        catch (InvalidDataException ex) { throw new AiException(ex.Message); }
        if (ApiKey.Length > 8192 || ApiKey.Any(c => c <= 32 || c > 126)) throw new AiException("API 密钥格式不正确，请勿包含空格或换行。");
        if (Enabled || connectionRequired)
        {
            var uri = CompletionUri();
            if (!uri.IsLoopback && ApiKey.Length == 0) throw new AiException("请填写本机 API 密钥；密钥不会随配置同步，本机免密服务可以留空。");
            if (ThinkingEnabled && EffectiveThinkingControl == "none") throw new AiException("当前服务未识别思考控制格式，请选择 DeepSeek 或 reasoning_effort 格式，或关闭思考模式。");
        }
    }
}

public sealed class AiException(string message) : Exception(message);

public sealed class AiSettingsStore
{
    private readonly NoteStore _store;
    public AiSettingsStore(string root) { _store = new NoteStore(root); }
    public string FilePath => Path.Combine(_store.Root, "device", "ai.json");
    public string? Warning { get; private set; }
    public event Action? Changed;
    private sealed class Saved
    {
        public int Format { get; set; } = 2;
        public Guid? ActiveConnectionId { get; set; }
        public Dictionary<string, string> ProtectedKeys { get; set; } = new(StringComparer.Ordinal);
    }
    private sealed class Legacy
    {
        public bool Enabled { get; set; }
        public string BaseUrl { get; set; } = "";
        public string Model { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 60;
        public string ProtectedKey { get; set; } = "";
    }
    private static byte[] Entropy(string endpoint) => Encoding.UTF8.GetBytes("Sticky.AI/v1|" + endpoint);
    private void WritePrivate(Saved saved) => WorkspaceFiles.WriteAtomic(_store.Root, FilePath,
        JsonSerializer.SerializeToUtf8Bytes(saved, new JsonSerializerOptions { WriteIndented = true }), keepBackup: false);
    private Saved ReadPrivate()
    {
        if (!File.Exists(FilePath)) return new();
        if (new FileInfo(FilePath).Length > 2 * 1024 * 1024) throw new InvalidDataException("本机 AI 密钥文件过大。");
        var bytes = File.ReadAllBytes(FilePath);
        using var json = JsonDocument.Parse(bytes);
        var format = json.RootElement.GetProperty("Format").GetInt32();
        if (format == 1)
        {
            var legacy = JsonSerializer.Deserialize<Legacy>(bytes) ?? throw new InvalidDataException("旧版 AI 配置为空。");
            var saved = new Saved();
            if (!string.IsNullOrEmpty(legacy.BaseUrl) || !string.IsNullOrEmpty(legacy.Model) || legacy.Enabled)
            {
                var connection = new AiConnection { Enabled = legacy.Enabled, BaseUrl = legacy.BaseUrl, Model = legacy.Model, TimeoutSeconds = legacy.TimeoutSeconds };
                var catalog = new AiCatalog { Connections = [connection], Actions = [AiAction.Polish()] };
                AiCatalogRules.Validate(catalog);
                var existing = _store.LoadAiCatalog();
                var merged = AiCatalogMerger.Merge(null, catalog, existing).Catalog!;
                if (!SyncJson.Equal(existing, merged)) _store.SaveAiCatalog(merged);
                saved.ActiveConnectionId = merged.Connections.First(c => c.BaseUrl == connection.BaseUrl && c.Model == connection.Model).Id;
                if (!string.IsNullOrEmpty(legacy.ProtectedKey))
                    saved.ProtectedKeys[AiCatalogRules.CompletionUri(legacy.BaseUrl).AbsoluteUri] = legacy.ProtectedKey;
            }
            WritePrivate(saved); return saved;
        }
        var current = JsonSerializer.Deserialize<Saved>(bytes) ?? throw new InvalidDataException("本机 AI 配置为空。");
        if (format != 2 || current.ProtectedKeys is null || current.ProtectedKeys.Count > 128) throw new InvalidDataException("本机 AI 配置格式无效。");
        return current;
    }
    public AiCatalog? ExportShared()
    { _ = ReadPrivate(); return _store.LoadAiCatalog(); }
    public AiCatalog EditingCatalog() => ExportShared() ?? new AiCatalog { Actions = [AiAction.Polish()] };
    public void ApplyShared(AiCatalog? catalog)
    {
        if (catalog is null || SyncJson.Equal(_store.LoadAiCatalog(), catalog)) return;
        _store.SaveAiCatalog(catalog); Changed?.Invoke();
    }
    public AiConfiguration Load(Guid? connectionId = null)
    {
        Warning = null;
        try
        {
            var saved = ReadPrivate(); var catalog = _store.LoadAiCatalog();
            var connections = catalog?.Connections.Where(c => c.DeletedAt is null).ToList() ?? [];
            var connection = connections.FirstOrDefault(c => c.Id == (connectionId ?? saved.ActiveConnectionId))
                ?? connections.FirstOrDefault() ?? new AiConnection();
            var key = "";
            if (connection.BaseUrl.Length > 0)
            {
                var endpoint = AiCatalogRules.CompletionUri(connection.BaseUrl).AbsoluteUri;
                if (saved.ProtectedKeys.TryGetValue(endpoint, out var encrypted) && encrypted.Length > 0)
                {
                    try
                    {
                        var plain = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), Entropy(endpoint), DataProtectionScope.CurrentUser);
                        try { key = Encoding.UTF8.GetString(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
                    }
                    catch (Exception ex) when (ex is CryptographicException or FormatException)
                    { Warning = "此密钥无法在当前 Windows 用户下解密，请重新输入本机密钥。原文件已保留。"; }
                }
                if (connection.Enabled && key.Length == 0 && !AiCatalogRules.CompletionUri(connection.BaseUrl).IsLoopback)
                    Warning ??= "已载入共享 AI 配置；当前设备尚无此服务的密钥，请填写后保存。";
            }
            return new AiConfiguration { ConnectionId = connection.Id, ConnectionName = connection.Name, Enabled = connection.Enabled,
                BaseUrl = connection.BaseUrl, Model = connection.Model, ApiKey = key, TimeoutSeconds = connection.TimeoutSeconds,
                ThinkingEnabled = connection.ThinkingEnabled, ThinkingControl = connection.ThinkingControl };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or AiException or InvalidOperationException or KeyNotFoundException)
        { Warning = "AI 配置暂不可读取，原文件已保留。请检查后重新配置。"; return new(); }
    }
    public void SelectConnection(Guid id)
    { var saved = ReadPrivate(); saved.ActiveConnectionId = id; WritePrivate(saved); }
    public void Save(AiConfiguration configuration, AiCatalog? baseline = null)
    {
        configuration.Validate();
        var current = ExportShared(); var basis = baseline ?? current;
        var draft = SyncJson.Read<AiCatalog>(SyncJson.Bytes(basis ?? new AiCatalog { Actions = [AiAction.Polish()] }));
        draft.Connections.RemoveAll(c => c.Id == configuration.ConnectionId); draft.Connections.Add(configuration.Connection());
        var merged = AiCatalogMerger.Merge(basis, draft, current).Catalog!;
        var connection = merged.Connections.FirstOrDefault(c => c.Id == configuration.ConnectionId && c.Name == configuration.ConnectionName && SameConnection(c, configuration.Connection()))
            ?? merged.Connections.First(c => c.Name == ConflictName(configuration.ConnectionName) && SameConnection(c, configuration.Connection()));
        var saved = ReadPrivate(); saved.ActiveConnectionId = connection.Id;
        if (configuration.BaseUrl.Length > 0)
        {
            var endpoint = configuration.CompletionUri().AbsoluteUri;
            if (configuration.ApiKey.Length > 0)
            {
                var plain = Encoding.UTF8.GetBytes(configuration.ApiKey);
                try { saved.ProtectedKeys[endpoint] = Convert.ToBase64String(ProtectedData.Protect(plain, Entropy(endpoint), DataProtectionScope.CurrentUser)); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            else saved.ProtectedKeys.Remove(endpoint);
        }
        // Persist the credential first. If the shared write fails, an unused protected key is harmless;
        // existing endpoints and their keys are retained, so no old key is sent to a new service.
        WritePrivate(saved); _store.SaveAiCatalog(merged); Changed?.Invoke();
    }
    private static bool SameConnection(AiConnection a, AiConnection b) => a.BaseUrl == b.BaseUrl && a.Model == b.Model && a.Enabled == b.Enabled
        && a.TimeoutSeconds == b.TimeoutSeconds && a.ThinkingEnabled == b.ThinkingEnabled && a.ThinkingControl == b.ThinkingControl;
    private static string ConflictName(string name) => name[..Math.Min(name.Length, 52)] + "（冲突副本）";
    public Guid SaveAction(AiAction action, AiCatalog baseline)
    {
        var current = ExportShared();
        var draft = SyncJson.Read<AiCatalog>(SyncJson.Bytes(baseline));
        draft.Actions.RemoveAll(a => a.Id == action.Id); draft.Actions.Add(action);
        var merged = AiCatalogMerger.Merge(baseline, draft, current).Catalog!;
        _store.SaveAiCatalog(merged); Changed?.Invoke();
        bool Matches(AiAction a) => a.Prompt == action.Prompt && a.Icon == action.Icon && a.DeletedAt == action.DeletedAt && a.Enabled == action.Enabled && a.Order == action.Order;
        return merged.Actions.FirstOrDefault(a => a.Id == action.Id && a.Name == action.Name && Matches(a))?.Id
            ?? merged.Actions.First(a => a.Name == ConflictName(action.Name) && Matches(a)).Id;
    }
    public void ForgetKey(AiConfiguration configuration)
    {
        var saved = ReadPrivate();
        if (configuration.BaseUrl.Length > 0) saved.ProtectedKeys.Remove(configuration.CompletionUri().AbsoluteUri);
        WritePrivate(saved); Changed?.Invoke();
    }
}
