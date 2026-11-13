using McResourceManager.Core.Domain;
using McResourceManager.Core.FileSystem;

namespace McResourceManager.Core.Engine;

public sealed partial class ResourceManagerEngine
{
    public Task<IReadOnlyList<InstanceResourceEntryView>> ResolveInstanceResourcesAsync(string instanceId,
        CancellationToken ct = default) => RunAsync(() => ResolveAsync(Instance(instanceId), ct), ct);

    private async Task<IReadOnlyList<InstanceResourceEntryView>> ResolveAsync(GameInstance instance, CancellationToken ct)
    {
        var anchors = new Dictionary<string, List<(LogicalResource Resource, ResourceVersion Version)>>();
        foreach (var resource in Index.Resources)
            foreach (var version in resource.Versions)
                foreach (var file in version.Files)
                    foreach (var id in file.ReplicaFileIdsByVolume.Values.Append(file.MasterFileId))
                    {
                        if (!anchors.TryGetValue(id, out var list)) anchors[id] = list = [];
                        list.Add((resource, version));
                    }
        var views = new List<InstanceResourceEntryView>();
        var changedCopies = false;
        foreach (var typeId in instance.TrackedTypeIds)
        {
            var type = Type(typeId);
            var folder = InstanceFolder(instance, typeId);
            if (!Directory.Exists(folder)) continue;
            ResourceFiles.AssertNoLinks(folder);
            var warnings = NtfsDriver.RecoverInterruptedSwapsInDirectory(folder);
            if (warnings.Count > 0) throw new IOException(string.Join(Environment.NewLine, warnings));
            foreach (var path in EnumerateEntries(folder, type))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                var actualIds = ResourceFiles.Files(path).Select(p => NtfsDriver.GetFastStat(p).FileId).ToHashSet();
                var candidates = actualIds.Where(anchors.ContainsKey).SelectMany(id => anchors[id])
                    .Where(pair => pair.Resource.TypeId == typeId).DistinctBy(pair => pair.Version.VersionId)
                    .OrderByDescending(pair => pair.Version.Files.Count(f => actualIds.Contains(f.MasterFileId)
                        || f.ReplicaFileIdsByVolume.Values.Any(actualIds.Contains)))
                    .ThenByDescending(pair => pair.Version.CreatedAtUtcTicks).ToArray();
                var match = candidates.FirstOrDefault();
                var copy = _machine.ControlledCopies.FirstOrDefault(c => c.InstanceId == instance.InstanceId
                    && c.TypeId == typeId && c.InstanceEntryName.Equals(name, StringComparison.OrdinalIgnoreCase));
                LogicalResource? resource = match.Resource ?? Index.Resources.FirstOrDefault(r => r.ResourceId == copy?.ResourceId);
                ResourceVersion? version = match.Version ?? resource?.Versions.FirstOrDefault(v => v.VersionId == copy?.SourceVersionId);
                var state = MountState.WildUntracked;
                if (resource is not null && version is not null)
                {
                    var intact = await ResourceFiles.MatchesAsync(path, version, ct);
                    if (!intact) state = MountState.TamperedAlert;
                    else
                    {
                        var validAnchor = HasLiveAnchor(instance, resource, version, actualIds);
                        state = match.Version is not null && validAnchor
                            ? version.VersionId == resource.LatestVersionId ? MountState.LinkedLatest : MountState.LinkedOldVersion
                            : MountState.ControlledCopy;
                        if (state == MountState.ControlledCopy && (copy?.SourceVersionId != version.VersionId || copy.ResourceId != resource.ResourceId))
                        { TrackCopy(instance.InstanceId, resource.ResourceId, typeId, name, version.VersionId); changedCopies = true; }
                    }
                }
                else if (copy is not null) state = MountState.ControlledCopy;
                var latest = resource?.Versions.FirstOrDefault(v => v.VersionId == resource.LatestVersionId);
                views.Add(new(typeId, name, path, state, resource?.ResourceId, resource?.DisplayName, version?.VersionId,
                    version?.VersionLabel, latest?.VersionId, latest?.VersionLabel, resource?.Versions ?? []));
            }
        }
        if (changedCopies) await SaveMachineAsync();
        return views;
    }

    private bool HasLiveAnchor(GameInstance instance, LogicalResource resource, ResourceVersion version, HashSet<string> actualIds)
    {
        var source = MasterEntry(resource, version);
        if (!NtfsDriver.IsSameVolume(instance.RootPath, Master))
        {
            if (!_machine.ReplicaVaultPathsByVolume.TryGetValue(instance.VolumeRoot, out var root)) return false;
            source = ResourceFiles.Child(ResourceFiles.Child(ResourceFiles.Child(root, Type(resource.TypeId).MasterFolderName), version.VersionId), version.MasterEntryName);
        }
        if (!ResourceFiles.Exists(source)) return false;
        var files = ResourceFiles.Files(source);
        if (files.Count != version.Files.Count || actualIds.Count != version.Files.Count) return false;
        return files.All(file => actualIds.Contains(NtfsDriver.GetFastStat(file).FileId));
    }
}
