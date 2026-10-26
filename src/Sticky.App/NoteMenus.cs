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
        void Add(string label, Guid? id)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = note.GroupId == id };
            item.Click += (_, _) => workspace.MoveNote(note, id);
            items.Add(item);
        }
        Add("未分组", null);
        foreach (var group in workspace.Groups.OrderBy(g => g.Name)) Add(group.Name, group.Id);
    }
}
