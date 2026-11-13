using McResourceManager.Core.Engine;

namespace McResourceManager.Tests;

internal static partial class Program
{
    private static Task RuntimeLocation()
    {
        using var f = new Fixture(TestRoot);
        var workspace = Path.Combine(f.Root, "checkout");
        Fixture.Write(Path.Combine(workspace, "work/src/McResourceManager.Desktop/McResourceManager.Desktop.csproj"), "<Project />");
        Fixture.Write(Path.Combine(workspace, "work/scripts/native.ps1"), "# build");
        var config = Path.Combine(workspace, "work/data/machine-config.json");
        Fixture.Write(config, "existing local data");
        Check(!File.Exists(Path.Combine(workspace, "AGENTS.md")), "The public-checkout test must not depend on internal files.");
        Check(Path.GetFullPath(RuntimePaths.FindConfigPath(Path.Combine(workspace, "releases/dist/native"))) == Path.GetFullPath(config),
            "Published workspace build no longer finds the existing machine configuration.");
        Check(Path.GetFullPath(RuntimePaths.FindConfigPath(Path.Combine(workspace, "work/cache/dotnet/bin/MCRM"))) == Path.GetFullPath(config),
            "Development build stores machine data under the cache directory.");
        var portable = Path.Combine(f.Root, "portable");
        Check(Path.GetFullPath(RuntimePaths.FindConfigPath(portable)) == Path.GetFullPath(Path.Combine(portable, "work/data/machine-config.json")),
            "Standalone distribution does not keep its own local configuration.");
        Check(File.ReadAllText(config) == "existing local data", "Path resolution modified existing configuration.");
        return Task.CompletedTask;
    }
}
