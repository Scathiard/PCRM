using System.Text.Json;
using McResourceManager.Core.Domain;
using McResourceManager.Core.FileSystem;
using McResourceManager.Core.Storage;

namespace McResourceManager.Core.Engine;

public sealed partial class ResourceManagerEngine : IResourceManagerEngine
{
    private readonly string _configPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private LocalMachineConfig _machine = new(null, new(StringComparer.OrdinalIgnoreCase), [], [], []);
    private MasterVaultIndex? _index;
    private bool _loaded;
    private string Master => _machine.MasterVaultPath ?? throw new InvalidOperationException("请先设置主资源库。");
    private MasterVaultIndex Index => _index ?? throw new InvalidOperationException("请先设置主资源库。");

    public ResourceManagerEngine(string? machineConfigPath = null)
    {
        _configPath = ResourceFiles.Full(machineConfigPath ?? Path.Combine(AppContext.BaseDirectory, "work/data/machine-config.json"));
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                if (!_loaded) await LoadAsync(ct);
                ct.ThrowIfCancellationRequested();
                return await action();
            }, ct).ConfigureAwait(false);
        }
        catch { _loaded = false; throw; }
        finally { _gate.Release(); }
    }

    private Task RunAsync(Func<Task> action, CancellationToken ct) => RunAsync(async () => { await action(); return true; }, ct);

    private async Task LoadAsync(CancellationToken ct)
    {
        ResourceFiles.AssertNoLinks(_configPath);
        _machine = File.Exists(_configPath)
            ? JsonSerializer.Deserialize<LocalMachineConfig>(await File.ReadAllTextAsync(_configPath, ct), JsonOptions)
                ?? throw new InvalidDataException("本机配置无法读取，请保留原文件后检查。")
            : new(null, new(StringComparer.OrdinalIgnoreCase), [], [], []);
        _machine = _machine with { ReplicaVaultPathsByVolume = new(_machine.ReplicaVaultPathsByVolume, StringComparer.OrdinalIgnoreCase) };
        _index = _machine.MasterVaultPath is { } root ? await VaultStorage.LoadMasterIndexAsync(root, ct) : null;
        if (_index is not null)
            foreach (var type in _index.ResourceTypes) ValidateType(type);
        _loaded = true;
    }

    private Task SaveMachineAsync() => VaultStorage.WriteJsonAtomicallyAsync(_configPath, _machine);
    private Task SaveIndexAsync() => VaultStorage.SaveMasterIndexAsync(Master, Index);
    private ResourceTypeDefinition Type(string id) => Index.ResourceTypes.SingleOrDefault(t => t.TypeId == id)
        ?? throw new ArgumentException("资源类型不存在。");
    private LogicalResource Resource(string id) => Index.Resources.SingleOrDefault(r => r.ResourceId == id)
        ?? throw new ArgumentException("资源不存在。");
    private GameInstance Instance(string id) => _machine.Instances.SingleOrDefault(i => i.InstanceId == id)
        ?? throw new ArgumentException("实例不存在。");
    private string MasterEntry(LogicalResource resource, ResourceVersion version) =>
        ResourceFiles.Child(ResourceFiles.Child(Master, Type(resource.TypeId).MasterFolderName), version.MasterEntryName);
    private string InstanceFolder(GameInstance instance, string typeId) => ResourceFiles.Child(instance.RootPath, Type(typeId).InstanceRelativePath);
    private static string NewId() => Guid.NewGuid().ToString("N");
    private void PutResource(LogicalResource resource) => _index = Index with
    { Resources = Index.Resources.Where(r => r.ResourceId != resource.ResourceId).Append(resource).ToArray() };

    public Task<(LocalMachineConfig MachineConfig, MasterVaultIndex? MasterIndex)> GetSnapshotAsync(CancellationToken ct = default) =>
        RunAsync(() => Task.FromResult((Clone(_machine), _index is null ? null : Clone(_index))), ct);
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;

    public Task ConfigureReplicaVaultAsync(string volumeRoot, string replicaVaultPath, CancellationToken ct = default) => RunAsync(async () =>
    {
        var path = ResourceFiles.Full(replicaVaultPath);
        ResourceFiles.AssertNoLinks(path);
        var volume = NtfsDriver.GetVolumeRoot(volumeRoot);
        if (!string.Equals(volume, NtfsDriver.GetVolumeRoot(path), StringComparison.OrdinalIgnoreCase))
            throw new IOException("共享缓存区必须在指定磁盘上。");
        if (Overlaps(Master, path) || _machine.Instances.Any(i => Overlaps(i.RootPath, path)))
            throw new IOException("共享缓存区不能与主库或实例重叠。");
        Directory.CreateDirectory(path);
        _machine.ReplicaVaultPathsByVolume[volume] = path;
        await SaveMachineAsync();
    }, ct);

    private static bool Overlaps(string a, string b) => ResourceFiles.Same(a, b) || ResourceFiles.Inside(a, b) || ResourceFiles.Inside(b, a);
    private static void ValidateType(ResourceTypeDefinition type)
    {
        if (string.IsNullOrWhiteSpace(type.TypeId) || string.IsNullOrWhiteSpace(type.Name)) throw new ArgumentException("类型名称不能为空。");
        var probe = Path.GetFullPath(".");
        _ = ResourceFiles.Child(probe, type.InstanceRelativePath);
        _ = ResourceFiles.Child(probe, type.MasterFolderName);
        if (type.MasterFolderName.Contains('/') || type.MasterFolderName.Contains('\\') || type.MasterFolderName.StartsWith('.'))
            throw new ArgumentException("主库类型文件夹需要使用单层普通名称。");
        if (type.InstanceRelativePath.TrimEnd('/', '\\').Equals("config", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请指定专用资源子目录，不能纳管整个 config 目录。");
    }
}
