using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Sticky.Sync.Git;

namespace Sticky.App.Synchronization;

public sealed class SyncCoordinator : INotifyPropertyChanged, IDisposable
{
    private readonly StickyWorkspace _workspace;
    private readonly GitSyncEngine _engine;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly string _settingsPath;
    private DateTimeOffset _nextAttempt = DateTimeOffset.UtcNow.AddSeconds(5);
    private CancellationTokenSource? _active;
    private bool _disposed;
    private int _failures;
    private long _changeVersion;
    private bool _applying;
    private string _status = "本地模式";
    private string _details = "便签保存在此设备；配置 GitHub 仓库后可启用同步。";
    public SyncSettings Settings { get; private set; } = new();
    public bool IsBusy { get; private set; }
    public bool CanConfigure => !IsBusy;
    public string Status => _status;
    public string Details => _details;
    public event PropertyChangedEventHandler? PropertyChanged;

    public SyncCoordinator(StickyWorkspace workspace)
    {
        _workspace = workspace;
        _engine = new GitSyncEngine(workspace.Store.Root);
        _settingsPath = Path.Combine(workspace.Store.Root, "device", "sync-settings.json");
        try
        {
            if (File.Exists(_settingsPath)) Settings = SyncJson.Read<SyncSettings>(File.ReadAllBytes(_settingsPath));
            if (Settings.Enabled) { _status = "等待同步 · 本地可正常记录"; _details = Settings.RemoteUrl + " · " + Settings.Branch; }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { _status = "同步配置无法读取 · 本地模式"; _details = ex.Message; Settings = new(); }
        workspace.Changed += OnLocalChanged;
        _timer.Tick += async (_, _) => { if (Settings.Enabled && !IsBusy && DateTimeOffset.UtcNow >= _nextAttempt) await RunAsync(); };
    }

    public void Start() { if (!_disposed) _timer.Start(); }
    public void Cancel() => _active?.Cancel();

    public void SaveSettings(string remote, string branch, bool enabled)
    {
        if (IsBusy) throw new InvalidOperationException("请等待当前同步完成，或先取消同步。");
        remote = remote.Trim(); branch = branch.Trim();
        if (enabled || remote.Length > 0) remote = GitSyncEngine.ValidateRemote(remote);
        if (enabled && (branch.Length is < 1 or > 100 || !System.Text.RegularExpressions.Regex.IsMatch(branch, @"^[A-Za-z0-9][A-Za-z0-9._/-]*$")
            || branch.Contains("..", StringComparison.Ordinal) || branch.EndsWith('/') || branch.EndsWith('.') || branch.Contains("//", StringComparison.Ordinal)
            || branch.Split('/').Any(part => part.StartsWith('.') || part.EndsWith(".lock", StringComparison.Ordinal)))) throw new ArgumentException("同步分支名称无效。");
        var settings = new SyncSettings { Enabled = enabled, RemoteUrl = remote, Branch = branch.Length == 0 ? "sticky-data" : branch,
            LastSuccess = remote == Settings.RemoteUrl && branch == Settings.Branch ? Settings.LastSuccess : null };
        SyncJson.WriteAtomic(_settingsPath, SyncJson.Bytes(settings), _workspace.Store.Root);
        Settings = settings; _failures = 0; _nextAttempt = DateTimeOffset.UtcNow.AddSeconds(15);
        SetStatus(enabled ? "已启用 · 等待同步" : "本地模式", enabled ? "可以点击「立即同步」完成首次登录；后台同步不会弹出登录窗口。" : "同步已停用，本地便签和远端数据均保留。");
        Notify();
    }

    public async Task RunAsync(bool interactive = false)
    {
        if (_disposed || IsBusy || !Settings.Enabled) return;
        IsBusy = true; _active = new CancellationTokenSource(); Notify();
        var token = _active.Token;
        SetStatus("正在同步…", "本地编辑和自动保存仍然可用。");
        try
        {
            var local = _workspace.CaptureForSync();
            var publishedVersion = _changeVersion;
            var settings = SyncJson.Read<SyncSettings>(SyncJson.Bytes(Settings));
            var uiConflicts = 0;
            var result = await Task.Run(() => _engine.SyncAsync(settings, local, async (captured, incoming, ct) =>
            {
                return await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (_disposed || _workspace.IsExiting) throw new OperationCanceledException(ct);
                    _applying = true;
                    try
                    {
                        var applied = _workspace.ApplySyncedSnapshot(captured, incoming, ct);
                        publishedVersion = _changeVersion;
                        uiConflicts += applied.ConflictCount;
                        return applied.Snapshot;
                    }
                    finally { _applying = false; }
                }, DispatcherPriority.Background, ct).Task;
            }, token, interactive), token);
            Settings.LastSuccess = DateTimeOffset.UtcNow;
            SyncJson.WriteAtomic(_settingsPath, SyncJson.Bytes(Settings), _workspace.Store.Root);
            _failures = 0; _nextAttempt = DateTimeOffset.UtcNow.AddSeconds(60);
            var count = result.ConflictCount + uiConflicts;
            var detail = count > 0 ? $"已保留 {count} 个冲突副本，可在便签管理、回收站或 AI 设置中查看。" : "便签、分组、图片和共享 AI 配置已同步。下次后台检查约在一分钟后。";
            if (_changeVersion != publishedVersion)
            {
                _nextAttempt = DateTimeOffset.UtcNow.AddSeconds(15);
                SetStatus("有新改动待同步 · 本地已保存", detail + "\n同步期间的新改动将在下一批上传。");
            }
            else SetStatus("已同步 · " + DateTime.Now.ToString("HH:mm"), detail);
        }
        catch (OperationCanceledException) { SetStatus("同步已取消 · 本地内容保留", "可随时重新点击立即同步。"); _nextAttempt = DateTimeOffset.UtcNow.AddSeconds(60); }
        catch (Exception ex)
        {
            _failures++;
            var seconds = Math.Min(300, 30 * Math.Pow(2, Math.Min(_failures - 1, 4)));
            _nextAttempt = DateTimeOffset.UtcNow.AddSeconds(seconds);
            SetStatus("同步暂不可用", GitProcess.Redact(ex.Message) + $"\n约 {seconds:0} 秒后自动重试；也可手动重试。首次连接或登录过期时请点击立即同步。");
        }
        finally { _active.Dispose(); _active = null; IsBusy = false; Notify(); }
    }

    private void OnLocalChanged()
    {
        if (_applying) return;
        _changeVersion++;
        if (_disposed || !Settings.Enabled || IsBusy) return;
        if (_failures == 0)
        {
            var due = DateTimeOffset.UtcNow.AddSeconds(15);
            if (_nextAttempt > due) _nextAttempt = due;
            SetStatus("待同步 · 本地已保存", "改动将批量同步；断网时仍然可以正常记录。");
        }
    }
    private void SetStatus(string status, string details) { _status = status; _details = details; Notify(); }
    private void Notify()
    {
        foreach (var property in new[] { nameof(Status), nameof(Details), nameof(IsBusy), nameof(CanConfigure), nameof(Settings) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop(); _active?.Cancel(); _workspace.Changed -= OnLocalChanged;
    }
}
