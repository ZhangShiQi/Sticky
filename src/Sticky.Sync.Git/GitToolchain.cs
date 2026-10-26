namespace Sticky.Sync.Git;

public sealed record GitToolchain(string GitExecutable, string CredentialHelper, string[] PathEntries)
{
    public static GitToolchain Resolve(string applicationDirectory)
    {
        var root = Path.GetFullPath(applicationDirectory);
        var gitRoot = Path.Combine(root, "tools", "git");
        var bundledGit = Path.Combine(gitRoot, "cmd", "git.exe");
        if (!File.Exists(bundledGit)) return new GitToolchain("git", "manager", []);
        var gcmRoot = Path.Combine(root, "tools", "gcm");
        var gcm = Path.Combine(gcmRoot, "git-credential-manager.exe");
        // Git invokes helpers through its shell. Quote a full path so spaces/apostrophes in portable folders are safe.
        var helper = File.Exists(gcm) ? "!'" + gcm.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal) + "'" : "manager";
        var paths = new[] { gcmRoot, Path.Combine(gitRoot, "cmd"), Path.Combine(gitRoot, "mingw64", "bin"), Path.Combine(gitRoot, "ucrt64", "bin"), Path.Combine(gitRoot, "clang64", "bin"), Path.Combine(gitRoot, "usr", "bin") }
            .Where(Directory.Exists).ToArray();
        return new GitToolchain(bundledGit, helper, paths);
    }
}
