using System.IO.Compression;
using McResourceManager.Core.Domain;
using McResourceManager.Core.FileSystem;
using McResourceManager.Core.Storage;

namespace McResourceManager.Core.Engine;

public sealed partial class ResourceManagerEngine
{
    public Task<IReadOnlyList<VaultIntakeCandidateItem>> AnalyzeExternalImportPathsAsync(IReadOnlyList<string> sourcePaths,
        string? preselectedTypeId = null, string? targetResourceId = null, string? preselectedInstanceId = null,
        CancellationToken ct = default) => RunAsync(async () =>
    {
        if (preselectedTypeId is not null) _ = Type(preselectedTypeId);
        if (preselectedInstanceId is not null) _ = Instance(preselectedInstanceId);
        var resource = targetResourceId is null ? null : Resource(targetResourceId);
        var result = new List<VaultIntakeCandidateItem>();
        foreach (var source in sourcePaths.Select(ResourceFiles.Full).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (ResourceFiles.Inside(Master, source) || ResourceFiles.Same(Master, source))
                throw new ArgumentException("主库内的文件请使用“检查主库变动”。");
            var typeId = resource?.TypeId ?? preselectedTypeId ?? RecognizeType(source);
            result.Add(await AnalyzeEntryAsync(source, true, typeId, resource,
                preselectedInstanceId is null ? [] : [preselectedInstanceId], ct));
        }
        return (IReadOnlyList<VaultIntakeCandidateItem>)result;
    }, ct);

    public Task<IReadOnlyList<VaultIntakeCandidateItem>> ScanMasterVaultChangesAsync(CancellationToken ct = default) => RunAsync(async () =>
    {
        var result = new List<VaultIntakeCandidateItem>();
        foreach (var type in Index.ResourceTypes)
        {
            var directory = ResourceFiles.Child(Master, type.MasterFolderName);
            if (!Directory.Exists(directory)) continue;
            foreach (var entry in EnumerateEntries(directory, type))
            {
                var known = Index.Resources.Where(r => r.TypeId == type.TypeId).SelectMany(r => r.Versions)
                    .Where(v => string.Equals(v.MasterEntryName, Path.GetFileName(entry), StringComparison.OrdinalIgnoreCase));
                var unchanged = false;
                foreach (var version in known)
                    if (await HasOriginalIdentityAsync(entry, version, ct)) { unchanged = true; break; }
                if (!unchanged) result.Add(await AnalyzeEntryAsync(entry, false, type.TypeId, null, [], ct));
            }
        }
        return (IReadOnlyList<VaultIntakeCandidateItem>)result;
    }, ct);

    private static IEnumerable<string> EnumerateEntries(string directory, ResourceTypeDefinition type) =>
        Directory.EnumerateFileSystemEntries(directory).Where(path => !NtfsDriver.IsInternalTempOrBackupName(Path.GetFileName(path))
            && !VaultStorage.IsIgnoredCompanionFile(type, Path.GetFileName(path), Directory.Exists(path))).Order(StringComparer.OrdinalIgnoreCase);

    private async Task<VaultIntakeCandidateItem> AnalyzeEntryAsync(string source, bool external, string? typeId,
        LogicalResource? suggested, IReadOnlyList<string> preselected, CancellationToken ct)
    {
        _ = ResourceFiles.Files(source);
        var isDirectory = Directory.Exists(source);
        var files = await NtfsDriver.BuildEntryFingerprintsAsync(source, ct);
        var name = Path.GetFileName(source);
        var (stem, label) = VaultStorage.ExtractStemAndVersion(name, isDirectory);
        var resources = Index.Resources.Where(r => r.TypeId == typeId).ToArray();
        suggested ??= resources.FirstOrDefault(r => VaultStorage.ExtractStemAndVersion(r.StandardTargetName, isDirectory).NormalizedStem == stem);
        var duplicate = resources.SelectMany(r => r.Versions.Select(v => (r, v)))
            .FirstOrDefault(pair => pair.v.IsDirectory == isDirectory && ResourceFiles.ContentEquals(pair.v.Files, files));
        var conflict = duplicate.v is not null ? VaultEntryConflictKind.ExactDuplicateInVault : VaultEntryConflictKind.None;
        suggested ??= duplicate.r;
        if (external && typeId is not null && ResourceFiles.Exists(ResourceFiles.Child(ResourceFiles.Child(Master, Type(typeId).MasterFolderName), name))
            && conflict == VaultEntryConflictKind.None) conflict = VaultEntryConflictKind.SameNameDifferentContentWillCoexist;
        var originalIds = files.Select(f => f.MasterFileId).ToHashSet();
        if (!external && resources.SelectMany(r => r.Versions).Any(v => v.MasterEntryName.Equals(name, StringComparison.OrdinalIgnoreCase)
            && v.Files.Any(f => originalIds.Contains(f.MasterFileId)) && !ResourceFiles.ContentEquals(v.Files, files)))
            conflict = VaultEntryConflictKind.ExternalInPlaceOverwrite;
        var affected = new List<string>();
        if (conflict == VaultEntryConflictKind.ExternalInPlaceOverwrite && typeId is not null)
            foreach (var instance in _machine.Instances)
            {
                var folder = InstanceFolder(instance, typeId);
                if (Directory.Exists(folder) && EnumerateEntries(folder, Type(typeId))
                    .SelectMany(ResourceFiles.Files).Any(f => originalIds.Contains(NtfsDriver.GetFastStat(f).FileId))) affected.Add(instance.InstanceId);
            }
        return new(source, name, isDirectory, external, files.Sum(f => f.Size), typeId,
            typeId is null ? "未识别，请指定资源类型" : "根据所选类型、资源结构或已有资源识别", suggested?.ResourceId,
            suggested?.DisplayName ?? Path.GetFileNameWithoutExtension(name), label, conflict, preselected, affected);
    }

    private string? RecognizeType(string source)
    {
        if (Path.GetExtension(source).Equals(".jar", StringComparison.OrdinalIgnoreCase)) return null;
        if (Path.GetExtension(source).Equals(".ysm", StringComparison.OrdinalIgnoreCase)) return Known("ysm_models");
        var names = new List<string>();
        if (Directory.Exists(source)) names.AddRange(ResourceFiles.Files(source).Select(f => Path.GetRelativePath(source, f).Replace('\\', '/')));
        else if (Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            try { using var zip = ZipFile.OpenRead(source); names.AddRange(zip.Entries.Select(e => e.FullName)); }
            catch (InvalidDataException) { return null; }
        }
        if (names.Any(n => n.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase))) return Known("shaderpacks");
        if (names.Any(n => n.Equals("pack.mcmeta", StringComparison.OrdinalIgnoreCase))) return Known("resourcepacks");
        if (names.Any(n => n.EndsWith(".ysm", StringComparison.OrdinalIgnoreCase))) return Known("ysm_models");
        if (names.Any(n => n.Equals("gunpack.meta.json", StringComparison.OrdinalIgnoreCase))
            && names.Any(n => n.Contains("/guns/", StringComparison.OrdinalIgnoreCase))) return Known("tacz_guns");
        var stem = VaultStorage.ExtractStemAndVersion(Path.GetFileName(source), Directory.Exists(source)).NormalizedStem;
        return Index.Resources.FirstOrDefault(r => VaultStorage.ExtractStemAndVersion(r.StandardTargetName, Directory.Exists(source)).NormalizedStem == stem)?.TypeId;
    }

    private string? Known(string typeId) => Index.ResourceTypes.Any(t => t.TypeId == typeId) ? typeId : null;

    private static async Task<bool> HasOriginalIdentityAsync(string path, ResourceVersion version, CancellationToken ct)
    {
        if (!await ResourceFiles.MatchesAsync(path, version, ct)) return false;
        return version.Files.All(f => NtfsDriver.GetFastStat(version.IsDirectory ? ResourceFiles.Child(path, f.RelativePath) : path).FileId == f.MasterFileId);
    }
}
