using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace Sticky.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private StickyWorkspace? _workspace;
    private System.Windows.Forms.NotifyIcon? _tray;
    public bool StartWorkspaceOnStartup { get; set; } = true;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!StartWorkspaceOnStartup) return;
        try
        {
            var root = AppPaths.ResolveDataDirectory(e.Args);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));
            _mutex = new Mutex(true, @"Local\Sticky-" + key, out var firstInstance);
            if (!firstInstance)
            {
                MessageBox.Show("这个工作空间的 Sticky 已在运行，请从任务栏托盘图标打开便签管理。", "Sticky");
                Shutdown();
                return;
            }
            _workspace = new StickyWorkspace(root);
            var main = new MainWindow(_workspace);
            MainWindow = main;
            CreateTray(_workspace);
            main.Show();
            _workspace.RestoreWindows();
            _workspace.Sync.Start();
            if (_workspace.Notes.Count == 0 && _workspace.Store.Warnings.Count == 0) _workspace.CreateNote();
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
        _workspace?.Sync.Dispose();
        _mutex?.Dispose();
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
        _tray = new System.Windows.Forms.NotifyIcon { Text = "Sticky · 本地便签", Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(workspace.ShowManager);
    }
}
