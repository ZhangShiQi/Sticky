using Sticky.Core;
using Sticky.Storage;
using Sticky.UpdateEngine;

namespace Sticky.App;

// Only file/model work belongs here. Dispatchers, timers, view models, and WPF objects are
// created by StickyWorkspace on the UI thread after the completed snapshot is handed over.
public sealed record WorkspaceStartupData(NoteStore Store, List<Note> Notes, List<NoteDirectory> Directories, List<NoteTag> Tags, DeviceState Device, EditorPreferences Preferences)
{
    public static WorkspaceStartupData Load(string root)
    {
        StartupTiming.Mark("data-load-start");
        var store = new NoteStore(root);
        var migrations = new VaultMigration(store);
        migrations.CompletePending();
        var notes = store.LoadNotes().Where(n => !migrations.MovedIds.Contains(n.Id)).ToList();
        var directories = store.LoadDirectories();
        var existingDirectoryCount = directories.Count;
        foreach (var id in notes.Where(n => n.DirectoryId.HasValue).Select(n => n.DirectoryId!.Value).Distinct())
            if (!directories.Any(d => d.Id == id)) directories.Add(new NoteDirectory { Id = id, Name = "恢复的目录 " + id.ToString("N")[..8] });
        var filed = DirectoryHierarchy.FileUnassigned(notes, directories);
        var recoveredTrash = DirectoryHierarchy.PreserveContentsUnderRemovedDirectories(directories, notes);
        if (directories.Count != existingDirectoryCount || recoveredTrash) store.SaveDirectories(directories);
        foreach (var note in filed) store.Save(note);
        var device = store.LoadDeviceState();
        var preferences = store.LoadEditorPreferences();
        var tags = store.LoadTags();
        try { store.History.Reconcile(); }
        catch (Exception ex) { store.Warnings.Add("历史暂不可读取，现有便签已保留：" + ex.Message); }
        StartupTiming.Mark("data-load-complete");
        return new(store, notes, directories, tags, device, preferences);
    }
}
