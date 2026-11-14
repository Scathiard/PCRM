using McResourceManager.Core.Domain;
using McResourceManager.Core.FileSystem;

namespace McResourceManager.Core.Engine;

public sealed partial class ResourceManagerEngine
{
    public Task<DeploymentResult> DeployOrSwitchVersionAsync(string instanceId, string resourceId, string targetVersionId,
        bool updateCompanionConfig = true, CancellationToken ct = default) =>
        RunAsync(() => DeployAsync(instanceId, resourceId, targetVersionId, updateCompanionConfig, null, ct), ct);

    private async Task<DeploymentResult> DeployAsync(string instanceId, string resourceId, string versionId, bool updateCompanion,
        string? authorizedOldEntry, CancellationToken ct)
    {
        var instance = Instance(instanceId);
        var resource = Resource(resourceId);
        var version = resource.Versions.Single(v => v.VersionId == versionId);
        if (version.MasterEntryOverwritten) throw new IOException("此版本的主库文件已被覆盖，请选择其他可用版本。");
        var source = MasterEntry(resource, version);
        if (!ResourceFiles.Exists(source) || !ResourceFiles.ContentEquals(version.Files, await NtfsDriver.BuildEntryFingerprintsAsync(source, ct)))
            throw new IOException("主库中的此版本已缺失或被改写，不能用于部署，请检查主库变动。");
        if (!NtfsDriver.IsSameVolume(source, instance.RootPath)) source = await EnsureReplicaAsync(instance.VolumeRoot, resource, version, ct);
        var folder = InstanceFolder(instance, resource.TypeId);
        Directory.CreateDirectory(folder);
        _ = NtfsDriver.RecoverInterruptedSwapsInDirectory(folder);
        var views = await ResolveAsync(instance, ct);
        var current = views.Where(v => v.MatchedResourceId == resourceId).ToArray();
        if (current.Length > 1 && authorizedOldEntry is null) throw new IOException("实例中有同一资源的多个条目，请先移除多余条目再切换。");
        var oldName = authorizedOldEntry ?? current.FirstOrDefault()?.InstanceEntryName;
        var targetName = version.RestoreStandardNameOnDeploy ? resource.StandardTargetName : version.MasterEntryName;
        var target = ResourceFiles.Child(folder, targetName);
        if (ResourceFiles.Exists(target) && !string.Equals(oldName, targetName, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"实例已有同名文件“{targetName}”，请先收编或移除，不能直接覆盖。");
        _ = ResourceFiles.Files(source);
        ct.ThrowIfCancellationRequested();
        NtfsDriver.DeployHardLinkEntry(source, target);
        string? warning = null;
        if (oldName is not null && !string.Equals(oldName, targetName, StringComparison.OrdinalIgnoreCase))
        {
            try { ResourceFiles.Delete(ResourceFiles.Child(folder, oldName)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { warning = $"新版本已部署，旧文件未移除：{error.Message}"; }
        }
        if (updateCompanion)
        {
            try { await CompanionConfigUpdater.UpdateCompanionReferencesOnSwitchAsync(instance.RootPath, Type(resource.TypeId), oldName, targetName); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { warning = $"{warning} 游戏内启用配置未全部更新：{error.Message}".Trim(); }
        }
        _machine = _machine with { ControlledCopies = _machine.ControlledCopies.Where(c => c.InstanceId != instanceId
            || (c.InstanceEntryName != oldName && c.InstanceEntryName != targetName) || c.TypeId != resource.TypeId).ToArray() };
        await SaveMachineAsync();
        return new(true, target, warning);
    }

    internal async Task<string> EnsureReplicaAsync(string volume, LogicalResource resource, ResourceVersion version, CancellationToken ct)
    {
        if (!_machine.ReplicaVaultPathsByVolume.TryGetValue(volume, out var root))
            throw new InvalidOperationException($"请为 {volume} 设置跨盘共享缓存区。");
        var folder = ResourceFiles.Child(ResourceFiles.Child(root, Type(resource.TypeId).MasterFolderName), version.VersionId);
        Directory.CreateDirectory(folder);
        var target = ResourceFiles.Child(folder, version.MasterEntryName);
        if (!ResourceFiles.Exists(target))
        {
            var stage = ResourceFiles.Child(folder, NtfsDriver.StagePrefix + version.MasterEntryName);
            if (ResourceFiles.Exists(stage)) ResourceFiles.Delete(stage);
            try
            {
                await ResourceFiles.CopyAsync(MasterEntry(resource, version), stage, ct);
                var copied = await NtfsDriver.BuildEntryFingerprintsAsync(stage, ct);
                if (!ResourceFiles.ContentEquals(copied, version.Files)) throw new IOException("缓存区复制校验失败。");
                ct.ThrowIfCancellationRequested();
                ResourceFiles.Move(stage, target);
            }
            finally { if (ResourceFiles.Exists(stage)) ResourceFiles.Delete(stage); }
        }
        var replicaFiles = await NtfsDriver.BuildEntryFingerprintsAsync(target, ct);
        if (!ResourceFiles.ContentEquals(replicaFiles, version.Files))
            throw new IOException("共享缓存区中的版本已被改写，请先处理内容异常。");
        var files = version.Files.Select(file => file with
        {
            ReplicaFileIdsByVolume = new(file.ReplicaFileIdsByVolume, StringComparer.OrdinalIgnoreCase)
            { [volume] = replicaFiles.Single(f => f.RelativePath == file.RelativePath).MasterFileId }
        }).ToArray();
        PutResource(resource with { Versions = resource.Versions.Select(v => v.VersionId == version.VersionId ? v with { Files = files } : v).ToArray() });
        await SaveIndexAsync();
        return target;
    }

    public Task DetachToControlledCopyAsync(string instanceId, string typeId, string instanceEntryName, CancellationToken ct = default) => RunAsync(async () =>
    {
        var instance = Instance(instanceId);
        var entry = (await ResolveAsync(instance, ct)).Single(v => v.TypeId == typeId && v.InstanceEntryName == instanceEntryName);
        if (entry.MatchedResourceId is null || entry.State == MountState.TamperedAlert) throw new IOException("请先收编或处理内容异常。");
        var target = entry.InstanceEntryFullPath;
        var stage = ResourceFiles.Child(Path.GetDirectoryName(target)!, NtfsDriver.StagePrefix + instanceEntryName);
        var backup = ResourceFiles.Child(Path.GetDirectoryName(target)!, NtfsDriver.BackupPrefix + instanceEntryName);
        if (ResourceFiles.Exists(stage) || ResourceFiles.Exists(backup)) throw new IOException("存在待处理的恢复材料。");
        try
        {
            await ResourceFiles.CopyAsync(target, stage, ct);
            ct.ThrowIfCancellationRequested();
            ResourceFiles.Move(target, backup);
            try { ResourceFiles.Move(stage, target); }
            catch { ResourceFiles.Move(backup, target); throw; }
            TrackCopy(instanceId, entry.MatchedResourceId, typeId, instanceEntryName, entry.MatchedVersionId);
            await SaveMachineAsync();
            ResourceFiles.Delete(backup);
        }
        finally { if (ResourceFiles.Exists(stage)) ResourceFiles.Delete(stage); }
    }, ct);

    public Task RemoveFromInstanceAsync(string instanceId, string typeId, string instanceEntryName, CancellationToken ct = default) => RunAsync(async () =>
    {
        var instance = Instance(instanceId);
        ResourceFiles.Delete(ResourceFiles.Child(InstanceFolder(instance, typeId), instanceEntryName));
        _machine = _machine with { ControlledCopies = _machine.ControlledCopies.Where(c => c.InstanceId != instanceId
            || c.TypeId != typeId || c.InstanceEntryName != instanceEntryName).ToArray() };
        await SaveMachineAsync();
    }, ct);

    private void TrackCopy(string instanceId, string resourceId, string typeId, string name, string? versionId)
    {
        _machine = _machine with { ControlledCopies = _machine.ControlledCopies.Where(c => c.InstanceId != instanceId
            || c.TypeId != typeId || c.InstanceEntryName != name).Append(new ControlledCopyRecord(instanceId, resourceId,
                typeId, name, versionId, DateTime.UtcNow.Ticks)).ToArray() };
    }
}
