using System.IO;
using System.Reflection;

namespace Sticky.App;

public static class AppPaths
{
    public static bool IsPortableBuild => typeof(AppPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(a => a.Key == "StickyPortable" && a.Value == "true");

    public static string ResolveDataDirectory(string[] args) => ResolveDataDirectory(args, AppContext.BaseDirectory, IsPortableBuild);

    public static string ResolveDataDirectory(string[] args, string applicationDirectory, bool portableBuild)
    {
        var dataIndex = Array.IndexOf(args, "--data-dir");
        if (dataIndex >= 0)
        {
            if (dataIndex + 1 >= args.Length || string.IsNullOrWhiteSpace(args[dataIndex + 1]) || args[dataIndex + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("--data-dir 后需要指定数据目录。");
            return Path.GetFullPath(args[dataIndex + 1]);
        }
        if (portableBuild || args.Contains("--portable") || File.Exists(Path.Combine(applicationDirectory, "portable.flag")))
            return Path.GetFullPath(Path.Combine(applicationDirectory, "Data"));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sticky", "Data");
    }
}
