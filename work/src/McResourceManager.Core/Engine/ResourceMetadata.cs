using McResourceManager.Core.Domain;
using McResourceManager.Core.FileSystem;

namespace McResourceManager.Core.Engine;

public sealed partial class ResourceManagerEngine
{
    public Task<ResourceTypeDefinition> UpsertResourceTypeAsync(ResourceTypeDefinition typeDef, CancellationToken ct = default) => RunAsync(async () =>
    {
        ValidateType(typeDef);
        var old = Index.ResourceTypes.FirstOrDefault(t => t.TypeId == typeDef.TypeId);
        if (old is not null && (old.MasterFolderName != typeDef.MasterFolderName || old.InstanceRelativePath != typeDef.InstanceRelativePath))
            throw new InvalidOperationException("已有类型的目录不能直接改名，请新建类型后重新导入。");
        if (Index.ResourceTypes.Any(t => t.TypeId != typeDef.TypeId &&
            (string.Equals(t.MasterFolderName, typeDef.MasterFolderName, StringComparison.OrdinalIgnoreCase)
            || Overlaps(ResourceFiles.Child(Master, t.InstanceRelativePath), ResourceFiles.Child(Master, typeDef.InstanceRelativePath)))))
            throw new ArgumentException("资源类型目录不能重复或互相嵌套。");
        _index = Index with { ResourceTypes = Index.ResourceTypes.Where(t => t.TypeId != typeDef.TypeId).Append(typeDef).ToArray() };
        _machine = _machine with { Instances = _machine.Instances.Select(i => i with
        { TrackedTypeIds = i.TrackedTypeIds.Append(typeDef.TypeId).Distinct().ToArray() }).ToArray() };
        await SaveIndexAsync();
        await SaveMachineAsync();
        return typeDef;
    }, ct);

    public Task UpdateResourceMetadataAsync(string resourceId, string displayName, string description, string? newCoverSourceFilePath,
        CancellationToken ct = default) => RunAsync(async () =>
    {
        var resource = Resource(resourceId);
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("资源名称不能为空。");
        var cover = resource.CustomCoverRelativePath;
        if (!string.IsNullOrWhiteSpace(newCoverSourceFilePath))
        {
            var extension = Path.GetExtension(newCoverSourceFilePath).ToLowerInvariant();
            if (!new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" }.Contains(extension)) throw new ArgumentException("请选择图片文件。");
            cover = $".mcrm/covers/{resourceId}-{NewId()}{extension}";
            await ResourceFiles.CopyAsync(newCoverSourceFilePath, ResourceFiles.Child(Master, cover), ct);
        }
        PutResource(resource with { DisplayName = displayName.Trim(), Description = description, CustomCoverRelativePath = cover });
        await SaveIndexAsync();
    }, ct);

    public Task MergeOrSplitVersionAsync(string sourceResourceId, string versionId, string? targetMergeResourceId,
        CancellationToken ct = default) => RunAsync(async () =>
    {
        var source = Resource(sourceResourceId);
        var version = source.Versions.Single(v => v.VersionId == versionId);
        if (targetMergeResourceId == sourceResourceId) return;
        var target = targetMergeResourceId is { } id ? Resource(id)
            : new LogicalResource(NewId(), source.TypeId, $"{source.DisplayName} {version.VersionLabel}", source.StandardTargetName,
                versionId, source.Description, source.CustomCoverRelativePath, []);
        if (target.TypeId != source.TypeId) throw new ArgumentException("只能合并相同类型的资源版本。");
        var combined = target.Versions.Append(version).ToArray();
        PutResource(target with { Versions = combined, LatestVersionId = combined.MaxBy(v => v.CreatedAtUtcTicks)!.VersionId });
        var remaining = source.Versions.Where(v => v.VersionId != versionId).ToArray();
        if (remaining.Length == 0) _index = Index with { Resources = Index.Resources.Where(r => r.ResourceId != sourceResourceId).ToArray() };
        else PutResource(source with { Versions = remaining, LatestVersionId = source.LatestVersionId == versionId
            ? remaining.MaxBy(v => v.CreatedAtUtcTicks)!.VersionId : source.LatestVersionId });
        _machine = _machine with { ControlledCopies = _machine.ControlledCopies.Select(c => c.ResourceId == sourceResourceId
            && c.SourceVersionId == versionId ? c with { ResourceId = target.ResourceId } : c).ToArray() };
        await SaveIndexAsync();
        await SaveMachineAsync();
    }, ct);
}
