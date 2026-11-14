using McResourceManager.Core.Domain;
using McResourceManager.Core.Engine;
using McResourceManager.Core.FileSystem;

namespace McResourceManager.Tests;

internal static partial class Program
{
    private static async Task Relocation()
    {
        using var f = new Fixture(TestRoot);
        await f.Engine.ConfigureMasterVaultAsync(f.Master);
        var a = await f.Instance("A");
        var resource = await f.Import(f.Zip("Move_v1.0.zip", ("shaders/main.fsh", "portable")), "shaderpacks");
        await f.Engine.UpdateResourceMetadataAsync(resource.ResourceId, "随库名称", "随库简介", null);
        var deployed = await f.Engine.DeployOrSwitchVersionAsync(a.InstanceId, resource.ResourceId, resource.LatestVersionId);
        var oldId = NtfsDriver.GetFastStat(deployed.TargetEntryFullPath).FileId;
        var relocated = Path.Combine(f.Root, "relocated-library");
        await ResourceFiles.CopyAsync(f.Master, relocated, CancellationToken.None);
        var index = await f.Engine.ConfigureMasterVaultAsync(relocated);
        Check(index.Resources.Single().DisplayName == "随库名称", "Library metadata was lost during relocation.");
        var masterFile = Path.Combine(relocated, "shaderpacks/Move_v1.0.zip");
        Check(index.Resources.Single().Versions[0].Files[0].MasterFileId == NtfsDriver.GetFastStat(masterFile).FileId,
            "Copied master retained the old physical file identity.");
        var old = (await f.Engine.ResolveInstanceResourcesAsync(a.InstanceId)).Single();
        Check(old.State == MountState.ControlledCopy && old.MatchedVersionId == resource.LatestVersionId,
            "Unselected instance lost its version association after relocating the library.");
        Check(NtfsDriver.GetFastStat(deployed.TargetEntryFullPath).FileId == oldId, "Relocating the library rewrote the instance.");
        var b = await f.Instance("B");
        await f.Engine.DeployOrSwitchVersionAsync(b.InstanceId, resource.ResourceId, resource.LatestVersionId);
        Check((await f.Engine.ResolveInstanceResourcesAsync(b.InstanceId)).Single().State == MountState.LinkedLatest,
            "Deployment from the copied library was not recognized as shared.");
        var fresh = new ResourceManagerEngine(Path.Combine(f.Root, "fresh-machine.json"));
        await fresh.ConfigureMasterVaultAsync(relocated);
        var freshInstance = (await fresh.RegisterInstancesAsync([("B", b.RootPath)])).Single();
        Check((await fresh.ResolveInstanceResourcesAsync(freshInstance.InstanceId)).Single().State == MountState.LinkedLatest,
            "A fresh machine configuration could not reuse the portable library.");
        File.WriteAllText(masterFile, "external modification");
        await f.Engine.ConfigureMasterVaultAsync(relocated);
        await Throws(() => f.Engine.DeployOrSwitchVersionAsync(a.InstanceId, resource.ResourceId, resource.LatestVersionId));
        Check((await f.Engine.ResolveInstanceResourcesAsync(b.InstanceId)).Single().State == MountState.TamperedAlert,
            "Opening a library silently accepted changed bytes as the old version.");
        await f.Engine.UpsertResourceTypeAsync(new("custom", "自定义", "custom", "custom", false, [], CompanionReferenceKind.None));
        await f.Engine.ConfigureMasterVaultAsync(Path.Combine(f.Root, "unrelated-library"));
        _ = await f.Engine.ResolveInstanceResourcesAsync(a.InstanceId);
    }

    private static async Task CancelDuringCopy()
    {
        using var f = new Fixture(TestRoot);
        await f.Engine.ConfigureMasterVaultAsync(f.Master);
        var source = Path.Combine(f.Source, "large.ysm");
        using (var stream = File.Create(source)) stream.SetLength(128L * 1024 * 1024);
        var originalHash = await NtfsDriver.ComputeFileSha256Async(source);
        using var cancel = new CancellationTokenSource();
        using var watch = new FileSystemWatcher(Path.Combine(f.Master, "ysm_models"), ".pcrm-stage.*");
        var stageCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watch.Created += (_, _) => { cancel.Cancel(); stageCreated.TrySetResult(); };
        watch.EnableRaisingEvents = true;
        var cancelled = false;
        try { await f.Engine.CommitVaultIntakeAsync([new(source, true, "ysm_models", null, "取消测试", "1", [])], cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(stageCreated.Task.IsCompleted && cancelled, "The cancellation did not occur during copying.");
        Check((await f.Engine.GetSnapshotAsync()).MasterIndex!.Resources.Count == 0, "Partial import entered the ledger.");
        Check(!Directory.EnumerateFileSystemEntries(Path.Combine(f.Master, "ysm_models")).Any(), "Cancelled import left a partial file.");
        Check(await NtfsDriver.ComputeFileSha256Async(source) == originalHash, "Cancellation damaged the external source.");
    }
}
