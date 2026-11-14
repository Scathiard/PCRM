using McResourceManager.Core.Domain;
using McResourceManager.Core.Engine;
using McResourceManager.Core.FileSystem;
using McResourceManager.Core.Storage;

namespace McResourceManager.Tests;

internal static partial class Program
{
    private static async Task LegacyVault()
    {
        using var f = new Fixture(TestRoot);
        await f.Engine.ConfigureMasterVaultAsync(f.Master);
        var instance = await f.Instance("old-install");
        var resource = await f.Import(f.Zip("Old_v1.0.zip", ("shaders/main.fsh", "original")), "shaderpacks");
        var deployed = await f.Engine.DeployOrSwitchVersionAsync(instance.InstanceId, resource.ResourceId, resource.LatestVersionId);
        var originalId = NtfsDriver.GetFastStat(deployed.TargetEntryFullPath).FileId;
        var cover = Path.Combine(f.Source, "custom.png");
        Fixture.Write(cover, "fixture cover");
        await f.Engine.UpdateResourceMetadataAsync(resource.ResourceId, "已有名称", "已有简介", cover);
        var oldIndex = (await f.Engine.GetSnapshotAsync()).MasterIndex!;
        var oldResource = oldIndex.Resources.Single();
        var oldCover = oldResource.CustomCoverRelativePath!.Replace(".pcrm/", ".mcrm\\");
        await VaultStorage.SaveMasterIndexAsync(f.Master, oldIndex with { Resources = [oldResource with { CustomCoverRelativePath = oldCover }] });
        var legacy = Path.Combine(f.Master, ".mcrm");
        var current = Path.Combine(f.Master, ".pcrm");
        Directory.Move(current, legacy);
        var oldIndexPath = Path.Combine(legacy, "index.json");
        var originalIndex = File.ReadAllBytes(oldIndexPath);
        var configBytes = File.ReadAllBytes(f.Config);
        var engine = new ResourceManagerEngine(f.Config);
        var restored = (await engine.GetSnapshotAsync()).MasterIndex!.Resources.Single();
        Check(restored.ResourceId == resource.ResourceId && restored.LatestVersionId == resource.LatestVersionId && restored.Description == "已有简介"
            && restored.CustomCoverRelativePath!.StartsWith(".pcrm/covers/") && File.ReadAllText(Path.Combine(f.Master, restored.CustomCoverRelativePath)) == "fixture cover",
            "Legacy lineage, description or cover did not survive first startup.");
        Check(File.ReadAllBytes(oldIndexPath).SequenceEqual(originalIndex) && File.Exists(Path.Combine(f.Master, oldCover))
            && File.ReadAllBytes(f.Config).SequenceEqual(configBytes), "Upgrade changed the original metadata or machine configuration.");
        Check((await engine.ResolveInstanceResourcesAsync(instance.InstanceId)).Single().State == MountState.LinkedLatest
            && NtfsDriver.GetFastStat(deployed.TargetEntryFullPath).FileId == originalId, "Upgrade broke the instance's link.");
        await engine.UpdateResourceMetadataAsync(resource.ResourceId, "新名称", "新备注", null);
        Check((await new ResourceManagerEngine(f.Config).GetSnapshotAsync()).MasterIndex!.Resources.Single().DisplayName == "新名称"
            && File.ReadAllBytes(oldIndexPath).SequenceEqual(originalIndex), "Restart reimported the old ledger.");

        var folder = Path.GetDirectoryName(deployed.TargetEntryFullPath)!;
        var oldStage = Path.Combine(folder, ".mcrm-stage.unfinished.zip");
        Fixture.Write(oldStage, "incomplete");
        File.Move(deployed.TargetEntryFullPath, Path.Combine(folder, ".mcrm-bak.Old_v1.0.zip"));
        Check((await engine.ResolveInstanceResourcesAsync(instance.InstanceId)).Single().State == MountState.LinkedLatest
            && !File.Exists(oldStage) && NtfsDriver.GetFastStat(deployed.TargetEntryFullPath).FileId == originalId, "Legacy interrupted swap did not recover.");
        Fixture.Write(Path.Combine(folder, ".pcrm-bak.conflict.zip"), "new backup");
        Fixture.Write(Path.Combine(folder, ".mcrm-bak.conflict.zip"), "old backup");
        Check(NtfsDriver.RecoverInterruptedSwapsInDirectory(folder).Count == 1 && !File.Exists(Path.Combine(folder, "conflict.zip"))
            && File.ReadAllText(Path.Combine(folder, ".mcrm-bak.conflict.zip")) == "old backup"
            && File.ReadAllText(Path.Combine(folder, ".pcrm-bak.conflict.zip")) == "new backup", "Ambiguous recovery chose a backup or lost recovery materials.");

        var broken = Path.Combine(f.Root, "broken");
        Fixture.Write(Path.Combine(broken, ".mcrm/index.json"), "invalid json");
        try { await VaultStorage.LoadMasterIndexAsync(broken); throw new Exception("Invalid ledger was replaced by an empty library."); }
        catch (System.Text.Json.JsonException) { }
        Check(!Directory.Exists(Path.Combine(broken, ".pcrm")) && File.ReadAllText(Path.Combine(broken, ".mcrm/index.json")) == "invalid json"
            && !Directory.EnumerateDirectories(broken, ".pcrm-upgrade-*").Any(), "Failed migration published a partial ledger or changed the old data.");
        Fixture.Write(Path.Combine(broken, ".mcrm/index.json"), System.Text.Encoding.UTF8.GetString(originalIndex));
        Fixture.Write(Path.Combine(broken, ".pcrm/existing"), "preserve");
        await Throws(() => VaultStorage.LoadMasterIndexAsync(broken));
        Check(File.ReadAllText(Path.Combine(broken, ".pcrm/existing")) == "preserve", "Migration overwrote an existing destination.");
    }
}
