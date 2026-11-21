using System.Text.Json;

namespace Sticky.Storage;

public sealed partial class NoteStore
{
    private string NoteZoomPath(Guid id) => Path.Combine(Root, "device", "note-zoom", id + ".json");

    public int? LoadNoteZoom(Guid id)
    {
        var path = NoteZoomPath(id);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<int?>(File.ReadAllBytes(path)) : null; }
        catch (Exception ex) when (IsDataError(ex)) { return null; }
    }

    public void SaveNoteZoom(Guid id, int percent) =>
        AtomicWrite(NoteZoomPath(id), JsonSerializer.SerializeToUtf8Bytes(percent), keepBackup: false);

    internal void RemoveNoteZoom(Guid id)
    {
        var path = NoteZoomPath(id);
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory)) return;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("保险箱迁移不支持目录链接。");
        File.Delete(path);
        foreach (var temporary in Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".*.tmp"))
            File.Delete(temporary);
    }
}
