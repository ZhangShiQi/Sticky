using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Sticky.App.ViewModels;

public sealed class SearchViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly StickyWorkspace _workspace;
    private string _query = "";
    private GroupFilter? _scope;
    public ObservableCollection<NoteCard> Results { get; } = [];
    public ObservableCollection<GroupFilter> Scopes { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Query { get => _query; set { _query = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Query))); RefreshResults(); } }
    public GroupFilter? Scope { get => _scope; set { _scope = value; RefreshResults(); } }
    public string Summary => string.IsNullOrWhiteSpace(Query) ? "输入关键词，搜索便签标题和正文。" : $"找到 {Results.Count} 张便签";
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
        Scopes.Add(new GroupFilter("全部分组", null));
        Scopes.Add(new GroupFilter("未分组", null, true));
        foreach (var group in _workspace.Groups.OrderBy(g => g.Name)) Scopes.Add(new GroupFilter(group.Name, group.Id));
        _scope = Scopes.FirstOrDefault(g => g.Id == previous?.Id && g.UngroupedOnly == previous?.UngroupedOnly) ?? Scopes[0];
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Scope)));
        RefreshResults();
    }

    private void RefreshResults()
    {
        Results.Clear();
        var query = Query.Trim();
        if (query.Length > 0)
            foreach (var note in _workspace.Notes.Where(n => n.DeletedAt is null)
                .Where(n => _scope?.UngroupedOnly == true ? n.GroupId is null : _scope?.Id is null || n.GroupId == _scope.Id)
                .Where(n => n.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase) || n.PlainText.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => n.ModifiedAt))
                Results.Add(new NoteCard(note, _workspace.Windows.ContainsKey(note.Id), _workspace.GroupName(note.GroupId)));
        foreach (var property in new[] { nameof(Summary), nameof(IsEmpty) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
    public void Dispose() => _workspace.Changed -= Refresh;
}

public sealed record GroupFilter(string Name, Guid? Id, bool UngroupedOnly = false);
