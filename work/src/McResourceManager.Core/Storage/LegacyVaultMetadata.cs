using McResourceManager.Core.FileSystem;

namespace McResourceManager.Core.Storage;

public static partial class VaultStorage
{
    private const string LegacyMetaFolderName = ".mcrm";

    private static async Task ImportLegacyMetadataAsync(string root, CancellationToken ct)
    {
        var legacy = ResourceFiles.Child(root, LegacyMetaFolderName);
        if (!Directory.Exists(legacy)) return;
        var target = ResourceFiles.Child(root, MasterMetaFolderName);
        if (ResourceFiles.Exists(target))
            throw new IOException($"旧账本仍在 {legacy}，但 {target} 已存在且没有可用账本。请先核对该目录。");
        var indexPath = ResourceFiles.Child(legacy, MasterIndexFileName);
        if (!File.Exists(indexPath)) throw new IOException($"旧主库缺少账本，请检查 {indexPath}。");

        // 先完整复制元数据，在同一目录内一次改名发布。原账本、封面和资源文件均可原样使用。
        var stage = ResourceFiles.Child(root, $".pcrm-upgrade-{Guid.NewGuid():N}");
        try
        {
            await ResourceFiles.CopyAsync(legacy, stage, ct);
            var stagedIndex = ResourceFiles.Child(stage, MasterIndexFileName);
            var index = await ReadIndexAsync(stagedIndex, ct);
            var resources = index.Resources.Select(resource =>
            {
                var cover = resource.CustomCoverRelativePath?.Replace('\\', '/');
                return cover?.StartsWith(LegacyMetaFolderName + "/", StringComparison.OrdinalIgnoreCase) == true
                    ? resource with { CustomCoverRelativePath = MasterMetaFolderName + cover[LegacyMetaFolderName.Length..] }
                    : resource;
            }).ToArray();
            await WriteJsonAtomicallyAsync(stagedIndex, index with { Resources = resources }, ct);
            ct.ThrowIfCancellationRequested();
            Directory.Move(stage, target);
        }
        finally { if (Directory.Exists(stage)) ResourceFiles.Delete(stage); }
    }
}
