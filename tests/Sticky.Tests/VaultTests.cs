using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private static int VaultReleaseChecks(string root)
    {
        // Explicit invocation prevents STICKY_TEST_FILTER from skipping the release gate.
        _root = Path.GetFullPath(root); Directory.CreateDirectory(_root);
        var app = new App { StartWorkspaceOnStartup = false }; app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var checks = new (string Name, Action Execute)[]
        {
            ("portable-password-recovery", VaultPortability), ("tampering-and-failed-writes", VaultTampering),
            ("plaintext-migration-and-sync-exclusion", VaultMigrationCleanup), ("shared-ui-lock-and-icons", VaultUiLock),
            ("interrupted-migration", VaultInterruptedMigration)
        };
        var results = new List<object>(); var failed = false;
        foreach (var check in checks)
        {
            try { check.Execute(); results.Add(new { check.Name, Passed = true }); Console.WriteLine("PASS vault release: " + check.Name); }
            catch (Exception ex) { failed = true; results.Add(new { check.Name, Passed = false }); Console.WriteLine("FAIL vault release: " + check.Name + "\n" + ex); }
        }
        File.WriteAllText(Path.Combine(_root, "vault-release-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        app.Shutdown(); return failed ? 1 : 0;
    }
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
        workspace.ShowVault(); var controller = workspace.VaultWindow!; workspace.RefreshPrivateNotes();
        var manager = workspace.Manager!; var model = (Sticky.App.ViewModels.MainViewModel)manager.DataContext;
        try
        {
            Check(model.IsVaultSelected && model.Cards.Count == 1 && !controller.IsVisible, "Vault did not use the shared manager.");
            var current = workspace.PrivateNotes.Single(); workspace.OpenNote(current, true); Pump(30);
            var window = workspace.Windows[current.Id];
            var lockIcon = (Sticky.App.Controls.LockIcon)window.FindName("PrivateLockIcon");
            Check(model.IsVaultUnlocked && lockIcon.IsUnlocked && lockIcon.Visibility == Visibility.Visible, "Unlocked vault/header icon missing.");
            var editor = (RichTextBox)window.FindName("Editor"); var title = (TextBox)window.FindName("TitleBox");
            title.Text = "已保存的私密标题"; new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text = "更新后的私密内容";
            Check(window.SaveNow(false), "Shared private editor save failed.");
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(24, 24, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var asset = Sticky.App.Editor.DocumentCodec.StoreBitmap(bitmap, workspace.StoreFor(current));
            var picture = Sticky.App.Editor.DocumentCodec.CreateImage(asset, 24, workspace.StoreFor(current));
            editor.Document.Blocks.Add(new Paragraph(new InlineUIContainer(picture)));
            Check(window.SaveNow(false) && picture.Source is not null && !File.Exists(workspace.Store.GetAssetPath(asset)), "Private image was not encrypted/in-memory.");
            using (var stream = workspace.StoreFor(current).OpenImage(asset)) Check(stream.Length > 0, "Private image stream missing.");
            var viewer = new ImageViewerWindow(workspace.StoreFor(current), asset) { Owner = window }; viewer.Show();
            Check(((System.Windows.Media.Imaging.BitmapSource)((Image)viewer.FindName("PreviewImage")).Source).PixelWidth == 24, "Shared viewer did not decode private image.");
            viewer.Close();
            workspace.RefreshPrivateNotes();
            Check(ReferenceEquals(current, workspace.PrivateNotes.Single()), "Refresh detached the open editor from the card.");
            ManagerScreenshot(manager, "vault-unlocked.png"); ManagerScreenshot(window, "vault-note.png");
            model.ToggleView(); Check(model.IsTitleView && model.Cards.Count == 1, "Private title view failed."); model.ToggleView();
            workspace.Delete(current); model.ToggleVaultTrash();
            Check(model.Cards.Count == 1 && model.ShowDeleted, "Private trash did not use shared cards.");
            workspace.Restore(current); model.ToggleVaultTrash();
            window = workspace.Windows[current.Id]; editor = (RichTextBox)window.FindName("Editor"); title = (TextBox)window.FindName("TitleBox");
            Check(controller.LockVault() && !workspace.Vault.IsUnlocked && model.Cards.Count == 0 && !window.IsVisible
                && string.IsNullOrWhiteSpace(new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text), "Lock retained private views.");
            Check(!model.IsVaultUnlocked && model.IsVaultLocked, "Locked vault retained the unlocked icon state.");
            ManagerScreenshot(manager, "vault-locked.png");
            manager.Width = 820; manager.UpdateLayout(); ManagerScreenshot(manager, "vault-locked-narrow.png");
            manager.Width = 1080;
            workspace.OpenNote(current);
            Check(workspace.Windows.Count == 0 && workspace.Notes.Count == 0 && workspace.CaptureForSync().Notes.Count == 0, "Private notes leaked to normal workspace/sync or reopened while locked.");
            workspace.Vault.Unlock(VaultPassword); workspace.RefreshPrivateNotes(); current = workspace.PrivateNotes.Single(); workspace.OpenNote(current, true);
            window = workspace.Windows[current.Id]; editor = (RichTextBox)window.FindName("Editor"); title = (TextBox)window.FindName("TitleBox");
            Check(title.Text == "已保存的私密标题" && new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.Contains("更新后的私密内容"), "Unlock lost changes.");
            title.Text = "写入失败后仍需保留的编辑";
            using (var exclusive = new FileStream(workspace.Vault.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                Check(!controller.LockVault() && workspace.Vault.IsUnlocked && !window.IsVisible && model.Cards.Count == 0, "Failed save discarded edits or exposed secrets.");
            Check(controller.LockVault(), "Lock retry failed.");
            workspace.Vault.Unlock(VaultPassword); workspace.RefreshPrivateNotes();
            Check(workspace.PrivateNotes.Single().Title == "写入失败后仍需保留的编辑", "Retry lost edits.");
            workspace.CreateVaultNote(); Check(workspace.PrivateNotes.Count == 2 && workspace.Notes.Count == 0, "Private new note escaped vault.");
            var markdown = workspace.PrivateNotes.Single(n => n.IsMarkdown);
            var markdownWindow = workspace.Windows[markdown.Id];
            var source = (MarkdownLiveEditor)markdownWindow.FindName("MarkdownEditor");
            const string privateSource = "**private-markdown-source**";
            source.Text = privateSource;
            Check(markdownWindow.SaveNow(false) && workspace.Vault.Notes.Single(n => n.Id == markdown.Id).MarkdownSource == privateSource,
                "The vault did not save Markdown source.");
            foreach (var file in Directory.EnumerateFiles(workspace.Store.Root, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith("writer.lock")))
            {
                var text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
                Check(!text.Contains("写入失败后仍需保留") && !text.Contains("更新后的私密内容") && !text.Contains(privateSource), "Plaintext leaked to " + file);
            }
            Check(workspace.Device.Windows.Count == 0, "Private windows leaked to startup layout.");
            typeof(VaultWindow).GetField("_lastInput", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(controller, DateTimeOffset.UtcNow.AddMinutes(-6));
            Pump(2300); Check(!workspace.Vault.IsUnlocked && model.Cards.Count == 0, "Idle lock failed.");
            Check(source.Text.Length == 0 && markdown.MarkdownSource is null, "Lock retained private Markdown source.");
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
