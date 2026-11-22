using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Sticky.Core;

namespace Sticky.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly StickyWorkspace _workspace;
    private bool _showDeleted;
    public bool IsVaultSelected { get; private set; }
    public bool IsVaultUnlocked => _workspace.Vault.IsUnlocked && !_workspace.VaultViewsHidden;
    public string VaultLockHint => IsVaultUnlocked ? "保险箱已解锁" : "保险箱已锁定";
    public bool IsVaultLocked => IsVaultSelected && (!_workspace.Vault.IsUnlocked || _workspace.VaultViewsHidden);
    public bool CanUnlockVault => IsVaultLocked && !_workspace.VaultViewsHidden;
    public bool CanCreateNote => !IsVaultLocked;
    public string VaultBackground => IsVaultSelected ? "#DDE4D8" : "Transparent";
    public void SelectVault() { IsVaultSelected = true; _showDeleted = false; Refresh(); }
    public string VaultTrashLabel => ShowDeleted ? "返回保险箱" : "私密回收站";
    public void ToggleVaultTrash() { if (!IsVaultSelected || IsVaultLocked) return; _showDeleted = !_showDeleted; Refresh(); }
    private Guid? _selectedDirectoryId;
    private readonly ResettableCollection<NoteCard> _cards = new();
    private readonly ResettableCollection<DirectoryItem> _directories = new();
    private readonly ResettableCollection<TrashedDirectoryItem> _trashedDirectories = new();
    private readonly HashSet<Guid> _expandedDirectories = [];
    private readonly Dictionary<Guid, DirectoryItem> _directoryItemsById = [];
    private readonly Dictionary<Guid, NoteCard> _cardCache = [];
    private List<(Guid Id, Guid? Parent, string Name, int Count)> _directorySnapshot = [];
    private Guid? _highlightedDirectoryId;
    private int _activeCount;
    private bool _titleView;
    public ObservableCollection<NoteCard> Cards => _cards;
    public ObservableCollection<DirectoryItem> DirectoryItems => _directories;
    public ObservableCollection<TrashedDirectoryItem> TrashedDirectories => _trashedDirectories;
    public bool HasTrashedDirectories => _trashedDirectories.Count > 0;
    public bool IsTitleView => _titleView;
    public string ViewModeLabel => _titleView ? "标题标签" : "内容预览";
    public string ViewModeHint => _titleView ? "切换为内容预览" : "切换为标题标签";
    public double CardCellWidth => _titleView ? 220 : 316;
    public double CardCellHeight => _titleView ? 52 : 242;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool ShowDeleted { get => _showDeleted; set { IsVaultSelected = false; _showDeleted = value; Refresh(); } }
    public Guid? SelectedDirectoryId => _selectedDirectoryId;
    public bool HasSelectedDirectory => _selectedDirectoryId.HasValue && !ShowDeleted && !IsVaultSelected;
    public string TrashBackground => !IsVaultSelected && ShowDeleted ? "#DDE4D8" : "Transparent";
    public string Heading => IsVaultSelected ? (ShowDeleted ? "保险箱 · 回收站" : "保险箱") : ShowDeleted ? "回收站" : _workspace.Directories.FirstOrDefault(d => d.Id == _selectedDirectoryId)?.Name ?? "工作空间";
    public string Subtitle => IsVaultSelected && _workspace.VaultViewsHidden ? "保存失败，内容已隐藏；正在重试保存并锁定。" : IsVaultSelected ? (IsVaultLocked ? "输入主密码后查看私密便签。" : "已解锁 · 便签自动加密保存。") : ShowDeleted ? "在这里恢复已移入回收站的目录或便签。" : _selectedDirectoryId is { } id ? _workspace.DirectoryPath(id) : "随手记录，让想法有处安放。";
    public string Summary => $"{_activeCount} 张便签 · {_workspace.Windows.Count} 个窗口已打开";
    public string ResultCount => HasTrashedDirectories ? $"{_trashedDirectories.Count} 个目录 · {Cards.Count} 张便签" : $"{Cards.Count} 张便签";
    public bool IsEmpty => Cards.Count == 0 && !HasTrashedDirectories;
    public string EmptyTitle => IsVaultSelected && _workspace.VaultViewsHidden ? "正在重试保存并锁定" : IsVaultLocked ? "保险箱已锁定" : ShowDeleted ? "回收站是空的" : "这里还没有便签";
    public string EmptyDescription => IsVaultSelected && _workspace.VaultViewsHidden ? "未保存的编辑仍保留在内存中，请勿强制退出程序。" : IsVaultLocked ? "点击上方「解锁保险箱」查看内容。" : ShowDeleted ? "移入回收站的便签会显示在这里。" : "点击右上角「新建便签」，开始记录。";

    public MainViewModel(StickyWorkspace workspace)
    {
        _workspace = workspace;
        _titleView = workspace.Store.LoadManagerPreferences().ViewMode == "titles";
        workspace.Changed += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        var hierarchy = new DirectoryHierarchy(_workspace.Directories);
        var active = hierarchy.InTreeOrder().Where(d => hierarchy.IsActive(d.Id)).ToList();
        var paths = active.ToDictionary(d => d.Id, d => hierarchy.PathLabel(d.Id));
        var hidden = _workspace.Directories.Where(d => !paths.ContainsKey(d.Id)).Select(d => d.Id).ToHashSet();
        _trashedDirectories.Replace(ShowDeleted && !IsVaultSelected ? hierarchy.TrashRoots()
            .Select(d => new TrashedDirectoryItem(d.Id, hierarchy.PathLabel(d.Id))).OrderBy(d => d.Path).ToList() : []);
        var counts = new Dictionary<Guid, int>(); var live = new HashSet<Guid>(); var next = new List<NoteCard>();
        foreach (var ordinary in _workspace.Notes.Where(n => n.DeletedAt is null && n.DirectoryId is { } id && !hidden.Contains(id)))
            counts[ordinary.DirectoryId!.Value] = counts.GetValueOrDefault(ordinary.DirectoryId.Value) + 1;
        if (_selectedDirectoryId is not { } selectedDirectory || !paths.ContainsKey(selectedDirectory))
        {
            var topLevel = active.Where(d => d.ParentId is null);
            _selectedDirectoryId = (topLevel.FirstOrDefault(d => counts.ContainsKey(d.Id)) ?? topLevel.FirstOrDefault())?.Id;
        }
        _activeCount = 0;
        foreach (var note in IsVaultSelected ? (IsVaultLocked ? [] : _workspace.PrivateNotes) : _workspace.Notes)
        {
            live.Add(note.Id);
            if (!IsVaultSelected && note.DirectoryId is { } folder && hidden.Contains(folder)) continue;
            if (note.DeletedAt is null) _activeCount++;
            if ((note.DeletedAt is not null) != ShowDeleted || !IsVaultSelected && !ShowDeleted && (!HasSelectedDirectory || note.DirectoryId != _selectedDirectoryId)) continue;
            if (!_cardCache.TryGetValue(note.Id, out var card) || !ReferenceEquals(card.Note, note)) _cardCache[note.Id] = card = new NoteCard(note, false);
            card.Update(_workspace.Windows.ContainsKey(note.Id), IsVaultSelected ? "保险箱" : note.DirectoryId is Guid id && paths.TryGetValue(id, out var path) ? path : DirectoryHierarchy.UnassignedLabel);
            next.Add(card);
        }
        foreach (var id in _cardCache.Keys.Where(id => !live.Contains(id)).ToArray()) _cardCache.Remove(id);
        _cards.Replace(next.OrderByDescending(c => c.Note.ModifiedAt).ToList());
        var directories = active.Select(d => (d.Id, d.ParentId, d.Name, counts.GetValueOrDefault(d.Id))).ToList();
        if (!_directorySnapshot.SequenceEqual(directories))
        {
            _directorySnapshot = directories;
            _expandedDirectories.RemoveWhere(id => !paths.ContainsKey(id));
            _directoryItemsById.Clear();
            _directories.Replace(active.Where(d => d.ParentId is null).Select(BuildItem).ToList());
        }
        var highlighted = HasSelectedDirectory ? _selectedDirectoryId : null;
        if (_highlightedDirectoryId is { } previousId && previousId != highlighted && _directoryItemsById.TryGetValue(previousId, out var previous)) previous.IsSelected = false;
        _highlightedDirectoryId = highlighted;
        if (highlighted is { } selectedId && _directoryItemsById.TryGetValue(selectedId, out var selected)) selected.IsSelected = true;
        foreach (var property in new[] { nameof(IsVaultUnlocked), nameof(VaultLockHint), nameof(VaultTrashLabel), nameof(IsVaultSelected), nameof(IsVaultLocked), nameof(CanCreateNote), nameof(CanUnlockVault), nameof(VaultBackground), nameof(Heading), nameof(Subtitle), nameof(Summary), nameof(ResultCount), nameof(IsEmpty), nameof(EmptyTitle), nameof(EmptyDescription), nameof(ShowDeleted), nameof(HasSelectedDirectory), nameof(TrashBackground), nameof(HasTrashedDirectories) })
            OnPropertyChanged(property);

        DirectoryItem BuildItem(NoteDirectory directory)
        {
            var children = hierarchy.Children(directory.Id).Where(d => paths.ContainsKey(d.Id)).Select(BuildItem).ToList();
            var item = new DirectoryItem(directory, paths[directory.Id], counts.GetValueOrDefault(directory.Id), children, _expandedDirectories);
            _directoryItemsById.Add(directory.Id, item);
            return item;
        }
    }

    public void ToggleView()
    {
        _workspace.Store.SaveManagerPreferences(new ManagerPreferences { ViewMode = _titleView ? "preview" : "titles" });
        _titleView = !_titleView;
        foreach (var property in new[] { nameof(IsTitleView), nameof(ViewModeLabel), nameof(ViewModeHint), nameof(CardCellWidth), nameof(CardCellHeight) }) OnPropertyChanged(property);
    }

    public void SelectDirectory(Guid id)
    {
        foreach (var ancestor in new DirectoryHierarchy(_workspace.Directories).AncestorsAndSelf(id))
            if (ancestor.Id != id && _directoryItemsById.TryGetValue(ancestor.Id, out var item)) item.IsExpanded = true;
        IsVaultSelected = false;
        _selectedDirectoryId = id; _showDeleted = false; Refresh();
    }

    public void Dispose() => _workspace.Changed -= Refresh;

    private void OnPropertyChanged([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

public sealed record TrashedDirectoryItem(Guid Id, string Path);

public sealed class DirectoryItem(NoteDirectory directory, string path, int count,
    IReadOnlyList<DirectoryItem> children, HashSet<Guid> expanded) : INotifyPropertyChanged
{
    public NoteDirectory Directory { get; } = directory;
    public string Path { get; } = path;
    public int Count { get; } = count;
    private bool _selected;
    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Background)));
        }
    }
    public IReadOnlyList<DirectoryItem> Children { get; } = children;
    public string Label { get; } = $"{directory.Name}  ·  {count}";
    public string Background => IsSelected ? "#DDE4D8" : "Transparent";
    public bool IsExpanded
    {
        get => expanded.Contains(Directory.Id);
        set
        {
            var changed = value ? expanded.Add(Directory.Id) : expanded.Remove(Directory.Id);
            if (changed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
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
