using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace McResourceManager.Core.FileSystem;

public sealed record ArchiveMetadata(string Description, byte[]? CoverPng);

public static class ArchiveInspector
{
    private const int CoverLimit = 16 * 1024 * 1024;
    private const int MetadataLimit = 1024 * 1024;

    public static async Task<ArchiveMetadata> TryReadAsync(string path, CancellationToken ct = default)
    {
        string description = "";
        byte[]? cover = null;
        try
        {
            ResourceFiles.AssertNoLinks(path);
            if (Directory.Exists(path))
            {
                var png = ResourceFiles.Child(path, "pack.png");
                var metadata = ResourceFiles.Child(path, "pack.mcmeta");
                if (File.Exists(png) && new FileInfo(png).Length <= CoverLimit) cover = await File.ReadAllBytesAsync(png, ct);
                if (File.Exists(metadata) && new FileInfo(metadata).Length <= MetadataLimit)
                    description = Description(await File.ReadAllTextAsync(metadata, ct));
            }
            else if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var archive = ZipFile.OpenRead(path);
                var png = archive.GetEntry("pack.png");
                var metadata = archive.GetEntry("pack.mcmeta");
                if (png is not null) cover = await ReadBoundedAsync(png, CoverLimit, ct);
                if (metadata is not null)
                {
                    var bytes = await ReadBoundedAsync(metadata, MetadataLimit, ct);
                    if (bytes is not null) description = Description(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { /* 元信息只是展示补充，解析失败不影响资源入库。 */ }
        if (cover is not null && (cover.Length < 8 || !cover.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))) cover = null;
        return new(description, cover);
    }

    private static async Task<byte[]?> ReadBoundedAsync(ZipArchiveEntry entry, int limit, CancellationToken ct)
    {
        if (entry.Length > limit) return null;
        await using var source = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > limit) return null;
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string Description(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("pack", out var pack) || !pack.TryGetProperty("description", out var text)) return "";
        string Flatten(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Array => string.Concat(element.EnumerateArray().Select(Flatten)),
            JsonValueKind.Object => (element.TryGetProperty("text", out var value) ? Flatten(value) : "")
                + (element.TryGetProperty("extra", out var extra) ? Flatten(extra) : ""),
            _ => ""
        };
        var description = Flatten(text);
        return description.Length <= 4096 ? description : description[..4096];
    }
}
