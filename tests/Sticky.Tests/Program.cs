using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sticky.App;
using Sticky.App.Editor;
using Sticky.App.ViewModels;
using Sticky.Core;
using Sticky.Storage;
using Sticky.Sync.Git;

internal static partial class Program
{
    private static string _root = "";
    private static int _passed;
    private static int _failed;
    private static readonly string[] TestFilters = (Environment.GetEnvironmentVariable("STICKY_TEST_FILTER") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--instance-probe") return InstanceProbe(args);
        _root = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "Sticky.Tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(_root);
        GitIntegrationTests.ToolBaseDirectory = args.Length > 1 ? Path.GetFullPath(args[1]) : null;
        var app = new App { StartWorkspaceOnStartup = false };
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Run("JSON preserves Chinese text, formatting, lists, and deletion", StorageRoundTrip);
        Run("Interrupted/corrupt save restores backup and preserves damaged source", BackupRecovery);
        Run("Corrupt notes do not hide healthy notes", CorruptIsolation);
        Run("Unsupported schema is reported without rewriting files", UnsupportedSchema);
        Run("Image assets are content-addressed; traversal is rejected", AssetSafety);
        Run("Rich document survives encode/decode including image and inherited styles", DocumentRoundTrip);
        Run("Paragraph layout stays identical after Enter and save/reopen", ParagraphLayout);
        Run("Typography preferences apply live, persist, and preserve explicit font sizes", TypographyPreferences);
        Run("Dedicated backups migrate legacy files without losing collisions or recovery", DedicatedBackups);
        Run("Backup-only notes and groups recover from the dedicated directory", BackupOnlyRecovery);
        Run("Invalid typography settings never replace saved preferences", InvalidTypography);
        Run("Missing image references survive subsequent saves", MissingImage);
        Run("ZIP backup round-trip and duplicate import preserve existing notes", ZipRoundTrip);
        Run("Unsafe ZIP paths are rejected before writing notes", UnsafeZip);
        Run("Device layout stays outside note content", DeviceRoundTrip);
        Run("Multiple windows, debounce autosave, close/reopen, exit/restart", WindowLifecycle);
        Run("New-note button and shortcut open beside the current note and retain the group", NearbyNoteWindows);
        Run("Nearby placement fits monitor edges, negative coordinates and scaling", NearbyPlacementEdges);
        Run("Repeated nearby creation finds free slots among differently placed notes", NearbyAvoidance);
        Run("Crowded nearby placement cascades distinctly instead of jumping far away", CrowdedNearbyPlacement);
        Run("Repeated new-note actions avoid visible windows and ignore minimized notes", RepeatedNearbyWindows);
        Run("Failed autosave retains edits and succeeds after the file unlocks", FailedAutosave);
        Run("Groups persist, rename, move and filter independently from search", GroupLifecycle);
        Run("Backup import remaps conflicting group IDs without overwriting groups", GroupBackup);
        Run("Sync configuration rejects tokens in URLs and non-GitHub destinations", GitIntegrationTests.ConfigValidation);
        Run("Git transfers rich text, groups and images without device settings", () => GitTest("git-round-trip", GitIntegrationTests.RoundTrip));
        Run("Two-device conflicts preserve both versions and do not duplicate on retry", () => GitTest("git-conflict", GitIntegrationTests.Conflict));
        Run("Offline edits survive failed sync, restart, and reconnect", () => GitTest("git-offline", GitIntegrationTests.Offline));
        Run("Independent note changes merge automatically", () => GitTest("git-different-notes", GitIntegrationTests.DifferentNotes));
        Run("Deletion versus editing keeps both tombstone and active content", () => GitTest("git-delete-edit", GitIntegrationTests.DeleteVsEdit));
        Run("Non-fast-forward pushes retry without force and retain concurrent commits", () => GitTest("git-push-race", GitIntegrationTests.PushRace));
        Run("Sync preserves unrelated files already present on the branch", () => GitTest("git-other-files", GitIntegrationTests.PreserveOtherFiles));
        Run("Corrupt remote JSON never overwrites healthy local data", () => GitTest("git-corrupt-remote", GitIntegrationTests.RejectInvalidRemote));
        Run("Concurrent equal group creation converges to one membership", () => GitTest("git-groups", GitIntegrationTests.Groups));
        Run("Edits made while sync is running remain in the original editor", InFlightEditing);
        Run("Previously edited conflict copies are never overwritten or reused", EditedConflictCopy);
        Run("Unreadable local notes block synchronization without replacing their files", UnreadableLocalSync);
        Run("Repository configuration is local, persistent, and can be disabled", SyncSettingsPersistence);
        Run("Portable data stays beside the executable regardless of working directory", PortableDataPaths);
        Run("Bundled tool resolution handles spaces and apostrophes", BundledToolPaths);
        Run("Workspace navigation submits search on Enter and reuses categorized settings", SidebarAndSettings);
        Run("Startup settings toggle registration and recover from access failures", StartupSettings);
        Run("Startup command preserves quoted paths, workspace and dotnet host", StartupCommands);
        Run("New-note colors and hover preferences default on, persist, and apply live", InteractionPreferences);
        Run("Hover expands only the header, collapses on leave, and keeps menus usable", HoverHeader);
        Run("Application singleton blocks other processes and forwards activation", SingleInstance);
        Run("Update releases use stable numeric versions and trusted assets with SHA256", UpdateReleaseValidation);
        Run("Update downloads reject corruption, unsafe redirects, truncation and cancellation", UpdateDownloadValidation);
        Run("Private update login authenticates only official APIs and never redirect hosts", PrivateUpdateAuthentication);
        Run("Update archives reject traversal, personal data, duplicate paths and symlinks", UpdateArchiveValidation);
        Run("Update installation preserves portable and external data with program backups", UpdateInstallPreservesData);
        Run("Update installation rolls back all changed program components on failure", UpdateInstallRollback);
        Run("Update preferences persist and offline checks never block local recording", UpdatePreferencesAndOffline);
        if (GitIntegrationTests.ToolBaseDirectory is not null)
            Run("Bundled native Git and GCM run with no system Git or dotnet on PATH", () => GitTest("portable-tools", GitIntegrationTests.PortableTools));
        Console.WriteLine($"\n{_passed} passed; {_failed} failed. Artifacts: {_root}");
        app.Shutdown();
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        if (TestFilters.Length > 0 && !TestFilters.Any(filter => name.Contains(filter, StringComparison.OrdinalIgnoreCase))) return;
        try { test(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + "\n" + ex); }
    }
    private static void GitTest(string name, Func<string, Task> test) => Task.Run(() => test(Path.Combine(_root, name))).GetAwaiter().GetResult();
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static NoteStore Store(string name) => new(Path.Combine(_root, name));
    private static Note Example() => new()
    {
        Title = "项目想法 📝", Color = "sage", Blocks =
        [
            new NoteBlock { Inlines = [new NoteInline { Text = "中文与 English", Bold = true, Italic = true, Underline = true, Strike = true, Foreground = "#FF285A9C", FontSize = 22 }] },
            new NoteBlock { Kind = "list", Marker = "Decimal", Children = [new NoteBlock { Kind = "listItem", Children = [new NoteBlock { Inlines = [new NoteInline { Text = "任务一" }] }] }] }
        ]
    };
    private static void StorageRoundTrip()
    {
        var store = Store("round-trip"); var note = Example(); note.DeletedAt = DateTimeOffset.UtcNow; store.Save(note);
        var loaded = store.LoadNotes().Single();
        Check(loaded.Title == note.Title && loaded.DeletedAt == note.DeletedAt && loaded.Blocks[0].Inlines[0].Strike && loaded.PlainText.Contains("任务一"), "Model did not round-trip.");
        Check(!File.ReadAllText(Path.Combine(store.NotesDirectory, note.Id + ".json")).Contains("plainText"), "Derived fields leaked into JSON.");
    }
    private static void BackupRecovery()
    {
        var store = Store("recovery"); var note = Example(); store.Save(note); note.Title = "第二个版本"; store.Save(note);
        var path = Path.Combine(store.NotesDirectory, note.Id + ".json"); File.WriteAllText(path, "{truncated");
        var loaded = store.LoadNotes();
        Check(loaded.Single().Title == "项目想法 📝" && store.Warnings.Count == 1, "Backup not restored.");
        Check(Directory.EnumerateFiles(Path.Combine(store.BackupsDirectory, "corrupt", "notes"), "*.corrupt").Any(), "Corrupt original not retained.");
        Check(store.LoadNotes().Count == 1 && store.Warnings.Count == 0, "Restored file is still unreadable.");
    }
    private static void CorruptIsolation()
    {
        var store = Store("corrupt"); store.Save(Example()); var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(store.NotesDirectory, id + ".json"), "{bad");
        Check(store.LoadNotes().Count == 1 && store.Warnings.Count == 1, "A bad note prevented normal loading.");
    }
    private static void UnsupportedSchema()
    {
        var store = Store("future"); var id = Guid.NewGuid(); var json = $"{{\"schemaVersion\":2,\"id\":\"{id}\"}}";
        var path = Path.Combine(store.NotesDirectory, id + ".json"); File.WriteAllText(path, json);
        var backup = Example(); backup.Id = id;
        var oldStore = Store("old-version"); oldStore.Save(backup);
        File.Copy(Path.Combine(oldStore.NotesDirectory, id + ".json"), path + ".bak");
        Check(store.LoadNotes().Count == 0 && store.Warnings.Count == 1 && File.ReadAllText(path) == json, "Future schema overwritten.");
    }
    private static void AssetSafety()
    {
        var store = Store("assets"); var first = store.AddImage([1, 2, 3]); var second = store.AddImage([1, 2, 3]);
        Check(first == second && Directory.EnumerateFiles(store.AssetsDirectory, "*.png").Count() == 1, "Deduplication failed.");
        try { store.GetAssetPath("../secret.png"); throw new Exception("Traversal accepted."); } catch (InvalidDataException) { }
    }
    private static BitmapSource SampleBitmap() => BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
        new byte[] { 80, 160, 220, 255, 50, 100, 160, 255, 80, 160, 220, 255, 50, 100, 160, 255 }, 8);

    private static void DocumentRoundTrip()
    {
        var store = Store("document"); var document = new FlowDocument();
        var p = new Paragraph();
        var span = new Bold(new Italic(new Run("保留样式"))) { TextDecorations = new TextDecorationCollection(TextDecorations.Underline) };
        p.Inlines.Add(span);
        var id = DocumentCodec.StoreBitmap(SampleBitmap(), store);
        p.Inlines.Add(new InlineUIContainer(DocumentCodec.CreateImage(id, 160, store)));
        document.Blocks.Add(p);
        var blocks = DocumentCodec.Encode(document);
        var reencoded = DocumentCodec.Encode(DocumentCodec.Decode(blocks, store));
        Check(reencoded[0].Inlines[0].Bold && reencoded[0].Inlines[0].Italic && reencoded[0].Inlines[0].Underline, "Inherited formatting lost.");
        Check(reencoded[0].Inlines[1].AssetId == id && reencoded[0].Inlines[1].Width == 160, "Image reference or display size lost.");
        var model = Example();
        var restored = DocumentCodec.Encode(DocumentCodec.Decode(model.Blocks, store));
        Check(restored[0].Inlines[0].Strike && restored[0].Inlines[0].FontSize == 22 && restored[1].Marker == "Decimal", "Formatting/list lost.");
    }
    private static void MissingImage()
    {
        var store = Store("missing-image"); var id = new string('a', 64) + ".png";
        List<NoteBlock> blocks = [new NoteBlock { Inlines = [new NoteInline { Kind = "image", AssetId = id }] }];
        Check(DocumentCodec.Encode(DocumentCodec.Decode(blocks, store))[0].Inlines[0].AssetId == id, "Missing image reference was discarded.");
    }

    private static void ParagraphLayout()
    {
        foreach (var (caseName, useList, blankLine, fontSize) in new[]
            { ("plain", false, false, 16.0), ("blank-line", false, true, 16.0), ("list", true, false, 22.0), ("large-font", false, false, 28.0) })
        {
            var workspace = new StickyWorkspace(Path.Combine(_root, "paragraph-layout-" + caseName));
            workspace.UpdateEditorPreferences(new EditorPreferences { DefaultFontSize = fontSize, ParagraphSpacing = caseName == "list" ? 0 : 12 });
            var main = new MainWindow(workspace); main.Show(); workspace.CreateNote();
            var note = workspace.Notes.Single(); var window = workspace.Windows[note.Id];
            try
            {
                var editor = (RichTextBox)window.FindName("Editor");
                window.Activate(); window.EnterEditMode();
                editor.AppendText("第一行内容");
                editor.CaretPosition = editor.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
                if (useList) EditingCommands.ToggleBullets.Execute(null, editor);
                EditingCommands.EnterParagraphBreak.Execute(null, editor);
                editor.CaretPosition.InsertTextInRun("第二行内容");
                editor.CaretPosition = editor.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
                EditingCommands.EnterParagraphBreak.Execute(null, editor);
                if (blankLine) EditingCommands.EnterParagraphBreak.Execute(null, editor);
                editor.CaretPosition.InsertTextInRun("第三行内容");
                Pump(40); window.UpdateLayout();
                var before = ParagraphPositions(editor);
                Check(before.Length == (blankLine ? 4 : 3), "Enter did not create the expected paragraphs.");
                Check(window.SaveNow(false), "Could not save layout test note."); window.Close();
                workspace.OpenNote(note, edit: true);
                window = workspace.Windows[note.Id]; editor = (RichTextBox)window.FindName("Editor");
                Pump(40); window.UpdateLayout();
                var after = ParagraphPositions(editor);
                Check(after.Length == before.Length, "Reopening changed paragraph count.");
                for (var i = 1; i < before.Length; i++)
                {
                    var oldGap = before[i] - before[i - 1]; var newGap = after[i] - after[i - 1];
                    Check(Math.Abs(oldGap - newGap) < 0.5, $"{caseName} spacing changed after reopening: {oldGap:0.##} -> {newGap:0.##} DIP.");
                }
            }
            finally { workspace.PrepareExit(); }
        }
    }

    private static double[] ParagraphPositions(RichTextBox editor)
    {
        static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
        {
            foreach (var block in blocks)
            {
                if (block is Paragraph paragraph) yield return paragraph;
                else if (block is System.Windows.Documents.List list)
                    foreach (var item in list.ListItems)
                        foreach (var child in Paragraphs(item.Blocks)) yield return child;
            }
        }
        return Paragraphs(editor.Document.Blocks).Select(p => p.ContentStart.GetInsertionPosition(LogicalDirection.Forward).GetCharacterRect(LogicalDirection.Forward).Top).ToArray();
    }
    private static void ZipRoundTrip()
    {
        var store = Store("export"); var note = Example();
        var asset = DocumentCodec.StoreBitmap(SampleBitmap(), store);
        note.Blocks[0].Inlines.Add(new NoteInline { Kind = "image", AssetId = asset }); store.Save(note);
        var zip = Path.Combine(_root, "backup.zip"); BackupService.Export(store, zip);
        var target = Store("import"); var imported = BackupService.Import(target, zip);
        Check(imported.Single().Id == note.Id && File.Exists(target.GetAssetPath(asset)), "Backup did not round-trip.");
        var duplicate = BackupService.Import(target, zip).Single();
        Check(duplicate.Id != note.Id && target.LoadNotes().Count == 2 && target.LoadNotes().Any(n => n.Title == note.Title), "Duplicate import overwrote original.");
    }
    private static void UnsafeZip()
    {
        var path = Path.Combine(_root, "unsafe.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("../outside.json").Open()); writer.Write("{}"); }
        var store = Store("unsafe-import");
        try { BackupService.Import(store, path); throw new Exception("Unsafe ZIP accepted."); } catch (InvalidDataException) { }
        Check(store.LoadNotes().Count == 0 && !File.Exists(Path.Combine(_root, "outside.json")), "Unsafe import wrote files.");
    }
    private static void DeviceRoundTrip()
    {
        var store = Store("device"); var note = Example(); store.Save(note);
        var state = new DeviceState(); state.Windows[note.Id] = new Sticky.Core.WindowState { IsOpen = true, Left = 300, Topmost = true };
        store.SaveDeviceState(state);
        Check(store.LoadDeviceState().Windows[note.Id].Topmost && !File.ReadAllText(Path.Combine(store.NotesDirectory, note.Id + ".json")).Contains("topmost"), "Device state leaked into shared note.");
    }
    private static void WindowLifecycle()
    {
        var path = Path.Combine(_root, "windows");
        var workspace = new StickyWorkspace(path);
        var main = new MainWindow(workspace); main.Show();
        workspace.CreateNote(); workspace.CreateNote();
        Check(workspace.Windows.Count == 2, "Could not open two independent windows.");
        var note = workspace.Notes[0]; var window = workspace.Windows[note.Id];
        Check(window.WindowStyle == WindowStyle.None, "Note still has a Windows title bar.");
        window.FinishEditing(); Pump(30);
        Check(!window.IsEditing && ((RichTextBox)window.FindName("Editor")).IsReadOnly && !((FrameworkElement)window.FindName("FormattingBar")).IsVisible, "Read mode still shows editing controls.");
        window.EnterEditMode(); Pump(30);
        Check(window.IsEditing && !((RichTextBox)window.FindName("Editor")).IsReadOnly && ((FrameworkElement)window.FindName("FormattingBar")).IsVisible, "Edit mode did not reveal the compact toolbar.");
        ((TextBox)window.FindName("TitleBox")).Text = "自动保存验证";
        var editor = (RichTextBox)window.FindName("Editor"); editor.AppendText("中文自动保存与重启恢复");
        Pump(950);
        var saved = new NoteStore(path).LoadNotes().Single(n => n.Id == note.Id);
        Check(saved.Title == "自动保存验证" && saved.PlainText.Contains("重启恢复"), "Debounced save did not persist edits.");
        // Clearing a manual title returns to a derived title, including rich text runs and line breaks.
        ((TextBox)window.FindName("TitleBox")).Text = "";
        editor.Document.Blocks.Clear();
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Bold(new Run("正文"))); paragraph.Inlines.Add(new Run("首行"));
        paragraph.Inlines.Add(new LineBreak()); paragraph.Inlines.Add(new Run("中文自动保存与重启恢复"));
        editor.Document.Blocks.Add(paragraph);
        Pump(950);
        saved = new NoteStore(path).LoadNotes().Single(n => n.Id == note.Id);
        Check(saved.Title == "" && saved.DisplayTitle == "正文首行" && window.Title == "正文首行 · Sticky", "Automatic title was persisted as a manual title or not shown in the window.");
        Check(((MainViewModel)main.DataContext).Cards.Single(c => c.Note.Id == note.Id).Title == "正文首行", "Manager did not refresh the derived title.");
        using (var search = new SearchViewModel(workspace))
        {
            search.Query = "正文首行";
            Check(search.Results.Single().Title == "正文首行", "Search did not show the derived title.");
        }
        window.Close(); Check(workspace.Windows.Count == 1, "Close did not remove window.");
        workspace.OpenNote(note);
        Check(new TextRange(((RichTextBox)workspace.Windows[note.Id].FindName("Editor")).Document.ContentStart,
            ((RichTextBox)workspace.Windows[note.Id].FindName("Editor")).Document.ContentEnd).Text.Contains("重启恢复"), "Reopened editor lost text.");
        main.Close();
        Check(!main.IsVisible && workspace.Windows.Count == 2 && workspace.Windows.Values.All(w => w.IsVisible), "Closing manager closed the independent notes.");
        workspace.ShowManager();
        Check(main.IsVisible && workspace.Manager == main, "Manager could not reopen after hiding.");
        workspace.PrepareExit();
        var restored = new StickyWorkspace(path); var restoredMain = new MainWindow(restored); restoredMain.Show(); restored.RestoreWindows();
        Check(restored.Windows.Count == 2, "Exit/restart did not restore open windows.");
        restored.Delete(restored.Notes.Single(n => n.Id == note.Id));
        Check(restored.Windows.Count == 1 && new NoteStore(path).LoadNotes().Single(n => n.Id == note.Id).DeletedAt is not null, "Delete did not persist or close window.");
        restored.Restore(restored.Notes.Single(n => n.Id == note.Id));
        Check(restored.Windows.Count == 2, "Restore did not reopen the note.");
        restored.PrepareExit();
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    private static void FailedAutosave()
    {
        var path = Path.Combine(_root, "save-failure");
        var workspace = new StickyWorkspace(path);
        var main = new MainWindow(workspace); main.Show(); workspace.CreateNote();
        var note = workspace.Notes.Single(); var window = workspace.Windows[note.Id];
        var editor = (RichTextBox)window.FindName("Editor");
        using (var locked = new FileStream(Path.Combine(workspace.Store.NotesDirectory, note.Id + ".json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            editor.AppendText("保存失败后仍然保留"); Pump(900);
            Check(((TextBlock)window.FindName("SaveStatus")).Text.Contains("保存失败"), "Write error was not surfaced.");
            Check(new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.Contains("仍然保留"), "Unsaved text was lost.");
        }
        Check(window.SaveNow(false), "Retry failed after releasing the lock.");
        Check(new NoteStore(path).LoadNotes().Single().PlainText.Contains("仍然保留"), "Retried save did not persist text.");
        workspace.PrepareExit();
    }

    private static void GroupLifecycle()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "groups"));
        var work = workspace.CreateGroup("工作") ?? throw new Exception("Create group failed.");
        var personal = workspace.CreateGroup("生活") ?? throw new Exception("Create group failed.");
        var empty = workspace.CreateGroup("空分组") ?? throw new Exception("Create empty group failed.");
        var note = Example(); note.GroupId = work.Id; workspace.Store.Save(note); workspace.Notes.Add(note);
        using var manager = new MainViewModel(workspace);
        manager.SelectGroup(work.Id); Check(manager.Cards.Count == 1, "Group filter missed its note.");
        Check(workspace.MoveNote(note, personal.Id), "Moving note failed.");
        Check(manager.Cards.Count == 0, "Moving note did not update previous group.");
        Check(workspace.RenameGroup(personal, "日常"), "Rename group failed.");
        manager.SelectGroup(personal.Id); Check(manager.Cards.Single().GroupName == "日常", "Group rename lost note membership.");
        using var search = new SearchViewModel(workspace);
        search.Query = "任务一"; Check(search.Results.Single().Note.Id == note.Id, "Body search did not find note.");
        search.Scope = search.Scopes.Single(s => s.Id == work.Id); Check(search.Results.Count == 0, "Search ignored group scope.");
        search.Scope = search.Scopes.Single(s => s.Id == personal.Id); Check(search.Results.Count == 1, "Search missed group note.");
        var reopened = new StickyWorkspace(workspace.Store.Root);
        Check(reopened.Groups.Any(g => g.Id == empty.Id) && reopened.Notes.Single().GroupId == personal.Id && reopened.GroupName(personal.Id) == "日常", "Restart lost empty groups or membership.");
        Check(workspace.MoveNote(note, null), "Move to ungrouped failed.");
        manager.SelectGroup(null, true); Check(manager.Cards.Count == 1, "Ungrouped filter missed note.");
        var searchWindow = new SearchWindow(workspace); searchWindow.Show();
        Check(searchWindow.FindName("QueryBox") is TextBox, "Search is not available in a separate window.");
        searchWindow.Close();
    }

    private static void GroupBackup()
    {
        var source = Store("group-export"); var group = new NoteGroup { Name = "项目" }; source.SaveGroups([group]);
        var note = Example(); note.GroupId = group.Id; source.Save(note);
        var zip = Path.Combine(_root, "groups-backup.zip"); BackupService.Export(source, zip);
        var target = Store("group-import"); target.SaveGroups([new NoteGroup { Id = group.Id, Name = "不同的本地分组" }]);
        var imported = BackupService.Import(target, zip).Single(); var groups = target.LoadGroups();
        Check(imported.GroupId != group.Id && groups.Single(g => g.Id == imported.GroupId).Name == "项目" && groups.Single(g => g.Id == group.Id).Name == "不同的本地分组", "Group ID collision overwrote or misassigned membership.");
    }

    private static void InFlightEditing()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "in-flight-editor"));
        var main = new MainWindow(workspace); main.Show(); workspace.CreateNote();
        var note = workspace.Notes.Single(); var window = workspace.Windows[note.Id];
        var editor = (RichTextBox)window.FindName("Editor");
        editor.AppendText("初始正文"); Check(window.SaveNow(false), "Initial save failed.");
        var captured = workspace.CaptureForSync(); var remote = captured.Clone();
        remote.Notes[note.Id].Blocks = [new NoteBlock { Inlines = [new NoteInline { Text = "远端正在传来的修改" }] }];
        remote.Notes[note.Id].ModifiedAt = DateTimeOffset.UtcNow;
        window.EnterEditMode(); editor.AppendText("，网络同步时仍然继续输入");
        var applied = workspace.ApplySyncedSnapshot(captured, remote);
        Check(applied.ConflictCount == 1 && workspace.Notes.Count == 2, "Concurrent UI edits did not preserve incoming content as a copy.");
        Check(note.PlainText.Contains("继续输入") && new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.Contains("继续输入"), "Sync replaced the active editor's new text.");
        Check(workspace.Notes.Any(n => n.ConflictOf == note.Id && n.PlainText == "远端正在传来的修改"), "Incoming content was lost.");
        workspace.ShowSyncSettings();
        Check(workspace.Settings?.FindName("SyncHost") is ContentControl { Content: Sticky.App.Synchronization.SyncSettingsView view } && view.FindName("RemoteBox") is TextBox, "Sync configuration UI is unavailable.");
        workspace.PrepareExit();
    }

    private static void EditedConflictCopy()
    {
        var note = Example(); var original = new SyncSnapshot { Notes = { [note.Id] = note } };
        var local = original.Clone(); var remote = original.Clone();
        local.Notes[note.Id].Title = "本地新版本"; remote.Notes[note.Id].Title = "远端新版本";
        var merged = SnapshotMerger.Merge(original, local, remote).Snapshot;
        var firstCopy = merged.Notes.Values.Single(n => n.ConflictOf == note.Id);
        firstCopy.Title = "后来编辑过的冲突副本";
        var again = SnapshotMerger.Merge(original, local, merged).Snapshot;
        Check(again.Notes.Count == 3 && again.Notes.Values.Any(n => n.Title == "后来编辑过的冲突副本")
              && again.Notes.Values.Any(n => n.Title.StartsWith("本地新版本", StringComparison.Ordinal)), "A prior conflict copy was reused for different content.");
    }

    private static void UnreadableLocalSync()
    {
        var root = Path.Combine(_root, "unreadable-sync"); var store = new NoteStore(root); var id = Guid.NewGuid();
        var path = Path.Combine(store.NotesDirectory, id + ".json");
        var bytes = "{\"schemaVersion\":2,\"id\":\"" + id + "\"}";
        File.WriteAllText(path, bytes);
        var workspace = new StickyWorkspace(root);
        try { workspace.CaptureForSync(); throw new Exception("Unreadable note did not block sync."); } catch (IOException) { }
        Check(File.ReadAllText(path) == bytes, "A future-version note was overwritten.");
        workspace.PrepareExit();
    }

    private static void SyncSettingsPersistence()
    {
        var root = Path.Combine(_root, "sync-settings"); var workspace = new StickyWorkspace(root);
        Check(!workspace.Sync.Settings.Enabled, "Sync should be opt-in.");
        workspace.Sync.SaveSettings("https://github.com/example/sticky", "my-notes", true);
        var reopened = new StickyWorkspace(root);
        Check(reopened.Sync.Settings.Enabled && reopened.Sync.Settings.Branch == "my-notes" && reopened.Sync.Settings.RemoteUrl.EndsWith("sticky.git", StringComparison.Ordinal), "Sync settings did not persist.");
        reopened.Sync.SaveSettings(reopened.Sync.Settings.RemoteUrl, "my-notes", false);
        Check(!reopened.Sync.Settings.Enabled && File.Exists(Path.Combine(root, "device", "sync-settings.json")), "Disabling sync removed configuration or remained enabled.");
        workspace.PrepareExit(); reopened.PrepareExit();
    }

    private static void PortableDataPaths()
    {
        var folder = Path.Combine(_root, "便携目录 with spaces 'quoted'"); Directory.CreateDirectory(folder);
        Check(AppPaths.ResolveDataDirectory([], folder, true) == Path.Combine(folder, "Data"), "Portable data depends on the shell working directory.");
        Check(AppPaths.ResolveDataDirectory(["--portable"], folder, false) == Path.Combine(folder, "Data"), "Portable command flag failed.");
        File.WriteAllText(Path.Combine(folder, "portable.flag"), "portable");
        Check(AppPaths.ResolveDataDirectory([], folder, false) == Path.Combine(folder, "Data"), "Portable marker failed.");
        var explicitRoot = Path.Combine(_root, "explicit-data");
        Check(AppPaths.ResolveDataDirectory(["--data-dir", explicitRoot], folder, true) == explicitRoot, "Explicit data directory should take precedence.");
        try { AppPaths.ResolveDataDirectory(["--data-dir"], folder, true); throw new Exception("Missing data directory accepted."); } catch (ArgumentException) { }
    }

    private static void BundledToolPaths()
    {
        var folder = Path.Combine(_root, "tool path 'quoted'");
        Directory.CreateDirectory(Path.Combine(folder, "tools", "git", "cmd")); Directory.CreateDirectory(Path.Combine(folder, "tools", "gcm"));
        var git = Path.Combine(folder, "tools", "git", "cmd", "git.exe"); File.WriteAllBytes(git, []);
        File.WriteAllBytes(Path.Combine(folder, "tools", "gcm", "git-credential-manager.exe"), []);
        var resolved = GitToolchain.Resolve(folder);
        Check(resolved.GitExecutable == git && resolved.CredentialHelper.Contains("'\\''", StringComparison.Ordinal), "Bundled executable or helper quoting is incorrect.");
        Check(GitToolchain.Resolve(_root).GitExecutable == "git", "Developer builds should still support installed Git.");
    }

    private static void SidebarAndSettings()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "sidebar-settings"));
        var group = workspace.CreateGroup("工作") ?? throw new Exception("Create group failed.");
        var note = Example(); workspace.Store.Save(note); workspace.Notes.Add(note);
        var main = new MainWindow(workspace); main.Show(); Pump(30);
        var navigation = (StackPanel)main.FindName("WorkspaceNavigation");
        Check(navigation.Children[0] == main.FindName("AllNotesButton") && navigation.Children[1] == main.FindName("GroupNavigation")
            && navigation.Children[2] == main.FindName("UngroupedButton") && navigation.Children[3] == main.FindName("TrashButton"), "Workspace order is incorrect.");
        var manager = (MainViewModel)main.DataContext;
        manager.SelectGroup(group.Id); Check(manager.AllBackground == "Transparent" && manager.GroupItems.Single().IsSelected, "All notes stayed selected while viewing a group.");
        var box = (TextBox)main.FindName("SidebarSearchBox"); box.Text = "任务一";
        Check(workspace.Search is null, "Typing in the sidebar opened results before Enter.");
        box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
        Check(workspace.Search is { IsVisible: true } search && ((TextBox)search.FindName("QueryBox")).Text == "任务一"
            && ((SearchViewModel)search.DataContext).Results.Single().Note.Id == note.Id, "Enter did not open populated search results.");
        workspace.ShowSettings(SettingsSection.Sync);
        var settings = workspace.Settings ?? throw new Exception("Settings were not opened.");
        var syncView = (Sticky.App.Synchronization.SyncSettingsView)((ContentControl)settings.FindName("SyncHost")).Content;
        ((TextBox)syncView.FindName("RemoteBox")).Text = "https://github.com/example/unsaved-settings.git";
        workspace.ShowSettings(SettingsSection.Data);
        Check(workspace.Settings == settings && ((TabControl)settings.FindName("SettingsTabs")).SelectedIndex == 1, "Data settings opened a second window.");
        workspace.ShowSyncSettings();
        Check(workspace.Settings == settings && ((TextBox)syncView.FindName("RemoteBox")).Text.Contains("unsaved-settings", StringComparison.Ordinal), "Changing settings categories discarded the current form.");
        Check(settings.DataPath == workspace.Store.Root && settings.FindName("DataStatus") is TextBlock, "Data and backup settings are missing.");
        workspace.PrepareExit();
    }

}
