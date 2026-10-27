using Sticky.Core;
using Sticky.Storage;
using Sticky.UpdateEngine;

namespace Sticky.App;

// Only file/model work belongs here. Dispatchers, timers, view models, and WPF objects are
// created by StickyWorkspace on the UI thread after the completed snapshot is handed over.
public sealed record WorkspaceStartupData(NoteStore Store, List<Note> Notes, List<NoteGroup> Groups, DeviceState Device, EditorPreferences Preferences)
{
    public static WorkspaceStartupData Load(string root)
    {
        StartupTiming.Mark("data-load-start");
        var store = new NoteStore(root);
        var notes = store.LoadNotes();
        var groups = store.LoadGroups();
        var recovered = false;
        foreach (var id in notes.Where(n => n.GroupId.HasValue).Select(n => n.GroupId!.Value).Distinct())
            if (!groups.Any(g => g.Id == id)) { groups.Add(new NoteGroup { Id = id, Name = "恢复的分组 " + id.ToString("N")[..8] }); recovered = true; }
        if (recovered) store.SaveGroups(groups);
        var device = store.LoadDeviceState();
        var preferences = store.LoadEditorPreferences();
        try { store.History.Reconcile(); }
        catch (Exception ex) { store.Warnings.Add("历史暂不可读取，现有便签已保留：" + ex.Message); }
        StartupTiming.Mark("data-load-complete");
        return new(store, notes, groups, device, preferences);
    }
}
