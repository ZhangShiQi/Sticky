using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Sticky.App.Updates;

public sealed record InstallPlan(string TargetDirectory, string DataDirectory, string PackagePath, string Sha256,
    string Version, int ParentProcessId, long ParentStartTime, bool WasStartup);

public static class UpdateInstaller
{
    public static void Start(InstallPlan plan)
    {
        UpdatePackage.ValidateTarget(plan.TargetDirectory, plan.DataDirectory);
        var helperDirectory = Path.Combine(Path.GetDirectoryName(plan.PackagePath)!, "helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperDirectory);
        var currentExe = Environment.ProcessPath ?? throw new IOException("无法定位当前程序。");
        var helper = Path.Combine(helperDirectory, "Sticky.exe");
        File.Copy(currentExe, helper);
        var planPath = Path.Combine(helperDirectory, "install.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
        Process.Start(new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = helperDirectory, ArgumentList = { "--apply-update", planPath } })
            ?.Dispose();
    }

    // Runs in a temporary copy of the self-contained exe, before creating any app windows.
    public static int Run(string planPath)
    {
        InstallPlan? plan = null;
        var result = "";
        try
        {
            if (Path.GetFullPath(planPath) != Path.Combine(AppContext.BaseDirectory, "install.json")) throw new IOException("更新计划不在辅助程序目录中。");
            plan = JsonSerializer.Deserialize<InstallPlan>(File.ReadAllText(planPath)) ?? throw new InvalidDataException("更新计划为空。");
            if (UpdatePackage.IsWithin(AppContext.BaseDirectory, plan.TargetDirectory)
                && UpdatePackage.ManagedEntries.Any(e => UpdatePackage.IsWithin(AppContext.BaseDirectory, Path.Combine(plan.TargetDirectory, e))))
                throw new IOException("更新辅助程序不能位于将要替换的目录。");
            try
            {
                using var parent = Process.GetProcessById(plan.ParentProcessId);
                if (parent.StartTime.ToUniversalTime().Ticks == plan.ParentStartTime && !parent.WaitForExit(120000))
                    throw new IOException("等待 Sticky 退出超时，未替换程序。");
            }
            catch (ArgumentException) { /* Parent has already exited. */ }
            using (var instance = new SingleInstanceHost())
            {
                if (!instance.IsPrimary) throw new IOException("另一个 Sticky 实例正在运行，未替换程序。");
                Apply(plan);
            }
            result = "已更新到 " + plan.Version + "；旧程序备份保留在程序目录的 .sticky-update-backup-* 中。";
            WriteResult(plan, true, result);
            Restart(plan);
            return 0;
        }
        catch (Exception ex)
        {
            result = "更新未完成：" + ex.Message;
            if (plan is not null)
            {
                WriteResult(plan, false, result);
                try { Restart(plan); } catch (Exception restartError) { result += "\n无法自动重启：" + restartError.Message; }
            }
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "update-error.log"), result); } catch (IOException) { }
            return 1;
        }
    }

    public static string Apply(InstallPlan plan, Action<string>? beforeInstallEntry = null)
    {
        UpdatePackage.ValidateTarget(plan.TargetDirectory, plan.DataDirectory);
        UpdatePackage.VerifyHashAsync(plan.PackagePath, plan.Sha256, CancellationToken.None).GetAwaiter().GetResult();
        // Stage on the installation volume; the data directory may live on another drive.
        var stage = Path.Combine(plan.TargetDirectory, ".sticky-update-stage-" + Guid.NewGuid().ToString("N"));
        UpdatePackage.RejectReparseAncestors(stage);
        UpdatePackage.Extract(plan.PackagePath, stage, plan.Version);
        var target = Path.GetFullPath(plan.TargetDirectory);
        var backup = Path.Combine(target, ".sticky-update-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup); // Fails before changing program files if the directory is read-only.
        var moved = new List<string>();
        var installed = new List<string>();
        try
        {
            foreach (var entry in UpdatePackage.ManagedEntries)
            {
                var source = Path.Combine(stage, entry);
                if (!File.Exists(source) && !Directory.Exists(source)) continue;
                beforeInstallEntry?.Invoke(entry);
                var destination = Path.Combine(target, entry);
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    Move(destination, Path.Combine(backup, entry));
                    moved.Add(entry);
                }
                Move(source, destination);
                installed.Add(entry);
            }
            return backup;
        }
        catch (Exception installError)
        {
            var failures = new List<Exception>();
            foreach (var entry in installed.AsEnumerable().Reverse())
            {
                try { Move(Path.Combine(target, entry), Path.Combine(stage, entry)); }
                catch (Exception ex) { failures.Add(ex); }
            }
            foreach (var entry in moved.AsEnumerable().Reverse())
            {
                try { Move(Path.Combine(backup, entry), Path.Combine(target, entry)); }
                catch (Exception ex) { failures.Add(ex); }
            }
            if (failures.Count > 0) throw new IOException("安装及回滚受阻。请退出 Sticky，从 " + backup + " 恢复程序文件；Data 未被修改。", new AggregateException(failures.Prepend(installError)));
            throw new IOException("已恢复原程序，便签数据未被修改。" + installError.Message, installError);
        }
    }
    private static void Move(string source, string destination)
    {
        Sticky.UpdateEngine.DurableFiles.RetryIO(() =>
        {
            if (Directory.Exists(source)) Directory.Move(source, destination);
            else File.Move(source, destination);
        });
    }
    private static void Restart(InstallPlan plan)
    {
        var info = new ProcessStartInfo(Path.Combine(plan.TargetDirectory, "Sticky.exe"))
        { UseShellExecute = false, WorkingDirectory = plan.TargetDirectory, ArgumentList = { "--data-dir", plan.DataDirectory } };
        if (plan.WasStartup) info.ArgumentList.Add("--startup");
        Process.Start(info)?.Dispose();
    }
    private static void WriteResult(InstallPlan plan, bool success, string message)
    {
        try
        {
            var path = Path.Combine(plan.DataDirectory, "device", "update-result.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new { success, message, time = DateTimeOffset.UtcNow }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
