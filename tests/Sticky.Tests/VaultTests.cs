using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Sticky.App;
using Sticky.Core;
using Sticky.Storage;
using Sticky.Sync.Git;

internal static partial class Program
{
    private const string VaultPassword = "独立的保险箱口令-2026-Testing!";
    private static void VaultPortability()
    {
        var store = Store("vault-portable"); using var vault = new VaultStore(store.Root);
        var recovery = vault.Create(VaultPassword);
        var image = store.AddImage([1, 2, 3, 4]);
        var note = new Note { Title = "秘密标题-secret-marker", Blocks = [new NoteBlock { Inlines = [new NoteInline { Text = "private-key-secret-value", Bold = true }, new NoteInline { Kind = "image", AssetId = image }] }] };
        vault.Save(note, new Dictionary<string, byte[]> { [image] = [1, 2, 3, 4] });
        note.Title = "第二个私密标题"; vault.Save(note);
        Check(vault.Versions(note.Id).Single().Title == "秘密标题-secret-marker", "Encrypted history was lost.");
        foreach (var path in new[] { vault.FilePath, WorkspaceFiles.BackupPath(store.Root, vault.FilePath) })
        {
            var text = File.ReadAllText(path);
            Check(!text.Contains("private-key-secret-value") && !text.Contains("secret-marker") && !text.Contains(VaultPassword) && !text.Contains(recovery), "Vault or its backup contains plaintext secrets.");
        }
        vault.Lock();
        try { _ = vault.Notes; throw new Exception("Locked vault exposed notes."); } catch (InvalidOperationException) { }
        var before = File.ReadAllBytes(vault.FilePath);
        try { vault.Unlock("incorrect password"); throw new Exception("Wrong password accepted."); } catch (CryptographicException) { }
        Check(!vault.IsUnlocked && before.SequenceEqual(File.ReadAllBytes(vault.FilePath)), "Failed unlock changed state or disk.");
        var exported = Path.Combine(_root, "portable.stickyvault"); vault.Export(exported);
        var other = Store("vault-other-computer"); using var imported = new VaultStore(other.Root);
        imported.Import(exported, VaultPassword); imported.Unlock(VaultPassword);
        Check(imported.Notes.Single().Blocks[0].Inlines[0].Bold && imported.ReadImage(image).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Portable unlock lost formatting/images.");
        imported.ChangePassword("全新的独立主密码-Password-Changed!"); imported.Lock();
        try { imported.Unlock(VaultPassword); throw new Exception("Old password still opens latest vault."); } catch (CryptographicException) { }
        imported.UnlockWithRecovery(recovery);
        Check(imported.Notes.Single().Title == note.Title, "Recovery key did not survive password change.");
    }
    private static void VaultTampering()
    {
        var store = Store("vault-tamper"); using var vault = new VaultStore(store.Root); var recovery = vault.Create(VaultPassword);
        vault.Save(new Note { Title = "secret" }); vault.Lock();
        var original = File.ReadAllBytes(vault.FilePath);
        foreach (var part in new[] { "content", "passwordKey", "recoveryKey" })
        {
            var e = JsonSerializer.Deserialize<VaultStore.Envelope>(original, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
            var box = part == "content" ? e.Content : part == "passwordKey" ? e.PasswordKey : e.RecoveryKey;
            box.Tag[0] ^= 1;
            File.WriteAllBytes(vault.FilePath, JsonSerializer.SerializeToUtf8Bytes(e, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            try
            {
                if (part == "recoveryKey") vault.UnlockWithRecovery(recovery); else vault.Unlock(VaultPassword);
                throw new Exception("Tampered ciphertext accepted.");
            }
            catch (CryptographicException) { }
            Check(!vault.IsUnlocked, "Tampered unlock exposed state.");
        }
        File.WriteAllBytes(vault.FilePath, original);
        using (var exclusive = new FileStream(vault.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { vault.Unlock(VaultPassword); throw new Exception("Locked file read succeeded."); } catch (IOException) { }
        }
        vault.Unlock(VaultPassword);
        var before = vault.Notes.Single().Title;
        using (var exclusive = new FileStream(vault.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { vault.Save(new Note { Title = "should-not-commit" }); throw new Exception("Write to locked file succeeded."); } catch (IOException) { }
        }
        Check(vault.Notes.Count == 1 && vault.Notes.Single().Title == before, "Failed save mutated committed state.");
        Check(!Directory.EnumerateFiles(store.Root, "*.tmp", SearchOption.AllDirectories).Any(), "Temporary ciphertext was left behind after failed save.");
    }
    private static void VaultMigrationCleanup()
    {
        var store = Store("vault-migration"); using var vault = new VaultStore(store.Root); vault.Create(VaultPassword);
        var secret = new Note { Title = "private-migration-marker", Blocks = [new NoteBlock { Inlines = [new NoteInline { Text = "migration-secret-body" }] }] };
        var keep = new Note { Title = "ordinary-note" };
        var asset = store.AddImage([9, 8, 7, 6]);
        secret.Blocks.Add(new NoteBlock { Inlines = [new NoteInline { Kind = "image", AssetId = asset }] });
        store.Save(secret); store.Save(keep); keep.Title = "ordinary-updated"; store.Save(keep);
        secret.Blocks[0].Inlines[0].Text = "migration-secret-version-two"; store.Save(secret);
        var original = Path.Combine(store.NotesDirectory, secret.Id + ".json");
        WorkspaceFiles.PreserveCorrupt(store.Root, original);
        var migration = new VaultMigration(store); migration.MoveIn(vault, secret);
        Check(vault.Notes.Single().Title == secret.Title && vault.Versions(secret.Id).Count >= 2, "Migration lost note/history.");
        Check(!File.Exists(store.GetAssetPath(asset)), "Private image still exists as plaintext.");
        foreach (var path in Directory.EnumerateFiles(store.Root, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith("writer.lock")))
        {
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Check(!text.Contains("private-migration-marker") && !text.Contains("migration-secret-"), "Migration left plaintext at " + path);
        }
        var timeline = store.History.ReadTimeline();
        Check(timeline.Events.All(e => e.Target != "notes/" + secret.Id + ".json") && timeline.Frame(timeline.Events.Last().Sequence).Notes[keep.Id].Title == "ordinary-updated", "Sanitization damaged unrelated history.");
        Check(store.History.NoteVersions(keep.Id).Count == 2, "Unrelated historical versions disappeared.");
        var restarted = WorkspaceStartupData.Load(store.Root);
        Check(restarted.Notes.Count == 1 && restarted.Notes.Single().Id == keep.Id, "Moved source returned at startup.");
        try { store.Save(secret); throw new Exception("Old source resurrected in plaintext."); } catch (InvalidOperationException) { }
        var workspace = new StickyWorkspace(restarted);
        try
        {
            var incoming = new SyncSnapshot { Notes = new() { [secret.Id] = secret } };
            var result = workspace.ApplySyncedSnapshot(workspace.CaptureForSync(), incoming);
            Check(result.Snapshot.Notes[secret.Id].Blocks.Count == 0 && workspace.Notes.All(n => n.Id != secret.Id), "Sync resurrected moved source.");
        }
        finally { workspace.PrepareExit(); }
        var zip = Path.Combine(_root, "vault-backup.zip"); BackupService.Export(store, zip);
        using var archive = ZipFile.OpenRead(zip);
        Check(archive.GetEntry(VaultStore.FileName) is not null, "Ordinary backup omitted encrypted vault.");
        var target = Store("vault-zip-target"); using var imported = new VaultStore(target.Root);
        try { imported.Import(zip, "incorrect password"); throw new Exception("ZIP import accepted a wrong password."); } catch (CryptographicException) { }
        Check(!imported.Exists, "Failed import created a destination vault.");
        imported.Import(zip, VaultPassword); imported.Unlock(VaultPassword);
        Check(imported.Notes.Single().Id == secret.Id, "Encrypted vault could not be imported from a ZIP backup.");
    }
    private static void VaultUiLock()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "vault-ui")); workspace.Vault.Create(VaultPassword);
        var note = new Note { Title = "测试私密便签", Blocks = [new NoteBlock { Inlines = [new NoteInline { Text = "private-ui-body" }] }] };
        workspace.Vault.Save(note);
        workspace.ShowVault(); var window = workspace.VaultWindow!; window.RefreshNotes(note.Id); Pump(30);
        var editor = (RichTextBox)window.FindName("Editor"); var title = (TextBox)window.FindName("TitleBox");
        try
        {
            title.Text = "已保存的私密标题"; new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text = "更新后的私密内容";
            Check(window.SaveCurrent(), "Private editor save failed.");
            ManagerScreenshot(window, "vault-unlocked.png");
            Check(window.LockVault() && !workspace.Vault.IsUnlocked && title.Text.Length == 0 && ((ListBox)window.FindName("NoteList")).Items.Count == 0
                && string.IsNullOrWhiteSpace(new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text), "Lock retained visible secrets.");
            ManagerScreenshot(window, "vault-locked.png");
            Check(workspace.Notes.Count == 0 && workspace.CaptureForSync().Notes.Count == 0, "Private notes leaked to ordinary workspace/sync.");
            workspace.Vault.Unlock(VaultPassword); window.RefreshNotes(note.Id);
            Check(title.Text == "已保存的私密标题" && new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.Contains("更新后的私密内容"), "Unlock lost private editor changes.");
            title.Text = "写入失败后仍需保留的编辑";
            using (var exclusive = new FileStream(workspace.Vault.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Check(!window.LockVault() && workspace.Vault.IsUnlocked && ((Grid)window.FindName("UnlockedPanel")).Visibility == Visibility.Collapsed,
                    "Failed lock lost edits, exposed the editor or reported a successful lock.");
            }
            Check(window.LockVault(), "Lock did not recover after the file became writable.");
            workspace.Vault.Unlock(VaultPassword); window.RefreshNotes(note.Id);
            Check(title.Text == "写入失败后仍需保留的编辑", "Lock retry lost unsaved edits.");
            typeof(VaultWindow).GetField("_lastInput", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(window, DateTimeOffset.UtcNow.AddMinutes(-6));
            Pump(2300);
            Check(!workspace.Vault.IsUnlocked && title.Text.Length == 0, "Idle timer failed to lock and clear private views.");
        }
        finally { workspace.PrepareExit(); }
    }
    private static void VaultInterruptedMigration()
    {
        var store = Store("vault-interrupted-migration"); using var vault = new VaultStore(store.Root); vault.Create(VaultPassword);
        var asset = store.AddImage([11, 22, 33]);
        var source = new Note { Title = "interrupted-private-marker", Blocks = [new NoteBlock { Inlines = [new NoteInline { Kind = "image", AssetId = asset }] }] };
        var other = new Note { Title = "shared image stays", Blocks = source.Blocks };
        store.Save(source); store.Save(other);
        var migration = new VaultMigration(store);
        using (var lockedSource = new FileStream(Path.Combine(store.NotesDirectory, source.Id + ".json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { migration.MoveIn(vault, source); throw new Exception("Migration unexpectedly replaced a locked source."); }
            catch (IOException) { }
        }
        Check(vault.Notes.Single().Id == source.Id && new VaultMigration(store).Transfers.Single().Complete == false, "Failed source cleanup lost encrypted destination or retry journal.");
        var restarted = WorkspaceStartupData.Load(store.Root);
        Check(restarted.Notes.Single().Id == other.Id && File.Exists(store.GetAssetPath(asset)) && new VaultMigration(store).Transfers.Single().Complete,
            "Restart failed to finish cleanup or deleted a shared image.");
        var stage = Path.Combine(store.Root, ".vault-history-stage"); Directory.CreateDirectory(stage);
        var stagedHistory = Path.Combine(stage, "history"); Directory.CreateDirectory(stagedHistory);
        foreach (var file in Directory.EnumerateFiles(store.History.DirectoryPath, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(stagedHistory, Path.GetRelativePath(store.History.DirectoryPath, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        File.WriteAllText(Path.Combine(stage, "ready"), "1");
        var old = Path.Combine(store.Root, ".vault-history-old"); Directory.Move(store.History.DirectoryPath, old);
        File.WriteAllText(Path.Combine(old, "private-old-marker"), "interrupted-private-marker");
        var recovered = new NoteStore(store.Root);
        Check(!Directory.Exists(old) && !Directory.Exists(stage) && recovered.History.ReadTimeline().Frame(recovered.History.ReadTimeline().Events.Last().Sequence).Notes.ContainsKey(other.Id),
            "Interrupted history directory swap was not recovered safely.");
    }
}
