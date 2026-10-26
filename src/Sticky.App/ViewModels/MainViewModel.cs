using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Sticky.Core;

namespace Sticky.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly StickyWorkspace _workspace;
    private bool _showDeleted;
    private bool _ungroupedOnly;
    private Guid? _selectedGroupId;
    public ObservableCollection<NoteCard> Cards { get; } = [];
    public ObservableCollection<GroupItem> GroupItems { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool ShowDeleted { get => _showDeleted; set { _showDeleted = value; if (value) { _selectedGroupId = null; _ungroupedOnly = false; } Refresh(); } }
    public Guid? SelectedGroupId => _selectedGroupId;
    public bool HasSelectedGroup => _selectedGroupId.HasValue && !ShowDeleted;
    public string AllBackground => !ShowDeleted && !_ungroupedOnly && _selectedGroupId is null ? "#DDE4D8" : "Transparent";
    public string UngroupedBackground => !ShowDeleted && _ungroupedOnly ? "#DDE4D8" : "Transparent";
    public string TrashBackground => ShowDeleted ? "#DDE4D8" : "Transparent";
    public string Heading => ShowDeleted ? "回收站" : _ungroupedOnly ? "未分组" : _selectedGroupId is { } id ? _workspace.GroupName(id) : "我的便签";
    public string Subtitle => ShowDeleted ? "删除的便签仍在这里，随时可以恢复。" : "随手记录，让想法有处安放。";
    public string Summary => $"{_workspace.Notes.Count(n => n.DeletedAt is null)} 张便签 · {_workspace.Windows.Count} 个窗口已打开";
    public string ResultCount => $"{Cards.Count} 张便签";
    public bool IsEmpty => Cards.Count == 0;
    public string EmptyTitle => ShowDeleted ? "回收站是空的" : "这里还没有便签";
    public string EmptyDescription => ShowDeleted ? "移入回收站的便签会显示在这里。" : "点击右上角「新建便签」，开始记录。";

    public MainViewModel(StickyWorkspace workspace)
    {
        _workspace = workspace;
        workspace.Changed += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        if (_selectedGroupId.HasValue && !_workspace.Groups.Any(g => g.Id == _selectedGroupId)) _selectedGroupId = null;
        GroupItems.Clear();
        foreach (var group in _workspace.Groups.OrderBy(g => g.Name))
            GroupItems.Add(new GroupItem(group, _workspace.Notes.Count(n => n.DeletedAt is null && n.GroupId == group.Id), group.Id == _selectedGroupId && !ShowDeleted));
        Cards.Clear();
        foreach (var note in _workspace.Notes.Where(n => (n.DeletedAt is not null) == ShowDeleted)
                     .Where(n => ShowDeleted || (_ungroupedOnly ? n.GroupId is null : _selectedGroupId is null || n.GroupId == _selectedGroupId)).OrderByDescending(n => n.ModifiedAt))
            Cards.Add(new NoteCard(note, _workspace.Windows.ContainsKey(note.Id), _workspace.GroupName(note.GroupId)));
        foreach (var property in new[] { nameof(Heading), nameof(Subtitle), nameof(Summary), nameof(ResultCount), nameof(IsEmpty), nameof(EmptyTitle), nameof(EmptyDescription), nameof(ShowDeleted), nameof(HasSelectedGroup), nameof(AllBackground), nameof(UngroupedBackground), nameof(TrashBackground) })
            OnPropertyChanged(property);
    }

    public void SelectGroup(Guid? id, bool ungroupedOnly = false)
    {
        _selectedGroupId = id; _ungroupedOnly = ungroupedOnly; _showDeleted = false; Refresh();
    }

    public void Dispose() => _workspace.Changed -= Refresh;

    private void OnPropertyChanged([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

public sealed record NoteCard(Note Note, bool IsOpen, string GroupName = "未分组")
{
    public string Title => Note.DisplayTitle;
    public string Preview => string.IsNullOrWhiteSpace(Note.PlainText) ? "空白便签，等你写点什么…" : Note.PlainText.Trim();
    public string Updated => Note.ModifiedAt.ToLocalTime().ToString("MM/dd HH:mm");
    public string State => Note.DeletedAt is not null ? "已删除" : Note.ConflictOf is not null ? "冲突副本 · 内容已保留" : IsOpen ? "窗口已打开" : "已保存在本地";
    public string Action => Note.DeletedAt is not null ? "恢复便签" : "打开便签 ↗";
    public bool CanDelete => Note.DeletedAt is null;
    public string Background => NotePalette.Background(Note.Color);
}

public sealed record GroupItem(NoteGroup Group, int Count, bool IsSelected)
{
    public string Label => $"{Group.Name}  ·  {Count}";
    public string Background => IsSelected ? "#DDE4D8" : "Transparent";
}

public static class NotePalette
{
    public static IReadOnlyList<(string Label, string Key)> Choices { get; } =
        [("暖黄", "amber"), ("鼠尾草绿", "sage"), ("浅玫瑰", "rose"), ("天空蓝", "sky"), ("薰衣草紫", "lavender")];
    public static string Accent(string key) => key switch
    {
        "sage" => "#A8CCA0", "rose" => "#ECA4B9", "sky" => "#94BEEB", "lavender" => "#C18AFF", _ => "#F0D078"
    };
    public static string Background(string key) => key switch
    {
        "sage" => "#E5EEE4", "rose" => "#F6E4E8", "sky" => "#E3EDF7", "lavender" => "#EBE5F4", _ => "#FFF1CA"
    };
}
