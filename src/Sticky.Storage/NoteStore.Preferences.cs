using System.Text.Json;
using Sticky.Core;

namespace Sticky.Storage;

public sealed partial class NoteStore
{
    public EditorPreferences LoadEditorPreferences()
    {
        var path = Path.Combine(Root, "device", "editor.json");
        foreach (var candidate in new[] { path }.Concat(WorkspaceFiles.BackupCandidates(Root, path)))
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var preferences = JsonSerializer.Deserialize<EditorPreferences>(File.ReadAllBytes(candidate), JsonOptions)
                    ?? throw new InvalidDataException("排版设置为空。");
                if (preferences.SchemaVersion != 1)
                {
                    Warnings.Add("排版设置版本过新，已使用默认值；原文件已保留。");
                    return new EditorPreferences();
                }
                ValidateEditorPreferences(preferences);
                if (candidate != path)
                {
                    if (File.Exists(path)) WorkspaceFiles.PreserveCorrupt(Root, path);
                    AtomicWrite(path, File.ReadAllBytes(candidate), keepBackup: false);
                    Warnings.Add("排版设置已从上次备份恢复。");
                }
                return preferences;
            }
            catch (Exception ex) when (IsDataError(ex)) { }
        }
        if (File.Exists(path)) Warnings.Add("排版设置无法读取，已使用默认值；原文件已保留。");
        return new EditorPreferences();
    }

    public void SaveEditorPreferences(EditorPreferences preferences)
    {
        ValidateEditorPreferences(preferences);
        AtomicWrite(Path.Combine(Root, "device", "editor.json"), JsonSerializer.SerializeToUtf8Bytes(preferences, JsonOptions));
    }

    public static void ValidateEditorPreferences(EditorPreferences preferences)
    {
        if (preferences.SchemaVersion != 1 || !double.IsFinite(preferences.DefaultFontSize) || preferences.DefaultFontSize is < 10 or > 48
            || !double.IsFinite(preferences.ParagraphSpacing) || preferences.ParagraphSpacing is < 0 or > 32)
            throw new InvalidDataException("默认字号应为 10–48，段落间距应为 0–32。单位为逻辑像素。");
    }
}
