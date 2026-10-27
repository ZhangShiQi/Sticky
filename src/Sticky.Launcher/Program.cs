using System.Runtime.InteropServices;
using Sticky.UpdateEngine;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        StartupTiming.Mark("launcher-entry");
        var root = AppContext.BaseDirectory;
        var diagnostic = false;
        try
        {
            var index = Array.IndexOf(args, "--data-dir");
            if (index >= 0 && (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))) throw new ArgumentException("缺少数据目录。");
            var data = index >= 0 ? Path.GetFullPath(args[index + 1]) : Path.Combine(root, "Data");
            var testIndex = Array.IndexOf(args, "--test-instance-key");
            string? testKey = null;
            if (testIndex >= 0)
            {
                if (testIndex + 1 >= args.Length || index < 0) throw new ArgumentException("隔离测试需要显式的数据目录和实例编号。");
                testKey = args[testIndex + 1];
                if (!testKey.StartsWith("Sticky.Tests.", StringComparison.Ordinal) || !File.Exists(Path.Combine(data, ".sticky-update-test"))
                    || File.ReadAllText(Path.Combine(data, ".sticky-update-test")) != testKey) throw new IOException("只能在标记的测试工作空间使用隔离实例。");
                diagnostic = true;
            }
            if (args.Contains("--finish-update")) LauncherEngine.WaitForQueuedParent(root);
            return new LauncherEngine(root, new LauncherOptions { InstanceKey = testKey }).Run(data, args.Contains("--startup"));
        }
        catch (Exception ex)
        {
            try { DurableFiles.Write(Path.Combine(root, ".sticky-launcher", "launcher-error.json"), new { error = ex.ToString(), time = DateTimeOffset.UtcNow }); }
            catch (Exception log) when (log is IOException or UnauthorizedAccessException) { }
            if (!diagnostic) MessageBoxW(0, "Sticky 启动未完成。已保留便签数据、旧版和更新恢复记录。\n\n" + ex.Message, "Sticky", 0x10);
            return 1;
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(nint window, string text, string title, uint type);
}
