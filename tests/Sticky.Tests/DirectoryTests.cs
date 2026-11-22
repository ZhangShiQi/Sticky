using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Sticky.App;
using Sticky.App.ViewModels;
using Sticky.Core;
using Sticky.Storage;
using Sticky.Sync.Git;

internal static partial class Program
{
    private static void DirectoryCatalogAndBackup()
    {
        var source = Store("directory-legacy");
        var project = new NoteDirectory { Name = "项目" };
        var legacyJson = JsonSerializer.Serialize(new { schemaVersion = 1, groups = new[] { new { id = project.Id, name = project.Name } } });
        var catalogPath = Path.Combine(source.Root, "groups.json");
        File.WriteAllText(catalogPath, legacyJson);
        var note = Example();
        note.DirectoryId = project.Id;
        source.Save(note);
        Check(source.LoadDirectories().Single().Id == project.Id && source.LoadDirectories().Single().ParentId is null &&
            File.ReadAllText(catalogPath) == legacyJson, "A legacy root directory changed when read.");
        using (var storedNote = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(source.NotesDirectory, note.Id + ".json"))))
            Check(storedNote.RootElement.GetProperty("groupId").GetGuid() == project.Id, "The note's existing directory wire field changed.");

        var drafts = new NoteDirectory { Name = "草稿", ParentId = project.Id };
        var outline = new NoteDirectory { Name = "大纲", ParentId = drafts.Id };
        source.SaveDirectories([outline, project, drafts]);
        using (var storedCatalog = JsonDocument.Parse(File.ReadAllBytes(catalogPath)))
            Check(storedCatalog.RootElement.GetProperty("schemaVersion").GetInt32() == 2, "An older client could silently write over nested directories.");
        note.DirectoryId = outline.Id;
        source.Save(note);
        var catalogBefore = File.ReadAllBytes(catalogPath);
        project.ParentId = outline.Id;
        try { source.SaveDirectories([drafts, outline, project]); throw new Exception("Cyclic directory data was accepted."); }
        catch (InvalidDataException) { }
        Check(File.ReadAllBytes(catalogPath).SequenceEqual(catalogBefore), "Rejecting a cycle replaced the valid catalog.");

        var archive = Path.Combine(_root, "directory-tree.zip");
        BackupService.Export(source, archive);
        var destination = Store("directory-import");
        var unrelated = new NoteDirectory { Id = project.Id, Name = "另一处项目" };
        var samePath = new NoteDirectory { Name = "项目" };
        var otherDrafts = new NoteDirectory { Name = "草稿", ParentId = unrelated.Id };
        destination.SaveDirectories([unrelated, samePath, otherDrafts]);
        var imported = BackupService.Import(destination, archive).Single();
        var directories = destination.LoadDirectories();
        var tree = new DirectoryHierarchy(directories);
        var path = tree.AncestorsAndSelf(imported.DirectoryId);
        Check(path.Count == 3 && path[0].Id == samePath.Id && path[1].Id != otherDrafts.Id && path[2].Name == "大纲" &&
            tree.PathLabel(otherDrafts.Id) == "另一处项目 › 草稿", "Import associated a descendant with the wrong parent or overwrote an existing path.");
        Check(BackupService.Import(destination, archive).Single().DirectoryId == imported.DirectoryId && destination.LoadDirectories().Count == directories.Count,
            "Another import created duplicate directories or remapped the same path again.");
    }

    private static void DirectoryWorkspaceAndHistory()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "directory-workspace"));
        var main = new MainWindow(workspace);
        main.Show();
        try
        {
            var projects = workspace.CreateDirectory("项目")!;
            var archive = workspace.CreateDirectory("归档")!;
            var work = workspace.CreateDirectory("工作", projects.Id)!;
            var drafts = workspace.CreateDirectory("草稿", work.Id)!;
            var design = Example(); design.Title = "设计草稿"; design.DirectoryId = drafts.Id;
            var checklist = Example(); checklist.Title = "检查表"; checklist.DirectoryId = drafts.Id;
            workspace.Store.Save(design); workspace.Store.Save(checklist);
            workspace.Notes.AddRange([design, checklist]);
            var model = (MainViewModel)main.DataContext;
            model.SelectDirectory(drafts.Id);
            Pump(40); main.UpdateLayout();

            var tree = (TreeView)main.FindName("DirectoryNavigation");
            var projectsContainer = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(model.DirectoryItems.Single(item => item.Directory.Id == projects.Id));
            var workContainer = (TreeViewItem)projectsContainer.ItemContainerGenerator.ContainerFromIndex(0);
            Check(projectsContainer.IsExpanded && workContainer.IsExpanded && workContainer.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem,
                "The selected deep directory was not revealed through the workspace tree.");
            Check(model.Cards.Count == 2 && model.Subtitle == "项目 › 工作 › 草稿", "The directory heading or cards lost their path.");
            projectsContainer.IsExpanded = false;
            model.Refresh();
            Check(!model.DirectoryItems.Single(item => item.Directory.Id == projects.Id).IsExpanded, "Refreshing reopened a collapsed directory.");

            var timeline = workspace.Store.History.ReadTimeline();
            var original = timeline.Frame(timeline.Events.Last().Sequence);
            Check(!workspace.MoveDirectory(work, drafts.Id), "The workspace accepted a move into a descendant.");
            Check(workspace.MoveDirectory(work, archive.Id), "Could not move a directory and its descendants.");
            Check(workspace.DirectoryPath(drafts.Id) == "归档 › 工作 › 草稿" && workspace.Notes.All(n => n.DirectoryId == drafts.Id),
                "Moving a directory rewrote memberships or detached descendants.");
            timeline = workspace.Store.History.ReadTimeline();
            var move = timeline.Rows(design.Id).Last().Last;
            Check(move.Changes.Any(c => c.Contains("项目 › 工作") && c.Contains("归档 › 工作")) && move.NoteIds.Contains(checklist.Id),
                "The parent move did not appear in all descendant notes' history.");
            Check(workspace.MoveNote(design, projects.Id), "Could not move a note into a directory.");
            model.SelectDirectory(drafts.Id);
            Check(model.Cards.Single().Note.Id == checklist.Id, "A moved note remained visible in its previous directory.");
            model.SelectDirectory(projects.Id);
            Check(model.Cards.Single().Note.Id == design.Id, "A moved note was missing from its destination.");

            var restored = workspace.RestoreHistoryNote(original.Notes[design.Id], original.Directories, original.Sequence, asCopy: false);
            Check(restored.Id == design.Id && workspace.Notes.Count == 2 && workspace.DirectoryPath(restored.DirectoryId) == "项目 › 工作 › 草稿" &&
                workspace.DirectoryPath(checklist.DirectoryId) == "归档 › 工作 › 草稿", "Restoring one historical path moved other notes or duplicated the restored note.");
            var reopened = WorkspaceStartupData.Load(workspace.Store.Root);
            Check(new DirectoryHierarchy(reopened.Directories).PathLabel(reopened.Notes.Single(n => n.Id == design.Id).DirectoryId) == "项目 › 工作 › 草稿",
                "A restored nested directory did not survive reopening.");
        }
        finally { workspace.PrepareExit(); }
    }

    private static void LegacyDirectoryAssignment()
    {
        var source = Store("legacy-unassigned-directory");
        var project = new NoteDirectory { Name = "项目" };
        source.SaveDirectories([project]);
        var legacy = Example();
        source.Save(legacy);
        var archived = Example(); archived.DeletedAt = DateTimeOffset.UtcNow;
        source.Save(archived);
        var assigned = Example(); assigned.DirectoryId = project.Id;
        source.Save(assigned);
        var backup = Path.Combine(_root, "legacy-unassigned-directory.zip");
        BackupService.Export(source, backup);

        var workspace = new StickyWorkspace(source.Root);
        try
        {
            var inbox = workspace.Directories.Single(d => d.Name == DirectoryHierarchy.InboxName && d.ParentId is null);
            legacy.DirectoryId = archived.DirectoryId = inbox.Id;
            var filed = workspace.Store.LoadNotes().ToDictionary(n => n.Id);
            Check(filed.Count == 3 && SyncJson.Equal(filed[legacy.Id], legacy) && SyncJson.Equal(filed[archived.Id], archived) &&
                SyncJson.Equal(filed[assigned.Id], assigned),
                "Migration lost notes or altered an existing assignment or deletion state.");
            using var view = new MainViewModel(workspace);
            view.SelectDirectory(inbox.Id);
            Check(view.Cards.Single().Note.Id == legacy.Id, "The ordinary inbox contains notes from a different directory or the recycle bin.");
            var local = workspace.CaptureForSync();
            var incoming = local.Clone();
            var synced = Example(); incoming.Notes.Add(synced.Id, synced);
            workspace.ApplySyncedSnapshot(local, incoming);
            Check(workspace.Store.LoadNotes().Single(n => n.Id == synced.Id).DirectoryId == inbox.Id, "A legacy sync reintroduced a note without a directory.");
            var syncedInbox = workspace.Directories.Single(d => d.Id == inbox.Id);
            Check(workspace.MoveDirectory(syncedInbox, project.Id) && workspace.DirectoryPath(inbox.Id) == "项目 › 收件箱", "The inbox cannot move like an ordinary directory.");
        }
        finally { workspace.PrepareExit(); }

        var target = Store("legacy-unassigned-import");
        var imported = BackupService.Import(target, backup);
        var importedInbox = target.LoadDirectories().Single(d => d.Name == DirectoryHierarchy.InboxName);
        Check(imported.Count(n => n.DirectoryId == importedInbox.Id) == 2, "Importing a legacy ZIP left notes outside the destination directories.");
    }

    private static void DirectoryTrashLifecycle()
    {
        var root = Path.Combine(_root, "directory-trash-lifecycle");
        var workspace = new StickyWorkspace(root);
        var main = new MainWindow(workspace);
        main.Show();
        try
        {
            var parent = workspace.CreateDirectory("项目")!;
            var folder = workspace.CreateDirectory("资料", parent.Id)!;
            var child = workspace.CreateDirectory("访谈", folder.Id)!;
            var separatelyTrashed = workspace.CreateDirectory("旧草稿", folder.Id)!;
            var empty = workspace.CreateDirectory("空目录", parent.Id)!;
            var note = Example(); note.Title = "访谈纪要"; note.DirectoryId = child.Id;
            var priorTrash = Example(); priorTrash.DirectoryId = child.Id; priorTrash.DeletedAt = DateTimeOffset.UtcNow;
            var draft = Example(); draft.DirectoryId = separatelyTrashed.Id;
            foreach (var item in new[] { note, priorTrash, draft }) { workspace.Store.Save(item); workspace.Notes.Add(item); }
            Check(workspace.DeleteDirectory(empty.Id) && empty.State == DirectoryState.Removed && workspace.CreateDirectory("空目录", parent.Id) is not null,
                "An empty directory went to the recycle bin or reserved its old name.");
            Check(workspace.DeleteDirectory(separatelyTrashed.Id), "Could not trash an independent descendant before its parent.");

            var model = (MainViewModel)main.DataContext;
            model.SelectDirectory(child.Id);
            workspace.OpenNote(note);
            Pump(40); main.UpdateLayout();
            var tree = (TreeView)main.FindName("DirectoryNavigation");
            var row = ManagerVisuals<Button>(tree).Single(b => b.DataContext is DirectoryItem item && item.Directory.Id == folder.Id && b.ContextMenu is not null);
            var menu = row.ContextMenu!;
            menu.PlacementTarget = row; menu.IsOpen = true; Pump(35);
            var delete = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "删除目录"));
            menu.IsOpen = false; delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(!workspace.Windows.ContainsKey(note.Id) && !workspace.IsActiveDirectory(child.Id) && folder.State == DirectoryState.Trashed,
                "Deleting a nonempty directory did not hide its subtree or close the note window.");
            Check(note.DeletedAt is null && priorTrash.DeletedAt is not null, "Trashing a directory rewrote its notes' independent states.");
            using var search = new SearchViewModel(workspace) { Query = "访谈纪要" };
            Check(search.Results.Count == 0, "A note in a trashed directory remained discoverable in ordinary search.");
            model.ShowDeleted = true; Pump(35); main.UpdateLayout();
            Check(model.TrashedDirectories.Single().Id == folder.Id && !model.IsEmpty && model.Cards.Count == 0,
                "The recycle bin duplicated a nested directory or its notes, or hid the discarded subtree.");

            var reloaded = WorkspaceStartupData.Load(root);
            Check(new DirectoryHierarchy(reloaded.Directories).TrashRoots().Single().Id == folder.Id && reloaded.Notes.Count == 3,
                "Restart lost the directory trash or its contents.");
            var backup = Path.Combine(_root, "directory-trash.zip");
            BackupService.Export(workspace.Store, backup);
            var importedStore = Store("directory-trash-import");
            var importedNotes = BackupService.Import(importedStore, backup);
            var importedTree = new DirectoryHierarchy(importedStore.LoadDirectories());
            Check(importedTree.TrashRoots().Single().Name == folder.Name && importedNotes.Count == 3 &&
                importedNotes.All(n => !importedTree.IsActive(n.DirectoryId)), "ZIP import lost the trashed hierarchy or leaked notes into active directories.");

            var history = workspace.Store.History.ReadTimeline();
            Check(history.Rows(note.Id).Last(r => r.Last.Kind == "groupsChanged").Last.Changes.Any(c => c.StartsWith("目录移入回收站", StringComparison.Ordinal)),
                "Directory trash was missing from descendant-note history.");
            var restore = ManagerVisuals<Button>(main).Single(b => Equals(b.Content, "恢复目录"));
            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(model.TrashedDirectories.Single().Id == separatelyTrashed.Id && model.Cards.Single().Note.Id == priorTrash.Id &&
                workspace.DirectoryPath(note.DirectoryId) == "项目 › 资料 › 访谈" && search.Results.Single().Note.Id == note.Id,
                "Restoration lost the original path or restored items that had already been deleted separately.");

            Check(workspace.DeleteDirectory(folder.Id), "Could not trash the folder again.");
            var replacement = workspace.CreateDirectory("资料", parent.Id)!;
            Check(workspace.RestoreDirectory(folder.Id) && folder.ParentId == parent.Id && replacement.Name == "资料" &&
                folder.Name == "资料（2）" && workspace.DirectoryPath(note.DirectoryId) == "项目 › 资料（2） › 访谈",
                "Restoring into a same-name collision moved contents or overwrote the new directory.");
        }
        finally { workspace.PrepareExit(); }
    }
}
