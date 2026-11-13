using McResourceManager.Core.Domain;
using McResourceManager.Core.FileSystem;

namespace McResourceManager.Core.Engine;

public sealed partial class ResourceManagerEngine
{
    public Task<IReadOnlyList<ProbedInstanceCandidate>> ProbeInstanceDirectoryAsync(string selectedPath, CancellationToken ct = default) => RunAsync(async () =>
    {
        var root = ResourceFiles.Full(selectedPath);
        ResourceFiles.AssertNoLinks(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("请选择存在的实例或启动器目录。");
        var minecraft = Directory.Exists(Path.Combine(root, ".minecraft")) ? Path.Combine(root, ".minecraft") : root;
        var versions = Path.Combine(minecraft, "versions");
        var roots = Directory.Exists(versions) ? Directory.GetDirectories(versions).ToList() : [minecraft];
        // 非隔离启动器的共享游戏目录也作为候选，避免只列出版本元数据目录。
        if (Directory.Exists(versions) && (Directory.Exists(Path.Combine(minecraft, "resourcepacks")) || File.Exists(Path.Combine(minecraft, "options.txt"))))
            roots.Insert(0, minecraft);
        var result = new List<ProbedInstanceCandidate>();
        foreach (var path in roots)
        {
            ct.ThrowIfCancellationRequested();
            ResourceFiles.AssertNoLinks(path);
            result.Add(new(Path.GetFileName(path), path, NtfsDriver.GetVolumeRoot(path),
                _machine.Instances.Any(i => ResourceFiles.Same(i.RootPath, path))));
        }
        await Task.CompletedTask;
        return (IReadOnlyList<ProbedInstanceCandidate>)result;
    }, ct);

    public Task<IReadOnlyList<GameInstance>> RegisterInstancesAsync(IReadOnlyList<(string DisplayName, string RootPath)> instances,
        CancellationToken ct = default) => RunAsync(async () =>
    {
        var result = new List<GameInstance>();
        foreach (var (displayName, rootPath) in instances)
        {
            var path = ResourceFiles.Full(rootPath);
            ResourceFiles.AssertNoLinks(path);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"实例目录不存在：{path}");
            if (Overlaps(path, Master) || _machine.ReplicaVaultPathsByVolume.Values.Any(cache => Overlaps(path, cache)))
                throw new IOException("实例目录不能与主库或共享缓存区重叠。");
            var existing = _machine.Instances.FirstOrDefault(i => ResourceFiles.Same(i.RootPath, path));
            if (existing is not null) { result.Add(existing); continue; }
            var volume = NtfsDriver.GetVolumeRoot(path);
            if (!NtfsDriver.IsSameVolume(path, Master) && !_machine.ReplicaVaultPathsByVolume.ContainsKey(volume))
                throw new InvalidOperationException($"请先为 {volume} 设置跨盘共享缓存区。");
            var item = new GameInstance(NewId(), string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(path) : displayName.Trim(),
                path, volume, Index.ResourceTypes.Select(t => t.TypeId).ToArray());
            result.Add(item);
        }
        _machine = _machine with { Instances = _machine.Instances.Concat(result).DistinctBy(i => i.InstanceId).ToArray() };
        await SaveMachineAsync();
        return (IReadOnlyList<GameInstance>)result;
    }, ct);

    public Task UnregisterInstanceAsync(string instanceId, CancellationToken ct = default) => RunAsync(async () =>
    {
        _ = Instance(instanceId);
        _machine = _machine with
        {
            Instances = _machine.Instances.Where(i => i.InstanceId != instanceId).ToArray(),
            ControlledCopies = _machine.ControlledCopies.Where(c => c.InstanceId != instanceId).ToArray()
        };
        await SaveMachineAsync();
    }, ct);
}
