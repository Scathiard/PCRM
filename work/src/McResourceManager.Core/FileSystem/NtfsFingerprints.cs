using System.Security.Cryptography;
using McResourceManager.Core.Domain;

namespace McResourceManager.Core.FileSystem;

public static partial class NtfsDriver
{
    public static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken ct = default)
    {
        ResourceFiles.AssertNoLinks(filePath);
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 131072, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    public static async Task<IReadOnlyList<FileEntryFingerprint>> BuildEntryFingerprintsAsync(string entryPath, CancellationToken ct = default)
    {
        var fullPath = ResourceFiles.Full(entryPath);
        var directory = Directory.Exists(fullPath);
        var result = new List<FileEntryFingerprint>();
        foreach (var file in ResourceFiles.Files(fullPath))
        {
            ct.ThrowIfCancellationRequested();
            var stat = GetFastStat(file);
            var hash = await ComputeFileSha256Async(file, ct);
            var after = GetFastStat(file);
            if (stat != after) throw new IOException($"文件在扫描期间发生变化，请重试：{file}");
            result.Add(new(directory ? Path.GetRelativePath(fullPath, file).Replace('\\', '/') : "",
                stat.Size, stat.LastWriteTimeUtcTicks, hash, stat.FileId, new(StringComparer.OrdinalIgnoreCase)));
        }
        return result;
    }
}
