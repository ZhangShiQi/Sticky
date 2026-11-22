using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Sticky.App;
using Sticky.App.ViewModels;
using Sticky.Core;
using Sticky.Storage;

internal static partial class Program
{
    private static void NoteTagInteraction()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "tag-interaction"));
        var design = workspace.CreateDirectory("设计")!;
        var personal = workspace.CreateDirectory("生活")!;
        var todo = new NoteTag(Guid.NewGuid(), "待办");
        var important = new NoteTag(Guid.NewGuid(), "重要");
        var extras = Enumerable.Range(1, 5).Select(i => new NoteTag(Guid.NewGuid(), $"示例长标签{i}")).ToList();
        workspace.Tags.AddRange([todo, important, .. extras]);
        workspace.Store.SaveTags(workspace.Tags);
        var a = new Note { Title = "设计方案", DirectoryId = design.Id };
        a.SetTags(workspace.Tags.Select(t => t.Id));
        var b = new Note { Title = "采购材料", DirectoryId = personal.Id };
        b.SetMarkdown("## 采购\n\n需要周五完成"); b.SetTags([todo.Id]);
        var c = new Note { Title = "设计补充", DirectoryId = design.Id };
        foreach (var note in new[] { a, b, c }) { workspace.Notes.Add(note); workspace.Store.Save(note); }
        try
        {
            workspace.ShowSearch("");
            var search = workspace.Search!;
            Pump(30);
            var model = (SearchViewModel)search.DataContext;
            var add = (Button)search.FindName("AddTagFilter");
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var menu = add.ContextMenu!;
            var item = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, todo.Name));
            menu.IsOpen = false; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(model.Results.Select(n => n.Note.Id).ToHashSet().SetEquals([a.Id, b.Id]), "Tag-only search did not return both directories.");
            model.AddTag(important.Id);
            Check(model.Results.Single().Note.Id == a.Id, "Selecting multiple tags was not an AND filter.");
            var filters = (WrapPanel)search.FindName("TagFilters");
            var remove = ManagerVisuals<Button>(filters).Single(button => AutomationProperties.GetName(button) == "移除标签 " + important.Name);
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            model.Scope = model.Scopes.Single(scope => scope.Id == personal.Id);
            model.Query = "需要";
            Check(model.Results.Single().Note.Id == b.Id && filters.Children.Count == 1, "Removing a chip or combining the remaining tag with directory and keyword failed.");
            search.Close();

            workspace.OpenNote(a, edit: true);
            var window = workspace.Windows[a.Id]; window.Width = 365;
            Pump(55); window.UpdateLayout();
            var strip = (StackPanel)window.FindName("TagStripItems");
            var more = strip.Children.OfType<Button>().Single(button => Equals(button.Content, "…"));
            var visible = strip.Children.OfType<Border>().Count();
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var hidden = more.ContextMenu!.Items.OfType<MenuItem>().Count(item => !Equals(item.Header, "编辑标签…"));
            more.ContextMenu.IsOpen = false;
            Check(visible + hidden == a.Tags.Count && hidden > 0, "The footer did not expose every hidden tag via the overflow menu.");

            var dialog = new TagEditorWindow(workspace, c);
            dialog.Show(); Pump(35);
            var available = (WrapPanel)dialog.FindName("AvailableTags");
            var applied = (WrapPanel)dialog.FindName("AppliedTags");
            var input = (TextBox)dialog.FindName("TagFilterBox");
            input.Text = "重要";
            Check(available.Children.Count == 1, "The editor's input did not filter available tags.");
            ManagerVisuals<Button>(available).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ManagerVisuals<Button>(applied).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(applied.Children.Count == 0 && available.Children.Count == 1, "The removed tag did not return to the available list.");
            input.Text = "评审";
            input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(input), 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(applied.Children.Count == 1 && c.Tags.Count == 0, "A new tag did not enter the draft or was prematurely saved.");
            var notePath = Path.Combine(workspace.Store.NotesDirectory, c.Id + ".json");
            using (var locked = new FileStream(notePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                dialog.Close();
                Check(dialog.IsVisible && c.Tags.Count == 0 && ((TextBlock)dialog.FindName("TagEditStatus")).Text.Contains("保存失败"),
                    "A failed close discarded the tag draft or changed the note.");
            }
            dialog.Close();
            Check(!dialog.IsVisible && workspace.Store.LoadNotes().Single(n => n.Id == c.Id).Tags.Single() == workspace.Tags.Single(t => t.Name == "评审").Id,
                "Closing the editor did not preserve and save the pending selection.");
        }
        finally { workspace.PrepareExit(); }
    }

    private static void NoteTagPersistence()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "tag-persistence"));
        try
        {
            var folder = workspace.CreateDirectory("资料")!;
            var note = Example(); note.DirectoryId = folder.Id;
            workspace.Notes.Add(note); workspace.Store.Save(note);
            var tag = new NoteTag(Guid.NewGuid(), "研究");
            workspace.SaveNoteTags(note, new HashSet<Guid>(), [tag]);
            var timeline = workspace.Store.History.ReadTimeline();
            var sequence = timeline.Events.Last().Sequence;
            var frame = timeline.Frame(sequence);
            Check(frame.Tags.Single().Name == tag.Name && frame.Notes[note.Id].Tags.Single() == tag.Id &&
                timeline.Events.Last().Changes.Contains("修改便签标签"), "Tag definitions or associations were missing from history.");
            var backup = Path.Combine(_root, "tag-backup.zip");
            BackupService.Export(workspace.Store, backup);
            var other = Store("tag-merge-backup");
            var matching = new NoteTag(Guid.NewGuid(), tag.Name);
            other.SaveTags([matching]);
            var imported = BackupService.Import(other, backup).Single();
            Check(other.LoadTags().Count == 1 && imported.Tags.Single() == matching.Id, "Import did not merge a same-name tag or remap the association.");

            workspace.SaveNoteTags(note, new HashSet<Guid> { tag.Id }, []);
            var restored = workspace.RestoreHistoryNote(frame.Notes[note.Id], frame.Directories, sequence, asCopy: false);
            Check(restored.Tags.Single() == tag.Id && workspace.Store.LoadNotes().Single().Tags.Single() == tag.Id,
                "Restoring a note's historical version did not restore its tags.");
        }
        finally { workspace.PrepareExit(); }
    }
}
