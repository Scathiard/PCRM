using McResourceManager.Core.Domain;
using McResourceManager.Core.FileSystem;
using McResourceManager.Core.Storage;

namespace McResourceManager.Core.Engine;

public sealed partial class ResourceManagerEngine
{
    public Task<MasterVaultIndex> ConfigureMasterVaultAsync(string masterVaultPath, CancellationToken ct = default) => RunAsync(async () =>
    {
        var path = ResourceFiles.Full(masterVaultPath);
        ResourceFiles.AssertNoLinks(path);
        if (_machine.Instances.Any(i => Overlaps(i.RootPath, path))
            || _machine.ReplicaVaultPathsByVolume.Values.Any(cache => Overlaps(cache, path)))
            throw new IOException("主库不能与实例或共享缓存区重叠。");
        _ = ResourceFiles.Child(path, ".mcrm/index.json");
        var index = await VaultStorage.LoadMasterIndexAsync(path, ct);
        foreach (var type in index.ResourceTypes) ValidateType(type);

        // 在刷新物理标识前保留已识别实例的版本归属，不替玩家搬动或重写实例。
        if (_index is not null)
            foreach (var instance in _machine.Instances)
                foreach (var entry in await ResolveAsync(instance, ct))
                    if (entry.MatchedResourceId is { } resourceId && entry.MatchedVersionId is { } versionId
                        && index.Resources.Any(r => r.ResourceId == resourceId && r.Versions.Any(v => v.VersionId == versionId)))
                        TrackCopy(instance.InstanceId, resourceId, entry.TypeId, entry.InstanceEntryName, versionId);

        var resources = new List<LogicalResource>();
        foreach (var resource in index.Resources)
        {
            var type = index.ResourceTypes.Single(t => t.TypeId == resource.TypeId);
            var versions = new List<ResourceVersion>();
            foreach (var version in resource.Versions)
            {
                ct.ThrowIfCancellationRequested();
                var entry = ResourceFiles.Child(ResourceFiles.Child(path, type.MasterFolderName), version.MasterEntryName);
                if (ResourceFiles.Exists(entry) && Directory.Exists(entry) == version.IsDirectory)
                {
                    var actual = await NtfsDriver.BuildEntryFingerprintsAsync(entry, ct);
                    // 迁库只更新锚点；内容被改写时保留原账本，继续报告异常。
                    if (ResourceFiles.ContentEquals(actual, version.Files))
                    {
                        var files = version.Files.Select(file =>
                        {
                            var current = actual.Single(f => f.RelativePath.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase));
                            return file with { MasterFileId = current.MasterFileId, LastWriteTimeUtcTicks = current.LastWriteTimeUtcTicks };
                        }).ToArray();
                        versions.Add(version with { Files = files });
                        continue;
                    }
                }
                versions.Add(version);
            }
            resources.Add(resource with { Versions = versions });
        }
        index = index with { Resources = resources };
        await VaultStorage.SaveMasterIndexAsync(path, index, ct);
        var validResources = index.Resources.Select(r => r.ResourceId).ToHashSet();
        _machine = _machine with
        {
            MasterVaultPath = path,
            Instances = _machine.Instances.Select(i => i with { TrackedTypeIds = index.ResourceTypes.Select(t => t.TypeId).ToArray() }).ToArray(),
            ControlledCopies = _machine.ControlledCopies.Where(c => validResources.Contains(c.ResourceId)).ToArray()
        };
        _index = index;
        await SaveMachineAsync();
        return Clone(index);
    }, ct);
}
