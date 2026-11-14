using McResourceManager.Core.Domain;
using McResourceManager.Core.FileSystem;
using McResourceManager.Core.Storage;

namespace McResourceManager.Core.Engine;

public sealed partial class ResourceManagerEngine
{
    public Task<IReadOnlyList<HarvestCandidateItem>> ScanInstanceForHarvestAsync(string instanceId,
        IReadOnlyList<string> typeIds, CancellationToken ct = default) => RunAsync(() => ScanHarvestAsync(Instance(instanceId), typeIds, ct), ct);

    private async Task<IReadOnlyList<HarvestCandidateItem>> ScanHarvestAsync(GameInstance instance, IReadOnlyList<string> typeIds, CancellationToken ct)
    {
        var results = new List<HarvestCandidateItem>();
        var views = await ResolveAsync(instance, ct);
        foreach (var typeId in typeIds.Distinct())
        {
            var type = Type(typeId);
            var folder = InstanceFolder(instance, typeId);
            if (!Directory.Exists(folder)) continue;
            foreach (var path in EnumerateEntries(folder, type))
            {
                var state = views.Single(v => v.TypeId == typeId && ResourceFiles.Same(v.InstanceEntryFullPath, path));
                if (state.State is MountState.LinkedLatest or MountState.LinkedOldVersion) continue;
                var directory = Directory.Exists(path);
                var files = await NtfsDriver.BuildEntryFingerprintsAsync(path, ct);
                var name = Path.GetFileName(path);
                var duplicates = Index.Resources.Where(r => r.TypeId == typeId)
                    .SelectMany(r => r.Versions.Select(v => (Resource: r, Version: v)))
                    .Where(pair => !pair.Version.MasterEntryOverwritten && pair.Version.IsDirectory == directory && ResourceFiles.ContentEquals(files, pair.Version.Files));
                (LogicalResource? Resource, ResourceVersion? Version) duplicate = (null, null);
                foreach (var pair in duplicates)
                    if (await HasOriginalIdentityAsync(MasterEntry(pair.Resource, pair.Version), pair.Version, ct)) { duplicate = pair; break; }
                var stem = VaultStorage.ExtractStemAndVersion(name, directory).NormalizedStem;
                var matching = duplicate.Resource ?? Index.Resources.FirstOrDefault(r => r.TypeId == typeId
                    && VaultStorage.ExtractStemAndVersion(r.StandardTargetName, directory).NormalizedStem == stem);
                results.Add(new(instance.InstanceId, typeId, name, path, directory, files.Sum(f => f.Size), duplicate.Version is not null
                    ? HarvestCategory.SameHashCanLink : matching is not null ? HarvestCategory.SameResourceDifferentContent : HarvestCategory.NewWildResource,
                    matching?.ResourceId, duplicate.Version?.VersionId));
            }
        }
        return results;
    }

    public Task<HarvestExecutionResult> ExecuteHarvestAsync(IReadOnlyList<HarvestExecutionDecision> decisions,
        CancellationToken ct = default) => RunAsync(async () =>
    {
        var linked = 0;
        var imported = 0;
        long saved = 0;
        var warnings = new List<string>();
        foreach (var decision in decisions)
        {
            ct.ThrowIfCancellationRequested();
            if (decision.Action == HarvestActionKind.Skip) continue;
            var instance = Instance(decision.InstanceId);
            var candidates = await ScanHarvestAsync(instance, [decision.TypeId], ct);
            var candidate = candidates.FirstOrDefault(c => c.EntryName == decision.EntryName);
            if (candidate is null) continue; // 已完成的收编重复提交不会再次修改文件。
            var resourceId = decision.TargetResourceId ?? candidate.MatchedResourceId;
            var reclaimable = ResourceFiles.Files(candidate.FullPath).Select(NtfsDriver.GetFastStat)
                .Where(stat => stat.NumberOfLinks == 1).Sum(stat => stat.Size);
            ResourceVersion? version = null;
            if (decision.Action == HarvestActionKind.KeepAsControlledCopy)
            {
                if (resourceId is null) throw new ArgumentException("独立副本必须先关联已入库资源，或选择导入新资源。");
                TrackCopy(instance.InstanceId, resourceId, decision.TypeId, candidate.EntryName, candidate.MatchedVersionId);
                await SaveMachineAsync();
                continue;
            }
            if (decision.Action == HarvestActionKind.ImportToMasterAndLink)
            {
                var label = VaultStorage.ExtractStemAndVersion(candidate.EntryName, candidate.IsDirectory).GuessedVersionLabel;
                var before = Index.Resources.SelectMany(r => r.Versions).Count();
                var intake = await IntakeOneAsync(new(candidate.FullPath, true, decision.TypeId, resourceId,
                    resourceId is null ? Path.GetFileNameWithoutExtension(candidate.EntryName) : Resource(resourceId).DisplayName, label, []), ct);
                resourceId = intake.Resource.ResourceId;
                version = intake.Version;
                if (Index.Resources.SelectMany(r => r.Versions).Count() > before) { imported++; reclaimable -= version.TotalSizeBytes; }
            }
            else if (decision.Action == HarvestActionKind.ConvertSameHashToLink)
            {
                if (candidate.Category != HarvestCategory.SameHashCanLink || resourceId != candidate.MatchedResourceId)
                    throw new IOException("资源内容已经变化，请重新扫描后确认。");
                version = Resource(resourceId!).Versions.Single(v => v.VersionId == candidate.MatchedVersionId);
            }
            else if (decision.Action == HarvestActionKind.ReplaceWithMasterLatest)
            {
                if (resourceId is null) throw new ArgumentException("请选择要替换成的主库资源。");
                version = Resource(resourceId).Versions.Single(v => v.VersionId == Resource(resourceId).LatestVersionId);
            }
            else throw new ArgumentException("不支持的收编操作。");
            var result = await DeployAsync(instance.InstanceId, resourceId!, version!.VersionId, true, candidate.EntryName, ct);
            if (result.CompanionWarning is not null) warnings.Add(result.CompanionWarning);
            linked++;
            saved += Math.Max(0, reclaimable);
        }
        return new HarvestExecutionResult(linked, imported, saved, warnings);
    }, ct);
}
