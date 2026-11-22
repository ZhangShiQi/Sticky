using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sticky.Core;
using Sticky.App.ViewModels;

namespace Sticky.App;

public partial class MainWindow : Window
{
    private StickyWorkspace _workspace = null!;
    private MainViewModel _viewModel = null!;

    private MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => { _viewModel?.Dispose(); if (_workspace?.Manager == this) _workspace.Manager = null; };
    }

    public static MainWindow CreateLoadingShell() => new();
    public MainWindow(StickyWorkspace workspace) : this() => AttachWorkspace(workspace);

    public void AttachWorkspace(StickyWorkspace workspace)
    {
        if (_workspace is not null) throw new InvalidOperationException("窗口已关联工作空间。");
        _workspace = workspace;
        workspace.Manager = this;
        PreviewKeyDown += (_, _) => { if (_viewModel.IsVaultSelected) workspace.TouchVault(); };
        PreviewMouseDown += (_, _) => { if (_viewModel.IsVaultSelected) workspace.TouchVault(); };
        _viewModel = new MainViewModel(workspace);
        DataContext = _viewModel;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.New, (_, _) => CreateSelectedNote()));
        InputBindings.Add(new KeyBinding(ApplicationCommands.New, Key.N, ModifierKeys.Control));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => { SidebarSearchBox.Focus(); SidebarSearchBox.SelectAll(); }));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Find, Key.F, ModifierKeys.Control));
    }

    private void NewNote(object sender, RoutedEventArgs e) => CreateSelectedNote();
    private void CreateSelectedNote()
    {
        if (_viewModel.IsVaultSelected) _workspace.CreateVaultNote();
        else _workspace.CreateNote(_viewModel.SelectedDirectoryId);
    }
    public void SelectVault() => _viewModel.SelectVault();
    private async void UnlockVault(object sender, RoutedEventArgs e) => await _workspace.EnsureVaultController().EnsureUnlockedAsync();
    private void ToggleVaultTrash(object sender, RoutedEventArgs e) => _viewModel.ToggleVaultTrash();
    private void LockVault(object sender, RoutedEventArgs e) => _workspace.EnsureVaultController().LockVault();
    private void VaultSettings(object sender, RoutedEventArgs e)
    {
        var dialog = _workspace.EnsureVaultController(); dialog.Owner = this; dialog.Show(); dialog.Activate();
    }
    private void ShowTrash(object sender, RoutedEventArgs e) => _viewModel.ShowDeleted = true;
    private void OpenSettings(object sender, RoutedEventArgs e) => _workspace.ShowSettings();
    private async void StartCapture(object sender, RoutedEventArgs e) => await _workspace.Capture.StartCaptureAsync(this);
    private void OpenHistory(object sender, RoutedEventArgs e) => _workspace.ShowHistory();
    private void OpenVault(object sender, RoutedEventArgs e) => _workspace.ShowVault();
    private void ToggleView(object sender, RoutedEventArgs e)
    {
        try { _viewModel.ToggleView(); }
        catch (Exception ex) { StickyWorkspace.ShowError("无法保存便签视图设置", ex); }
    }
    private void SidebarSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var query = SidebarSearchBox.Text.Trim();
        _workspace.ShowSearch(query);
    }
    private void SelectDirectory(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DirectoryItem item) _viewModel.SelectDirectory(item.Directory.Id);
    }
    private void NewDirectory(object sender, RoutedEventArgs e) => CreateDirectory(null);
    private void CreateDirectory(Guid? parentId)
    {
        var name = TextPrompt.Show(this, parentId is null ? "新建目录" : "新建子目录", "目录名称");
        if (name is not null && _workspace.CreateDirectory(name, parentId) is { } directory) _viewModel.SelectDirectory(directory.Id);
    }
    private void DirectoryMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.PlacementTarget is not FrameworkElement { DataContext: DirectoryItem item }) return;
        var directory = item.Directory;
        menu.Items.Clear();
        var create = new MenuItem { Header = "新建子目录…" };
        create.Click += (_, _) => CreateDirectory(directory.Id);
        var rename = new MenuItem { Header = "重命名目录…" };
        rename.Click += (_, _) => Rename(directory);
        var move = new MenuItem { Header = "移动目录到" };
        var occupied = _workspace.Directories.Where(d => d.Id != directory.Id && d.State == DirectoryState.Active &&
            StringComparer.OrdinalIgnoreCase.Equals(d.Name.Trim(), directory.Name.Trim())).Select(d => d.ParentId).ToHashSet();
        NoteMenus.FillDirectoryTargets(move.Items, _workspace.Directories, id =>
        {
            if (_workspace.MoveDirectory(directory, id)) _viewModel.SelectDirectory(directory.Id);
        }, directory.ParentId, excluded: directory.Id, canSelect: id => !occupied.Contains(id), includeTopLevel: true);
        menu.Items.Add(create);
        menu.Items.Add(rename);
        menu.Items.Add(move);
        menu.Items.Add(new Separator());
        var delete = new MenuItem { Header = "删除目录" };
        delete.Click += (_, _) => _workspace.DeleteDirectory(directory.Id);
        menu.Items.Add(delete);
    }
    private void RestoreDirectory(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TrashedDirectoryItem item }) _workspace.RestoreDirectory(item.Id);
    }
    private void RenameSelectedDirectory(object sender, RoutedEventArgs e)
    {
        if (_workspace.Directories.FirstOrDefault(d => d.Id == _viewModel.SelectedDirectoryId) is { } directory) Rename(directory);
    }
    private void Rename(NoteDirectory directory)
    {
        var name = TextPrompt.Show(this, "重命名目录", "目录名称", directory.Name);
        if (name is not null) _workspace.RenameDirectory(directory, name);
    }
    private void MoveCard(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: NoteCard card } button) return;
        var menu = NoteMenus.MoveMenu(_workspace, card.Note);
        button.ContextMenu = menu; menu.PlacementTarget = button; menu.IsOpen = true;
    }
    private void CardMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: NoteCard card } button) return;
        var menu = new ContextMenu { PlacementTarget = button };
        var isPrivate = _workspace.IsVaultNote(card.Note.Id);
        var vault = new MenuItem { Header = isPrivate ? "移出保险箱…" : "移入保险箱…" };
        vault.Click += (_, _) => { if (isPrivate) _workspace.EnsureVaultController().MoveOut(card.Note.Id); else _workspace.MoveIntoVault(card.Note); };
        menu.Items.Add(vault);
        button.ContextMenu = menu;
        var history = new MenuItem { Header = "历史版本与重放…" };
        history.Click += (_, _) => _workspace.ShowHistory(card.Note.Id);
        menu.Items.Add(history);
        if (card.Note.DeletedAt is null)
        {
            if (!isPrivate)
            {
                var move = new MenuItem { Header = "移动到目录" };
                NoteMenus.FillNoteDirectoryTargets(move.Items, _workspace, card.Note);
                menu.Items.Add(move);
                AddEditTags(menu, card.Note);
            }
            var delete = new MenuItem { Header = "移入回收站" };
            delete.Click += (_, _) => _workspace.Delete(card.Note);
            menu.Items.Add(delete);
        }
        menu.IsOpen = true;
    }
    private void CardContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { IsOpen: true } menu }) menu.IsOpen = false;
    }
    private void TitleCardMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.PlacementTarget is not FrameworkElement { DataContext: NoteCard card }) return;
        menu.Items.Clear();
        var open = new MenuItem { Header = card.Note.DeletedAt is null ? "打开便签" : "恢复便签" };
        open.Click += (_, _) => { if (card.Note.DeletedAt is null) _workspace.OpenNote(card.Note); else _workspace.Restore(card.Note); }; menu.Items.Add(open);
        if (card.Note.DeletedAt is null)
        {
            AddMoveMenu(menu, card);
            AddEditTags(menu, card.Note);
            var delete = new MenuItem { Header = "移入回收站" }; delete.Click += (_, _) => _workspace.Delete(card.Note); menu.Items.Add(delete);
        }
        var history = new MenuItem { Header = "历史版本与重放…" }; history.Click += (_, _) => _workspace.ShowHistory(card.Note.Id); menu.Items.Add(history);
    }
    private void AddMoveMenu(ContextMenu menu, NoteCard card)
    {
        if (_workspace.IsVaultNote(card.Note.Id))
        {
            NoteMenus.FillMoveItems(menu.Items, _workspace, card.Note);
            return;
        }
        var move = new MenuItem { Header = "移动到目录" };
        NoteMenus.FillMoveItems(move.Items, _workspace, card.Note);
        menu.Items.Add(move);
    }
    private void AddEditTags(ContextMenu menu, Note note)
    {
        if (!_workspace.CanEditTags(note)) return;
        var tags = new MenuItem { Header = "编辑标签…" };
        tags.Click += (_, _) => new TagEditorWindow(_workspace, note) { Owner = this }.ShowDialog();
        menu.Items.Add(tags);
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
        if (_workspace is null) { e.Cancel = true; return; }
        if (_workspace.IsExiting) return;
        e.Cancel = true;
        if (_workspace.SaveAllNotes()) Hide();
    }
}
