using System.Windows;
using System.Windows.Input;
using Sticky.App.ViewModels;

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
        Loaded += (_, _) => FocusQuery();
        Closed += (_, _) => _viewModel.Dispose();
    }
    public void FocusQuery() { QueryBox.Focus(); QueryBox.SelectAll(); }
    public void SetQuery(string query) { _viewModel.Scope = _viewModel.Scopes[0]; _viewModel.Query = query; }
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
