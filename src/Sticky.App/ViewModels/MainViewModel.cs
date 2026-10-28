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
    private readonly ResettableCollection<NoteCard> _cards = new();
    private readonly ResettableCollection<GroupItem> _groups = new();
    private readonly Dictionary<Guid, NoteCard> _cardCache = [];
    private List<(Guid Id, string Name, int Count, bool Selected)> _groupSnapshot = [];
    private int _activeCount;
    private bool _titleView;
    public ObservableCollection<NoteCard> Cards => _cards;
    public ObservableCollection<GroupItem> GroupItems => _groups;
    public bool IsTitleView => _titleView;
    public string ViewModeLabel => _titleView ? "标题标签" : "内容预览";
    public string ViewModeHint => _titleView ? "切换为内容预览" : "切换为标题标签";
    public double CardCellWidth => _titleView ? 220 : 316;
    public double CardCellHeight => _titleView ? 52 : 242;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool ShowDeleted { get => _showDeleted; set { _showDeleted = value; if (value) { _selectedGroupId = null; _ungroupedOnly = false; } Refresh(); } }
    public Guid? SelectedGroupId => _selectedGroupId;
    public bool HasSelectedGroup => _selectedGroupId.HasValue && !ShowDeleted;
    public string AllBackground => !ShowDeleted && !_ungroupedOnly && _selectedGroupId is null ? "#DDE4D8" : "Transparent";
    public string UngroupedBackground => !ShowDeleted && _ungroupedOnly ? "#DDE4D8" : "Transparent";
    public string TrashBackground => ShowDeleted ? "#DDE4D8" : "Transparent";
    public string Heading => ShowDeleted ? "回收站" : _ungroupedOnly ? "未分组" : _selectedGroupId is { } id ? _workspace.GroupName(id) : "我的便签";
    public string Subtitle => ShowDeleted ? "删除的便签仍在这里，随时可以恢复。" : "随手记录，让想法有处安放。";
    public string Summary => $"{_activeCount} 张便签 · {_workspace.Windows.Count} 个窗口已打开";
    public string ResultCount => $"{Cards.Count} 张便签";
    public bool IsEmpty => Cards.Count == 0;
    public string EmptyTitle => ShowDeleted ? "回收站是空的" : "这里还没有便签";
    public string EmptyDescription => ShowDeleted ? "移入回收站的便签会显示在这里。" : "点击右上角「新建便签」，开始记录。";

    public MainViewModel(StickyWorkspace workspace)
    {
        _workspace = workspace;
        _titleView = workspace.Store.LoadManagerPreferences().ViewMode == "titles";
        workspace.Changed += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        if (_selectedGroupId.HasValue && !_workspace.Groups.Any(g => g.Id == _selectedGroupId)) _selectedGroupId = null;
        var groupObjects = _workspace.Groups.ToDictionary(g => g.Id);
        var groupNames = groupObjects.ToDictionary(g => g.Key, g => g.Value.Name);
        var counts = new Dictionary<Guid, int>(); var live = new HashSet<Guid>(); var next = new List<NoteCard>();
        _activeCount = 0;
        foreach (var note in _workspace.Notes)
        {
            live.Add(note.Id);
            if (note.DeletedAt is null) { _activeCount++; if (note.GroupId is Guid groupId) counts[groupId] = counts.GetValueOrDefault(groupId) + 1; }
            if ((note.DeletedAt is not null) != ShowDeleted || !ShowDeleted && (_ungroupedOnly ? note.GroupId is not null : _selectedGroupId is not null && note.GroupId != _selectedGroupId)) continue;
            if (!_cardCache.TryGetValue(note.Id, out var card) || !ReferenceEquals(card.Note, note)) _cardCache[note.Id] = card = new NoteCard(note, false);
            card.Update(_workspace.Windows.ContainsKey(note.Id), note.GroupId is Guid id && groupNames.TryGetValue(id, out var name) ? name : "未分组");
            next.Add(card);
        }
        foreach (var id in _cardCache.Keys.Where(id => !live.Contains(id)).ToArray()) _cardCache.Remove(id);
        _cards.Replace(next.OrderByDescending(c => c.Note.ModifiedAt).ToList());
        var groups = _workspace.Groups.OrderBy(g => g.Name).Select(g => (g.Id, g.Name, counts.GetValueOrDefault(g.Id), g.Id == _selectedGroupId && !ShowDeleted)).ToList();
        if (!_groupSnapshot.SequenceEqual(groups))
        {
            _groupSnapshot = groups;
            _groups.Replace(groups.Select(g => new GroupItem(groupObjects[g.Id], g.Item3, g.Item4)).ToList());
        }
        foreach (var property in new[] { nameof(Heading), nameof(Subtitle), nameof(Summary), nameof(ResultCount), nameof(IsEmpty), nameof(EmptyTitle), nameof(EmptyDescription), nameof(ShowDeleted), nameof(HasSelectedGroup), nameof(AllBackground), nameof(UngroupedBackground), nameof(TrashBackground) })
            OnPropertyChanged(property);
    }

    public void ToggleView()
    {
        _workspace.Store.SaveManagerPreferences(new ManagerPreferences { ViewMode = _titleView ? "preview" : "titles" });
        _titleView = !_titleView;
        foreach (var property in new[] { nameof(IsTitleView), nameof(ViewModeLabel), nameof(ViewModeHint), nameof(CardCellWidth), nameof(CardCellHeight) }) OnPropertyChanged(property);
    }

    public void SelectGroup(Guid? id, bool ungroupedOnly = false)
    {
        _selectedGroupId = id; _ungroupedOnly = ungroupedOnly; _showDeleted = false; Refresh();
    }

    public void Dispose() => _workspace.Changed -= Refresh;

    private void OnPropertyChanged([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

public sealed record GroupItem(NoteGroup Group, int Count, bool IsSelected)
{
    public string Label { get; } = $"{Group.Name}  ·  {Count}";
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
