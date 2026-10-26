using System.IO;
using System.Text;
using Sticky.Core;
using Sticky.Storage;
using Sticky.Sync.Git;

internal static class GitIntegrationTests
{
    public static string? ToolBaseDirectory { get; set; }
    private static GitProcess CreateGitProcess() => new(ToolBaseDirectory);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Device
    {
        public NoteStore Store { get; }
        public GitSyncEngine Engine { get; }
        public SyncSettings Settings { get; }
        public Func<SyncSnapshot, SyncSnapshot, CancellationToken, Task<SyncSnapshot>>? BeforeApply { get; set; }
        public Device(string path, string remote)
        {
            Store = new NoteStore(path); Engine = new GitSyncEngine(path, allowLocalRemotes: true, gitProcess: CreateGitProcess());
            Settings = new SyncSettings { Enabled = true, RemoteUrl = remote };
        }
        public SyncSnapshot Capture() => new() { Notes = Store.LoadNotes().ToDictionary(n => n.Id), Groups = Store.LoadGroups() };
        public Task<SyncResult> Sync(CancellationToken token = default) => Engine.SyncAsync(Settings, Capture(), async (captured, incoming, ct) =>
        {
            if (BeforeApply is not null) incoming = await BeforeApply(captured, incoming, ct);
            var final = SnapshotMerger.Merge(captured, incoming, Capture()).Snapshot;
            foreach (var bytes in final.Assets.Values) Store.AddImage(bytes);
            Store.SaveGroups(final.Groups);
            foreach (var note in final.Notes.Values.OrderBy(n => File.Exists(Path.Combine(Store.NotesDirectory, n.Id + ".json")))) Store.Save(note);
            return final;
        }, token);
        public Note Edit(Guid id, string text, bool deleted = false)
        {
            var note = Store.LoadNotes().Single(n => n.Id == id);
            note.Blocks = [new NoteBlock { Inlines = [new NoteInline { Text = text, Bold = true }] }];
            note.DeletedAt = deleted ? DateTimeOffset.UtcNow : null; note.ModifiedAt = DateTimeOffset.UtcNow; Store.Save(note); return note;
        }
    }

    private sealed class Fixture
    {
        public string Root { get; }
        public string Remote { get; }
        public Device A { get; }
        public Device B { get; }
        private Fixture(string root)
        {
            Root = root; Remote = Path.Combine(root, "remote.git");
            A = new Device(Path.Combine(root, "device-a"), Remote); B = new Device(Path.Combine(root, "device-b"), Remote);
        }
        public static async Task<Fixture> Create(string root)
        {
            Directory.CreateDirectory(root); var fixture = new Fixture(root);
            await Git(root, ["init", "--bare", fixture.Remote]); return fixture;
        }
        public static async Task<string> Git(string cwd, string[] args, byte[]? input = null)
        {
            var result = await CreateGitProcess().RunAsync(cwd, args, CancellationToken.None, input);
            if (result.ExitCode != 0) throw new IOException(result.Error);
            return result.Text;
        }
        public async Task<Note> Seed()
        {
            var note = new Note { Title = "共同便签", Blocks = [new NoteBlock { Inlines = [new NoteInline { Text = "共同版本" }] }] };
            A.Store.Save(note); await A.Sync(); await B.Sync(); return note;
        }
    }

    public static async Task RoundTrip(string root)
    {
        var f = await Fixture.Create(root);
        var group = new NoteGroup { Name = "同步的工作分组" }; f.A.Store.SaveGroups([group]);
        var image = f.A.Store.AddImage(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aZioAAAAASUVORK5CYII="));
        var note = new Note { Title = "中文富文本", GroupId = group.Id, Blocks = [new NoteBlock { Inlines =
            [new NoteInline { Text = "需要同步的正文", Bold = true, Underline = true, Foreground = "#FF285A9C" }, new NoteInline { Kind = "image", AssetId = image, Width = 160 }] }] };
        f.A.Store.Save(note);
        f.A.Store.SaveDeviceState(new DeviceState());
        f.A.Store.SaveEditorPreferences(new EditorPreferences { DefaultFontSize = 22, ParagraphSpacing = 12 });
        f.B.Store.SaveEditorPreferences(new EditorPreferences { DefaultFontSize = 14, ParagraphSpacing = 0 });
        SyncJson.WriteAtomic(Path.Combine(f.A.Store.Root, "device", "sync-settings.json"), SyncJson.Bytes(new { secret = "DO-NOT-UPLOAD" }), f.A.Store.Root);
        await f.A.Sync(); await f.B.Sync();
        var downloaded = f.B.Store.LoadNotes().Single();
        Check(downloaded.Title == note.Title && downloaded.GroupId == group.Id && downloaded.Blocks[0].Inlines[0].Underline, "Rich text or group did not sync.");
        Check(File.ReadAllBytes(f.B.Store.GetAssetPath(image)).AsSpan().SequenceEqual(File.ReadAllBytes(f.A.Store.GetAssetPath(image))), "Image bytes changed.");
        var tree = await Fixture.Git(f.Root, ["--git-dir", f.Remote, "ls-tree", "-r", "--name-only", "refs/heads/sticky-data"]);
        Check(!tree.Contains("device/") && !tree.Contains("backups/") && !tree.Contains(".bak") && tree.Split('\n').All(p => p.StartsWith("sticky-v1/", StringComparison.Ordinal)), "Device settings or backups leaked into Git.");
        Check(f.B.Store.LoadEditorPreferences().DefaultFontSize == 14, "Sync overwrote a device's typography preferences.");
        Check(!(await f.B.Sync()).Uploaded, "An unchanged snapshot created another commit.");
        var baseline = Directory.EnumerateFiles(Path.Combine(f.B.Store.Root, "device", "sync"), "baseline.json", SearchOption.AllDirectories).Single();
        Check(File.Exists(WorkspaceFiles.BackupPath(f.B.Store.Root, baseline)), "Sync baseline was not backed up in the dedicated directory.");
        File.WriteAllText(baseline, "{broken");
        Check(!(await f.B.Sync()).Uploaded, "Recovering a corrupt sync baseline changed remote content.");
        File.Delete(baseline);
        Check(!(await f.B.Sync()).Uploaded, "A backup-only sync baseline was not recovered.");
    }

    public static async Task Conflict(string root)
    {
        var f = await Fixture.Create(root); var note = await f.Seed();
        f.A.Edit(note.Id, "A 的修改"); f.B.Edit(note.Id, "B 的修改");
        await f.A.Sync(); var result = await f.B.Sync(); await f.A.Sync();
        var a = f.A.Store.LoadNotes(); var b = f.B.Store.LoadNotes();
        Check(result.ConflictCount > 0 && a.Count == 2 && b.Count == 2, "Conflict did not create exactly one copy.");
        Check(a.Any(n => n.PlainText == "A 的修改") && a.Any(n => n.PlainText == "B 的修改" && n.ConflictOf == note.Id), "One side's content was lost.");
        await f.B.Sync(); await f.A.Sync();
        Check(f.A.Store.LoadNotes().Count == 2 && f.B.Store.LoadNotes().Count == 2, "Repeated sync duplicated conflict copies.");
    }

    public static async Task Offline(string root)
    {
        var f = await Fixture.Create(root); var note = await f.Seed();
        var offline = Path.Combine(f.Root, "remote.offline");
        // Both paths are explicitly within this disposable fixture.
        Check(Path.GetFullPath(offline).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Unexpected test path.");
        Directory.Move(f.Remote, offline);
        f.A.Edit(note.Id, "离线时 A 仍然可以记录"); f.B.Edit(note.Id, "离线时 B 仍然可以记录");
        try { await f.A.Sync(); throw new Exception("Disconnected sync unexpectedly succeeded."); } catch (IOException) { }
        Check(f.A.Store.LoadNotes().Single().PlainText.Contains("离线时 A"), "Offline sync damaged local edits.");
        Directory.Move(offline, f.Remote);
        // A restarted process uses the same on-disk baseline, not any in-memory state.
        var restartedA = new Device(f.A.Store.Root, f.Remote); await restartedA.Sync(); await f.B.Sync(); await restartedA.Sync();
        var notes = restartedA.Store.LoadNotes();
        Check(notes.Count == 2 && notes.Any(n => n.PlainText.Contains("离线时 A")) && notes.Any(n => n.PlainText.Contains("离线时 B")), "Reconnect/restart lost offline edits.");
    }

    public static async Task DifferentNotes(string root)
    {
        var f = await Fixture.Create(root);
        var one = new Note { Title = "便签一" }; var two = new Note { Title = "便签二" };
        f.A.Store.Save(one); f.A.Store.Save(two); await f.A.Sync(); await f.B.Sync();
        f.A.Edit(one.Id, "第一张的修改"); f.B.Edit(two.Id, "第二张的修改");
        await f.A.Sync(); await f.B.Sync(); await f.A.Sync();
        var notes = f.A.Store.LoadNotes();
        Check(notes.Count == 2 && notes.Any(n => n.PlainText == "第一张的修改") && notes.Any(n => n.PlainText == "第二张的修改"), "Independent notes conflicted or disappeared.");
    }

    public static async Task DeleteVsEdit(string root)
    {
        var f = await Fixture.Create(root); var note = await f.Seed();
        f.A.Edit(note.Id, "删除前内容", deleted: true); f.B.Edit(note.Id, "另一台设备的新增内容");
        await f.A.Sync(); await f.B.Sync(); await f.A.Sync();
        var notes = f.A.Store.LoadNotes();
        Check(notes.Any(n => n.DeletedAt is not null) && notes.Any(n => n.DeletedAt is null && n.PlainText == "另一台设备的新增内容"), "Deletion conflict discarded the edited content or tombstone.");
    }

    public static async Task PushRace(string root)
    {
        var f = await Fixture.Create(root); await f.Seed();
        var fromA = new Note { Title = "并发提交 A" }; var fromB = new Note { Title = "并发提交 B" };
        f.A.Store.Save(fromA); f.B.Store.Save(fromB);
        var raced = false;
        f.A.BeforeApply = async (_, incoming, _) =>
        {
            if (!raced) { raced = true; await f.B.Sync(); }
            return incoming;
        };
        await f.A.Sync(); await f.B.Sync();
        Check(f.A.Store.LoadNotes().Count == 3 && f.B.Store.LoadNotes().Count == 3, "Non-fast-forward retry lost a concurrent commit.");
    }

    public static async Task PreserveOtherFiles(string root)
    {
        var f = await Fixture.Create(root);
        var seed = Path.Combine(root, "seed"); Directory.CreateDirectory(seed);
        await Fixture.Git(seed, ["init", "--quiet"]);
        File.WriteAllText(Path.Combine(seed, "README.md"), "existing content");
        await Fixture.Git(seed, ["add", "README.md"]); await Fixture.Git(seed, ["commit", "-m", "Initial README"]);
        await Fixture.Git(seed, ["push", f.Remote, "HEAD:refs/heads/sticky-data"]);
        f.A.Store.Save(new Note { Title = "新增便签" }); await f.A.Sync();
        Check(await Fixture.Git(root, ["--git-dir", f.Remote, "show", "refs/heads/sticky-data:README.md"]) == "existing content", "Sync overwrote unrelated repository content.");
    }

    public static async Task RejectInvalidRemote(string root)
    {
        var f = await Fixture.Create(root); await f.Seed();
        var seed = Path.Combine(root, "bad-data"); Directory.CreateDirectory(seed);
        await Fixture.Git(seed, ["clone", "--quiet", "--branch", "sticky-data", f.Remote, "."]);
        var json = Directory.EnumerateFiles(Path.Combine(seed, "sticky-v1", "notes"), "*.json").Single();
        File.WriteAllText(json, "{truncated");
        await Fixture.Git(seed, ["add", "."]); await Fixture.Git(seed, ["commit", "-m", "Corrupt note"]); await Fixture.Git(seed, ["push", "origin", "HEAD:refs/heads/sticky-data"]);
        try { await f.A.Sync(); throw new Exception("Invalid remote accepted."); } catch (System.Text.Json.JsonException) { }
        Check(f.A.Store.LoadNotes().Single().PlainText == "共同版本", "Invalid remote overwrote healthy local content.");
    }

    public static async Task Groups(string root)
    {
        var f = await Fixture.Create(root); var note = await f.Seed();
        var ga = new NoteGroup { Name = "同名分组" }; var gb = new NoteGroup { Name = "同名分组" };
        f.A.Store.SaveGroups([ga]); f.B.Store.SaveGroups([gb]);
        var a = f.A.Store.LoadNotes().Single(); a.GroupId = ga.Id; f.A.Store.Save(a);
        var b = f.B.Store.LoadNotes().Single(); b.GroupId = gb.Id; f.B.Store.Save(b);
        await f.A.Sync(); await f.B.Sync(); await f.A.Sync();
        Check(f.A.Store.LoadGroups().Count == 1 && f.A.Store.LoadNotes().Count == 1, "Equal group creation caused duplicates or content conflicts.");
        Check(f.A.Store.LoadNotes().Single().GroupId == f.A.Store.LoadGroups().Single().Id, "Group membership was not remapped.");
    }

    public static void ConfigValidation()
    {
        Check(GitSyncEngine.ValidateRemote("https://github.com/example/sticky") == "https://github.com/example/sticky.git", "Repository normalization failed.");
        foreach (var url in new[] { "http://github.com/a/b.git", "https://user:secret@github.com/a/b.git", "https://github.com/a/b.git?token=secret", "https://example.com/a/b.git", "ext::bad" })
        {
            try { GitSyncEngine.ValidateRemote(url); throw new Exception("Unsafe repository URL accepted."); } catch (ArgumentException) { }
        }
        Check(!GitProcess.Redact("ghp_ABC123 github_pat_SECRET123 https://u:secret@github.com/a/b").Contains("SECRET123"), "Error messages leaked a credential.");
    }

    public static async Task PortableTools(string root)
    {
        Check(ToolBaseDirectory is not null, "A portable package directory is required.");
        Directory.CreateDirectory(root);
        var toolchain = GitToolchain.Resolve(ToolBaseDirectory!);
        Check(Path.IsPathFullyQualified(toolchain.GitExecutable) && toolchain.CredentialHelper.StartsWith("!'", StringComparison.Ordinal), "Bundled Git/GCM were not selected.");
        var version = await CreateGitProcess().RunAsync(root, ["--version"], CancellationToken.None);
        Check(version.ExitCode == 0 && version.Text.Contains("2.56.0"), "Bundled Git failed to start.");
        // Invoke the exact shell-quoted helper command with --version, without requesting credentials or network access.
        var helper = await CreateGitProcess().RunAsync(root, ["-c", "alias.sticky-gcm-probe=" + toolchain.CredentialHelper + " --version", "sticky-gcm-probe"], CancellationToken.None);
        Check(helper.ExitCode == 0 && helper.Text.Contains("3.0.1"), "Bundled native GCM failed, or portable path quoting broke: " + helper.Error);
    }
}
