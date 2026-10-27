using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Sticky.App;
using Sticky.UpdateEngine;

internal static partial class Program
{
    private static void StartupFirstFrame()
    {
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            foreach (var background in new[] { false, true })
            {
                var workspace = new StickyWorkspace(Path.Combine(_root, "first-frame-" + background));
                var main = new MainWindow(workspace);
                try
                {
                    var presentation = new StartupPresentation(main, background);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var render = presentation.RenderAsync(timeout.Token);
                    while (!render.IsCompleted) Pump(20);
                    render.GetAwaiter().GetResult();
                    Check(main.IsLoaded && main.Opacity == 1 && !main.IsEnabled, "Pending startup has a transparent/black surface or writable controls.");
                    Check(((FrameworkElement)main.FindName("StartupOverlay")).IsVisible, "First startup did not draw its loading content.");
                    if (background) Check(!main.ShowInTaskbar && main.Left + main.Width < SystemParameters.VirtualScreenLeft,
                        "Logon startup exposed a manager window on-screen.");
                    presentation.Commit();
                    Check(main.IsEnabled && ((FrameworkElement)main.FindName("StartupOverlay")).Visibility == Visibility.Collapsed,
                        "Commit did not unlock and reveal the manager.");
                    Check(main.IsVisible != background && main.ShowInTaskbar, "Logon startup did not remain in the tray or manager could not be reopened.");
                    if (background) { workspace.ShowManager(); Check(main.IsVisible && main.Left >= SystemParameters.VirtualScreenLeft, "Tray manager reopened off-screen."); }
                }
                finally { workspace.PrepareExit(); }
            }
        }
        finally { SynchronizationContext.SetSynchronizationContext(oldContext); }
    }

    private static void LauncherFastConfirmedStartup()
    {
        foreach (var failAfterCommit in new[] { false, true })
        {
            var fixture = LauncherFixture("launcher-fast-confirmed-" + failAfterCommit);
            Process? worker = null;
            try
            {
                using (var first = StartLauncherWorker(fixture.Root, fixture.Data)) WaitWorker(first, fixture.Root);
                WaitFile(Path.Combine(fixture.Root, "test-active.json")); StopProbeChildren(fixture.Root);
                File.Delete(Path.Combine(fixture.Root, "test-active.json"));
                File.WriteAllText(Path.Combine(fixture.Data, "notes", "content.txt"), "edit after successful update");
                var confirmed = fixture.Store.ReadState();
                if (failAfterCommit)
                {
                    var slot = VersionLayout.Slot(fixture.Root, confirmed.Active);
                    var runtime = DurableFiles.Child(slot, VersionLayout.Runtime);
                    File.WriteAllText(runtime, "after-commit-crash");
                    var manifestPath = Path.Combine(slot, "release-manifest.json");
                    var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
                    manifest["files"]![VersionLayout.Runtime] = DurableFiles.Hash(runtime);
                    manifest["runtimeSha256"] = DurableFiles.Hash(runtime);
                    File.WriteAllText(manifestPath, manifest.ToJsonString());
                    worker = StartLauncherWorker(fixture.Root, fixture.Data); WaitWorker(worker, fixture.Root);
                    Check(fixture.Store.ReadState().Active == "bootstrap", "A confirmed version crashing after commit was not rolled back.");
                    Check(File.ReadAllText(Path.Combine(fixture.Data, "notes", "content.txt")) == "edit after successful update", "Late crash fallback overwrote newer edits.");
                }
                else
                {
                    var signal = Path.Combine(fixture.Root, "observing.signal");
                    worker = StartLauncherWorker(fixture.Root, fixture.Data, "observing-confirmed", signal);
                    WaitFile(signal, worker); WaitFile(Path.Combine(fixture.Root, "test-active.json"), worker);
                    Check(!worker.HasExited && fixture.Store.ReadState() == confirmed, "Confirmed startup changed its durable version pointer before observation.");
                    // The app has received commit and become active while the observer is still held.
                    Check(!DurableFiles.Exists(fixture.Store.PendingFile), "Confirmed startup created a trial transaction unnecessarily.");
                }
            }
            finally
            {
                if (worker is not null) { if (!worker.HasExited) { worker.Kill(true); worker.WaitForExit(5000); } worker.Dispose(); }
                StopProbeChildren(fixture.Root);
            }
        }
    }

    private static void ParallelStartupValidation()
    {
        var root = Path.Combine(_root, "parallel-validation"); ModernPayload(root, "0.1.0", "good");
        Check(VersionLayout.Validate(root, maxParallelism: 1) == VersionLayout.Validate(root, maxParallelism: 4), "Parallel validation disagreed with serial validation.");
        foreach (var name in new[] { "Sticky.exe", VersionLayout.Runtime, "portable.flag", "tools/git/cmd/git.exe", "tools/gcm/git-credential-manager.exe" })
        {
            var path = DurableFiles.Child(root, name); var original = File.ReadAllBytes(path);
            try
            {
                var changed = original.ToArray(); changed[0] ^= 1; File.WriteAllBytes(path, changed);
                foreach (var degree in new[] { 1, 4 })
                {
                    try { VersionLayout.Validate(root, maxParallelism: degree); throw new Exception("Corrupt component accepted: " + name); }
                    catch (InvalidDataException) { }
                }
            }
            finally { File.WriteAllBytes(path, original); }
        }
        Check(VersionLayout.Validate(root) == "0.1.0", "Restoring valid bytes did not restore validation.");
    }

    private static void ParallelWorkspaceStartup()
    {
        var root = Path.Combine(_root, "parallel-workspace"); var store = new Sticky.Storage.NoteStore(root);
        var state = new Sticky.Core.DeviceState();
        for (var i = 0; i < 8; i++)
        {
            var note = new Sticky.Core.Note { Title = "恢复 " + i }; store.Save(note);
            state.Windows[note.Id] = new Sticky.Core.WindowState { IsOpen = true, Left = -900000, Top = -900000 };
        }
        store.SaveDeviceState(state);
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        StickyWorkspace? workspace = null;
        MainWindow? main = null;
        try
        {
            var uiThread = Environment.CurrentManagedThreadId;
            var load = Task.Run(() =>
            {
                Check(Environment.CurrentManagedThreadId != uiThread, "Disk preparation ran on the UI thread.");
                return WorkspaceStartupData.Load(root);
            });
            main = MainWindow.CreateLoadingShell();
            var presentation = new StartupPresentation(main, false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var render = presentation.RenderAsync(timeout.Token);
            while (!render.IsCompleted || !load.IsCompleted) Pump(20);
            render.GetAwaiter().GetResult();
            Check(!main.IsEnabled, "The loading shell accepted input before its workspace was attached.");
            workspace = new StickyWorkspace(load.GetAwaiter().GetResult()) { StartupPending = true };
            main.AttachWorkspace(workspace);
            var layoutEvents = workspace.Store.History.ReadTimeline().Events.Count(e => e.Target == "device/windows.json");
            var changes = 0; workspace.Changed += () => changes++;
            var restore = workspace.RestoreStartupWindowsAsync();
            while (!restore.IsCompleted) Pump(20);
            restore.GetAwaiter().GetResult();
            Check(workspace.Windows.Count == 8 && workspace.Windows.Values.All(w => w.Dispatcher == Application.Current.Dispatcher), "Restored windows were missing or constructed on a worker thread.");
            Check(changes == 1, "Startup refreshed the manager once per restored note.");
            Check(workspace.Store.History.ReadTimeline().Events.Count(e => e.Target == "device/windows.json") == layoutEvents + 1,
                "Startup recorded partial layouts instead of one complete restored layout.");
            Check(((Sticky.App.ViewModels.MainViewModel)main.DataContext).Cards.All(c => c.IsOpen), "The final manager list missed restored windows.");
            workspace.StartupPending = false; presentation.Commit();
        }
        finally
        {
            workspace?.PrepareExit();
            if (workspace is null && main is not null) Application.Current.Dispatcher.Invoke(() => { main.Hide(); });
            SynchronizationContext.SetSynchronizationContext(oldContext);
        }
    }
}
