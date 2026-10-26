using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sticky.App;
using Sticky.App.Editor;
using Sticky.App.Controls;
using Sticky.Core;
using Sticky.Storage;
using Sticky.Sync.Git;

internal static partial class Program
{
    private static string[] HistoryNoteBytes(string root) => Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
        .Where(path => !Path.GetRelativePath(root, path).StartsWith("history" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).Order().ToArray();

    private static void HistoryRoundTrip()
    {
        var root = Path.Combine(_root, "history-round-trip");
        var store = new NoteStore(root);
        var note = Example(); var asset = DocumentCodec.StoreBitmap(SampleBitmap(), store);
        note.Blocks.Add(new NoteBlock { Inlines = [new NoteInline { Kind = "image", AssetId = asset }] });
        store.Save(note);
        var group = new NoteGroup { Name = "旅行" }; store.SaveGroups([group]);
        note.GroupId = group.Id; store.Save(note);
        var layout = new DeviceState(); layout.Windows[note.Id] = new Sticky.Core.WindowState { Left = -400, Top = 200, IsOpen = true }; store.SaveDeviceState(layout);
        group.Name = "旅行计划"; store.SaveGroups([group]);
        note.Color = "rose"; note.Title = "旅行清单"; note.Blocks[0].Inlines[0].Text += " · 更新"; store.Save(note);
        var edited = store.History.ReadTimeline().Events.Last().Sequence;
        note.DeletedAt = DateTimeOffset.UtcNow; store.Save(note);
        note.DeletedAt = null; store.Save(note);
        var timeline = new NoteStore(root).History.ReadTimeline();
        Check(timeline.Events.Select(e => e.Kind).Contains("noteCreated") && timeline.Events.Any(e => e.Kind == "noteMoved")
            && timeline.Events.Any(e => e.Kind == "noteDeleted") && timeline.Events.Any(e => e.Kind == "noteRestored"), "Lifecycle events were not retained.");
        var frame = timeline.Frame(edited);
        Check(frame.Notes[note.Id].Blocks[0].Inlines[0].Bold && frame.Notes[note.Id].Blocks[2].Inlines[0].AssetId == asset, "History lost rich text or image references.");
        Check(frame.Notes[note.Id].GroupId == group.Id && frame.Groups.Single().Name == "旅行计划" && frame.Layout.Windows[note.Id].Left == -400, "History lost group or position state.");
        Check(timeline.Rows(note.Id).Any(r => r.Last.Kind == "groupsChanged" && r.Last.Changes.Any(c => c.Contains("重命名"))), "Group rename is missing from per-note history.");
        frame.Notes[note.Id].Title = "mutated preview";
        Check(store.LoadNotes().Single().Title == "旅行清单" && timeline.Frame(edited).Notes[note.Id].Title == "旅行清单", "A preview mutated persisted state.");
        Check(Directory.EnumerateFiles(store.AssetsDirectory).Count() == 1, "History duplicated image assets.");
    }

    private static void HistoryBatchingAndSources()
    {
        var time = DateTimeOffset.Parse("2026-10-26T01:00:00Z");
        var store = new NoteStore(Path.Combine(_root, "history-batching"), new HistoryOptions { Clock = () => time });
        var note = Example(); store.Save(note);
        using (store.History.WithContext("local", "session-a"))
        {
            time = time.AddSeconds(2); note.Blocks[0].Inlines[0].Text = "第一步"; store.Save(note);
            time = time.AddSeconds(2); note.Blocks[0].Inlines[0].Text = "第一步，第二步"; store.Save(note);
            time = time.AddMinutes(2); note.Blocks[0].Inlines[0].Text += "，第三步"; store.Save(note);
        }
        using (store.History.WithContext("local", "session-b")) { time = time.AddSeconds(1); note.Title = "新会话"; store.Save(note); }
        using (store.History.WithContext("sync")) { note.Title = "同步修改"; store.Save(note); }
        var timeline = store.History.ReadTimeline();
        Check(timeline.Events.Count == 6 && timeline.Rows(note.Id).Count == 5 && timeline.Rows(note.Id)[1].Count == 2, "Input batching lost events or combined separate sessions.");
        Check(timeline.Events.Last().Source == "sync", "Sync source was not retained.");
        note.ModifiedAt = DateTimeOffset.UtcNow.AddMinutes(5); store.Save(note);
        Check(store.History.ReadTimeline().Events.Count == 6, "Timestamp-only saves polluted history.");
        var diff = HistoryDescriptions.TextDifference("一起去公园 🌳", "一起去海边 🌊");
        Check(diff.Removed.Contains("公园") && diff.Added.Contains("海边") && !diff.Added.Contains('\uFFFD'), "Text diff or surrogate boundaries were incorrect.");
    }

    private static void HistoryBaselineAndCheckpoints()
    {
        var root = Path.Combine(_root, "history-baseline"); var store = new NoteStore(root); var note = Example();
        // Simulate a workspace written before history existed.
        WorkspaceFiles.WriteAtomic(root, Path.Combine(store.NotesDirectory, note.Id + ".json"), SyncJson.Bytes(note));
        var first = store.History.ReadTimeline();
        Check(first.Events.Count == 0 && first.Frame(0).Notes[note.Id].Title == note.Title, "Existing notes were not captured as a baseline.");
        for (var i = 1; i <= 105; i++) { note.Title = "版本 " + i; store.Save(note); }
        var timeline = store.History.ReadTimeline();
        Check(File.Exists(Path.Combine(root, "history", "checkpoints", "00000000000000000100.json")), "Periodic checkpoint was not created.");
        Check(timeline.Frame(100).Notes[note.Id].Title == "版本 100" && timeline.Frame(105).Notes[note.Id].Title == "版本 105", "Checkpoint replay returned the wrong state.");
        var checkpoint = Path.Combine(root, "history", "checkpoints", "00000000000000000100.json");
        var text = File.ReadAllText(checkpoint); File.WriteAllText(checkpoint, text.Replace("\"integrity\": \"", "\"integrity\": \"broken"));
        Check(store.History.ReadTimeline().Frame(100).Notes[note.Id].Title == "版本 100", "Corrupt checkpoint was not rebuilt from the event log.");
    }

    private static void HistoryWriteFailures()
    {
        var store = Store("history-write-failures"); var note = Example(); store.Save(note);
        var path = Path.Combine(store.NotesDirectory, note.Id + ".json");
        var original = File.ReadAllBytes(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            note.Title = "must not become saved history";
            var failed = false; try { store.Save(note); } catch (IOException) { failed = true; }
            Check(failed && File.ReadAllBytes(path).SequenceEqual(original), "Failed materialization changed note bytes.");
        }
        Check(store.History.ReadTimeline().Events.Count == 1, "Failed save became a completed historical event.");
        var head = Path.Combine(store.Root, "history", "head.json");
        using (var locked = new FileStream(head, FileMode.Open, FileAccess.Read, FileShare.Read))
        { note.Title = "saved despite history index lock"; store.Save(note); }
        var recovered = new NoteStore(store.Root);
        Check(recovered.LoadNotes().Single().Title == note.Title && recovered.History.ReadTimeline().Events.Count == 2, "Written note was lost or duplicated after history-index failure.");
        Check(!File.Exists(Path.Combine(store.Root, "history", "pending.json")), "Pending history commit did not recover.");
        var latest = recovered.History.ReadTimeline().Events.Last();
        var obj = Path.Combine(store.Root, "history", "objects", latest.After + ".json"); var bytes = File.ReadAllBytes(obj);
        File.WriteAllText(obj, "{damaged");
        var rejected = false; try { recovered.History.ReadTimeline().Frame(latest.Sequence); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && recovered.LoadNotes().Single().Title == note.Title, "Damaged history affected live notes or was silently accepted.");
        File.WriteAllBytes(obj, bytes);
        var eventPath = Path.Combine(store.Root, "history", "events", latest.Sequence.ToString("D20") + ".json");
        var eventBytes = File.ReadAllBytes(eventPath);
        File.WriteAllText(eventPath, File.ReadAllText(eventPath).Replace("saved despite history index lock", "tampered metadata"));
        rejected = false; try { recovered.History.ReadTimeline(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && recovered.LoadNotes().Single().Title == note.Title, "Damaged event metadata was accepted or changed current data.");
        File.WriteAllBytes(eventPath, eventBytes);
        var firstSave = Store("history-backup-write-failure"); var fresh = Example();
        var backup = WorkspaceFiles.BackupPath(firstSave.Root, Path.Combine(firstSave.NotesDirectory, fresh.Id + ".json"));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        using (var locked = new FileStream(backup, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read)) firstSave.Save(fresh);
        Check(firstSave.LoadNotes().Single().Id == fresh.Id && firstSave.History.ReadTimeline().Events.Count == 1,
            "An auxiliary backup failure made a durably written note appear unsaved.");
    }

    private static int HistoryTransactionProbe(string[] args)
    {
        var store = new NoteStore(args[1], new HistoryOptions { CommitCheckpoint = phase => { if (phase == args[2]) { File.WriteAllText(args[3], phase); Thread.Sleep(Timeout.Infinite); } } });
        var note = store.LoadNotes().Single(); note.Title = "recovered child edit"; note.ModifiedAt = DateTimeOffset.UtcNow;
        store.Save(note); return 0;
    }
    private static void HistoryForcedTermination()
    {
        foreach (var phase in new[] { "prepared", "state-written", "event-written" })
        {
            var store = Store("history-kill-" + phase); var note = Example(); store.Save(note);
            var signal = Path.Combine(store.Root, "signal");
            using (var child = Process.Start(TestChild("--history-transaction-probe", store.Root, phase, signal))!)
            {
                try { WaitFile(signal, child); child.Kill(); child.WaitForExit(5000); }
                finally { if (!child.HasExited) { child.Kill(true); child.WaitForExit(); } }
            }
            var recovered = new NoteStore(store.Root); var timeline = recovered.History.ReadTimeline();
            var expected = phase == "prepared" ? note.Title : "recovered child edit";
            Check(recovered.LoadNotes().Single().Title == expected && timeline.Frame(timeline.Events.Last().Sequence).Notes[note.Id].Title == expected,
                "History and note disagree after termination at " + phase);
            Check(timeline.Events.Count == (phase == "prepared" ? 1 : 2), "Interrupted transaction was duplicated or fabricated.");
        }
    }

    private static void HistoryRestoreAndReplayUi()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "history-ui"));
        try
        {
            workspace.CreateNote(); var note = workspace.Notes.Single(); var window = workspace.Windows[note.Id];
            var editor = (RichTextBox)window.FindName("Editor"); editor.AppendText("周末出行计划\n带相机和一瓶水"); Check(window.SaveNow(false), "Initial history edit did not save.");
            var oldSequence = workspace.Store.History.ReadTimeline().Events.Last(e => e.Target.StartsWith("notes/", StringComparison.Ordinal)).Sequence;
            var oldFrame = workspace.Store.History.ReadTimeline().Frame(oldSequence);
            var group = workspace.CreateGroup("生活")!; workspace.MoveNote(note, group.Id);
            editor.AppendText("\n新增：带上雨伞"); Check(window.SaveNow(false), "Later edit did not save.");
            workspace.ShowHistory(note.Id); Pump(60);
            var history = workspace.History!;
            var before = HistoryNoteBytes(workspace.Store.Root);
            var slider = (Slider)history.FindName("ProgressSlider");
            for (var i = 0; i <= (int)slider.Maximum; i++) slider.Value = i;
            ((ComboBox)history.FindName("SpeedBox")).SelectedIndex = 4;
            ((Button)history.FindName("PlayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(800);
            if (history.IsPlaying) ((Button)history.FindName("PlayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(HistoryNoteBytes(workspace.Store.Root).SequenceEqual(before), "Seeking or playing history wrote live data.");
            Check(((RichTextBox)history.FindName("BeforePreview")).IsReadOnly && ((RichTextBox)history.FindName("AfterPreview")).IsReadOnly, "Replay preview was editable.");
            slider.Value = slider.Maximum; Pump(80); history.UpdateLayout();
            var afterPreview = (RichTextBox)history.FindName("AfterPreview");
            Check(((WrapPanel)history.FindName("SnapshotPanel")).Children.Count > 0
                && new TextRange(afterPreview.Document.ContentStart, afterPreview.Document.ContentEnd).Text.Contains("雨伞"), "Latest historical frame did not render note content.");
            var bitmap = new RenderTargetBitmap((int)history.ActualWidth, (int)history.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(history);
            using (var output = File.Create(Path.Combine(_root, "history-ui.png"))) { var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); png.Save(output); }
            var originalText = note.PlainText;
            var copy = workspace.RestoreHistoryNote(oldFrame.Notes[note.Id], oldFrame.Groups, oldSequence, asCopy: true);
            Check(copy.Id != note.Id && note.PlainText == originalText && !copy.PlainText.Contains("雨伞"), "Restore-as-copy changed the original or restored wrong content.");
            workspace.RestoreHistoryNote(oldFrame.Notes[note.Id], oldFrame.Groups, oldSequence, asCopy: false);
            Check(!note.PlainText.Contains("雨伞") && note.PlainText.Contains("相机") && workspace.Store.History.ReadTimeline().Events.Any(e => e.Kind == "historyRestored"), "Restore did not append a new historical action.");
        }
        finally { workspace.PrepareExit(); }
    }

    private static void HistoryBackupAndExternalChanges()
    {
        var store = Store("history-backup"); var note = Example(); store.Save(note);
        note.Title = "第二版"; store.Save(note);
        var originalCount = store.History.ReadTimeline().Events.Count;
        note.Title = "旧版或外部工具写入";
        WorkspaceFiles.WriteAtomic(store.Root, Path.Combine(store.NotesDirectory, note.Id + ".json"), SyncJson.Bytes(note));
        store.History.Reconcile(); store.History.Reconcile();
        var timeline = store.History.ReadTimeline();
        Check(timeline.Events.Count == originalCount + 1 && timeline.Events.Last().Source == "external"
            && timeline.Frame(timeline.Events.Last().Sequence).Notes[note.Id].Title == note.Title, "External changes were lost, duplicated or replaced by old history.");
        File.WriteAllText(Path.Combine(store.Root, "device", "sync-settings.json"), "private config not for archive");
        var destination = Path.Combine(_root, "history-backup.zip"); store.History.Export(destination);
        using (var zip = ZipFile.OpenRead(destination))
            Check(zip.GetEntry("history/baseline.json") is not null && zip.GetEntry("device/sync-settings.json") is null
                && zip.GetEntry("history/writer.lock") is null && zip.GetEntry("history/pending.json") is null, "Archive lost history or included private config/transient files.");
        var restoredPath = Path.Combine(_root, "history-backup-restored"); ZipFile.ExtractToDirectory(destination, restoredPath);
        var restored = new NoteStore(restoredPath);
        Check(restored.LoadNotes().Single().Title == note.Title && restored.History.ReadTimeline().Events.Count == timeline.Events.Count
            && restored.History.ReadTimeline().Frame(1).Notes[note.Id].Title == "项目想法 📝", "History archive did not restore an independent full timeline.");
    }

    private static void HistorySyncAndInvalidRestore()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "history-sync"));
        try
        {
            workspace.CreateNote(); var note = workspace.Notes.Single();
            var captured = workspace.CaptureForSync(); var incoming = captured.Clone();
            incoming.Notes[note.Id].Title = "来自另一台电脑"; incoming.Notes[note.Id].ModifiedAt = DateTimeOffset.UtcNow;
            workspace.ApplySyncedSnapshot(captured, incoming);
            var entry = workspace.Store.History.ReadTimeline().Events.Last(e => e.Target.StartsWith("notes/", StringComparison.Ordinal));
            Check(entry.Source == "sync" && entry.Summary.Contains("来自另一台电脑"), "Incoming sync did not enter the local timeline.");
            var before = HistoryNoteBytes(workspace.Store.Root);
            var missing = Example(); missing.GroupId = Guid.NewGuid(); missing.Blocks.Add(new NoteBlock { Inlines = [new NoteInline { Kind = "image", AssetId = new string('a', 64) + ".png" }] });
            var failed = false;
            try { workspace.RestoreHistoryNote(missing, [new NoteGroup { Id = missing.GroupId.Value, Name = "不能意外创建" }], 1, asCopy: true); }
            catch (IOException) { failed = true; }
            Check(failed && HistoryNoteBytes(workspace.Store.Root).SequenceEqual(before) && workspace.Groups.Count == 0, "Invalid restore partially changed notes or created a group.");
        }
        finally { workspace.PrepareExit(); }
    }

    private static void HistoryUserPresentation()
    {
        var time = DateTimeOffset.Parse("2026-10-23T09:20:00+08:00");
        var store = new NoteStore(Path.Combine(_root, "history-user-presentation"), new HistoryOptions { Clock = () => time });
        var a = Example(); var b = Example(); store.Save(a); store.Save(b);
        using (store.History.WithContext("local", "editor-a"))
        {
            time = time.AddSeconds(2); a.Title = "第一次输入"; store.Save(a);
            var layout = new DeviceState(); layout.Windows[a.Id] = new Sticky.Core.WindowState { Left = 800, Width = 500, Topmost = true };
            store.SaveDeviceState(layout);
            b.Title = "另一张便签"; store.Save(b);
            time = time.AddSeconds(2); a.Title = "继续输入"; store.Save(a);
        }
        var timeline = store.History.ReadTimeline(); var rows = timeline.UserRows();
        Check(timeline.Events.Any(e => e.Target == "device/windows.json") && rows.All(r => r.Last.Target != "device/windows.json"), "Layout was deleted from the log or exposed to the user.");
        Check(rows.Single(r => r.Last.NoteIds.Contains(a.Id) && r.Last.Kind == "noteEdited").Count == 2, "Window events or another note interrupted one edit session.");
        Check(rows.Select(r => r.Sequence).SequenceEqual(rows.Select(r => r.Sequence).Order()), "User history reordered persisted operations.");
        a.DeletedAt = time; store.Save(a); a.DeletedAt = null; store.Save(a);
        Check(store.History.ReadTimeline().UserRows().TakeLast(2).Select(r => r.Last.Kind).SequenceEqual(new[] { "noteDeleted", "noteRestored" }), "Lifecycle boundaries were merged into edits.");
        Check(timeline.Frame(timeline.Events.Last().Sequence).Layout.Windows[a.Id].Left == 800, "User filtering removed recoverable layout state.");
    }

    private static IEnumerable<T> HistoryVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in HistoryVisuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void HistoryMultiSelectionUi()
    {
        var root = Path.Combine(_root, "history-multi-ui");
        var time = DateTimeOffset.Parse("2026-10-23T09:20:00+08:00");
        var store = new NoteStore(root, new HistoryOptions { Clock = () => time });
        Note Create(string title, string color, params string[] lines) => new() { Title = title, Color = color, Blocks = lines.Select(t => new NoteBlock { Inlines = [new NoteInline { Text = t }] }).ToList() };
        var trip = Create("周末出行", "amber", "周末想去海边。", "留一个下午，慢慢走。"); store.Save(trip);
        time = time.AddHours(6); var shop = Create("采购清单", "sage", "咖啡豆", "牛奶", "面包"); store.Save(shop);
        time = time.AddHours(18); trip.Blocks.Add(new NoteBlock { Inlines = [new NoteInline { Text = "带上相机，日落前到达。", Bold = true }] }); store.Save(trip);
        var changedSequence = store.History.ReadTimeline().Events.Last().Sequence;
        time = time.AddHours(7); var idea = Create("忽然想到", "lavender", "做一个会讲故事的便签。"); store.Save(idea);
        time = time.AddHours(16); trip.Blocks.Add(new NoteBlock { Inlines = [new NoteInline { Kind = "image", AssetId = DocumentCodec.StoreBitmap(SampleBitmap(), store), Width = 180 }] }); store.Save(trip);
        time = time.AddHours(3); shop.Blocks[2].Inlines[0].Text = "酸奶"; store.Save(shop);
        time = time.AddHours(30); shop.DeletedAt = time; store.Save(shop);
        var layout = new DeviceState(); layout.Windows[trip.Id] = new Sticky.Core.WindowState { Left = 9000, Width = 600, Topmost = true }; store.SaveDeviceState(layout);
        var workspace = new StickyWorkspace(root);
        try
        {
            workspace.ShowHistory(); Pump(80);
            var history = workspace.History!; var timeline = (HistoryTimelineView)history.FindName("TimelineView");
            var list = (ListBox)history.FindName("NoteList"); var slider = (Slider)history.FindName("ProgressSlider");
            var choices = list.Items.Cast<HistoryWindow.HistoryNoteChoice>().ToList();
            var before = HistoryNoteBytes(root); var count = timeline.VisibleEventCount; var sequence = history.PreviewSequence;
            Check(choices.Count == 3 && timeline.TrackCount == 3, "The left note list did not open all note tracks.");
            Check(timeline.VisibleEventCount == workspace.Store.History.ReadTimeline().UserRows().Count + 1, "Internal layout events leaked into the common timeline.");
            var workspaceNodes = HistoryVisuals<Button>(timeline).Where(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b).StartsWith("HistoryWorkspaceNode-", StringComparison.Ordinal)).ToList();
            Check(workspaceNodes.Count == count && timeline.WorkspaceNodeCount == count, "Workspace events were collapsed into daily summaries instead of individual nodes.");
            var timeTicks = HistoryVisuals<TextBlock>(timeline).Where(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t).StartsWith("HistoryTimeTick-", StringComparison.Ordinal)).ToList();
            Check(timeTicks.Count == count, "Event timestamps were not moved into the shared top axis.");
            foreach (var node in workspaceNodes)
            {
                var sequenceText = System.Windows.Automation.AutomationProperties.GetAutomationId(node)["HistoryWorkspaceNode-".Length..];
                var icon = HistoryVisuals<System.Windows.Shapes.Path>(node).Single();
                Check(icon.Data is not null && HistoryVisuals<TextBlock>(node).Count() == 1, "Workspace nodes should show a shared SVG icon above a title, without duplicated time or action text.");
                var tick = timeTicks.Single(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == "HistoryTimeTick-" + sequenceText);
                Check(Math.Abs(Canvas.GetLeft(node) + node.Width / 2 - Canvas.GetLeft(tick) - tick.Width / 2) < 0.1 && Canvas.GetTop(tick) < Canvas.GetTop(node), "A shared timestamp was misaligned with its operation column.");
                var noteNodes = HistoryVisuals<Button>(timeline).Where(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b).StartsWith("HistoryNoteNode-", StringComparison.Ordinal)
                    && System.Windows.Automation.AutomationProperties.GetAutomationId(b).EndsWith("-" + sequenceText, StringComparison.Ordinal));
                foreach (var noteNode in noteNodes)
                {
                    Check(Math.Abs(Canvas.GetLeft(node) + node.Width / 2 - Canvas.GetLeft(noteNode) - noteNode.Width / 2) < 0.1, "Workspace and note nodes for one operation are misaligned.");
                    Check(HistoryVisuals<TextBlock>(noteNode).Count() == 0 && ReferenceEquals(icon.Data, HistoryVisuals<System.Windows.Shapes.Path>(noteNode).Single().Data), "Note nodes duplicated labels or did not reuse the workspace SVG geometry.");
                }
                node.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(history.PreviewSequence == long.Parse(sequenceText), "A workspace operation sought the last event of its day rather than the selected event.");
                Check(icon.Stroke is SolidColorBrush stroke && stroke.Color == Colors.White && node.ToolTip is string tip && tip.Contains(HistoryTimelineView.ActionLabel(workspace.Store.History.ReadTimeline().UserRows().FirstOrDefault(r => r.Sequence.ToString() == sequenceText)?.Last.Kind ?? "baseline")), "Selected icon color or the accessible action tooltip was lost.");
            }
            // Exercise the native checkbox binding, rather than only changing the view model.
            var check = HistoryVisuals<CheckBox>(list).Single(c => c.DataContext is HistoryWindow.HistoryNoteChoice n && n.Id == idea.Id);
            check.IsChecked = false; Pump(20);
            Check(history.SelectedNoteIds.Count == 2 && timeline.TrackCount == 2 && history.PreviewSequence == sequence, "Multiselection reset the common cursor or failed to update tracks.");
            foreach (var choice in choices) choice.IsSelected = false;
            Check(timeline.TrackCount == 0 && timeline.VisibleEventCount == count && timeline.WorkspaceNodeCount == count, "Clearing selection hid the global operation nodes.");
            Check(((TextBlock)history.FindName("PreviewEmpty")).Visibility == Visibility.Visible && !((Button)history.FindName("RestoreCopyButton")).IsEnabled, "Empty selection left stale restore content.");
            var overviewButton = HistoryVisuals<Button>(timeline).First(); overviewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(history.PreviewSequence < sequence && timeline.TrackCount == 0, "Global operation nodes stopped working without selected notes.");
            history.SelectNote(trip.Id);
            var rows = workspace.Store.History.ReadTimeline().UserRows();
            var ideaCreated = rows.Single(r => r.Last.Kind == "noteCreated" && r.Last.NoteIds.Contains(idea.Id));
            HistoryVisuals<Button>(timeline).Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "HistoryWorkspaceNode-" + ideaCreated.Sequence)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(history.PreviewSequence == ideaCreated.Sequence && history.SelectedNoteIds.SetEquals(new[] { trip.Id }), "Selecting another note's global event changed the note selection or failed to seek.");
            slider.Value = rows.FindIndex(r => r.Sequence == changedSequence) + 1;
            var expectedNext = rows.First(r => r.Sequence > changedSequence && r.Last.NoteIds.Contains(trip.Id)).Sequence;
            var nextButton = HistoryVisuals<Button>(history).Single(b => b.Content is string s && s == "下一步");
            nextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(history.PreviewSequence == expectedNext && timeline.VisibleEventCount == count, "Selected playback steps included unrelated notes or filtered the workspace overview.");
            slider.Value = rows.FindIndex(r => r.Sequence == changedSequence) + 1;
            ((ComboBox)history.FindName("SpeedBox")).SelectedIndex = 4;
            ((Button)history.FindName("PlayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(300);
            Check(history.PreviewSequence == expectedNext && !history.IsPlaying, "Playback did not skip unrelated notes or finish at the last selected change.");
            history.SelectNote(null); slider.Value = slider.Maximum; Pump(40);
            var snapshots = (WrapPanel)history.FindName("SnapshotPanel");
            Check(snapshots.Children.Count == 3 && HistoryVisuals<TextBlock>(snapshots).Any(t => t.Text.Contains("已移入回收站")), "Deleted note content disappeared from the historical preview.");
            Check(HistoryNoteBytes(root).SequenceEqual(before), "Selection, seeking, or selected playback modified current data.");
            slider.Value = rows.FindIndex(r => r.Sequence == expectedNext) + 1; history.Width = 1280; history.Height = 900; Pump(60); history.UpdateLayout();
            var workspaceLabel = HistoryVisuals<TextBlock>(timeline).Single(t => t.Text == "工作空间");
            Check(workspaceLabel.TransformToAncestor(timeline).Transform(new Point()).X >= 0, "Horizontal seeking scrolled workspace and note labels out of view.");
            void Capture(string name)
            {
                var bitmap = new RenderTargetBitmap((int)history.ActualWidth, (int)history.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(history);
                using var output = File.Create(Path.Combine(_root, name)); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); png.Save(output);
            }
            Capture("history-multi-ui.png");
            history.Width = 1000; history.Height = 720; Pump(60); history.UpdateLayout(); Capture("history-multi-small.png");
            Check(timeline.ActualWidth > 600 && snapshots.ActualWidth > 600, "Minimum-size timeline or preview lost usable space.");
        }
        finally { workspace.PrepareExit(); }
    }
}
