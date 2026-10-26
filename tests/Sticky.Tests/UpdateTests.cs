using System.IO;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sticky.App;
using Sticky.App.Updates;
using System.Windows.Threading;

internal static partial class Program
{
    private static byte[] ReleaseJson(string version = "0.3.0", string? digest = null, string? url = null, bool draft = false, bool prerelease = false) =>
        JsonSerializer.SerializeToUtf8Bytes(new { tag_name = "v" + version, draft, prerelease, body = "版本说明", assets = new[] {
            new { name = $"Sticky-{version}-win-x64-portable.zip", size = 4,
                url = "https://api.github.com/repos/ZhangShiQi/Sticky/releases/assets/123",
                digest = digest ?? "sha256:" + new string('a', 64),
                browser_download_url = url ?? $"https://github.com/ZhangShiQi/Sticky/releases/download/v{version}/Sticky-{version}-win-x64-portable.zip" } } });
    private static void ExpectUpdateFailure(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException or OperationCanceledException) { return; }
        throw new InvalidOperationException("Unsafe or failed update was accepted.");
    }
    private static void UpdateReleaseValidation()
    {
        Check(UpdateRelease.Parse(ReleaseJson("0.10.0"), "0.2.9")?.Version == "0.10.0", "Versions were compared as strings.");
        Check(UpdateRelease.Parse(ReleaseJson("0.2.0"), "0.2.0") is null && UpdateRelease.Parse(ReleaseJson("0.1.9"), "0.2.0") is null, "Equal or older release was offered.");
        Check(UpdateRelease.Parse(ReleaseJson(draft: true), "0.2.0") is null && UpdateRelease.Parse(ReleaseJson(prerelease: true), "0.2.0") is null, "Draft or preview release was offered.");
        ExpectUpdateFailure(() => UpdateRelease.Parse(ReleaseJson(digest: ""), "0.2.0"));
        ExpectUpdateFailure(() => UpdateRelease.Parse(ReleaseJson(url: "https://example.com/Sticky.zip"), "0.2.0"));
        foreach (var version in new[] { "01.2.3", "1.2", "1.2.3.4", "1.2.3-beta", "-1.2.3" }) ExpectUpdateFailure(() => UpdateRelease.ParseVersion(version));
    }

    private sealed class UpdateHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
    private static void UpdateDownloadValidation() => Task.Run(async () =>
    {
        var folder = Path.Combine(_root, "update-download"); Directory.CreateDirectory(folder);
        var bytes = Encoding.UTF8.GetBytes("test");
        var release = UpdateRelease.Parse(ReleaseJson(digest: "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes))), "0.2.0")!;
        using var client = new UpdateClient(new UpdateHttp(request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var file = Path.Combine(folder, "download.zip");
        await client.DownloadAsync(release, file, null, CancellationToken.None);
        Check(File.ReadAllBytes(file).SequenceEqual(bytes) && !File.Exists(file + ".partial"), "Valid download failed.");
        ExpectUpdateFailure(() => client.DownloadAsync(release with { Sha256 = new string('0', 64) }, file + ".bad", null, CancellationToken.None).GetAwaiter().GetResult());
        ExpectUpdateFailure(() => client.DownloadAsync(release with { Size = 8 }, file + ".short", null, CancellationToken.None).GetAwaiter().GetResult());
        using var redirect = new UpdateClient(new UpdateHttp(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://example.com/evil") } }));
        ExpectUpdateFailure(() => redirect.DownloadAsync(release, file + ".redirect", null, CancellationToken.None).GetAwaiter().GetResult());
        using var missing = new UpdateClient(new UpdateHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Check(await missing.CheckAsync("0.2.0", CancellationToken.None) is null, "Empty release repository failed.");
        using var denied = new UpdateClient(new UpdateHttp(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        ExpectUpdateFailure(() => denied.CheckAsync("0.2.0", CancellationToken.None).GetAwaiter().GetResult());
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        ExpectUpdateFailure(() => client.DownloadAsync(release, file + ".cancel", null, cancel.Token).GetAwaiter().GetResult());
        Check(!Directory.EnumerateFiles(folder, "*.partial").Any(), "Failed download left partial files.");
        Check(!File.Exists(file + ".bad") && !File.Exists(file + ".short"), "Failed downloads were published.");
    }).GetAwaiter().GetResult();

    private static void PrivateUpdateAuthentication() => Task.Run(async () =>
    {
        var requests = 0; var apiDownload = false; var redirected = false;
        var bytes = Encoding.UTF8.GetBytes("test");
        var json = ReleaseJson(digest: "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
        using var client = new UpdateClient(new UpdateHttp(request =>
        {
            requests++;
            if (request.RequestUri!.Host == "release-assets.githubusercontent.com")
            {
                Check(request.Headers.Authorization is null, "Credential leaked to a download redirect.");
                redirected = true;
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }
            if (request.Headers.Authorization is null) return new(HttpStatusCode.NotFound);
            Check(request.Headers.Authorization.Parameter == "test-access", "Private API request was not authenticated.");
            if (request.RequestUri.AbsolutePath.EndsWith("/assets/123", StringComparison.Ordinal))
            {
                Check(request.Headers.Accept.ToString() == "application/octet-stream", "Private asset was not requested as binary.");
                apiDownload = true;
                return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/download?signature=ignored") } };
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(json) };
        }), (interactive, _) =>
        {
            Check(interactive, "Manual login did not enable interactive credentials.");
            return Task.FromResult<string?>("test-access");
        });
        var release = await client.CheckAsync("0.2.0", CancellationToken.None, interactive: true);
        Check(release is not null && requests == 2, "Private release was not retried with credentials.");
        await client.DownloadAsync(release!, Path.Combine(_root, "private-update", "download.zip"), null, CancellationToken.None);
        Check(apiDownload && redirected, "Private asset download did not use authenticated API and redirect.");
        using var inaccessible = new UpdateClient(new UpdateHttp(_ => new(HttpStatusCode.NotFound)), (_, _) => Task.FromResult<string?>(null));
        ExpectUpdateFailure(() => inaccessible.CheckAsync("0.2.0", CancellationToken.None).GetAwaiter().GetResult());
        using var emptyPublic = new UpdateClient(new UpdateHttp(request => request.RequestUri!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal)
            ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK)), (_, _) => Task.FromResult<string?>(null));
        Check(await emptyPublic.CheckAsync("0.2.0", CancellationToken.None) is null, "Empty accessible repository should not offer an update.");
    }).GetAwaiter().GetResult();

    private static string CreateUpdateZip(string name, string? extra = null, string version = "0.3.0", bool wrongManifest = false, bool duplicate = false)
    {
        var directory = Path.Combine(_root, name); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "package.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var prefix = $"Sticky-{version}-win-x64-portable/";
        void Add(string relative, string content) { using var writer = new StreamWriter(zip.CreateEntry(prefix + relative).Open()); writer.Write(content); }
        var exe = "new executable";
        Add("Sticky.exe", exe);
        Add("portable.flag", "portable");
        Add("tools/git/cmd/git.exe", "new git");
        Add("tools/gcm/git-credential-manager.exe", "new gcm");
        Add("assets/Sticky.svg", "new icon");
        Add("licenses/LICENSE.txt", "license");
        Add("使用说明.txt", "instructions");
        Add("release-manifest.json", JsonSerializer.Serialize(new { application = "Sticky", version = wrongManifest ? "9.9.9" : version,
            runtimeIdentifier = "win-x64", selfContained = true, singleFile = true,
            executableSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(exe))) }));
        if (extra is not null) Add(extra, "unsafe");
        if (duplicate) Add("sticky.exe", "duplicate");
        return path;
    }
    private static void UpdateArchiveValidation()
    {
        var valid = CreateUpdateZip("update-zip-valid");
        UpdatePackage.Extract(valid, valid + ".out", "0.3.0");
        var invalid = new[] { "../escape.txt", "Data/notes/personal.json", "tools/../../escape.txt", "tools/file.exe:stream", "tools/NUL.txt", "tools/a./bad.txt", "tools\\bad.txt", "random.txt" };
        for (var i = 0; i < invalid.Length; i++)
        {
            var zip = CreateUpdateZip("update-zip-unsafe-" + i, invalid[i]);
            ExpectUpdateFailure(() => UpdatePackage.Extract(zip, zip + ".out", "0.3.0"));
            Check(!Directory.Exists(zip + ".out"), "Unsafe archive wrote files before path validation.");
        }
        var wrong = CreateUpdateZip("update-zip-version", wrongManifest: true);
        ExpectUpdateFailure(() => UpdatePackage.Extract(wrong, wrong + ".out", "0.3.0"));
        var duplicate = CreateUpdateZip("update-zip-duplicate", duplicate: true);
        ExpectUpdateFailure(() => UpdatePackage.Extract(duplicate, duplicate + ".out", "0.3.0"));
        var link = CreateUpdateZip("update-zip-link");
        using (var zip = ZipFile.Open(link, ZipArchiveMode.Update)) zip.CreateEntry("Sticky-0.3.0-win-x64-portable/tools/link").ExternalAttributes = unchecked((int)0xA1FF0000);
        ExpectUpdateFailure(() => UpdatePackage.Extract(link, link + ".out", "0.3.0"));
    }
    private static InstallPlan CreateInstallPlan(string name)
    {
        var zip = CreateUpdateZip(name);
        var target = Path.Combine(_root, name, "installed folder 中文's"); Directory.CreateDirectory(target);
        var data = Path.Combine(target, "Data"); Directory.CreateDirectory(Path.Combine(data, "assets"));
        File.WriteAllText(Path.Combine(data, "notes.json"), "private notes");
        File.WriteAllText(Path.Combine(data, "assets", "image.png"), "private image");
        File.WriteAllText(Path.Combine(target, "Sticky.exe"), "old executable");
        Directory.CreateDirectory(Path.Combine(target, "tools")); File.WriteAllText(Path.Combine(target, "tools", "old.exe"), "old tools");
        File.WriteAllText(Path.Combine(target, "custom.txt"), "user file");
        return new(target, data, zip, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zip))), "0.3.0", 0, 0, false);
    }
    private static void UpdateInstallPreservesData()
    {
        var plan = CreateInstallPlan("update-install-success");
        var backup = UpdateInstaller.Apply(plan);
        Check(File.ReadAllText(Path.Combine(plan.TargetDirectory, "Sticky.exe")) == "new executable", "New program not installed.");
        Check(File.ReadAllText(Path.Combine(backup, "Sticky.exe")) == "old executable", "Old program backup lost.");
        Check(File.ReadAllText(Path.Combine(plan.DataDirectory, "notes.json")) == "private notes"
            && File.ReadAllText(Path.Combine(plan.DataDirectory, "assets", "image.png")) == "private image"
            && File.ReadAllText(Path.Combine(plan.TargetDirectory, "custom.txt")) == "user file", "Personal files changed.");
        var external = CreateInstallPlan("update-install-external");
        var externalData = Path.Combine(_root, "external data 中文"); Directory.CreateDirectory(externalData);
        File.WriteAllText(Path.Combine(externalData, "notes.json"), "external notes");
        UpdateInstaller.Apply(external with { DataDirectory = externalData });
        Check(File.ReadAllText(Path.Combine(externalData, "notes.json")) == "external notes", "External data changed.");
    }
    private static void UpdateInstallRollback()
    {
        var plan = CreateInstallPlan("update-install-rollback");
        ExpectUpdateFailure(() => UpdateInstaller.Apply(plan, entry => { if (entry == "assets") throw new IOException("Simulated locked file/disk error"); }));
        Check(File.ReadAllText(Path.Combine(plan.TargetDirectory, "Sticky.exe")) == "old executable"
            && File.ReadAllText(Path.Combine(plan.TargetDirectory, "tools", "old.exe")) == "old tools", "Rollback did not restore program and tools.");
        Check(!Directory.Exists(Path.Combine(plan.TargetDirectory, "licenses")), "Rollback left newly added components.");
        Check(File.ReadAllText(Path.Combine(plan.DataDirectory, "notes.json")) == "private notes", "Rollback touched data.");
        ExpectUpdateFailure(() => UpdateInstaller.Apply(plan with { Sha256 = new string('0', 64) }));
        ExpectUpdateFailure(() => UpdatePackage.ValidateTarget(plan.TargetDirectory, plan.TargetDirectory));
        ExpectUpdateFailure(() => UpdatePackage.ValidateTarget(plan.TargetDirectory, Path.Combine(plan.TargetDirectory, "assets", "data")));
    }
    private static void UpdatePreferencesAndOffline()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "update-settings"));
        try
        {
            Check(workspace.Updates.Automatic, "Automatic updates should default on.");
            workspace.Updates.SetAutomatic(false);
            using (var restored = new UpdateCoordinator(workspace)) Check(!restored.Automatic, "Update preference was not persisted.");
            using var offline = new UpdateCoordinator(workspace, new UpdateClient(new UpdateHttp(_ => throw new HttpRequestException("Offline test"))));
            Task.Run(() => offline.CheckAsync()).GetAwaiter().GetResult();
            Check(!offline.IsBusy && offline.Status == "暂时无法更新", "Offline check did not recover.");
            workspace.CreateNote();
            Check(workspace.SaveAllNotes() && workspace.Store.LoadNotes().Count == 1, "Offline updater blocked local notes.");
            workspace.ShowSettings(SettingsSection.Updates); Pump(50);
            Check(workspace.Settings!.Title.Contains("设置"), "Update settings did not open.");
        }
        finally { workspace.PrepareExit(); }
    }

    private static int UpdateUiContextProbe(string planPath)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var plan = JsonSerializer.Deserialize<InstallPlan>(File.ReadAllText(planPath))!;
        UpdateInstaller.Apply(plan);
        return 0;
    }

    private static void UpdateUiContext()
    {
        var plan = CreateInstallPlan("update-ui-context");
        // Ensure hashing suspends instead of completing inline on a tiny cached fixture.
        using (var zip = ZipFile.Open(plan.PackagePath, ZipArchiveMode.Update))
        using (var output = zip.CreateEntry("Sticky-0.3.0-win-x64-portable/tools/context-probe.bin", CompressionLevel.NoCompression).Open())
        {
            var bytes = new byte[1024 * 1024]; Random.Shared.NextBytes(bytes);
            for (var i = 0; i < 16; i++) output.Write(bytes);
        }
        plan = plan with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(plan.PackagePath))) };
        var planPath = Path.Combine(Path.GetDirectoryName(plan.PackagePath)!, "context-plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--update-ui-context-probe"); start.ArgumentList.Add(planPath);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(8000))
        {
            process.Kill(true); process.WaitForExit();
            throw new InvalidOperationException("Update helper deadlocked while the WPF startup thread synchronously waited for package hashing.");
        }
        Check(process.ExitCode == 0 && File.ReadAllText(Path.Combine(plan.TargetDirectory, "Sticky.exe")) == "new executable", "UI-context installation did not complete.");
        Check(File.ReadAllText(Path.Combine(plan.DataDirectory, "notes.json")) == "private notes", "UI-context installation changed data.");
    }
}
