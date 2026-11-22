using Sticky.Core;

namespace Sticky.App;

public sealed partial class StickyWorkspace
{
    public bool IsActiveDirectory(Guid id) => Directories.Any(d => d.Id == id) && new DirectoryHierarchy(Directories).IsActive(id);

    private bool CanUseNoteDirectory(Note note) => note.DirectoryId is not { } id || IsActiveDirectory(id);

    public bool DeleteDirectory(Guid id)
    {
        var directory = Directories.FirstOrDefault(d => d.Id == id);
        var tree = new DirectoryHierarchy(Directories);
        if (directory is null || !tree.IsActive(id)) return false;
        var descendants = tree.InTreeOrder(id).Select(d => d.Id).ToHashSet();
        descendants.Add(id);
        var openNotes = Notes.Where(n => n.DirectoryId is { } key && descendants.Contains(key))
            .Select(n => Windows.GetValueOrDefault(n.Id)).OfType<NoteWindow>().ToArray();
        foreach (var window in openNotes)
            if (!window.SaveNow()) return false;

        var hasContents = tree.Children(id).Any(d => d.State != DirectoryState.Removed) || Notes.Any(n => n.DirectoryId == id);
        var state = hasContents ? DirectoryState.Trashed : DirectoryState.Removed;
        if (!SaveDirectoryState(directory, state, directory.Name, "无法删除目录")) return false;
        foreach (var window in openNotes) window.Close();
        NotifyChanged();
        return true;
    }

    public bool RestoreDirectory(Guid id)
    {
        var directory = new DirectoryHierarchy(Directories).TrashRoots().FirstOrDefault(d => d.Id == id);
        if (directory is null) return false;
        var siblingNames = Directories.Where(d => d.State == DirectoryState.Active && d.ParentId == directory.ParentId)
            .Select(d => d.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = directory.Name;
        for (var number = 2; siblingNames.Contains(name.Trim()); number++)
        {
            var suffix = $"（{number}）";
            var length = Math.Min(50 - suffix.Length, directory.Name.Length);
            if (length < directory.Name.Length && char.IsHighSurrogate(directory.Name[length - 1])) length--;
            name = directory.Name[..length] + suffix;
        }
        if (!SaveDirectoryState(directory, DirectoryState.Active, name, "无法恢复目录")) return false;
        NotifyChanged();
        return true;
    }

    private bool SaveDirectoryState(NoteDirectory directory, DirectoryState state, string name, string failureMessage)
    {
        var previousState = directory.State;
        var previousName = directory.Name;
        directory.State = state;
        directory.Name = name;
        try { Store.SaveDirectories(Directories); }
        catch (Exception ex)
        {
            directory.State = previousState;
            directory.Name = previousName;
            ShowError(failureMessage, ex);
            return false;
        }
        return true;
    }
}
