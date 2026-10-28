using System.ComponentModel;
using System.Text;
using Sticky.Core;

namespace Sticky.App.ViewModels;

public sealed class NoteCard : INotifyPropertyChanged
{
    public Note Note { get; }
    public bool IsOpen { get; private set; }
    public string GroupName { get; private set; }
    private DateTimeOffset _modified;
    private DateTimeOffset? _deleted;
    private Guid? _conflict;
    private string _manualTitle = "", _color = "";
    private List<NoteBlock>? _blocks;
    private string? _title, _preview;
    public event PropertyChangedEventHandler? PropertyChanged;
    public NoteCard(Note note, bool isOpen, string groupName = "未分组")
    { Note = note; GroupName = groupName; Update(isOpen, groupName); }
    public void Update(bool isOpen, string groupName)
    {
        var changed = _modified != Note.ModifiedAt || _deleted != Note.DeletedAt || _conflict != Note.ConflictOf
            || _manualTitle != Note.Title || _color != Note.Color || !ReferenceEquals(_blocks, Note.Blocks);
        var stateChanged = IsOpen != isOpen || GroupName != groupName;
        IsOpen = isOpen; GroupName = groupName;
        if (!changed && !stateChanged) return;
        if (changed) { _title = null; _preview = null; }
        _modified = Note.ModifiedAt; _deleted = Note.DeletedAt; _conflict = Note.ConflictOf;
        _manualTitle = Note.Title; _color = Note.Color; _blocks = Note.Blocks;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }
    public string Title => _title ??= !string.IsNullOrWhiteSpace(Note.Title) ? Note.Title.Trim() : ReadText(Note.Blocks, 512, firstLine: true);
    public string Preview => _preview ??= ReadText(Note.Blocks, 240, firstLine: false);
    public string Updated => Note.ModifiedAt.ToLocalTime().ToString("MM/dd HH:mm");
    public string State => Note.DeletedAt is not null ? "已删除" : Note.ConflictOf is not null ? "冲突副本 · 内容已保留" : IsOpen ? "窗口已打开" : "已保存在本地";
    public string Action => Note.DeletedAt is not null ? "恢复便签" : "打开便签 ↗";
    public bool CanDelete => Note.DeletedAt is null;
    public string Background => NotePalette.Background(Note.Color);
    private static IEnumerable<string> Parts(IEnumerable<NoteBlock> blocks, bool images)
    {
        var first = true;
        foreach (var block in blocks)
        {
            if (!first) yield return "\n"; first = false;
            if (block.Kind == "paragraph")
            {
                foreach (var inline in block.Inlines)
                    if (inline.Kind != "image") yield return inline.Text;
                    else if (images) yield return " [图片] ";
            }
            else foreach (var part in Parts(block.Children, images)) yield return part;
        }
    }
    private static string ReadText(IEnumerable<NoteBlock> blocks, int limit, bool firstLine)
    {
        var text = new StringBuilder(limit + 1);
        foreach (var part in Parts(blocks, images: !firstLine))
        foreach (var ch in part)
        {
            if (firstLine && ch is '\r' or '\n')
            {
                if (text.Length > 0) return text.ToString().TrimEnd();
                continue;
            }
            if (text.Length == 0 && char.IsWhiteSpace(ch)) continue;
            if (text.Length == limit)
            {
                if (char.IsHighSurrogate(text[^1])) text.Length--;
                return text.ToString().TrimEnd() + "…";
            }
            text.Append(ch);
        }
        var result = text.ToString().TrimEnd();
        return result.Length > 0 ? result : firstLine ? "未命名便签" : "空白便签，等你写点什么…";
    }
}
