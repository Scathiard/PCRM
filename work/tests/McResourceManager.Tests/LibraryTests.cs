using McResourceManager.Core.Domain;
using McResourceManager.Core.Engine;
using McResourceManager.Core.FileSystem;

namespace McResourceManager.Tests;

internal static partial class Program
{
    private static async Task Intake()
    {
        using var f = new Fixture(TestRoot);
        await f.Engine.ConfigureMasterVaultAsync(f.Master);
        var source = f.Zip("first/BSL_v1.0.zip", ("shaders/main.fsh", "version one"));
        var original = File.ReadAllBytes(source);
        var analysis = (await f.Engine.AnalyzeExternalImportPathsAsync([source])).Single();
        Check(analysis.RecognizedTypeId == "shaderpacks", "Shader recognition failed.");
        var first = await f.Import(source, "shaderpacks");
        var firstPath = Path.Combine(f.Master, "shaderpacks", first.Versions[0].MasterEntryName);
        var firstId = NtfsDriver.GetFastStat(firstPath).FileId;
        Check((await f.Engine.AnalyzeExternalImportPathsAsync([source])).Single().ConflictKind == VaultEntryConflictKind.ExactDuplicateInVault, "Duplicate not detected.");
        await f.Import(source, "shaderpacks");
        Check((await f.Engine.GetSnapshotAsync()).MasterIndex!.Resources.Single().Versions.Count == 1, "Duplicate import created another version.");
        var second = f.Zip("second/BSL_v1.0.zip", ("shaders/main.fsh", "different version"));
        Check((await f.Engine.AnalyzeExternalImportPathsAsync([second])).Single().ConflictKind == VaultEntryConflictKind.SameNameDifferentContentWillCoexist, "Conflict not previewed.");
        var updated = await f.Import(second, "shaderpacks", first.ResourceId, "2.0");
        Check(updated.Versions.Count == 2 && updated.Versions.Last().RestoreStandardNameOnDeploy, "Versions did not coexist.");
        Check(File.ReadAllBytes(firstPath).SequenceEqual(original) && NtfsDriver.GetFastStat(firstPath).FileId == firstId, "Old library file was overwritten.");
        Check(File.ReadAllBytes(source).SequenceEqual(original), "External source changed.");
        var reloaded = new ResourceManagerEngine(f.Config);
        Check((await reloaded.GetSnapshotAsync()).MasterIndex!.Resources.Single().Versions.Count == 2, "Ledger did not survive reload.");
    }

    private static async Task Versions()
    {
        using var f = new Fixture(TestRoot);
        await f.Engine.ConfigureMasterVaultAsync(f.Master);
        var a = await f.Instance("A"); var b = await f.Instance("B");
        var resource = await f.Import(f.Zip("BSL_v1.0.zip", ("shaders/main.fsh", "v1")), "shaderpacks");
        var v1 = resource.LatestVersionId;
        var deployed = await f.Engine.DeployOrSwitchVersionAsync(a.InstanceId, resource.ResourceId, v1);
        await f.Engine.DeployOrSwitchVersionAsync(b.InstanceId, resource.ResourceId, v1);
        Check(NtfsDriver.GetFastStat(deployed.TargetEntryFullPath).NumberOfLinks == 3, "Instances did not share one physical file.");
        Fixture.Write(Path.Combine(a.RootPath, "config/iris.properties"), "shaderPack=BSL_v1.0.zip\nother=保持\n");
        Fixture.Write(Path.Combine(a.RootPath, "optionsshaders.txt"), "shaderPack=BSL_v1.0.zip\n");
        Fixture.Write(Path.Combine(a.RootPath, "shaderpacks/BSL_v1.0.zip.txt"), "quality=high");
        resource = await f.Import(f.Zip("BSL_v2.0.zip", ("shaders/main.fsh", "v2")), "shaderpacks", resource.ResourceId, "2.0");
        await f.Engine.DeployOrSwitchVersionAsync(a.InstanceId, resource.ResourceId, resource.LatestVersionId);
        Check(File.ReadAllText(Path.Combine(a.RootPath, "config/iris.properties")).Contains("shaderPack=BSL_v2.0.zip"), "Iris reference was not updated.");
        Check(File.ReadAllText(Path.Combine(a.RootPath, "optionsshaders.txt")).Contains("BSL_v2.0.zip"), "OptiFine reference was not updated.");
        Check(File.ReadAllText(Path.Combine(a.RootPath, "shaderpacks/BSL_v2.0.zip.txt")) == "quality=high", "Shader settings were lost.");
        Check((await f.Engine.ResolveInstanceResourcesAsync(a.InstanceId)).Single().State == MountState.LinkedLatest, "Latest state wrong or companion file not ignored.");
        Check((await f.Engine.ResolveInstanceResourcesAsync(b.InstanceId)).Single().State == MountState.LinkedOldVersion, "Unselected instance lost its old version.");
        await f.Engine.DeployOrSwitchVersionAsync(a.InstanceId, resource.ResourceId, v1);
        Check((await f.Engine.ResolveInstanceResourcesAsync(a.InstanceId)).Single().MatchedVersionId == v1, "Version rollback failed.");
        var pack = await f.Import(f.Zip("Pack_v1.0.zip", ("pack.mcmeta", "{}")), "resourcepacks");
        await f.Engine.DeployOrSwitchVersionAsync(a.InstanceId, pack.ResourceId, pack.LatestVersionId);
        Fixture.Write(Path.Combine(a.RootPath, "options.txt"), "resourcePacks:[\"vanilla\",\"file/Pack_v1.0.zip\"]\nincompatibleResourcePacks:[\"file/Pack_v1.0.zip\"]\ngamma:0.5\n");
        pack = await f.Import(f.Zip("Pack_v2.0.zip", ("pack.mcmeta", "{\"pack\":{}}")), "resourcepacks", pack.ResourceId, "2.0");
        await f.Engine.DeployOrSwitchVersionAsync(a.InstanceId, pack.ResourceId, pack.LatestVersionId);
        var options = File.ReadAllText(Path.Combine(a.RootPath, "options.txt"));
        Check(!options.Contains("Pack_v1.0") && options.Contains("Pack_v2.0") && options.Contains("gamma:0.5"), "Resource pack references damaged unrelated options.");
    }

    private static async Task ExternalChanges()
    {
        using var f = new Fixture(TestRoot);
        await f.Engine.ConfigureMasterVaultAsync(f.Master);
        var instance = await f.Instance("A");
        var r = await f.Import(f.Zip("BSL_v1.0.zip", ("shaders/main.fsh", "original")), "shaderpacks");
        var target = await f.Engine.DeployOrSwitchVersionAsync(instance.InstanceId, r.ResourceId, r.LatestVersionId);
        var master = Path.Combine(f.Master, "shaderpacks", r.Versions[0].MasterEntryName);
        File.WriteAllText(master, "external overwrite");
        Check((await f.Engine.ResolveInstanceResourcesAsync(instance.InstanceId)).Single().State == MountState.TamperedAlert, "In-place write not detected.");
        var pending = (await f.Engine.ScanMasterVaultChangesAsync()).Single();
        Check(pending.ConflictKind == VaultEntryConflictKind.ExternalInPlaceOverwrite && pending.AffectedInstanceIds.Contains(instance.InstanceId), "Affected instance not listed.");
        await Throws(() => f.Engine.DeployOrSwitchVersionAsync(instance.InstanceId, r.ResourceId, r.LatestVersionId));
        var second = await f.Import(f.Zip("Other_v1.0.zip", ("shaders/main.fsh", "keep old")), "shaderpacks");
        var secondTarget = await f.Engine.DeployOrSwitchVersionAsync(instance.InstanceId, second.ResourceId, second.LatestVersionId);
        var oldBytes = File.ReadAllBytes(secondTarget.TargetEntryFullPath);
        File.Delete(Path.Combine(f.Master, "shaderpacks", second.Versions[0].MasterEntryName));
        var state = (await f.Engine.ResolveInstanceResourcesAsync(instance.InstanceId)).Single(v => v.MatchedResourceId == second.ResourceId);
        Check(state.State == MountState.ControlledCopy && File.ReadAllBytes(state.InstanceEntryFullPath).SequenceEqual(oldBytes), "Deleted master did not preserve instance bytes.");
        Check((await new ResourceManagerEngine(f.Config).GetSnapshotAsync()).MachineConfig.ControlledCopies.Count == 1, "Controlled copy was not persisted.");
        Check(File.ReadAllText(target.TargetEntryFullPath) == "external overwrite", "Test did not exercise real linked overwrite.");
    }
}
