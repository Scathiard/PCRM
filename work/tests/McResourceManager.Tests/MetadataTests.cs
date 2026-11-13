using System.IO.Compression;
using McResourceManager.Core.Domain;
using McResourceManager.Core.FileSystem;

namespace McResourceManager.Tests;

internal static partial class Program
{
    private static async Task Grouping()
    {
        using var f = new Fixture(TestRoot);
        await f.Engine.ConfigureMasterVaultAsync(f.Master);
        var instance = await f.Instance("A");
        var resource = await f.Import(f.Zip("Group_v1.0.zip", ("shaders/main.fsh", "one")), "shaderpacks");
        var oldVersion = resource.LatestVersionId;
        var deployed = await f.Engine.DeployOrSwitchVersionAsync(instance.InstanceId, resource.ResourceId, oldVersion);
        await f.Engine.DetachToControlledCopyAsync(instance.InstanceId, "shaderpacks", "Group_v1.0.zip");
        var physicalId = NtfsDriver.GetFastStat(deployed.TargetEntryFullPath).FileId;
        resource = await f.Import(f.Zip("Group_v2.0.zip", ("shaders/main.fsh", "two")), "shaderpacks", resource.ResourceId, "2.0");
        await f.Engine.MergeOrSplitVersionAsync(resource.ResourceId, oldVersion, null);
        var split = (await f.Engine.GetSnapshotAsync()).MasterIndex!.Resources.Single(r => r.ResourceId != resource.ResourceId);
        var instanceState = (await f.Engine.ResolveInstanceResourcesAsync(instance.InstanceId)).Single();
        Check(instanceState.MatchedResourceId == split.ResourceId && instanceState.State == MountState.ControlledCopy,
            "Splitting a version lost the independent instance's association.");
        await f.Engine.MergeOrSplitVersionAsync(split.ResourceId, oldVersion, resource.ResourceId);
        var joined = (await f.Engine.GetSnapshotAsync()).MasterIndex!.Resources.Single();
        Check(joined.Versions.Count == 2 && joined.LatestVersionId == resource.LatestVersionId, "Merging history incorrectly replaced the newest version.");
        Check((await f.Engine.ResolveInstanceResourcesAsync(instance.InstanceId)).Single().MatchedResourceId == resource.ResourceId,
            "Merged controlled copy still referenced the removed group.");
        Check(NtfsDriver.GetFastStat(deployed.TargetEntryFullPath).FileId == physicalId, "Metadata grouping modified physical instance files.");
        await f.Engine.UnregisterInstanceAsync(instance.InstanceId);
        var snapshot = await f.Engine.GetSnapshotAsync();
        Check(snapshot.MachineConfig.Instances.Count == 0 && snapshot.MachineConfig.ControlledCopies.Count == 0
            && File.Exists(deployed.TargetEntryFullPath), "Unregistering an instance removed files or retained local tracking.");
    }

    private static async Task Metadata()
    {
        using var f = new Fixture(TestRoot);
        await f.Engine.ConfigureMasterVaultAsync(f.Master);
        var path = f.Zip("Pack.zip", ("pack.mcmeta", "{\"pack\":{\"description\":{\"text\":\"通用\",\"extra\":[{\"text\":\"测试\"}]}}}"));
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j4f8AAAAASUVORK5CYII=");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        { using var stream = archive.CreateEntry("pack.png").Open(); stream.Write(png); }
        var resource = await f.Import(path, "resourcepacks");
        Check(resource.Description == "通用测试", "Structured pack description not extracted.");
        Check(resource.CustomCoverRelativePath is not null && File.ReadAllBytes(Path.Combine(f.Master, resource.CustomCoverRelativePath)).SequenceEqual(png), "Cover not persisted in library metadata.");
        var folder = Path.Combine(f.Source, "folder");
        Fixture.Write(Path.Combine(folder, "pack.mcmeta"), "{\"pack\":{\"description\":\"folder description\"}}");
        File.WriteAllBytes(Path.Combine(folder, "pack.png"), png);
        Check((await ArchiveInspector.TryReadAsync(folder)).Description == "folder description", "Folder metadata not extracted.");
        var invalid = Path.Combine(f.Source, "invalid.zip"); Fixture.Write(invalid, "not a zip");
        Check((await ArchiveInspector.TryReadAsync(invalid)).CoverPng is null, "Invalid archive was not tolerated.");
        await f.Engine.UpdateResourceMetadataAsync(resource.ResourceId, "手动名称", "手动简介", Path.Combine(folder, "pack.png"));
        var updated = (await f.Engine.GetSnapshotAsync()).MasterIndex!.Resources.Single();
        Check(updated.DisplayName == "手动名称" && updated.Description == "手动简介", "Metadata edits were not saved.");
        var launcher = Path.Combine(f.Root, "launcher/.minecraft");
        Directory.CreateDirectory(Path.Combine(launcher, "versions/PackA"));
        Directory.CreateDirectory(Path.Combine(launcher, "versions/PackB"));
        var probed = await f.Engine.ProbeInstanceDirectoryAsync(Path.GetDirectoryName(launcher)!);
        Check(probed.Count == 2 && probed.All(p => !p.AlreadyRegistered), "Launcher instance discovery failed.");
        await f.Engine.RegisterInstancesAsync(probed.Select(p => (p.DisplayName, p.RootPath)).ToArray());
        Check((await f.Engine.ProbeInstanceDirectoryAsync(launcher)).All(p => p.AlreadyRegistered), "Already registered instance not detected.");
        Check((await f.Engine.ProbeInstanceDirectoryAsync(probed[0].RootPath)).Count == 1, "Single instance discovery failed.");
    }
}
