using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Sticky.App.Editor;
using Sticky.Core;
using Sticky.Storage;
using Sticky.UpdateEngine;

internal static partial class Program
{
    private static int PortableUpdateSmoke(string package, string fixture)
    {
        fixture = Path.GetFullPath(fixture);
        if (Directory.Exists(fixture)) throw new IOException("Smoke workspace already exists.");
        Directory.CreateDirectory(fixture);
        var root = Path.Combine(fixture, "便携 程序's");
        using var zip = System.IO.Compression.ZipFile.OpenRead(package);
        var manifestEntry = zip.Entries.Single(e => e.FullName.EndsWith("/release-manifest.json", StringComparison.Ordinal));
        using var stream = manifestEntry.Open(); using var metadataReader = new StreamReader(stream); using var metadata = JsonDocument.Parse(metadataReader.ReadToEnd());
        var version = metadata.RootElement.GetProperty("version").GetString()!;
        Sticky.App.Updates.UpdatePackage.Extract(package, root, version);
        var data = Path.Combine(fixture, "外置 Data's");
        var key = "Sticky.Tests." + Guid.NewGuid().ToString("N");
        var store = new NoteStore(data);
        File.WriteAllText(Path.Combine(data, ".sticky-update-test"), key);
        var note = Example();
        var asset = DocumentCodec.StoreBitmap(SampleBitmap(), store);
        note.Blocks.Add(new NoteBlock { Inlines = [new NoteInline { Kind = "image", AssetId = asset, Width = 160 }] });
        var group = new NoteGroup { Name = "升级测试分组" }; note.GroupId = group.Id;
        store.Save(note); store.SaveGroups([group]);
        var device = new DeviceState(); device.Windows[note.Id] = new Sticky.Core.WindowState { IsOpen = true };
        store.SaveDeviceState(device);
        var files = new[] { Path.Combine(data, "notes", note.Id + ".json"), Path.Combine(data, "assets", asset), Path.Combine(data, "groups.json") };
        var hashes = files.ToDictionary(p => p, DurableFiles.Hash);
        var launcherHash = DurableFiles.Hash(Path.Combine(root, "Sticky.exe"));
        void AssertContent()
        { foreach (var (path, hash) in hashes) Check(DurableFiles.Hash(path) == hash, "Portable smoke changed notes, formatting, group or image bytes."); }
        Process? app = null;
        var launcherStore = new LauncherStore(root);
        try
        {
            var start = new ProcessStartInfo(Path.Combine(root, "Sticky.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = fixture };
            foreach (var arg in new[] { "--data-dir", data, "--test-instance-key", key }) start.ArgumentList.Add(arg);
            using (var launcher = Process.Start(start)!)
            {
                if (!launcher.WaitForExit(65000)) { launcher.Kill(true); throw new TimeoutException("Actual self-contained launcher did not complete startup."); }
                Check(launcher.ExitCode == 0, "Actual stable launcher failed: " + ReadLauncherError(launcherStore));
            }
            app = FindSmokeRuntime(VersionLayout.Slot(root, launcherStore.ReadState().Active));
            Check(app.MainWindowHandle != 0 && launcherStore.ReadState().Initialized, "Real WPF windows were not initialized and confirmed.");
            AssertContent(); StopSmokeRuntime(app); app = null;
            Console.WriteLine("PASS Portable stable exe initializes real WPF windows, rich notes, groups and images with external Data");

            var request = new UpdateRequest(root, data, Path.GetFullPath(package), DurableFiles.Hash(package), version, int.MaxValue, 0);
            DurableFiles.Write(launcherStore.QueueFile, request);
            // First protocol release has no older protocol package. Reinstall the actual candidate through every
            // production staging/snapshot/handshake/commit step; future prior-version packages can be tested separately.
            var options = new LauncherOptions { InstanceKey = key, AllowReinstallForTests = true };
            new LauncherEngine(root, options).Run(data);
            var active = launcherStore.ReadState().Active;
            app = FindSmokeRuntime(VersionLayout.Slot(root, active));
            Check(active != "bootstrap" && launcherStore.ReadState().Previous == "bootstrap" && app.MainWindowHandle != 0, "Real portable transaction did not confirm candidate startup.");
            Check(DurableFiles.Hash(Path.Combine(root, "Sticky.exe")) == launcherHash, "Portable update changed stable launcher bytes.");
            AssertContent(); StopSmokeRuntime(app); app = null;
            Console.WriteLine("PASS Real portable package stages a version, snapshots data, confirms WPF startup and retains old program");

            DurableFiles.Write(launcherStore.QueueFile, request);
            var launches = 0;
            var rejectFirst = new LauncherOptions
            {
                InstanceKey = key, AllowReinstallForTests = true,
                RuntimeCommand = runtime =>
                {
                    var info = new ProcessStartInfo(runtime);
                    if (launches++ == 0) { info.ArgumentList.Add("--startup-nonce"); info.ArgumentList.Add("wrong-identity"); }
                    return info;
                }
            };
            new LauncherEngine(root, rejectFirst).Run(data);
            Check(launcherStore.ReadState().Active == active && LauncherStore.IsRejected(root, request.Sha256), "Failed real portable trial was not quarantined.");
            app = FindSmokeRuntime(VersionLayout.Slot(root, active));
            Check(app.MainWindowHandle != 0, "Known portable version did not restart after rejected startup.");
            AssertContent();
            Console.WriteLine("PASS Failed real WPF startup restores snapshot, restarts the confirmed version and blocks repeated bad package");
            DurableFiles.Write(Path.Combine(fixture, "result.json"), new { success = true, version, packageSha256 = request.Sha256, checks = 3, time = DateTimeOffset.UtcNow });
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(fixture, "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); return 1; }
        finally
        {
            if (app is not null) StopSmokeRuntime(app);
            // Stop only processes whose exact executable lies in THIS newly created test root.
            foreach (var process in Process.GetProcessesByName("Sticky"))
            {
                using (process)
                {
                    try { if (process.MainModule?.FileName is string image && Sticky.App.Updates.UpdatePackage.IsWithin(image, root)) StopSmokeRuntime(process); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
            }
        }
    }
    private static string ReadLauncherError(LauncherStore store) => File.Exists(Path.Combine(store.DirectoryPath, "launcher-error.json"))
        ? File.ReadAllText(Path.Combine(store.DirectoryPath, "launcher-error.json")) : "No launcher diagnostic";
    private static Process FindSmokeRuntime(string slot)
    {
        var expected = DurableFiles.Child(slot, VersionLayout.Runtime);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            foreach (var process in Process.GetProcessesByName("Sticky"))
            {
                try
                {
                    if (!process.HasExited && string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase))
                    { process.Refresh(); if (process.MainWindowHandle != 0) return process; }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                process.Dispose();
            }
            Thread.Sleep(50);
        }
        throw new IOException("Could not find initialized real WPF process: " + expected);
    }
    private static void StopSmokeRuntime(Process process)
    { if (!process.HasExited) { process.Kill(true); process.WaitForExit(10000); } }
}
