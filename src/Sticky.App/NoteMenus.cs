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
        void Add(string label, Guid? id)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = note.GroupId == id };
            item.Click += (_, _) => workspace.MoveNote(note, id);
            items.Add(item);
        }
        Add("未分组", null);
        foreach (var group in workspace.Groups.OrderBy(g => g.Name)) Add(group.Name, group.Id);
        items.Add(new Separator());
        var vault = new MenuItem { Header = "移入保险箱…" };
        vault.Click += (_, _) => workspace.MoveIntoVault(note);
        items.Add(vault);
    }
}
