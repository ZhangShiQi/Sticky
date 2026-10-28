using System.Text.Json;
using Sticky.Core;

namespace Sticky.Storage;

public sealed partial class NoteStore
{
    public ManagerPreferences LoadManagerPreferences()
    {
        var path = Path.Combine(Root, "device", "manager.json");
        foreach (var candidate in new[] { path }.Concat(WorkspaceFiles.BackupCandidates(Root, path)))
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var preferences = JsonSerializer.Deserialize<ManagerPreferences>(File.ReadAllBytes(candidate), JsonOptions)
                    ?? throw new InvalidDataException("视图设置为空。");
                if (preferences.SchemaVersion != 1) return new ManagerPreferences();
                ValidateManagerPreferences(preferences);
                if (candidate != path)
                {
                    if (File.Exists(path)) WorkspaceFiles.PreserveCorrupt(Root, path);
                    AtomicWrite(path, File.ReadAllBytes(candidate), keepBackup: false);
                }
                return preferences;
            }
            catch (Exception ex) when (IsDataError(ex)) { }
        }
        return new ManagerPreferences();
    }
    public void SaveManagerPreferences(ManagerPreferences preferences)
    {
        ValidateManagerPreferences(preferences);
        AtomicWrite(Path.Combine(Root, "device", "manager.json"), JsonSerializer.SerializeToUtf8Bytes(preferences, JsonOptions));
    }
    private static void ValidateManagerPreferences(ManagerPreferences preferences)
    {
        if (preferences.SchemaVersion != 1 || preferences.ViewMode is not ("preview" or "titles"))
            throw new InvalidDataException("不支持的便签视图设置。");
    }
}
