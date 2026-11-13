using McResourceManager.Core.Domain;

namespace McResourceManager.Core.FileSystem;

internal static class ResourceFiles
{
    public static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    public static bool Same(string a, string b) => string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);
    public static bool Inside(string root, string path) => Full(path).StartsWith(Full(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string Child(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new ArgumentException("请填写管理目录内的相对路径。");
        var full = Full(Path.Combine(root, relative));
        if (!Inside(root, full)) throw new ArgumentException("路径不能越过管理目录。");
        AssertNoLinks(full);
        return full;
    }

    public static void AssertNoLinks(string path)
    {
        for (var current = Full(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"不支持通过目录联接或符号链接操作资源：{current}");
    }

    public static IReadOnlyList<string> Files(string path)
    {
        AssertNoLinks(path);
        if (File.Exists(path)) return [path];
        if (!Directory.Exists(path)) throw new FileNotFoundException("资源已不存在。", path);
        var files = new List<string>();
        void Visit(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                AssertNoLinks(entry);
                if (Directory.Exists(entry)) Visit(entry); else files.Add(entry);
            }
        }
        Visit(path);
        return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static async Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        AssertNoLinks(source);
        AssertNoLinks(destination);
        if (Same(source, destination) || Inside(source, destination)) throw new IOException("不能把资源复制到自身内部。");
        if (Exists(destination)) throw new IOException($"目标已存在：{destination}");
        if (File.Exists(source)) await CopyFileAsync(source, destination, ct);
        else
        {
            var files = Files(source);
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Child(destination, Path.GetRelativePath(source, directory)));
            foreach (var file in files)
                await CopyFileAsync(file, Child(destination, Path.GetRelativePath(source, file)), ct);
        }
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        var modified = File.GetLastWriteTimeUtc(source);
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
        {
            await input.CopyToAsync(output, ct);
            await output.FlushAsync(ct);
        }
        File.SetLastWriteTimeUtc(destination, modified);
    }

    public static void Move(string source, string destination)
    {
        AssertNoLinks(source);
        AssertNoLinks(destination);
        if (Directory.Exists(source)) Directory.Move(source, destination); else File.Move(source, destination);
    }

    public static void Delete(string path)
    {
        AssertNoLinks(path);
        if (Directory.Exists(path)) { _ = Files(path); Directory.Delete(path, true); }
        else if (File.Exists(path)) File.Delete(path);
    }

    public static bool ContentEquals(IReadOnlyList<FileEntryFingerprint> a, IReadOnlyList<FileEntryFingerprint> b) =>
        a.Count == b.Count && a.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Zip(b.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
            .All(pair => string.Equals(pair.First.RelativePath, pair.Second.RelativePath, StringComparison.OrdinalIgnoreCase)
                && pair.First.Size == pair.Second.Size && pair.First.Sha256 == pair.Second.Sha256);

    public static async Task<bool> MatchesAsync(string path, ResourceVersion version, CancellationToken ct)
    {
        if (!Exists(path) || Directory.Exists(path) != version.IsDirectory) return false;
        var files = Files(path);
        if (files.Count != version.Files.Count) return false;
        foreach (var expected in version.Files)
        {
            ct.ThrowIfCancellationRequested();
            var file = version.IsDirectory ? Child(path, expected.RelativePath) : path;
            if (!File.Exists(file)) return false;
            var stat = NtfsDriver.GetFastStat(file);
            if ((stat.Size != expected.Size || stat.LastWriteTimeUtcTicks != expected.LastWriteTimeUtcTicks)
                && await NtfsDriver.ComputeFileSha256Async(file, ct) != expected.Sha256) return false;
        }
        return true;
    }
}
