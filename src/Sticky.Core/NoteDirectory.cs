using System.Text.Json.Serialization;

namespace Sticky.Core;

public sealed class NoteDirectory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ParentId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DirectoryState State { get; set; }
}

public enum DirectoryState { Active, Trashed, Removed }

public sealed class DirectoryCatalog
{
    public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("groups")]
    public List<NoteDirectory> Directories { get; set; } = [];
}

public sealed class DirectoryHierarchy
{
    public const string UnassignedLabel = "未归档";
    public const string InboxName = "收件箱";
    private readonly Dictionary<Guid, NoteDirectory> _byId;
    private readonly ILookup<Guid?, NoteDirectory> _children;

    public DirectoryHierarchy(IEnumerable<NoteDirectory> directories)
    {
        _byId = directories.ToDictionary(directory => directory.Id);
        _children = _byId.Values.OrderBy(directory => directory.Name).ToLookup(directory => directory.ParentId);
    }

    public IEnumerable<NoteDirectory> Children(Guid? parentId) => _children[parentId];

    public bool IsActive(Guid? id)
    {
        while (id is { } key && _byId.TryGetValue(key, out var directory))
        {
            if (directory.State != DirectoryState.Active) return false;
            id = directory.ParentId;
        }
        return true;
    }

    public IEnumerable<NoteDirectory> TrashRoots() => _byId.Values
        .Where(d => d.State == DirectoryState.Trashed && IsActive(d.ParentId));

    public IEnumerable<NoteDirectory> InTreeOrder(Guid? parentId = null)
    {
        foreach (var directory in Children(parentId))
        {
            yield return directory;
            foreach (var child in InTreeOrder(directory.Id)) yield return child;
        }
    }

    public IReadOnlyList<NoteDirectory> AncestorsAndSelf(Guid? id)
    {
        var path = new Stack<NoteDirectory>();
        while (id is { } key && _byId.TryGetValue(key, out var directory))
        {
            path.Push(directory);
            id = directory.ParentId;
        }
        return path.ToArray();
    }

    public string PathLabel(Guid? id)
    {
        var path = AncestorsAndSelf(id);
        return path.Count == 0 ? UnassignedLabel : string.Join(" › ", path.Select(directory => directory.Name));
    }

    public static Guid EnsureInbox(List<NoteDirectory> directories)
    {
        var inbox = directories.FirstOrDefault(d => d.ParentId is null && d.State == DirectoryState.Active &&
            StringComparer.OrdinalIgnoreCase.Equals(d.Name.Trim(), InboxName));
        if (inbox is not null) return inbox.Id;
        inbox = new NoteDirectory { Name = InboxName };
        directories.Add(inbox);
        return inbox.Id;
    }

    public static List<Note> FileUnassigned(IEnumerable<Note> notes, List<NoteDirectory> directories)
    {
        var unassigned = notes.Where(note => note.DirectoryId is null).ToList();
        if (unassigned.Count == 0) return unassigned;
        var inbox = EnsureInbox(directories);
        foreach (var note in unassigned) note.DirectoryId = inbox;
        return unassigned;
    }

    public static bool PreserveContentsUnderRemovedDirectories(List<NoteDirectory> directories, IEnumerable<Note> notes)
    {
        if (!directories.Any(d => d.State == DirectoryState.Removed)) return false;
        var byId = directories.ToDictionary(d => d.Id);
        var parents = directories.Where(d => d.State != DirectoryState.Removed).Select(d => d.ParentId)
            .Concat(notes.Select(note => note.DirectoryId));
        var changed = false;
        foreach (var parent in parents)
        {
            for (var id = parent; id is { } key && byId.TryGetValue(key, out var directory) && directory.State == DirectoryState.Removed; id = directory.ParentId)
            {
                directory.State = DirectoryState.Trashed;
                changed = true;
            }
        }
        return changed;
    }

    // Source folders are visited parent-first so matching is always scoped to the already mapped parent.
    public static Dictionary<Guid, Guid> ImportPaths(List<NoteDirectory> destination, IEnumerable<NoteDirectory> source, bool preserveTrash = false)
    {
        var ids = destination.Select(directory => directory.Id).ToHashSet();
        var remap = new Dictionary<Guid, Guid>();
        foreach (var directory in source)
        {
            Guid? parent = directory.ParentId is { } parentId ? remap[parentId] : null;
            var state = preserveTrash ? directory.State : DirectoryState.Active;
            var match = destination.FirstOrDefault(candidate => candidate.ParentId == parent && candidate.State == state &&
                (state == DirectoryState.Active || candidate.Id == directory.Id) &&
                StringComparer.OrdinalIgnoreCase.Equals(candidate.Name.Trim(), directory.Name.Trim()));
            if (match is null)
            {
                var id = ids.Contains(directory.Id) ? Guid.NewGuid() : directory.Id;
                match = new NoteDirectory { Id = id, Name = directory.Name, ParentId = parent, State = state };
                destination.Add(match);
                ids.Add(id);
            }
            remap.Add(directory.Id, match.Id);
        }
        return remap;
    }
}
