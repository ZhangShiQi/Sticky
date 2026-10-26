using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Sticky.Storage;

namespace Sticky.App.Updates;

public sealed class UpdatePreferences
{
    public bool Automatic { get; set; } = true;
}

public sealed class UpdateCoordinator : INotifyPropertyChanged, IDisposable
{
    private readonly StickyWorkspace _workspace;
    private readonly UpdateClient _client;
    private readonly string _settingsPath;
    private readonly string _cache;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private CancellationTokenSource? _active;
    private DateTimeOffset _nextCheck = DateTimeOffset.UtcNow.AddSeconds(15);
    private string? _package;
    private bool _disposed;
    public bool Automatic { get; private set; } = true;
    public bool IsBusy { get; private set; }
    public bool CanCheck => !IsBusy;
    public bool CanInstall => !IsBusy && Available is not null && _package is not null && SupportsInstall;
    public bool SupportsInstall => AppPaths.IsPortableBuild && string.Equals(Path.GetFileName(Environment.ProcessPath), "Sticky.exe", StringComparison.OrdinalIgnoreCase);
    public UpdateRelease? Available { get; private set; }
    public string Status { get; private set; } = "等待检查更新";
    public string Details { get; private set; } = "自动检查正式版本；校验下载后，在保存便签并退出时更新。";
    public string ReleaseNotes => Available?.Notes ?? "";
    public string CurrentVersion => "Sticky " + UpdateRelease.CurrentVersion;
    public event PropertyChangedEventHandler? PropertyChanged;

    public UpdateCoordinator(StickyWorkspace workspace, UpdateClient? client = null)
    {
        _workspace = workspace;
        _client = client ?? new UpdateClient();
        _settingsPath = Path.Combine(workspace.Store.Root, "device", "update-settings.json");
        _cache = Path.Combine(workspace.Store.Root, "device", "updates");
        try
        {
            if (File.Exists(_settingsPath)) Automatic = JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(_settingsPath))?.Automatic ?? true;
            var result = Path.Combine(workspace.Store.Root, "device", "update-result.json");
            if (File.Exists(result))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(result));
                Status = doc.RootElement.GetProperty("success").GetBoolean() ? "上次更新成功" : "上次更新未完成";
                Details = doc.RootElement.GetProperty("message").GetString() ?? "";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException)
        { Status = "更新设置无法读取"; Details = ex.Message; Automatic = false; }
        _timer.Tick += async (_, _) => { if (Automatic && !IsBusy && DateTimeOffset.UtcNow >= _nextCheck) await CheckAsync(); };
    }
    public void Start() { if (!_disposed) _timer.Start(); }
    public void SetAutomatic(bool enabled)
    {
        WorkspaceFiles.WriteAtomic(_workspace.Store.Root, _settingsPath, JsonSerializer.SerializeToUtf8Bytes(new UpdatePreferences { Automatic = enabled }));
        Automatic = enabled;
        if (!enabled) _active?.Cancel();
        _nextCheck = DateTimeOffset.UtcNow.AddSeconds(1);
        Notify();
    }
    public async Task CheckAsync(bool interactive = false)
    {
        if (_disposed || IsBusy) return;
        IsBusy = true;
        _active = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var token = _active.Token;
        Status = "正在检查更新…"; Notify();
        try
        {
            var release = await _client.CheckAsync(UpdateRelease.CurrentVersion, token, interactive);
            if (release is null)
            {
                Available = null; _package = null;
                Status = "当前没有更新的正式版本";
                Details = "仅检查官方 GitHub Releases；尚未发布正式版时也会显示此状态。";
                return;
            }
            if (_package is not null && Available == release && File.Exists(_package))
            {
                Status = "新版本 " + release.Version + " 已就绪";
                Details = "更新包已下载并校验，可保存并重启更新。";
                return;
            }
            Available = release; _package = null;
            if (!SupportsInstall)
            {
                Status = "发现新版本 " + release.Version;
                Details = "当前为源码运行，请使用 Windows 便携版体验自动更新。";
                return;
            }
            var directory = Path.Combine(_cache, release.Version + "-" + Guid.NewGuid().ToString("N"));
            var package = Path.Combine(directory, release.PackageName);
            Status = "正在下载 " + release.Version; Notify();
            await _client.DownloadAsync(release, package, new Progress<double>(percent =>
            {
                if (!_disposed && Status.StartsWith("正在下载", StringComparison.Ordinal)) { Details = $"已下载 {percent:0}% · 本地编辑和自动保存仍然可用。"; Notify(); }
            }), token);
            await Task.Run(() =>
            {
                var verified = Path.Combine(directory, "verified");
                UpdatePackage.Extract(package, verified, release.Version);
                Directory.Delete(verified, true);
            }, token);
            token.ThrowIfCancellationRequested();
            _package = package;
            Status = "新版本 " + release.Version + " 已就绪";
            Details = Automatic ? "下次从托盘或设置退出时自动安装并重新启动；也可点击「保存并重启更新」。" : "点击「保存并重启更新」安装；关闭自动更新时不会在退出时安装。";
        }
        catch (OperationCanceledException) { Status = "更新已取消或超时"; Details = "当前版本与便签数据保留，可稍后重试。"; }
        catch (Exception ex)
        {
            Status = "暂时无法更新";
            Details = ex.Message + " 本地记录不受影响；网络恢复后会重试。";
        }
        finally
        {
            IsBusy = false; _active.Dispose(); _active = null;
            if (_disposed) _client.Dispose();
            _nextCheck = DateTimeOffset.UtcNow.AddHours(6);
            if (Status == "暂时无法更新") _nextCheck = DateTimeOffset.UtcNow.AddMinutes(15);
            Notify();
        }
    }
    public bool ScheduleInstallOnExit(bool requested)
    {
        if (!requested && !Automatic) return true;
        if (!CanInstall)
        {
            if (requested) throw new InvalidOperationException("更新包尚未准备好。");
            return true;
        }
        using var process = Process.GetCurrentProcess();
        UpdateInstaller.Start(new InstallPlan(AppContext.BaseDirectory, _workspace.Store.Root, _package!, Available!.Sha256,
            Available.Version, process.Id, process.StartTime.ToUniversalTime().Ticks, false));
        _package = null;
        return true;
    }
    private void Notify() { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop(); _active?.Cancel();
        // HttpClient is disposed after the active operation observes cancellation.
        if (!IsBusy) _client.Dispose();
    }
}
