using System.IO;
using Sticky.Core;

namespace Sticky.App;

public sealed partial class StickyWorkspace
{
    public List<NoteTag> Tags { get; } = [];

    internal bool CanEditTags(Note note) => Notes.Contains(note) && note.DeletedAt is null && CanUseNoteDirectory(note) && !IsVaultNote(note.Id);

    public void SaveNoteTags(Note note, IReadOnlySet<Guid> original, IReadOnlyList<NoteTag> selected)
    {
        if (!CanEditTags(note)) throw new InvalidOperationException("这张便签已移出可编辑的工作空间。");
        if (Windows.TryGetValue(note.Id, out var window) && !window.SaveNow(showError: false))
            throw new IOException("无法保存便签正文，请修复保存问题后再试。");

        var next = Tags.ToList();
        var selectedIds = selected.Select(t => t.Id).ToHashSet();
        // Apply only edits made in this dialog, so a concurrent sync can add its own labels.
        var membership = note.Tags.Where(id => !original.Contains(id) || selectedIds.Contains(id)).ToHashSet();
        foreach (var tag in selected.Where(t => !original.Contains(t.Id)))
        {
            var existing = next.FirstOrDefault(t => t.Id == tag.Id);
            if (existing is not { Deleted: true }) membership.Add((existing ?? NoteTagNames.FindOrAdd(next, tag)).Id);
        }
        membership.ExceptWith(next.Where(t => t.Deleted).Select(t => t.Id));

        if (membership.SetEquals(note.Tags)) return;
        // Save new definitions first. If the note cannot be written, the definitions are still available on retry.
        if (next.Count != Tags.Count)
        {
            Store.SaveTags(next);
            Tags.Clear(); Tags.AddRange(next);
        }
        var previous = note.TagIds;
        try
        {
            note.SetTags(membership);
            Store.Save(note);
        }
        catch
        {
            note.SetTags(previous ?? []);
            throw;
        }
        NotifyChanged();
    }

    internal void UpdateTag(Guid id, string? name)
    {
        var tag = Tags.FirstOrDefault(t => t.Id == id && !t.Deleted);
        if (tag is null) return;
        var normalized = name is null ? tag.Name : NoteTagNames.Normalize(name);
        if (!NoteTagNames.IsValid(normalized) || Tags.Any(t => t.Id != id && !t.Deleted && NoteTagNames.Comparer.Equals(t.Name, normalized)))
            throw new InvalidDataException("标签名称须为 1–48 字且不可重复。");
        var next = Tags.Select(t => t.Id == id ? t with { Name = normalized, Deleted = name is null } : t).ToList();
        Store.SaveTags(next);
        Tags.Clear(); Tags.AddRange(next);
        NotifyChanged();
    }
}
