namespace McResourceManager.Core.Engine;

public static class RuntimePaths
{
    public static string DefaultConfigPath() => FindConfigPath(AppContext.BaseDirectory);

    internal static string FindConfigPath(string appDirectory)
    {
        var appPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory));
        for (var directory = appPath; directory is not null; directory = Path.GetDirectoryName(directory))
            if (IsWorkspaceBuild(appPath, directory)
                && File.Exists(Path.Combine(directory, "work/src/McResourceManager.Desktop/McResourceManager.Desktop.csproj"))
                && File.Exists(Path.Combine(directory, "work/scripts/native.ps1")))
                return Path.Combine(directory, "work/data/machine-config.json");
        return Path.Combine(appPath, "work/data/machine-config.json");
    }

    private static bool IsWorkspaceBuild(string appPath, string workspace) =>
        appPath.StartsWith(Path.Combine(workspace, "work", "cache") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || appPath.StartsWith(Path.Combine(workspace, "releases", "dist") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
