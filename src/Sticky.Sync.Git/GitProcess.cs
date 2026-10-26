using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Sticky.Sync.Git;

public sealed record GitOutput(int ExitCode, byte[] Bytes, string Error)
{
    public string Text => Encoding.UTF8.GetString(Bytes).Trim();
}

public sealed class GitProcess
{
    private readonly GitToolchain _toolchain;
    public GitProcess(string? applicationDirectory = null) => _toolchain = GitToolchain.Resolve(applicationDirectory ?? AppContext.BaseDirectory);

    public async Task<GitOutput> RunAsync(string directory, IEnumerable<string> arguments, CancellationToken cancellationToken,
        byte[]? input = null, bool interactive = false, Dictionary<string, string>? environment = null, int maxBytes = 300 * 1024 * 1024)
    {
        var start = new ProcessStartInfo(_toolchain.GitExecutable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var arg in new[] { "-c", "core.hooksPath=NUL", "-c", "gc.auto=0", "-c", "maintenance.auto=false", "-c", "credential.helper=", "-c", "credential.helper=" + _toolchain.CredentialHelper,
            "-c", "credential.useHttpPath=true", "-c", "http.lowSpeedLimit=1024", "-c", "http.lowSpeedTime=20" }.Concat(arguments)) start.ArgumentList.Add(arg);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = interactive ? "always" : "never";
        start.Environment["GIT_AUTHOR_NAME"] = "Sticky";
        start.Environment["GIT_AUTHOR_EMAIL"] = "sticky@local.invalid";
        start.Environment["GIT_COMMITTER_NAME"] = "Sticky";
        start.Environment["GIT_COMMITTER_EMAIL"] = "sticky@local.invalid";
        // Do not let parent-shell Git variables redirect operations into another repository/index.
        foreach (var key in new[] { "GIT_DIR", "GIT_COMMON_DIR", "GIT_EXEC_PATH", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES" }) start.Environment.Remove(key);
        if (_toolchain.PathEntries.Length > 0)
            start.Environment["PATH"] = string.Join(Path.PathSeparator, _toolchain.PathEntries) + Path.PathSeparator + start.Environment["PATH"];
        if (environment is not null) foreach (var (key, value) in environment) start.Environment[key] = value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(interactive ? 180 : 60));
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new IOException("无法启动 Git。"); }
        catch (System.ComponentModel.Win32Exception ex) { throw new IOException("没有找到 Git，请安装 Git for Windows（包含 Git Credential Manager）。", ex); }
        using var registration = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, maxBytes, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError.BaseStream, 64 * 1024, timeout.Token);
        foreach (var reader in new[] { stdout, stderr })
            _ = reader.ContinueWith(_ => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var write = Task.Run(async () =>
        {
            try { if (input is not null) await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token); }
            finally { process.StandardInput.Close(); }
        }, timeout.Token);
        try
        {
            await Task.WhenAll(stdout, stderr, write, process.WaitForExitAsync(timeout.Token));
            return new GitOutput(process.ExitCode, await stdout, Redact(Encoding.UTF8.GetString(await stderr)));
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            cancellationToken.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) throw new IOException("Git 连接超时；本地内容已保留，稍后可以重试。");
            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int count;
        while ((count = await stream.ReadAsync(chunk, token)) != 0)
        {
            if (buffer.Length + count > limit) throw new InvalidDataException("Git 数据超出同步大小限制。");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }
    public static string Redact(string message)
    {
        message = Regex.Replace(message, @"(?:github_pat_|gh[pousr]_)[A-Za-z0-9_]+", "[凭据已隐藏]");
        message = Regex.Replace(message, @"https://[^\s/@]+:[^\s/@]+@", "https://[凭据已隐藏]@");
        return message.Length > 2000 ? message[..2000] : message.Trim();
    }
}
