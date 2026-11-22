using System.Text;
using System.Text.Json.Serialization;

namespace Sticky.Core;

public sealed record NoteTag(Guid Id, string Name, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Deleted = false);

public static class NoteTagNames
{
    public const int MaxLength = 48;
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;
    public static string Normalize(string name) => name.Trim().Normalize(NormalizationForm.FormC);
    public static bool IsValid(string name) => name.Length is > 0 and <= MaxLength && !name.Any(char.IsControl);

    public static NoteTag FindOrAdd(List<NoteTag> catalog, NoteTag candidate)
    {
        var existing = catalog.FirstOrDefault(t => !t.Deleted && Comparer.Equals(t.Name, candidate.Name));
        if (existing is not null) return existing;
        var tag = catalog.Any(t => t.Id == candidate.Id) ? candidate with { Id = Guid.NewGuid(), Deleted = false } : candidate with { Deleted = false };
        catalog.Add(tag);
        return tag;
    }
}
