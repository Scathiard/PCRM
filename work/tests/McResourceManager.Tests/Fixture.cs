using System.IO.Compression;
using McResourceManager.Core.Engine;
using McResourceManager.Core.Domain;

namespace McResourceManager.Tests;

internal sealed class Fixture : IDisposable
{
    public string Root { get; }
    public string Master => Path.Combine(Root, "library");
    public string Source => Path.Combine(Root, "sources");
    public string Config => Path.Combine(Root, "machine-config.json");
    public ResourceManagerEngine Engine { get; }
    public Fixture(string testRoot)
    {
        Root = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Source);
        Engine = new(Config);
    }
    public async Task<GameInstance> Instance(string name)
    {
        var path = Path.Combine(Root, "instances", name);
        Directory.CreateDirectory(path);
        return (await Engine.RegisterInstancesAsync([(name, path)])).Single();
    }
    public string Zip(string name, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(Source, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entryName).Open());
            writer.Write(content);
        }
        return path;
    }
    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
    public async Task<LogicalResource> Import(string path, string type, string? resourceId = null, string label = "1.0")
    {
        await Engine.CommitVaultIntakeAsync([new(path, true, type, resourceId, "通用测试资源", label, [])]);
        return (await Engine.GetSnapshotAsync()).MasterIndex!.Resources.Last();
    }
    public void Dispose()
    {
        var root = Path.GetFullPath(Root);
        if (!root.StartsWith(Path.GetFullPath(Program.TestRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝清理测试根以外的目录。");
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("测试夹具包含重解析点，保留供检查。");
        Directory.Delete(root, true);
    }
}
