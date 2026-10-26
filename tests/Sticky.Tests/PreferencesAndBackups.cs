using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Sticky.App;
using Sticky.App.Editor;
using Sticky.Core;
using Sticky.Storage;
using Sticky.Sync.Git;

internal static partial class Program
{
    private static void TypographyPreferences()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "typography"));
        var note = new Note { Blocks = [new NoteBlock { Inlines = [
            new NoteInline { Text = "旧版默认文字" },
            new NoteInline { Text = "手动大字", FontSize = 28 },
            new NoteInline { Text = "手动16", FontSize = 16, UsesDefaultFontSize = false }
        ] }] };
        workspace.Store.Save(note); workspace.Notes.Add(note); workspace.OpenNote(note, edit: true);
        workspace.CreateNote();
        var fresh = workspace.Notes.First(n => n.Id != note.Id);
        var editor = (RichTextBox)workspace.Windows[fresh.Id].FindName("Editor");
        editor.AppendText("新建便签的默认文字");
        Check(workspace.SaveAllNotes(), "Initial typography save failed.");
        var savedBytes = File.ReadAllBytes(Path.Combine(workspace.Store.NotesDirectory, fresh.Id + ".json"));
        var timestamp = fresh.ModifiedAt;
        try
        {
            workspace.ShowSettings(SettingsSection.Typography);
            var settings = workspace.Settings!;
            ((TextBox)settings.FindName("DefaultFontSizeBox")).Text = "22";
            ((TextBox)settings.FindName("ParagraphSpacingBox")).Text = "12";
            // Invoke the actual settings button, so parsing and persistence are covered too.
            var tab = (TabItem)((TabControl)settings.FindName("SettingsTabs")).SelectedItem;
            var panel = (StackPanel)((ScrollViewer)tab.Content).Content;
            panel.Children.OfType<StackPanel>().Last().Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(workspace.EditorPreferences.DefaultFontSize == 22 && workspace.EditorPreferences.ParagraphSpacing == 12, "Settings button did not apply preferences.");
            var runs = ((Paragraph)((RichTextBox)workspace.Windows[note.Id].FindName("Editor")).Document.Blocks.FirstBlock).Inlines.OfType<Run>().ToArray();
            Check(runs.Select(r => r.FontSize).SequenceEqual(new[] { 22.0, 28.0, 16.0 }), "Defaults replaced manual formatting or did not update legacy defaults.");
            Check(editor.Document.FontSize == 22 && ((Paragraph)editor.Document.Blocks.FirstBlock).Margin.Bottom == 12, "Existing new note did not update live.");
            Pump(800);
            Check(workspace.SaveAllNotes() && fresh.ModifiedAt == timestamp && savedBytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(workspace.Store.NotesDirectory, fresh.Id + ".json"))), "Local layout preferences dirtied synced note content.");
            workspace.Windows[fresh.Id].Close(); workspace.OpenNote(fresh, edit: true);
            editor = (RichTextBox)workspace.Windows[fresh.Id].FindName("Editor");
            Check(((Paragraph)editor.Document.Blocks.FirstBlock).Inlines.OfType<Run>().First().FontSize == 22, "Reopening lost the default font size.");
            workspace.Windows[fresh.Id].Activate(); workspace.Windows[fresh.Id].EnterEditMode();
            editor.CaretPosition = editor.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
            EditingCommands.EnterParagraphBreak.Execute(null, editor);
            editor.CaretPosition.InsertTextInRun("修改设置后继续输入");
            Check(workspace.Windows[fresh.Id].SaveNow(false), "Saving new text failed.");
            var encoded = DocumentCodec.Encode(editor.Document);
            Check(encoded.SelectMany(p => p.Inlines).Where(i => i.Text.Length > 0).All(i => i.UsesDefaultFontSize == true && i.FontSize == 16), "Typing persisted a device's default size as explicit formatting.");
            workspace.CreateNote();
            Check(((RichTextBox)workspace.Windows[workspace.Notes[0].Id].FindName("Editor")).Document.FontSize == 22, "New note ignored saved preferences.");
        }
        finally { workspace.PrepareExit(); }
        var restarted = new StickyWorkspace(workspace.Store.Root);
        try
        {
            Check(restarted.EditorPreferences.DefaultFontSize == 22 && restarted.EditorPreferences.ParagraphSpacing == 12, "Restart lost typography preferences.");
            restarted.OpenNote(restarted.Notes.Single(n => n.Id == fresh.Id));
            var doc = ((RichTextBox)restarted.Windows[fresh.Id].FindName("Editor")).Document;
            Check(doc.Blocks.OfType<Paragraph>().All(p => p.Margin.Bottom == 12 && p.Inlines.OfType<Run>().All(r => r.FontSize == 22)), "Restart changed paragraph/font layout.");
        }
        finally { restarted.PrepareExit(); }
    }

    private static void InvalidTypography()
    {
        var store = Store("invalid-typography");
        store.SaveEditorPreferences(new EditorPreferences { DefaultFontSize = 20, ParagraphSpacing = 4 });
        var path = Path.Combine(store.Root, "device", "editor.json"); var valid = File.ReadAllBytes(path);
        foreach (var bad in new[] { new EditorPreferences { DefaultFontSize = double.NaN }, new EditorPreferences { DefaultFontSize = 49 }, new EditorPreferences { ParagraphSpacing = -1 }, new EditorPreferences { ParagraphSpacing = double.PositiveInfinity } })
        {
            try { store.SaveEditorPreferences(bad); throw new Exception("Invalid preferences were accepted."); } catch (InvalidDataException) { }
            Check(valid.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "Invalid settings replaced good preferences.");
        }
        store.SaveEditorPreferences(new EditorPreferences { DefaultFontSize = 24 }); File.WriteAllText(path, "{broken");
        Check(store.LoadEditorPreferences().DefaultFontSize == 20 && store.Warnings.Any(w => w.Contains("排版设置")), "Preferences could not recover from dedicated backup.");
    }

    private static void DedicatedBackups()
    {
        var store = Store("legacy-backups"); var note = Example(); store.Save(note);
        store.SaveGroups([new NoteGroup { Name = "旧分组" }]); store.SaveDeviceState(new DeviceState());
        store.SaveEditorPreferences(new EditorPreferences { DefaultFontSize = 20 });
        var baseline = Path.Combine(store.Root, "device", "sync", "test-target", "baseline.json");
        SyncJson.WriteAtomic(baseline, SyncJson.Bytes(new SyncSnapshot()), store.Root);
        var originals = new[] { Path.Combine(store.NotesDirectory, note.Id + ".json"), Path.Combine(store.Root, "groups.json"),
            Path.Combine(store.Root, "device", "windows.json"), Path.Combine(store.Root, "device", "editor.json"), baseline };
        foreach (var path in originals) File.Move(WorkspaceFiles.BackupPath(store.Root, path), path + ".bak");
        var notePath = originals[0]; var central = WorkspaceFiles.BackupPath(store.Root, notePath);
        Directory.CreateDirectory(Path.GetDirectoryName(central)!); File.WriteAllText(central, "existing older backup");
        File.SetLastWriteTimeUtc(central, DateTime.UtcNow.AddDays(-1));
        store = new NoteStore(store.Root);
        Check(originals.All(p => !File.Exists(p + ".bak") && File.Exists(WorkspaceFiles.BackupPath(store.Root, p))), "Legacy backups were not moved into mirrored dedicated folders.");
        Check(Directory.EnumerateFiles(Path.Combine(store.BackupsDirectory, "legacy"), "*.bak", SearchOption.AllDirectories).Any(p => File.ReadAllText(p) == "existing older backup"), "Migration discarded an existing backup on collision.");
        File.WriteAllText(notePath, "{broken"); File.WriteAllText(originals[1], "{broken");
        Check(store.LoadNotes().Single().Title == note.Title && store.LoadGroups().Single().Name == "旧分组", "Migrated backups no longer recover notes/groups.");
        note.Title = "新版本"; store.Save(note); SyncJson.WriteAtomic(baseline, SyncJson.Bytes(new SyncSnapshot()), store.Root);
        Check(Directory.EnumerateFiles(store.Root, "*.bak", SearchOption.AllDirectories).All(p => Path.GetRelativePath(store.Root, p).StartsWith("backups" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)), "A new .bak leaked beside normal data files.");
        try { WorkspaceFiles.BackupPath(store.Root, Path.Combine(_root, "outside.json")); throw new Exception("Backup path escaped the workspace."); } catch (ArgumentException) { }
    }

    private static void BackupOnlyRecovery()
    {
        var store = Store("backup-only"); var note = Example(); store.Save(note);
        store.SaveGroups([new NoteGroup { Name = "恢复分组" }]);
        File.Delete(Path.Combine(store.NotesDirectory, note.Id + ".json")); File.Delete(Path.Combine(store.Root, "groups.json"));
        var reopened = new NoteStore(store.Root);
        Check(reopened.LoadNotes().Single().Id == note.Id && reopened.LoadGroups().Single().Name == "恢复分组", "Backup-only files were not discovered/restored.");
        Check(File.Exists(Path.Combine(store.NotesDirectory, note.Id + ".json")) && File.Exists(Path.Combine(store.Root, "groups.json")), "Recovery did not recreate primary data.");
    }
}
