namespace Sticky.Core;

public sealed class Note
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Color { get; set; } = "amber";
    public Guid? GroupId { get; set; }
    public Guid? ConflictOf { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public List<NoteBlock> Blocks { get; set; } = [];

    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title)) return Title.Trim();
            using var reader = new StringReader(string.Join("\n", Blocks.Select(b => GetText(b, includeImages: false))));
            while (reader.ReadLine() is { } line)
                if (!string.IsNullOrWhiteSpace(line)) return line.Trim();
            return "未命名便签";
        }
    }
    public string PlainText => string.Join("\n", Blocks.Select(b => GetText(b, includeImages: true)));
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
}

public sealed class WindowState
{
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

public sealed class NoteGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
}

public sealed class GroupCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<NoteGroup> Groups { get; set; } = [];
}

public sealed class EditorPreferences
{
    public int SchemaVersion { get; set; } = 1;
    public double DefaultFontSize { get; set; } = 16;
    public double ParagraphSpacing { get; set; } = 8;
    public bool RandomizeNewNoteColor { get; set; } = true;
    public bool AutoHideHeader { get; set; }
    public bool ShowHeaderOnHover { get; set; } = true;
}

public sealed class ManagerPreferences
{
    public int SchemaVersion { get; set; } = 1;
    public string ViewMode { get; set; } = "preview";
}
