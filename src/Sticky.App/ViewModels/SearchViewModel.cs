using System.Collections.ObjectModel;
using System.ComponentModel;
using Sticky.Core;

namespace Sticky.App.ViewModels;

public sealed class SearchViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly StickyWorkspace _workspace;
    private string _query = "";
    private DirectoryFilter? _scope;
    private readonly HashSet<Guid> _selectedTags = [];
    public IReadOnlyList<NoteTag> SelectedTags { get; private set; } = [];
    public ObservableCollection<NoteCard> Results { get; } = [];
    public ObservableCollection<DirectoryFilter> Scopes { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Query { get => _query; set { _query = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Query))); RefreshResults(); } }
    public DirectoryFilter? Scope { get => _scope; set { _scope = value; RefreshResults(); } }
    public string Summary => string.IsNullOrWhiteSpace(Query) && SelectedTags.Count == 0 ? "输入关键词，或添加标签进行筛选。" : $"找到 {Results.Count} 张便签";
    public bool IsEmpty => Results.Count == 0;

    public SearchViewModel(StickyWorkspace workspace)
    {
        _workspace = workspace;
        workspace.Changed += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        var previous = _scope;
        Scopes.Clear();
        Scopes.Add(new DirectoryFilter("全部目录", null));
        var hierarchy = new DirectoryHierarchy(_workspace.Directories);
        foreach (var directory in hierarchy.InTreeOrder().Where(d => hierarchy.IsActive(d.Id)))
            Scopes.Add(new DirectoryFilter(hierarchy.PathLabel(directory.Id), directory.Id));
        _scope = Scopes.FirstOrDefault(g => g.Id == previous?.Id) ?? Scopes[0];
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Scope)));
        RefreshTags();
    }

    public void AddTag(Guid id)
    {
        if (_selectedTags.Add(id)) RefreshTags();
    }

    public void RemoveTag(Guid id)
    {
        if (_selectedTags.Remove(id)) RefreshTags();
    }

    private void RefreshTags()
    {
        var selected = _workspace.Tags.Where(t => !t.Deleted && _selectedTags.Contains(t.Id)).OrderBy(t => t.Name, NoteTagNames.Comparer).ToList();
        _selectedTags.IntersectWith(selected.Select(t => t.Id));
        if (!SelectedTags.SequenceEqual(selected))
        {
            SelectedTags = selected;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTags)));
        }
        RefreshResults();
    }

    private void RefreshResults()
    {
        Results.Clear();
        var query = Query.Trim();
        var hierarchy = new DirectoryHierarchy(_workspace.Directories);
        if (query.Length > 0 || _selectedTags.Count > 0)
            foreach (var note in _workspace.Notes.Where(n => n.DeletedAt is null && hierarchy.IsActive(n.DirectoryId))
                .Where(n => _scope?.Id is null || n.DirectoryId == _scope.Id)
                .Where(n => _selectedTags.IsSubsetOf(n.Tags))
                .Where(n => query.Length == 0 || n.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase) || n.PlainText.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => n.ModifiedAt))
                Results.Add(new NoteCard(note, _workspace.Windows.ContainsKey(note.Id), hierarchy.PathLabel(note.DirectoryId)));
        foreach (var property in new[] { nameof(Summary), nameof(IsEmpty) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
    public void Dispose() => _workspace.Changed -= Refresh;
}

public sealed record DirectoryFilter(string Name, Guid? Id);
