using McResourceManager.Core.Domain;
using McResourceManager.Core.FileSystem;
using McResourceManager.Core.Storage;

namespace McResourceManager.Core.Engine;

public sealed partial class ResourceManagerEngine
{
    public Task<IReadOnlyList<DeploymentResult>> CommitVaultIntakeAsync(IReadOnlyList<VaultIntakeCommitDecision> decisions,
        CancellationToken ct = default) => RunAsync(async () =>
    {
        foreach (var decision in decisions)
        {
            _ = Type(decision.TypeId);
            if (string.IsNullOrWhiteSpace(decision.DisplayResourceName)) throw new ArgumentException("资源名称不能为空。");
            if (decision.MergeIntoResourceId is { } id && Resource(id).TypeId != decision.TypeId)
                throw new ArgumentException("归组资源与所选类型不一致。");
            foreach (var instance in decision.InstanceIdsToDeployOrUpgrade) _ = Instance(instance);
        }
        var results = new List<DeploymentResult>();
        foreach (var decision in decisions)
        {
            ct.ThrowIfCancellationRequested();
            var (resource, version) = await IntakeOneAsync(decision, ct);
            foreach (var instanceId in decision.InstanceIdsToDeployOrUpgrade.Distinct())
            {
                try { results.Add(await DeployAsync(instanceId, resource.ResourceId, version.VersionId, true, null, ct)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                { results.Add(new(false, Instance(instanceId).RootPath, $"资源已入库，但实例部署失败：{error.Message}")); }
            }
        }
        return (IReadOnlyList<DeploymentResult>)results;
    }, ct);

    private async Task<(LogicalResource Resource, ResourceVersion Version)> IntakeOneAsync(VaultIntakeCommitDecision decision, CancellationToken ct)
    {
        var source = ResourceFiles.Full(decision.SourceFullPath);
        _ = ResourceFiles.Files(source);
        if (Path.GetExtension(source).Equals(".jar", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("首版不管理 Mod 的 .jar 文件。");
        var type = Type(decision.TypeId);
        var folder = ResourceFiles.Child(Master, type.MasterFolderName);
        Directory.CreateDirectory(folder);
        if (!decision.IsExternalImport && !ResourceFiles.Same(Path.GetDirectoryName(source)!, folder))
            throw new ArgumentException("主库变动确认只能处理对应类型目录中的条目。");
        if (decision.IsExternalImport && (ResourceFiles.Inside(Master, source) || ResourceFiles.Same(Master, source)))
            throw new ArgumentException("主库内部文件不能作为外部导入源。");
        var isDirectory = Directory.Exists(source);
        var sourceFiles = await NtfsDriver.BuildEntryFingerprintsAsync(source, ct);
        if (sourceFiles.Count == 0) throw new ArgumentException("空文件夹没有可入库的资源文件。");
        if (NtfsDriver.IsInternalTempOrBackupName(Path.GetFileName(source))) throw new ArgumentException("此文件名属于恢复材料，请先恢复或改名。");
        var resource = decision.MergeIntoResourceId is { } id ? Resource(id) : null;
        var entryName = Path.GetFileName(source);
        var active = Index.Resources.Where(r => r.TypeId == type.TypeId)
            .SelectMany(r => r.Versions.Where(v => !v.MasterEntryOverwritten).Select(v => (Resource: r, Version: v))).ToArray();
        var owners = active.Where(pair => !decision.IsExternalImport &&
            pair.Version.MasterEntryName.Equals(entryName, StringComparison.OrdinalIgnoreCase)).ToArray();
        // Accepting a change to a known path must reconcile that path even when another file has identical bytes.
        var duplicates = owners.Length > 0 ? owners : active.Where(pair => resource is null || pair.Resource.ResourceId == resource.ResourceId);
        foreach (var pair in duplicates)
            if (pair.Version.IsDirectory == isDirectory && ResourceFiles.ContentEquals(sourceFiles, pair.Version.Files)
                && await HasOriginalIdentityAsync(MasterEntry(pair.Resource, pair.Version), pair.Version, ct)) return pair;
        var destination = source;
        var renamed = owners.Any(pair => pair.Resource.ResourceId == resource?.ResourceId && pair.Version.RestoreStandardNameOnDeploy);
        if (decision.IsExternalImport)
        {
            destination = ResourceFiles.Child(folder, entryName);
            while (ResourceFiles.Exists(destination))
            {
                renamed = true;
                var stem = isDirectory ? entryName : Path.GetFileNameWithoutExtension(entryName);
                var extension = isDirectory ? "" : Path.GetExtension(entryName);
                destination = ResourceFiles.Child(folder, $"{stem}--{NewId()[..8]}{extension}");
            }
            var stage = ResourceFiles.Child(folder, NtfsDriver.StagePrefix + Path.GetFileName(destination));
            if (ResourceFiles.Exists(stage)) throw new IOException("存在待恢复的入库暂存文件，请先检查资源库。");
            try
            {
                await ResourceFiles.CopyAsync(source, stage, ct);
                var copied = await NtfsDriver.BuildEntryFingerprintsAsync(stage, ct);
                if (!ResourceFiles.ContentEquals(sourceFiles, copied)) throw new IOException("复制校验失败，源文件可能正在变化。");
                ct.ThrowIfCancellationRequested();
                ResourceFiles.Move(stage, destination);
            }
            finally { if (ResourceFiles.Exists(stage)) ResourceFiles.Delete(stage); }
        }
        var fingerprints = decision.IsExternalImport ? await NtfsDriver.BuildEntryFingerprintsAsync(destination, CancellationToken.None) : sourceFiles;
        var version = new ResourceVersion(NewId(), string.IsNullOrWhiteSpace(decision.VersionLabel) ? "默认" : decision.VersionLabel.Trim(),
            Path.GetFileName(destination), isDirectory, renamed, fingerprints.Sum(f => f.Size), DateTime.UtcNow.Ticks, fingerprints);
        _index = MarkOverwritten(Index, owners.Select(pair => pair.Version.VersionId).ToHashSet());
        resource = resource is null ? new(NewId(), type.TypeId, decision.DisplayResourceName.Trim(), entryName, version.VersionId, "", null, [])
            : Resource(resource.ResourceId);
        resource = resource with { LatestVersionId = version.VersionId, Versions = resource.Versions.Append(version).ToArray() };
        var metadata = await ArchiveInspector.TryReadAsync(destination);
        if (string.IsNullOrEmpty(resource.Description)) resource = resource with { Description = metadata.Description };
        if (resource.CustomCoverRelativePath is null && metadata.CoverPng is not null)
        {
            var cover = $"{VaultStorage.MasterMetaFolderName}/{VaultStorage.MasterCoversFolderName}/{resource.ResourceId}-{version.VersionId}.png";
            try
            {
                await File.WriteAllBytesAsync(ResourceFiles.Child(Master, cover), metadata.CoverPng);
                resource = resource with { CustomCoverRelativePath = cover };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        PutResource(resource);
        // 一旦复制完成，提交账本是短小的不可取消步骤；后续实例部署可独立取消或失败。
        await SaveIndexAsync();
        return (resource, version);
    }
}
