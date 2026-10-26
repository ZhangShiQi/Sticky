using System.Text;
using System.Text.RegularExpressions;
using Sticky.Core;
using Sticky.Storage;

namespace Sticky.Sync.Git;

public sealed class GitSyncEngine
{
    private const string Prefix = "sticky-v1/";
    private const int MaxFileBytes = 20 * 1024 * 1024;
    private const int MaxTotalBytes = 256 * 1024 * 1024;
    private readonly string _dataRoot;
    private readonly bool _allowLocalRemotes;
    private readonly GitProcess _git;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly byte[] Marker = Encoding.UTF8.GetBytes("{\"format\":\"Sticky\",\"schemaVersion\":1}\n");

    public GitSyncEngine(string dataRoot, bool allowLocalRemotes = false, GitProcess? gitProcess = null)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _allowLocalRemotes = allowLocalRemotes;
        _git = gitProcess ?? new GitProcess();
    }

    public static string ValidateRemote(string url, bool allowLocal = false)
    {
        url = url.Trim();
        if (allowLocal && Path.IsPathFullyQualified(url)) return Path.GetFullPath(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("请输入 GitHub 仓库的 HTTPS 地址，不要在地址中填写令牌或密码。");
        var match = Regex.Match(uri.AbsolutePath.TrimEnd('/'), @"^/([A-Za-z0-9-]+)/([A-Za-z0-9_.-]+?)(?:\.git)?$");
        if (!match.Success || match.Groups[2].Value is "." or "..") throw new ArgumentException("仓库地址格式应为 https://github.com/账号/仓库.git。");
        return $"https://github.com/{match.Groups[1].Value}/{match.Groups[2].Value}.git";
    }

    public async Task<SyncResult> SyncAsync(SyncSettings settings, SyncSnapshot local,
        Func<SyncSnapshot, SyncSnapshot, CancellationToken, Task<SyncSnapshot>> apply,
        CancellationToken cancellationToken = default, bool interactive = false)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var remoteUrl = ValidateRemote(settings.RemoteUrl, _allowLocalRemotes);
            var branch = settings.Branch.Trim();
            if (branch.Length is < 1 or > 100 || !Regex.IsMatch(branch, @"^[A-Za-z0-9][A-Za-z0-9._/-]*$")) throw new ArgumentException("同步分支名称无效。");
            var targetKey = SyncJson.Hash(Encoding.UTF8.GetBytes(remoteUrl + "\n" + branch));
            var target = Path.Combine(_dataRoot, "device", "sync", targetKey);
            var repo = Path.Combine(target, "repository");
            Directory.CreateDirectory(repo);
            await Require(repo, ["check-ref-format", "--branch", branch], cancellationToken);
            if (!Directory.Exists(Path.Combine(repo, ".git"))) await Require(repo, ["init", "--quiet"], cancellationToken);
            await Require(repo, ["config", "remote.origin.url", remoteUrl], cancellationToken);
            var baselinePath = Path.Combine(target, "baseline.json");
            var baseline = ReadBaseline(baselinePath);
            local = local.Clone();
            var conflictCount = 0;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var heads = await Require(repo, ["ls-remote", "--heads", "origin", "refs/heads/" + branch], cancellationToken, interactive: interactive);
                string? parent = null;
                if (heads.Text.Length != 0)
                {
                    await Require(repo, ["fetch", "--quiet", "--no-tags", "--recurse-submodules=no", "origin", $"+refs/heads/{branch}:refs/remotes/origin/sticky-sync"], cancellationToken, interactive: interactive);
                    parent = (await Require(repo, ["rev-parse", "--verify", "refs/remotes/origin/sticky-sync^{commit}"], cancellationToken)).Text;
                }
                var remoteFiles = parent is null ? new Dictionary<string, byte[]>(StringComparer.Ordinal) : await ReadFiles(repo, parent, cancellationToken);
                var remote = ParseSnapshot(remoteFiles);
                var merged = SnapshotMerger.Merge(baseline, local, remote);
                conflictCount += merged.ConflictCount;
                // Fill immutable resources off the UI thread. No image or note ever depends on network availability to save locally.
                FillAssets(merged.Snapshot);
                var beforeApply = local;
                local = await apply(beforeApply, merged.Snapshot, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                FillAssets(local);
                ValidateSnapshot(local);
                // This remote revision has been integrated locally even if push is interrupted.
                SyncJson.WriteAtomic(baselinePath, SyncJson.Bytes(remote), _dataRoot);
                baseline = remote;
                var files = SerializeSnapshot(local);
                var changed = files.Any(pair => !remoteFiles.TryGetValue(pair.Key, out var old) || !pair.Value.AsSpan().SequenceEqual(old));
                if (!changed)
                {
                    SyncJson.WriteAtomic(baselinePath, SyncJson.Bytes(local), _dataRoot);
                    return new SyncResult(conflictCount, false);
                }
                var commit = await CreateCommit(repo, target, parent, remoteFiles, files, cancellationToken);
                var push = await _git.RunAsync(repo, ["push", "--porcelain", "origin", $"{commit}:refs/heads/{branch}"], cancellationToken, interactive: interactive);
                if (push.ExitCode == 0)
                {
                    SyncJson.WriteAtomic(baselinePath, SyncJson.Bytes(local), _dataRoot);
                    return new SyncResult(conflictCount, true);
                }
                var error = push.Error + "\n" + push.Text;
                if (!error.Contains("[rejected]", StringComparison.OrdinalIgnoreCase) && !error.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
                    && !error.Contains("fetch first", StringComparison.OrdinalIgnoreCase)) throw new IOException("推送未完成：" + GitProcess.Redact(error));
            }
            throw new IOException("其他设备正在频繁更新仓库，本地内容已保留，将稍后重新同步。");
        }
        finally { _gate.Release(); }
    }

    private SyncSnapshot ReadBaseline(string path)
    {
        var candidates = new[] { path }.Concat(WorkspaceFiles.BackupCandidates(_dataRoot, path)).ToArray();
        if (!candidates.Any(File.Exists)) return new SyncSnapshot();
        foreach (var candidate in candidates)
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var baseline = SyncJson.Read<SyncSnapshot>(File.ReadAllBytes(candidate));
                ValidateSnapshot(baseline, checkAssets: false);
                return baseline;
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException or ArgumentException) { }
        }
        throw new InvalidDataException("同步基线无法读取。便签仍可在本地编辑，请从 backups/device/sync 的基线备份恢复后重试。");
    }

    private async Task<Dictionary<string, byte[]>> ReadFiles(string repo, string revision, CancellationToken token)
    {
        var tree = await Require(repo, ["ls-tree", "-rz", "--full-tree", revision, "--", Prefix], token);
        var entries = new List<(string Path, string Hash)>();
        foreach (var entry in Encoding.UTF8.GetString(tree.Bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = entry.IndexOf('\t');
            if (split < 0) throw new InvalidDataException("远端 Git 文件树无效。");
            var header = entry[..split].Split(' ');
            var path = entry[(split + 1)..];
            if (header.Length != 3 || header[0] != "100644" || header[1] != "blob" || !Regex.IsMatch(header[2], "^[a-f0-9]{40,64}$"))
                throw new InvalidDataException("同步目录只允许普通数据文件。");
            ValidatePath(path);
            entries.Add((path, header[2]));
            if (entries.Count > 10000) throw new InvalidDataException("远端同步文件超过 10000 个。");
        }
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (entries.Count == 0) return files;
        var batch = await Require(repo, ["cat-file", "--batch"], token, Encoding.ASCII.GetBytes(string.Join('\n', entries.Select(e => e.Hash)) + "\n"));
        var position = 0;
        long total = 0;
        foreach (var entry in entries)
        {
            var end = Array.IndexOf(batch.Bytes, (byte)'\n', position);
            if (end < 0) throw new InvalidDataException("Git 对象数据不完整。");
            var header = Encoding.ASCII.GetString(batch.Bytes, position, end - position).Split(' ');
            if (header.Length != 3 || header[0] != entry.Hash || header[1] != "blob" || !int.TryParse(header[2], out var length)
                || length < 0 || length > MaxFileBytes || (total += length) > MaxTotalBytes)
                throw new InvalidDataException("远端文件超过同步大小限制（单文件 20 MB，总计 256 MB）。");
            position = end + 1;
            if (position + (long)length >= batch.Bytes.Length || batch.Bytes[position + length] != (byte)'\n') throw new InvalidDataException("Git 对象数据不完整。");
            files.Add(entry.Path, batch.Bytes.AsSpan(position, length).ToArray());
            position += length + 1;
        }
        return files;
    }

    private static void ValidatePath(string path)
    {
        if (path is Prefix + "manifest.json" or Prefix + "groups.json") return;
        if (Regex.IsMatch(path, @"^sticky-v1/notes/[a-fA-F0-9-]{36}\.json$") || Regex.IsMatch(path, @"^sticky-v1/assets/[a-f0-9]{64}\.png$")) return;
        throw new InvalidDataException("远端同步目录包含不支持的文件：" + path);
    }

    private static SyncSnapshot ParseSnapshot(Dictionary<string, byte[]> files)
    {
        var snapshot = new SyncSnapshot();
        if (files.Count == 0) return snapshot;
        if (!files.TryGetValue(Prefix + "manifest.json", out var marker)) throw new InvalidDataException("远端 sticky-v1 目录不是有效的 Sticky 数据目录。");
        using (var json = System.Text.Json.JsonDocument.Parse(marker))
        {
            if (!json.RootElement.TryGetProperty("format", out var format) || format.GetString() != "Sticky"
                || !json.RootElement.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != 1)
                throw new InvalidDataException("远端同步格式不受支持，请升级应用。");
        }
        foreach (var (path, bytes) in files)
        {
            if (path == Prefix + "groups.json") snapshot.Groups = NoteStore.ParseGroups(bytes);
            else if (path.StartsWith(Prefix + "notes/", StringComparison.Ordinal))
            {
                if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var id)) throw new InvalidDataException("远端便签标识无效。");
                snapshot.Notes.Add(id, NoteStore.ParseNote(bytes, id));
            }
            else if (path.StartsWith(Prefix + "assets/", StringComparison.Ordinal))
            {
                var id = Path.GetFileName(path);
                if (SyncJson.Hash(bytes) + ".png" != id) throw new InvalidDataException("远端图片校验失败。");
                snapshot.Assets.Add(id, bytes);
            }
        }
        ValidateSnapshot(snapshot);
        return snapshot;
    }

    public static void ValidateSnapshot(SyncSnapshot snapshot, bool checkAssets = true)
    {
        if (snapshot.Notes is null || snapshot.Groups is null) throw new InvalidDataException("同步快照无效。");
        _ = NoteStore.ParseGroups(SyncJson.Bytes(new GroupCatalog { Groups = snapshot.Groups }));
        foreach (var (id, note) in snapshot.Notes)
        {
            if (note is null) throw new InvalidDataException("同步便签为空。");
            _ = NoteStore.ParseNote(SyncJson.Bytes(note), id);
            if (note.GroupId is { } group && !snapshot.Groups.Any(g => g.Id == group)) throw new InvalidDataException("同步快照缺少便签所属的分组。");
            if (checkAssets)
                foreach (var asset in ImageIds(note.Blocks))
                    if (!snapshot.Assets.TryGetValue(asset, out var bytes) || SyncJson.Hash(bytes) + ".png" != asset) throw new InvalidDataException("同步快照缺少有效图片：" + asset);
        }
    }

    private void FillAssets(SyncSnapshot snapshot)
    {
        var store = new NoteStore(_dataRoot);
        foreach (var id in snapshot.Notes.Values.SelectMany(n => ImageIds(n.Blocks)).Distinct())
        {
            if (snapshot.Assets.ContainsKey(id)) continue;
            var path = store.GetAssetPath(id);
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("本地缺少图片或图片过大，便签已保留：" + id);
            var bytes = File.ReadAllBytes(path);
            if (SyncJson.Hash(bytes) + ".png" != id) throw new InvalidDataException("本地图片校验失败：" + id);
            snapshot.Assets[id] = bytes;
        }
    }

    private static Dictionary<string, byte[]> SerializeSnapshot(SyncSnapshot snapshot)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [Prefix + "manifest.json"] = Marker,
            [Prefix + "groups.json"] = SyncJson.Bytes(new GroupCatalog { Groups = snapshot.Groups.OrderBy(g => g.Id).ToList() })
        };
        foreach (var note in snapshot.Notes.Values) files[Prefix + "notes/" + note.Id + ".json"] = SyncJson.Bytes(note);
        foreach (var id in snapshot.Notes.Values.SelectMany(n => ImageIds(n.Blocks)).Distinct()) files[Prefix + "assets/" + id] = snapshot.Assets[id];
        if (files.Count > 10000 || files.Values.Any(b => b.Length > MaxFileBytes) || files.Values.Sum(b => (long)b.Length) > MaxTotalBytes)
            throw new InvalidDataException("便签数据超过同步大小限制（单文件 20 MB，总计 256 MB）。");
        return files;
    }

    private async Task<string> CreateCommit(string repo, string target, string? parent, Dictionary<string, byte[]> oldFiles, Dictionary<string, byte[]> files, CancellationToken token)
    {
        var index = Path.Combine(target, "index-" + Guid.NewGuid().ToString("N"));
        var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
        try
        {
            await Require(repo, parent is null ? ["read-tree", "--empty"] : ["read-tree", parent], token, environment: environment);
            foreach (var (path, bytes) in files)
            {
                if (oldFiles.TryGetValue(path, out var old) && old.AsSpan().SequenceEqual(bytes)) continue;
                var hash = (await Require(repo, ["hash-object", "-w", "--stdin"], token, bytes)).Text;
                await Require(repo, ["update-index", "--add", "--cacheinfo", $"100644,{hash},{path}"], token, environment: environment);
            }
            var tree = (await Require(repo, ["write-tree"], token, environment: environment)).Text;
            var args = new List<string> { "commit-tree", tree };
            if (parent is not null) { args.Add("-p"); args.Add(parent); }
            args.Add("-m"); args.Add("Sync Sticky notes");
            return (await Require(repo, args, token)).Text;
        }
        finally { if (File.Exists(index)) File.Delete(index); if (File.Exists(index + ".lock")) File.Delete(index + ".lock"); }
    }

    private async Task<GitOutput> Require(string repo, IEnumerable<string> args, CancellationToken token, byte[]? input = null,
        bool interactive = false, Dictionary<string, string>? environment = null)
    {
        var output = await _git.RunAsync(repo, args, token, input, interactive, environment);
        if (output.ExitCode != 0) throw new IOException("Git 操作未完成：" + output.Error);
        return output;
    }

    public static IEnumerable<string> ImageIds(IEnumerable<NoteBlock> blocks)
    {
        foreach (var block in blocks)
        {
            foreach (var inline in block.Inlines) if (inline.Kind == "image" && inline.AssetId is { } id) yield return id;
            foreach (var id in ImageIds(block.Children)) yield return id;
        }
    }
}
