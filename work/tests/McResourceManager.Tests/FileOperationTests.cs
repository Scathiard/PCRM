using McResourceManager.Core.Domain;
using McResourceManager.Core.Engine;
using McResourceManager.Core.FileSystem;

namespace McResourceManager.Tests;

internal static partial class Program
{
    private static async Task Folders()
    {
        using var f = new Fixture(TestRoot);
        await f.Engine.ConfigureMasterVaultAsync(f.Master);
        await f.Engine.UpsertResourceTypeAsync(new("models", "自定义模型", "models/custom", "models", false, ["*.txt"], CompanionReferenceKind.None));
        var instance = await f.Instance("A");
        var source = Path.Combine(f.Source, "ExampleModel");
        Fixture.Write(Path.Combine(source, "model.json"), "{}");
        Fixture.Write(Path.Combine(source, "textures/body.bin"), "texture");
        var r = await f.Import(source, "models");
        var target = await f.Engine.DeployOrSwitchVersionAsync(instance.InstanceId, r.ResourceId, r.LatestVersionId);
        Check(NtfsDriver.GetFastStat(Path.Combine(source, "model.json")).FileId != NtfsDriver.GetFastStat(Path.Combine(target.TargetEntryFullPath, "model.json")).FileId, "Import linked external source instead of copying it.");
        Check(NtfsDriver.GetFastStat(Path.Combine(f.Master, "models/ExampleModel/textures/body.bin")).FileId == NtfsDriver.GetFastStat(Path.Combine(target.TargetEntryFullPath, "textures/body.bin")).FileId, "Folder child did not share bytes.");
        await f.Engine.DetachToControlledCopyAsync(instance.InstanceId, "models", "ExampleModel");
        Check((await f.Engine.ResolveInstanceResourcesAsync(instance.InstanceId)).Single().State == MountState.ControlledCopy, "Detach was not tracked.");
        Check(NtfsDriver.GetFastStat(Path.Combine(f.Master, "models/ExampleModel/model.json")).FileId != NtfsDriver.GetFastStat(Path.Combine(target.TargetEntryFullPath, "model.json")).FileId, "Detach still shares physical content.");
        await f.Engine.DeployOrSwitchVersionAsync(instance.InstanceId, r.ResourceId, r.LatestVersionId);
        await f.Engine.RemoveFromInstanceAsync(instance.InstanceId, "models", "ExampleModel");
        Check(!Directory.Exists(target.TargetEntryFullPath) && File.Exists(Path.Combine(f.Master, "models/ExampleModel/model.json")), "Remove damaged master.");
    }

    private static async Task Harvest()
    {
        using var f = new Fixture(TestRoot);
        await f.Engine.ConfigureMasterVaultAsync(f.Master);
        var a = await f.Instance("A"); var b = await f.Instance("B");
        var source = f.Zip("BSL_v1.0.zip", ("shaders/main.fsh", "same"));
        Fixture.Write(Path.Combine(a.RootPath, "shaderpacks/BSL_v1.0.zip.txt"), "local options");
        Directory.CreateDirectory(Path.Combine(b.RootPath, "shaderpacks"));
        File.Copy(source, Path.Combine(a.RootPath, "shaderpacks/BSL_v1.0.zip"));
        File.Copy(source, Path.Combine(b.RootPath, "shaderpacks/BSL_v1.0.zip"));
        var scan = await f.Engine.ScanInstanceForHarvestAsync(a.InstanceId, ["shaderpacks"]);
        Check(scan.Count == 1 && scan[0].Category == HarvestCategory.NewWildResource, "New resource classification or ignore rule wrong.");
        var first = await f.Engine.ExecuteHarvestAsync([new(a.InstanceId, "shaderpacks", "BSL_v1.0.zip", HarvestActionKind.ImportToMasterAndLink, null)]);
        Check(first.ImportedCount == 1 && first.SavedBytes == 0, "Unique import claimed nonexistent savings.");
        var same = (await f.Engine.ScanInstanceForHarvestAsync(b.InstanceId, ["shaderpacks"])).Single();
        Check(same.Category == HarvestCategory.SameHashCanLink, "Identical resource not recognized.");
        var decision = new HarvestExecutionDecision(b.InstanceId, "shaderpacks", same.EntryName, HarvestActionKind.ConvertSameHashToLink, same.MatchedResourceId);
        var second = await f.Engine.ExecuteHarvestAsync([decision]);
        Check(second.SavedBytes == new FileInfo(source).Length && second.LinkedCount == 1, "Dedup savings not backed by physical file removal.");
        Check((await f.Engine.ExecuteHarvestAsync([decision])).LinkedCount == 0, "Repeated harvest was not idempotent.");
        var other = f.Zip("BSL_v2.0.zip", ("shaders/main.fsh", "different"));
        File.Copy(other, Path.Combine(b.RootPath, "shaderpacks/BSL_v2.0.zip"));
        Check((await f.Engine.ScanInstanceForHarvestAsync(b.InstanceId, ["shaderpacks"])).Single().Category == HarvestCategory.SameResourceDifferentContent, "Version grouping not suggested.");
    }

    private static async Task Recovery()
    {
        using var f = new Fixture(TestRoot);
        var source = Path.Combine(f.Source, "new.bin"); Fixture.Write(source, "new");
        var target = Path.Combine(f.Root, "target/item.bin"); Fixture.Write(target, "old");
        using (var handle = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Throws(() => Task.Run(() => NtfsDriver.DeployHardLinkEntry(source, target)));
        Check(File.ReadAllText(target) == "old", "Failed swap damaged existing target.");
        var directory = Path.GetDirectoryName(target)!;
        var backup = Path.Combine(directory, NtfsDriver.BackupPrefix + "item.bin");
        File.Move(target, backup);
        Fixture.Write(Path.Combine(directory, NtfsDriver.StagePrefix + "item.bin"), "partial");
        Check(NtfsDriver.RecoverInterruptedSwapsInDirectory(directory).Count == 0 && File.ReadAllText(target) == "old", "Interrupted replacement not restored.");
        Fixture.Write(backup, "recovery material");
        Check(NtfsDriver.RecoverInterruptedSwapsInDirectory(directory).Count == 1 && File.ReadAllText(backup) == "recovery material", "Conflicting backup was deleted.");
        await Throws(() => Task.Run(() => NtfsDriver.DeployHardLinkEntry(source, target)));
        Check(File.ReadAllText(backup) == "recovery material", "New deployment overwrote recovery material.");
    }

    private static async Task Replica()
    {
        using var f = new Fixture(TestRoot);
        await f.Engine.ConfigureMasterVaultAsync(f.Master);
        var r = await f.Import(f.Zip("Shader.zip", ("shaders/main.fsh", "cache")), "shaderpacks");
        var volume = NtfsDriver.GetVolumeRoot(f.Root);
        await f.Engine.ConfigureReplicaVaultAsync(volume, Path.Combine(f.Root, "cache"));
        var cachePath = await f.Engine.EnsureReplicaAsync(volume, r, r.Versions[0], CancellationToken.None);
        var target = Path.Combine(f.Root, "replica-instance/Shader.zip");
        NtfsDriver.DeployHardLinkEntry(cachePath, target);
        Check(NtfsDriver.GetFastStat(cachePath).FileId == NtfsDriver.GetFastStat(target).FileId, "Replica deployment did not link to cache.");
        Check(NtfsDriver.GetFastStat(cachePath).FileId != NtfsDriver.GetFastStat(Path.Combine(f.Master, "shaderpacks/Shader.zip")).FileId, "Replica was not an independent copy.");
        Check(NtfsDriver.GetFastStat(cachePath).LastWriteTimeUtcTicks == r.Versions[0].Files[0].LastWriteTimeUtcTicks, "Replica timestamp would force unnecessary daily rehashing.");
        var reused = await f.Engine.EnsureReplicaAsync(volume, r, r.Versions[0], CancellationToken.None);
        Check(reused == cachePath, "Replica was not reused.");
        File.WriteAllText(cachePath, "corrupt cache");
        await Throws(() => f.Engine.EnsureReplicaAsync(volume, r, r.Versions[0], CancellationToken.None));
        Check(await NtfsDriver.ComputeFileSha256Async(Path.Combine(f.Master, "shaderpacks/Shader.zip")) == r.Versions[0].Files[0].Sha256, "Cache change propagated to master.");
    }

    private static async Task Boundaries()
    {
        using var f = new Fixture(TestRoot);
        await f.Engine.ConfigureMasterVaultAsync(f.Master);
        var unknown = Path.Combine(f.Source, "unknown.bin"); Fixture.Write(unknown, "unknown");
        Check((await f.Engine.AnalyzeExternalImportPathsAsync([unknown])).Single().RecognizedTypeId is null, "Unknown type silently assigned.");
        await Throws(() => f.Engine.CommitVaultIntakeAsync([new(unknown, true, "", null, "unknown", "1", [])]));
        await Throws(() => f.Engine.UpsertResourceTypeAsync(new("bad", "bad", "../../escape", "bad", false, [], CompanionReferenceKind.None)));
        await Throws(() => f.Engine.ConfigureReplicaVaultAsync(NtfsDriver.GetVolumeRoot(f.Root), f.Master));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Throws(() => f.Engine.CommitVaultIntakeAsync([new(unknown, true, "ysm_models", null, "unknown", "1", [])], cancellation.Token));
        Check((await f.Engine.GetSnapshotAsync()).MasterIndex!.Resources.Count == 0, "Cancelled operation left committed resources.");
        Check(File.ReadAllText(unknown) == "unknown", "Boundary checks changed external source.");
    }
}
