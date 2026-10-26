using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sticky.App.ViewModels;
using GroupItem = Sticky.App.ViewModels.GroupItem;

namespace Sticky.App;

public partial class MainWindow : Window
{
    private readonly StickyWorkspace _workspace;
    private readonly MainViewModel _viewModel;

    public MainWindow(StickyWorkspace workspace)
    {
        _workspace = workspace;
        workspace.Manager = this;
        _viewModel = new MainViewModel(workspace);
        InitializeComponent();
        DataContext = _viewModel;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.New, (_, _) => workspace.CreateNote(_viewModel.SelectedGroupId)));
        InputBindings.Add(new KeyBinding(ApplicationCommands.New, Key.N, ModifierKeys.Control));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => { SidebarSearchBox.Focus(); SidebarSearchBox.SelectAll(); }));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Find, Key.F, ModifierKeys.Control));
        Closed += (_, _) => { _viewModel.Dispose(); if (workspace.Manager == this) workspace.Manager = null; };
    }

    private void NewNote(object sender, RoutedEventArgs e) => _workspace.CreateNote(_viewModel.SelectedGroupId);
    private void ShowNotes(object sender, RoutedEventArgs e) => _viewModel.SelectGroup(null);
    private void ShowUngrouped(object sender, RoutedEventArgs e) => _viewModel.SelectGroup(null, ungroupedOnly: true);
    private void ShowTrash(object sender, RoutedEventArgs e) => _viewModel.ShowDeleted = true;
    private void OpenSettings(object sender, RoutedEventArgs e) => _workspace.ShowSettings();
    private void OpenHistory(object sender, RoutedEventArgs e) => _workspace.ShowHistory();
    private void SidebarSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var query = SidebarSearchBox.Text.Trim();
        if (query.Length > 0) _workspace.ShowSearch(query);
    }
    private void SelectGroup(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupItem item) _viewModel.SelectGroup(item.Group.Id);
    }
    private void NewGroup(object sender, RoutedEventArgs e)
    {
        var name = TextPrompt.Show(this, "新建分组", "分组名称");
        if (name is not null && _workspace.CreateGroup(name) is { } group) _viewModel.SelectGroup(group.Id);
    }
    private void RenameGroup(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupItem item) Rename(item.Group);
    }
    private void RenameSelectedGroup(object sender, RoutedEventArgs e)
    {
        if (_workspace.Groups.FirstOrDefault(g => g.Id == _viewModel.SelectedGroupId) is { } group) Rename(group);
    }
    private void Rename(Core.NoteGroup group)
    {
        var name = TextPrompt.Show(this, "重命名分组", "分组名称", group.Name);
        if (name is not null) _workspace.RenameGroup(group, name);
    }
    private void MoveCard(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: NoteCard card } button) return;
        var menu = NoteMenus.MoveMenu(_workspace, card.Note);
        menu.PlacementTarget = button; menu.IsOpen = true;
    }
    private void CardMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: NoteCard card } button) return;
        var menu = new ContextMenu { PlacementTarget = button };
        var history = new MenuItem { Header = "历史版本与重放…" };
        history.Click += (_, _) => _workspace.ShowHistory(card.Note.Id);
        menu.Items.Add(history);
        if (card.Note.DeletedAt is null)
        {
            var delete = new MenuItem { Header = "移入回收站" };
            delete.Click += (_, _) => _workspace.Delete(card.Note);
            menu.Items.Add(delete);
        }
        menu.IsOpen = true;
    }
    private void OpenCard(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NoteCard card) return;
        if (card.Note.DeletedAt is null) _workspace.OpenNote(card.Note); else _workspace.Restore(card.Note);
    }
    private void DeleteCard(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NoteCard card) _workspace.Delete(card.Note);
    }
    private void CardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) OpenCard(sender, e);
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_workspace.IsExiting) return;
        e.Cancel = true;
        if (_workspace.SaveAllNotes()) Hide();
    }
}
