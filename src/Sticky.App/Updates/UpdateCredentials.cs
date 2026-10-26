using System.Security.Cryptography;
using System.Text;
using Sticky.Sync.Git;

namespace Sticky.App.Updates;

public static class UpdateCredentials
{
    public static async Task<string?> ReadAsync(bool interactive, CancellationToken token)
    {
        // Git for Windows commonly stores an existing login for the host, not a single repository.
        foreach (var useHttpPath in new[] { "true", "false" })
        {
            var saved = await FillAsync(useHttpPath, false, token);
            if (!string.IsNullOrWhiteSpace(saved)) return saved;
        }
        return interactive ? await FillAsync("true", true, token) : null;
    }

    private static async Task<string?> FillAsync(string useHttpPath, bool interactive, CancellationToken token)
    {
        // Credential output stays in memory and must never be included in status/log messages.
        var output = await new GitProcess().RunAsync(AppContext.BaseDirectory, ["-c", "credential.useHttpPath=" + useHttpPath, "credential", "fill"], token,
            Encoding.UTF8.GetBytes($"protocol=https\nhost=github.com\npath={UpdateRelease.Repository}.git\n\n"),
            interactive: interactive, maxBytes: 32 * 1024);
        try
        {
            if (output.ExitCode != 0) return null;
            return Encoding.UTF8.GetString(output.Bytes).Split('\n')
                .FirstOrDefault(line => line.StartsWith("password=", StringComparison.Ordinal))?[9..].TrimEnd('\r');
        }
        finally { CryptographicOperations.ZeroMemory(output.Bytes); }
    }
}
