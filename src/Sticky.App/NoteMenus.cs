using System.Windows.Controls;
using Sticky.Core;

namespace Sticky.App;

public static class NoteMenus
{
    public static ContextMenu MoveMenu(StickyWorkspace workspace, Note note)
    {
        var menu = new ContextMenu();
        FillMoveItems(menu.Items, workspace, note);
        return menu;
    }

    public static void FillMoveItems(ItemCollection items, StickyWorkspace workspace, Note note)
    {
        if (workspace.IsVaultNote(note.Id))
        {
            var exit = new MenuItem { Header = "移出保险箱…" };
            exit.Click += (_, _) => workspace.EnsureVaultController().MoveOut(note.Id);
            items.Add(exit);
            return;
        }
        FillNoteDirectoryTargets(items, workspace, note);
        items.Add(new Separator());
        var vault = new MenuItem { Header = "移入保险箱…" };
        vault.Click += (_, _) => workspace.MoveIntoVault(note);
        items.Add(vault);
    }

    public static void FillNoteDirectoryTargets(ItemCollection items, StickyWorkspace workspace, Note note) =>
        FillDirectoryTargets(items, workspace.Directories, id => { if (id is { } directoryId) workspace.MoveNote(note, directoryId); }, note.DirectoryId);

    public static void FillDirectoryTargets(ItemCollection items, IEnumerable<NoteDirectory> directories, Action<Guid?> move,
        Guid? current, Guid? excluded = null, Func<Guid?, bool>? canSelect = null, bool includeTopLevel = false)
    {
        var tree = new DirectoryHierarchy(directories);
        if (includeTopLevel) items.Add(Destination("移到工作空间顶层", null));
        AddChildren(items, null);

        MenuItem Destination(string label, Guid? id)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = current == id, IsEnabled = canSelect?.Invoke(id) ?? true };
            item.Click += (_, _) => move(id);
            return item;
        }

        void AddChildren(ItemCollection parentItems, Guid? parentId)
        {
            foreach (var directory in tree.Children(parentId).Where(d => d.Id != excluded && d.State == DirectoryState.Active))
            {
                if (!tree.Children(directory.Id).Any(d => d.Id != excluded && d.State == DirectoryState.Active))
                {
                    parentItems.Add(Destination(directory.Name, directory.Id));
                    continue;
                }
                var branch = new MenuItem { Header = directory.Name };
                branch.Items.Add(Destination("移动到此目录", directory.Id));
                branch.Items.Add(new Separator());
                AddChildren(branch.Items, directory.Id);
                parentItems.Add(branch);
            }
        }
    }
}
