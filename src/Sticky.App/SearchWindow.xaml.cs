using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Sticky.App.Controls;
using Sticky.App.ViewModels;
using Sticky.Core;

namespace Sticky.App;

public partial class SearchWindow : Window
{
    private readonly StickyWorkspace _workspace;
    private readonly SearchViewModel _viewModel;
    public SearchWindow(StickyWorkspace workspace)
    {
        _workspace = workspace;
        _viewModel = new SearchViewModel(workspace);
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SearchViewModel.SelectedTags)) RefreshTagFilters(); };
        Loaded += (_, _) => FocusQuery();
        Closed += (_, _) => _viewModel.Dispose();
    }
    public void FocusQuery() { QueryBox.Focus(); QueryBox.SelectAll(); }
    public void SetQuery(string query) { _viewModel.Scope = _viewModel.Scopes[0]; _viewModel.Query = query; }

    private void ShowTagFilters(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddTagFilter, Placement = PlacementMode.Bottom };
        foreach (var tag in _workspace.Tags.Where(t => !t.Deleted).OrderBy(t => t.Name, NoteTagNames.Comparer))
        {
            var item = new MenuItem { Header = tag.Name, Icon = TagPills.Swatch(tag), IsCheckable = true,
                IsChecked = _viewModel.SelectedTags.Any(t => t.Id == tag.Id) };
            item.Click += (_, _) => _viewModel.AddTag(tag.Id);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "暂无标签", IsEnabled = false });
        AddTagFilter.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void RefreshTagFilters()
    {
        TagFilters.Children.Clear();
        foreach (var tag in _viewModel.SelectedTags)
        {
            var pill = TagPills.Create(tag, remove: () => _viewModel.RemoveTag(tag.Id));
            var menu = new ContextMenu();
            var rename = new MenuItem { Header = "重命名标签…" };
            rename.Click += (_, _) =>
            {
                var name = TextPrompt.Show(this, "重命名标签", "标签名称", tag.Name);
                if (name is not null) UpdateTag(tag.Id, name);
            };
            var remove = new MenuItem { Header = "删除标签…" };
            remove.Click += (_, _) =>
            {
                if (MessageBox.Show(this, $"删除「{tag.Name}」会解除它与所有便签的关联。继续吗？", "删除标签", MessageBoxButton.OKCancel,
                    MessageBoxImage.Question) == MessageBoxResult.OK) UpdateTag(tag.Id, null);
            };
            menu.Items.Add(rename);
            menu.Items.Add(remove);
            pill.ContextMenu = menu;
            TagFilters.Children.Add(pill);
        }
    }

    private void UpdateTag(Guid id, string? name)
    {
        try { _workspace.UpdateTag(id, name); }
        catch (Exception ex) { StickyWorkspace.ShowError("无法更新标签", ex); }
    }
    private void OpenResult(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NoteCard card) _workspace.OpenNote(card.Note);
    }
    private void OpenSelected(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is NoteCard card) _workspace.OpenNote(card.Note);
    }
    private void ResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ResultsList.SelectedItem is NoteCard card) { _workspace.OpenNote(card.Note); e.Handled = true; }
    }
}
