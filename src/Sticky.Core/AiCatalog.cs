namespace Sticky.Core;

public sealed class AiCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<AiConnection> Connections { get; set; } = [];
    public List<AiAction> Actions { get; set; } = [];
}

// This model deliberately has no credential field. Only these records enter synchronization.
public sealed class AiConnection
{
    public static readonly Guid DefaultId = Guid.Parse("a1000000-0000-4000-8000-000000000001");
    public Guid Id { get; set; } = DefaultId;
    public Guid? ConflictOf { get; set; }
    public string Name { get; set; } = "默认连接";
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 60;
    public bool ThinkingEnabled { get; set; }
    public string ThinkingControl { get; set; } = "auto";
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class AiAction
{
    public static readonly Guid PolishId = Guid.Parse("a2000000-0000-4000-8000-000000000001");
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ConflictOf { get; set; }
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Icon { get; set; } = "sparkles";
    public bool Enabled { get; set; } = true;
    public int Order { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public static AiAction Polish() => new()
    {
        Id = PolishId, Name = "语法修正与润色", Icon = "edit",
        Prompt = "只修正语法、错别字、标点、口语赘词和不通顺的表达。保留原意、语气、事实、语言、段落和列表顺序。不翻译，不扩写，不添加事实，只返回修正后的正文。"
    };
}

public static class AiCatalogRules
{
    public static readonly string[] IconKeys = ["sparkles", "edit", "translate", "summary", "list", "idea", "formal", "code"];
    public static Uri CompletionUri(string baseUrl)
    {
        if (baseUrl is null || baseUrl.Length > 2048 || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidDataException("请输入 HTTPS API 地址；仅本机回环地址支持 HTTP，地址不能包含账号密码、查询参数或片段。");
        var path = uri.AbsoluteUri.TrimEnd('/');
        return new Uri(path.EndsWith("/chat/completions", StringComparison.Ordinal) ? path : path + "/chat/completions");
    }
    public static void Validate(AiCatalog catalog)
    {
        if (catalog.SchemaVersion != 1 || catalog.Connections is null || catalog.Actions is null || catalog.Connections.Count > 64 || catalog.Actions.Count > 512)
            throw new InvalidDataException("AI 配置格式或记录数量无效。");
        var ids = new HashSet<Guid>();
        foreach (var connection in catalog.Connections)
        {
            if (connection is null || connection.Id == Guid.Empty || !ids.Add(connection.Id)) throw new InvalidDataException("AI 连接标识重复或无效。");
            Name(connection.Name);
            if (connection.BaseUrl is null || connection.Model is null || connection.Model.Length > 200 || connection.Model.Any(char.IsControl)
                || connection.TimeoutSeconds is < 5 or > 180 || connection.ThinkingControl is not ("auto" or "deepseek" or "effort" or "none") || connection.ConflictOf == connection.Id)
                throw new InvalidDataException("AI 连接参数无效。");
            if (connection.Enabled || connection.BaseUrl.Length > 0 || connection.Model.Length > 0)
            { _ = CompletionUri(connection.BaseUrl); if (string.IsNullOrWhiteSpace(connection.Model)) throw new InvalidDataException("AI 模型名称不能为空。"); }
        }
        ids.Clear();
        foreach (var action in catalog.Actions)
        {
            if (action is null || action.Id == Guid.Empty || !ids.Add(action.Id)) throw new InvalidDataException("AI 功能标识重复或无效。");
            Name(action.Name);
            if (string.IsNullOrWhiteSpace(action.Prompt) || action.Prompt.Length > 8000 || !IconKeys.Contains(action.Icon, StringComparer.Ordinal)
                || action.Order is < 0 or > 100000 || action.ConflictOf == action.Id) throw new InvalidDataException("AI 提示词、图标或顺序无效。");
        }
    }
    private static void Name(string name)
    { if (string.IsNullOrWhiteSpace(name) || name.Length > 60 || name.Any(char.IsControl)) throw new InvalidDataException("名称应为 1–60 个字符，不能包含换行。"); }
}
