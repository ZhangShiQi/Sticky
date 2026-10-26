using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Sticky.App.Updates;

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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!StartWorkspaceOnStartup) return;
        if (e.Args.Length == 2 && e.Args[0] == "--apply-update")
        {
            Shutdown(UpdateInstaller.Run(e.Args[1]));
            return;
        }
        try
        {
            var startup = e.Args.Contains("--startup", StringComparer.Ordinal);
            _instance = new SingleInstanceHost();
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
            _workspace = new StickyWorkspace(root);
            var main = new MainWindow(_workspace);
            MainWindow = main;
            CreateTray(_workspace);
            if (!startup) main.Show();
            _workspace.RestoreWindows();
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
            MessageBox.Show("无法启动 Sticky：\n" + ex.Message + "\n\n诊断日志：" + log, "Sticky", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _trayIcon?.Dispose();
        _workspace?.Sync.Dispose();
        _workspace?.Updates.Dispose();
        if (_ownsWorkspaceMutex) _workspaceMutex?.ReleaseMutex();
        _workspaceMutex?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void CreateTray(StickyWorkspace workspace)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        void Add(string label, Action action) => menu.Items.Add(label, null, (_, _) => Dispatcher.BeginInvoke(action));
        Add("便签管理", workspace.ShowManager);
        Add("搜索便签", workspace.ShowSearch);
        Add("新建便签", () => workspace.CreateNote());
        Add("设置…", workspace.ShowSettings);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        Add("退出 Sticky", () => { if (workspace.PrepareExit()) Shutdown(); });
        using (var stream = GetResourceStream(new Uri("pack://application:,,,/Sticky;component/Assets/Sticky.ico")).Stream)
        using (var icon = new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize))
            _trayIcon = (System.Drawing.Icon)icon.Clone();
        _tray = new System.Windows.Forms.NotifyIcon { Text = "Sticky · 本地便签", Icon = _trayIcon,
            ContextMenuStrip = menu, Visible = true };
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
