using System.Text.Json.Serialization;

namespace Sticky.Core;

public sealed class Note
{
    public const int RichTextVersion = 1;
    public const int MarkdownVersion = 2;
    public const int TaggedRichTextVersion = 3;
    public const int TaggedMarkdownVersion = 4;

    public int SchemaVersion { get; set; } = RichTextVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Color { get; set; } = "amber";
    [JsonPropertyName("groupId")]
    public Guid? DirectoryId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Guid>? TagIds { get; set; }
    public Guid? ConflictOf { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public List<NoteBlock> Blocks { get; set; } = [];
    private string? _markdownSource;
    private MarkdownProjection? _markdownProjection;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MarkdownSource
    {
        get => _markdownSource;
        set
        {
            if (_markdownSource == value) return;
            _markdownSource = value;
            _markdownProjection = null;
        }
    }

    [JsonIgnore] public bool IsMarkdown => SchemaVersion is MarkdownVersion or TaggedMarkdownVersion;
    [JsonIgnore] public string FormatLabel => IsMarkdown ? "Markdown" : "富文本";
    [JsonIgnore] public IReadOnlyList<Guid> Tags => TagIds is null ? Array.Empty<Guid>() : TagIds;

    public void SetTags(IEnumerable<Guid> ids)
    {
        var markdown = IsMarkdown;
        var tags = ids.Distinct().Order().ToList();
        TagIds = tags.Count > 0 ? tags : null;
        SchemaVersion = markdown ? (TagIds is null ? MarkdownVersion : TaggedMarkdownVersion)
            : (TagIds is null ? RichTextVersion : TaggedRichTextVersion);
    }

    public void SetMarkdown(string source)
    {
        SchemaVersion = Tags.Count == 0 ? MarkdownVersion : TaggedMarkdownVersion;
        MarkdownSource = source;
        Blocks = [];
    }

    public void SetRichText(List<NoteBlock> blocks)
    {
        SchemaVersion = Tags.Count == 0 ? RichTextVersion : TaggedRichTextVersion;
        MarkdownSource = null;
        Blocks = blocks;
    }

    public void CopyBodyFrom(Note note)
    {
        SchemaVersion = note.IsMarkdown ? (Tags.Count == 0 ? MarkdownVersion : TaggedMarkdownVersion)
            : (Tags.Count == 0 ? RichTextVersion : TaggedRichTextVersion);
        MarkdownSource = note.MarkdownSource;
        Blocks = note.Blocks;
    }

    public MarkdownProjection ProjectMarkdown() => _markdownProjection ??= MarkdownBody.Project(MarkdownSource ?? "");
    public IReadOnlyList<NoteBlock> GetBodyBlocks() => IsMarkdown ? ProjectMarkdown().Blocks : Blocks;
    public IEnumerable<string> GetImageIds() => ImageIds(GetBodyBlocks());

    private static IEnumerable<string> ImageIds(IEnumerable<NoteBlock> blocks)
    {
        foreach (var block in blocks)
        {
            foreach (var inline in block.Inlines)
                if (inline.Kind == "image" && inline.AssetId is { } id) yield return id;
            foreach (var id in ImageIds(block.Children)) yield return id;
        }
    }

    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title)) return Title.Trim();
            using var reader = new StringReader(string.Join("\n", GetBodyBlocks().Select(b => GetText(b, includeImages: false))));
            while (reader.ReadLine() is { } line)
                if (!string.IsNullOrWhiteSpace(line)) return line.Trim();
            return "未命名便签";
        }
    }
    public string PlainText => string.Join("\n", GetBodyBlocks().Select(b => GetText(b, includeImages: true)));
    private static string GetText(NoteBlock block, bool includeImages) => block.Kind == "paragraph"
        ? string.Concat(block.Inlines.Select(i => i.Kind == "image" ? (includeImages ? " [图片] " : "") : i.Text))
        : string.Join("\n", block.Children.Select(b => GetText(b, includeImages)));
}

// Only this small, versioned vocabulary is persisted; no executable XAML or HTML.
public sealed class NoteBlock
{
    public string Kind { get; set; } = "paragraph";
    public string Alignment { get; set; } = "Left";
    public string Marker { get; set; } = "Disc";
    public List<NoteInline> Inlines { get; set; } = [];
    public List<NoteBlock> Children { get; set; } = [];
}

public sealed class NoteInline
{
    public string Kind { get; set; } = "text";
    public string Text { get; set; } = "";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strike { get; set; }
    public string? Foreground { get; set; }
    public double FontSize { get; set; } = 16;
    // null is a legacy note: its 16-point text is treated as the application's default size.
    public bool? UsesDefaultFontSize { get; set; }
    public string? AssetId { get; set; }
    public double Width { get; set; } = 280;
    [JsonIgnore] public bool MarkdownCode { get; set; }
}

public sealed class WindowState
{
    [JsonIgnore] public int? ContentZoomPercent { get; set; }
    public bool IsOpen { get; set; }
    public bool Topmost { get; set; }
    public double Left { get; set; } = 160;
    public double Top { get; set; } = 120;
    public double Width { get; set; } = 390;
    public double Height { get; set; } = 320;
}

public sealed class DeviceState
{
    public Dictionary<Guid, WindowState> Windows { get; set; } = [];
}

public sealed class EditorPreferences
{
    public int SchemaVersion { get; set; } = 1;
    public double DefaultFontSize { get; set; } = 16;
    public double ParagraphSpacing { get; set; } = 8;
    public bool RandomizeNewNoteColor { get; set; } = true;
    public bool AutoHideHeader { get; set; }
    public bool ShowHeaderOnHover { get; set; } = true;
    public string NewNoteFormat { get; set; } = "markdown";
}

public sealed class ManagerPreferences
{
    public int SchemaVersion { get; set; } = 1;
    public string ViewMode { get; set; } = "preview";
}
