using System.Windows;
using Sticky.Core;
using Sticky.Storage;
using Sticky.Sync.Git;
using Sticky.App.Synchronization;
using WindowState = Sticky.Core.WindowState;

namespace Sticky.App;

public sealed class StickyWorkspace
{
    public NoteStore Store { get; }
    public List<Note> Notes { get; }
    public List<NoteGroup> Groups { get; }
    public DeviceState Device { get; }
    public EditorPreferences EditorPreferences { get; private set; }
    public Dictionary<Guid, NoteWindow> Windows { get; } = [];
    public event Action? Changed;
    public bool IsExiting { get; private set; }
    public MainWindow? Manager { get; set; }
    public SearchWindow? Search { get; private set; }
    public SyncCoordinator Sync { get; }
    public SettingsWindow? Settings { get; private set; }
    public StartupRegistration Startup { get; }

    public StickyWorkspace(string root, StartupRegistration? startup = null)
    {
        Store = new NoteStore(root);
        Startup = startup ?? new StartupRegistration(Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "Sticky.exe"), AppContext.BaseDirectory, Store.Root);
        Notes = Store.LoadNotes();
        Groups = Store.LoadGroups();
        // Recover names for externally copied notes without dropping their membership.
        foreach (var id in Notes.Where(n => n.GroupId.HasValue).Select(n => n.GroupId!.Value).Distinct())
            if (!Groups.Any(g => g.Id == id)) Groups.Add(new NoteGroup { Id = id, Name = "恢复的分组 " + id.ToString("N")[..8] });
        if (Groups.Count > 0) Store.SaveGroups(Groups);
        Device = Store.LoadDeviceState();
        EditorPreferences = Store.LoadEditorPreferences();
        Sync = new SyncCoordinator(this);
    }

    public void NotifyChanged() => Changed?.Invoke();

    public void CreateNote(Guid? groupId = null)
    {
        var note = new Note { GroupId = groupId };
        try { Store.Save(note); }
        catch (Exception ex) { ShowError("无法创建便签", ex); return; }
        Notes.Insert(0, note);
        NotifyChanged();
        OpenNote(note, edit: true);
    }

    public void OpenNote(Note note, bool edit = false)
    {
        if (note.DeletedAt is not null) return;
        if (Windows.TryGetValue(note.Id, out var existing))
        {
            if (existing.WindowState == System.Windows.WindowState.Minimized) existing.WindowState = System.Windows.WindowState.Normal;
            existing.Activate();
            if (edit) existing.EnterEditMode();
            return;
        }
        var state = GetWindowState(note.Id);
        var window = new NoteWindow(this, note, state);
        Windows.Add(note.Id, window);
        state.IsOpen = true;
        window.Show();
        if (edit) window.EnterEditMode();
        SaveDevice();
        NotifyChanged();
    }

    public WindowState GetWindowState(Guid id)
    {
        if (!Device.Windows.TryGetValue(id, out var state))
        {
            var offset = Windows.Count % 7 * 30;
            state = new WindowState { Left = 190 + offset, Top = 130 + offset };
            Device.Windows[id] = state;
        }
        return state;
    }

    public void RestoreWindows()
    {
        foreach (var note in Notes.Where(n => n.DeletedAt is null && GetWindowState(n.Id).IsOpen).ToArray()) OpenNote(note);
    }

    public void Delete(Note note)
    {
        if (Windows.TryGetValue(note.Id, out var window) && !window.SaveNow()) return;
        var previous = note.DeletedAt;
        var previousModified = note.ModifiedAt;
        note.DeletedAt = DateTimeOffset.UtcNow;
        note.ModifiedAt = DateTimeOffset.UtcNow;
        try { Store.Save(note); }
        catch (Exception ex)
        {
            note.DeletedAt = previous;
            note.ModifiedAt = previousModified;
            ShowError("无法移入回收站", ex);
            return;
        }
        window?.Close();
        NotifyChanged();
    }

    public void Restore(Note note)
    {
        var deleted = note.DeletedAt;
        var modified = note.ModifiedAt;
        note.DeletedAt = null;
        note.ModifiedAt = DateTimeOffset.UtcNow;
        try { Store.Save(note); }
        catch (Exception ex) { note.DeletedAt = deleted; note.ModifiedAt = modified; ShowError("无法恢复便签", ex); return; }
        NotifyChanged();
        OpenNote(note);
    }

    public bool PrepareExit()
    {
        foreach (var window in Windows.Values.ToArray()) if (!window.SaveNow()) return false;
        foreach (var window in Windows.Values) window.CaptureWindowState();
        if (!SaveDevice()) return false;
        IsExiting = true;
        Sync.Dispose();
        foreach (var window in Windows.Values.ToArray()) window.Close();
        Search?.Close();
        Settings?.Close();
        Manager?.Close();
        return true;
    }

    public bool SaveAllNotes()
    {
        foreach (var window in Windows.Values.ToArray()) if (!window.SaveNow()) return false;
        return true;
    }

    public bool UpdateEditorPreferences(EditorPreferences preferences)
    {
        NoteStore.ValidateEditorPreferences(preferences);
        if (!SaveAllNotes()) return false;
        var saved = new EditorPreferences { DefaultFontSize = preferences.DefaultFontSize, ParagraphSpacing = preferences.ParagraphSpacing };
        Store.SaveEditorPreferences(saved);
        EditorPreferences = saved;
        foreach (var window in Windows.Values) window.ApplyEditorPreferences(saved);
        return true;
    }

    public void ShowManager()
    {
        if (Manager is null) Manager = new MainWindow(this);
        if (Manager.WindowState == System.Windows.WindowState.Minimized) Manager.WindowState = System.Windows.WindowState.Normal;
        Manager.Show();
        Manager.Activate();
    }

    public void ShowSearch() => ShowSearch(null);

    public void ShowSearch(string? query)
    {
        if (!SaveAllNotes()) return;
        if (Search is null)
        {
            Search = new SearchWindow(this);
            Search.Closed += (_, _) => Search = null;
        }
        if (Search.WindowState == System.Windows.WindowState.Minimized) Search.WindowState = System.Windows.WindowState.Normal;
        Search.Show();
        Search.Activate();
        if (query is not null) Search.SetQuery(query);
        Search.FocusQuery();
    }

    public string GroupName(Guid? id) => id is null ? "未分组" : Groups.FirstOrDefault(g => g.Id == id)?.Name ?? "未分组";

    public void ShowSyncSettings() => ShowSettings(SettingsSection.Sync);

    public void ShowSettings() => ShowSettings(SettingsSection.Sync);

    public void ShowSettings(SettingsSection section)
    {
        if (Settings is null)
        {
            Settings = new SettingsWindow(this);
            Settings.Closed += (_, _) => Settings = null;
        }
        Settings.SelectSection(section);
        if (Settings.WindowState == System.Windows.WindowState.Minimized) Settings.WindowState = System.Windows.WindowState.Normal;
        Settings.Show(); Settings.Activate();
    }

    public SyncSnapshot CaptureForSync()
    {
        if (!SaveAllNotes()) throw new System.IO.IOException("存在尚未保存成功的便签，同步已暂停；编辑内容仍保留在窗口中。");
        foreach (var file in System.IO.Directory.EnumerateFiles(Store.NotesDirectory, "*.json"))
            if (Guid.TryParse(System.IO.Path.GetFileNameWithoutExtension(file), out var id) && !Notes.Any(n => n.Id == id))
                throw new System.IO.IOException("存在尚未读取的本地便签文件，同步已暂停以保留原始数据。请先处理启动时的数据恢复提示。");
        return new SyncSnapshot { Notes = Notes.ToDictionary(n => n.Id, n => n), Groups = Groups }.Clone();
    }

    public MergeResult ApplySyncedSnapshot(SyncSnapshot captured, SyncSnapshot incoming, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var current = CaptureForSync();
        // Edits made after the first capture own the original editor; incoming competing content becomes a copy.
        var reconciled = SnapshotMerger.Merge(captured, incoming, current);
        var final = reconciled.Snapshot;
        GitSyncEngine.ValidateSnapshot(final, checkAssets: false);
        foreach (var bytes in final.Assets.Values) { token.ThrowIfCancellationRequested(); Store.AddImage(bytes); }
        Store.SaveGroups(final.Groups);
        Groups.Clear(); Groups.AddRange(final.Groups);
        // Save new conflict copies before changing any existing original, including across crashes/disk errors.
        foreach (var next in final.Notes.Values.OrderBy(n => Notes.Any(existing => existing.Id == n.Id)))
        {
            token.ThrowIfCancellationRequested();
            var existing = Notes.FirstOrDefault(n => n.Id == next.Id);
            if (existing is not null && SyncJson.Equal(existing, next)) continue;
            Store.Save(next);
            if (existing is null) { Notes.Add(next); continue; }
            var reloadBody = !SyncJson.Equal(existing.Blocks, next.Blocks);
            existing.Title = next.Title; existing.Color = next.Color; existing.Blocks = next.Blocks;
            existing.GroupId = next.GroupId; existing.ConflictOf = next.ConflictOf;
            existing.CreatedAt = next.CreatedAt; existing.ModifiedAt = next.ModifiedAt; existing.DeletedAt = next.DeletedAt;
            if (Windows.TryGetValue(existing.Id, out var window))
            {
                window.ReloadAfterSync(reloadBody);
                if (existing.DeletedAt is not null) window.Close();
            }
        }
        NotifyChanged();
        return reconciled;
    }

    public NoteGroup? CreateGroup(string name)
    {
        var group = new NoteGroup { Name = name.Trim() };
        var next = Groups.Append(group).ToList();
        try { Store.SaveGroups(next); }
        catch (Exception ex) { ShowError("无法创建分组", ex); return null; }
        Groups.Add(group);
        NotifyChanged();
        return group;
    }

    public bool RenameGroup(NoteGroup group, string name)
    {
        var previous = group.Name;
        group.Name = name.Trim();
        try { Store.SaveGroups(Groups); }
        catch (Exception ex) { group.Name = previous; ShowError("无法重命名分组", ex); return false; }
        NotifyChanged();
        return true;
    }

    public bool MoveNote(Note note, Guid? groupId)
    {
        if (groupId is not null && !Groups.Any(g => g.Id == groupId)) return false;
        if (Windows.TryGetValue(note.Id, out var window) && !window.SaveNow()) return false;
        if (note.GroupId == groupId) return true;
        var previous = note.GroupId;
        var modified = note.ModifiedAt;
        note.GroupId = groupId;
        note.ModifiedAt = DateTimeOffset.UtcNow;
        try { Store.Save(note); }
        catch (Exception ex) { note.GroupId = previous; note.ModifiedAt = modified; ShowError("无法移动便签", ex); return false; }
        NotifyChanged();
        return true;
    }

    public bool SaveDevice()
    {
        try { Store.SaveDeviceState(Device); return true; }
        catch (Exception ex) { ShowError("无法保存窗口布局", ex); return false; }
    }

    public static void ShowError(string message, Exception ex) => MessageBox.Show(message + "：\n" + ex.Message,
        "Sticky", MessageBoxButton.OK, MessageBoxImage.Error);
}
