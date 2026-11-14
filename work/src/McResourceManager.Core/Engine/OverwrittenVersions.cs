using McResourceManager.Core.Domain;
using McResourceManager.Core.FileSystem;
using McResourceManager.Core.Storage;

namespace McResourceManager.Core.Engine;

public sealed partial class ResourceManagerEngine
{
    private static ResourceVersion PreferredLatest(IEnumerable<ResourceVersion> versions) =>
        versions.OrderBy(v => v.MasterEntryOverwritten).ThenByDescending(v => v.CreatedAtUtcTicks).First();

    private static MasterVaultIndex MarkOverwritten(MasterVaultIndex index, HashSet<string> versionIds)
    {
        if (versionIds.Count == 0) return index;
        var resources = index.Resources.Select(resource =>
        {
            if (!resource.Versions.Any(v => versionIds.Contains(v.VersionId))) return resource;
            var versions = resource.Versions.Select(v => versionIds.Contains(v.VersionId)
                ? v with { MasterEntryOverwritten = true } : v).ToArray();
            return resource with { Versions = versions, LatestVersionId = versionIds.Contains(resource.LatestVersionId)
                ? PreferredLatest(versions).VersionId : resource.LatestVersionId };
        }).ToArray();
        return index with { Resources = resources };
    }

    private static async Task<MasterVaultIndex> LoadVaultIndexAsync(string root, CancellationToken ct)
    {
        var index = await VaultStorage.LoadMasterIndexAsync(root, ct);
        foreach (var type in index.ResourceTypes) ValidateType(type);
        // Earlier builds could record multiple current fingerprints for one master path.
        // Verify only these paths. Preserve every version ID, including those used by detached copies.
        var folders = index.ResourceTypes.ToDictionary(t => t.TypeId, t => t.MasterFolderName);
        var collisions = index.Resources.SelectMany(r => r.Versions.Where(v => !v.MasterEntryOverwritten)
                .Select(v => (Path: Path.Combine(folders[r.TypeId], v.MasterEntryName), Version: v)))
            .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1);
        var overwritten = new HashSet<string>();
        foreach (var group in collisions)
        {
            ct.ThrowIfCancellationRequested();
            var path = ResourceFiles.Child(root, group.Key);
            var versions = group.Select(entry => entry.Version).OrderByDescending(v => v.CreatedAtUtcTicks).ToArray();
            ResourceVersion? owner = null;
            if (ResourceFiles.Exists(path))
            {
                var actual = await NtfsDriver.BuildEntryFingerprintsAsync(path, ct);
                owner = versions.FirstOrDefault(v => v.IsDirectory == Directory.Exists(path) && ResourceFiles.ContentEquals(actual, v.Files));
            }
            foreach (var version in versions.Where(v => v.VersionId != owner?.VersionId)) overwritten.Add(version.VersionId);
        }
        if (overwritten.Count == 0) return index;
        index = MarkOverwritten(index, overwritten);
        await VaultStorage.SaveMasterIndexAsync(root, index, ct);
        return index;
    }
}
