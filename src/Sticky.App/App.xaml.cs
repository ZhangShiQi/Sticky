using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Sticky.App.Updates;
using Sticky.UpdateEngine;

namespace Sticky.App;

public partial class App : Application
{
    private SingleInstanceHost? _instance;
    private Mutex? _workspaceMutex;
    private bool _ownsWorkspaceMutex;
    private StickyWorkspace? _workspace;
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    public bool StartWorkspaceOnStartup { get; set; } = true;

    protected override async void OnStartup(StartupEventArgs e)
    {
        StartupTiming.Mark("runtime-entry");
        base.OnStartup(e);
        if (!StartWorkspaceOnStartup) return;
        if (e.Args.Length == 2 && e.Args[0] == "--apply-update")
        {
            Shutdown(UpdateInstaller.Run(e.Args[1]));
            return;
        }
        StartupHandshake? handshake = null;
        var managedStartup = e.Args.Contains("--startup-pipe", StringComparer.Ordinal);
        using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            if (LauncherContext.ConfigureOrRedirect(e.Args)) { Shutdown(); return; }
            if (managedStartup)
            {
                if (LauncherContext.Root is null) throw new IOException("启动确认缺少稳定启动器。");
                handshake = await StartupHandshake.ConnectAsync(LauncherContext.Argument(e.Args, "--startup-pipe")!,
                    LauncherContext.Argument(e.Args, "--startup-nonce") ?? throw new IOException("缺少启动随机码。"), startupTimeout.Token);
                StartupTiming.Mark("identity-authorized");
            }
            var startup = e.Args.Contains("--startup", StringComparer.Ordinal);
            var testKey = LauncherContext.Argument(e.Args, "--test-instance-key");
            if (testKey is not null)
            {
                var testData = AppPaths.ResolveDataDirectory(e.Args);
                if (!testKey.StartsWith("Sticky.Tests.", StringComparison.Ordinal)
                    || !File.Exists(Path.Combine(testData, ".sticky-update-test"))
                    || File.ReadAllText(Path.Combine(testData, ".sticky-update-test")) != testKey)
                    throw new IOException("隔离测试参数只能用于明确标记的测试数据目录。");
            }
            _instance = new SingleInstanceHost(testKey);
            if (!_instance.IsPrimary)
            {
                if (!startup) _instance.NotifyExistingAsync().GetAwaiter().GetResult();
                Shutdown(); return;
            }
            _instance.StartListening(() => Dispatcher.BeginInvoke(() =>
            {
                if (_workspace is { IsExiting: false }) _workspace.ShowManager();
            }));
            var root = AppPaths.ResolveDataDirectory(e.Args);
            // Keep the old per-workspace lock as protection when upgrading from a running older build.
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));
            _workspaceMutex = new Mutex(true, @"Local\Sticky-" + key, out _ownsWorkspaceMutex);
            if (!_ownsWorkspaceMutex)
            {
                if (!startup) MessageBox.Show("此工作空间仍有旧版 Sticky 在运行，请先从托盘退出旧版本。", "Sticky");
                Shutdown();
                return;
            }
            // File parsing/recovery and XAML construction have no shared mutable state.
            var dataTask = Task.Run(() => WorkspaceStartupData.Load(root));
            var main = Sticky.App.MainWindow.CreateLoadingShell();
            MainWindow = main;
            StartupTiming.Mark("main-created");
            var presentation = new StartupPresentation(main, startup);
            await presentation.RenderAsync(startupTimeout.Token);
            var prepared = await dataTask;
            _workspace = new StickyWorkspace(prepared);
            _workspace.StartupPending = managedStartup;
            main.AttachWorkspace(_workspace);
            StartupTiming.Mark("workspace-ready");
            CreateTray(_workspace, visible: !managedStartup);
            await _workspace.RestoreStartupWindowsAsync();
            if (managedStartup)
            {
                foreach (var window in _workspace.Windows.Values) { window.IsEnabled = false; window.Opacity = 0; }
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                StartupTiming.Mark("ui-rendered");
                if (LauncherContext.Root is string healthRoot && File.Exists(new LauncherStore(healthRoot).PendingFile))
                    await Sticky.Capture.CaptureHealth.CheckAsync(startupTimeout.Token);
                await handshake!.ReadyAsync(startupTimeout.Token);
                _workspace.StartupPending = false;
                foreach (var window in _workspace.Windows.Values) { window.IsEnabled = true; window.Opacity = 1; }
                _tray!.Visible = true;
            }
            presentation.Commit();
            Sticky.Capture.CaptureEnvironment.OpenSettings = _workspace.ShowCaptureSettings;
            Sticky.Capture.CaptureEnvironment.SharedTextSettings = () =>
            {
                var selectedId = _workspace.Capture.Preferences.StickyTextConnectionId;
                if (selectedId is null || !_workspace.AiSettings.EditingCatalog().Connections.Any(c => c.Id == selectedId && c.DeletedAt is null))
                    throw new Sticky.Capture.Services.AiException("所选便签 AI 连接已不存在，请在截图设置中重新选择。");
                var configuration = _workspace.AiSettings.Load(_workspace.Capture.Preferences.StickyTextConnectionId);
                if (!configuration.Enabled) throw new Sticky.Capture.Services.AiException("请先在 AI 接入中启用便签 AI 连接。");
                return new Sticky.Capture.Services.AiSettings { Endpoint = configuration.CompletionUri().AbsoluteUri,
                    Model = configuration.Model, ApiKey = configuration.ApiKey };
            };
            _workspace.Capture.Start();
            StartupTiming.Mark("interactive");
            _workspace.Sync.Start();
            _workspace.Updates.Start();
            if (!startup && _workspace.Notes.Count == 0 && _workspace.Store.Warnings.Count == 0) _workspace.CreateNote();
            if (_workspace.Store.Warnings.Count > 0)
                MessageBox.Show(string.Join("\n", _workspace.Store.Warnings), "数据恢复提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            var log = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
            try { File.WriteAllText(log, ex.ToString()); } catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException) { }
            if (!managedStartup) MessageBox.Show("无法启动 Sticky：\n" + ex.Message + "\n\n诊断日志：" + log, "Sticky", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
        finally { if (handshake is not null) await handshake.DisposeAsync(); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _trayIcon?.Dispose();
        _workspace?.Sync.Dispose();
        _workspace?.Updates.Dispose();
        _workspace?.Capture.Dispose();
        Sticky.Capture.CaptureEnvironment.OpenSettings = null;
        Sticky.Capture.CaptureEnvironment.SharedTextSettings = null;
        if (_ownsWorkspaceMutex) _workspaceMutex?.ReleaseMutex();
        _workspaceMutex?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void CreateTray(StickyWorkspace workspace, bool visible = true)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        void Add(string label, Action action) => menu.Items.Add(label, null, (_, _) => Dispatcher.BeginInvoke(action));
        Add("便签管理", workspace.ShowManager);
        Add("搜索便签", workspace.ShowSearch);
        Add("时间轴与重放", () => workspace.ShowHistory());
        Add("新建便签", () => workspace.CreateNote());
        Add("开始截图", () => { _ = workspace.Capture.StartCaptureAsync(); });
        Add("设置…", workspace.ShowSettings);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        Add("退出 Sticky", () => { if (workspace.PrepareExit()) Shutdown(); });
        using (var stream = GetResourceStream(new Uri("pack://application:,,,/Sticky;component/Assets/Sticky.ico")).Stream)
        using (var icon = new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize))
            _trayIcon = (System.Drawing.Icon)icon.Clone();
        _tray = new System.Windows.Forms.NotifyIcon { Text = "Sticky · 本地便签", Icon = _trayIcon,
            ContextMenuStrip = menu, Visible = visible };
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(workspace.ShowManager);
        string? announcedVersion = null;
        workspace.Updates.PropertyChanged += (_, _) =>
        {
            if (!workspace.Updates.CanInstall || workspace.Updates.Available?.Version is not string version || announcedVersion == version) return;
            announcedVersion = version;
            _tray.ShowBalloonTip(6000, "Sticky " + version + " 已就绪", "保存并退出时自动更新，也可在设置中立即重启更新。", System.Windows.Forms.ToolTipIcon.Info);
        };
        _tray.BalloonTipClicked += (_, _) => Dispatcher.BeginInvoke(() => workspace.ShowSettings(SettingsSection.Updates));
    }
}
