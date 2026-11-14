using McResourceManager.Core.Domain;
using McResourceManager.Core.Engine;
using McResourceManager.Core.FileSystem;
using McResourceManager.Core.Storage;

namespace McResourceManager.Tests;

internal static partial class Program
{
    private static async Task AcceptOverwrite()
    {
        using var f = new Fixture(TestRoot);
        await f.Engine.ConfigureMasterVaultAsync(f.Master);
        var linked = await f.Instance("linked");
        var detached = await f.Instance("detached");
        var source = Path.Combine(f.Source, "Bird.ysm");
        Fixture.Write(source, "first");
        var resource = await f.Import(source, "ysm_models");
        Fixture.Write(source, "second version");
        resource = await f.Import(source, "ysm_models", resource.ResourceId, "2");
        var old = resource.Versions.Single(v => v.VersionLabel == "2");
        Check(old.RestoreStandardNameOnDeploy, "Fixture must exercise a collision-renamed master path.");
        var link = await f.Engine.DeployOrSwitchVersionAsync(linked.InstanceId, resource.ResourceId, old.VersionId);
        var copy = await f.Engine.DeployOrSwitchVersionAsync(detached.InstanceId, resource.ResourceId, old.VersionId);
        await f.Engine.DetachToControlledCopyAsync(detached.InstanceId, "ysm_models", "Bird.ysm");
        var physical = Path.Combine(f.Master, "ysm_models", old.MasterEntryName);
        File.WriteAllText(physical, "first"); // Same bytes already exist at a different master path.
        var pending = (await f.Engine.ScanMasterVaultChangesAsync()).Single();
        Check(pending.SuggestedResourceId == resource.ResourceId && pending.ConflictKind == VaultEntryConflictKind.ExternalInPlaceOverwrite
            && pending.AffectedInstanceIds.SequenceEqual([linked.InstanceId]), "Wrong resource or affected-instance attribution.");
        var choice = new VaultIntakeCommitDecision(physical, false, "ysm_models", resource.ResourceId, "Bird", "3", []);
        await f.Engine.CommitVaultIntakeAsync([choice]);
        await f.Engine.CommitVaultIntakeAsync([choice]);
        var engine = new ResourceManagerEngine(f.Config);
        var index = (await engine.GetSnapshotAsync()).MasterIndex!;
        var updated = index.Resources.Single();
        var latest = updated.Versions.Single(v => v.VersionId == updated.LatestVersionId);
        Check(updated.Versions.Count == 3 && updated.Versions.Single(v => v.VersionId == old.VersionId).MasterEntryOverwritten
            && !latest.MasterEntryOverwritten && latest.RestoreStandardNameOnDeploy, "Old and new versions still claim one deployable path or retry duplicated the version.");
        Check((await engine.ScanMasterVaultChangesAsync()).Count == 0, "Accepted overwrite is offered again.");
        Check((await engine.ResolveInstanceResourcesAsync(linked.InstanceId)).Single().MatchedVersionId == latest.VersionId
            && File.ReadAllText(link.TargetEntryFullPath) == "first", "Live link does not match the accepted bytes.");
        var independent = (await engine.ResolveInstanceResourcesAsync(detached.InstanceId)).Single();
        Check(independent.State == MountState.ControlledCopy && independent.MatchedVersionId == old.VersionId
            && File.ReadAllText(copy.TargetEntryFullPath) == "second version", "Detached copy lost its lineage or bytes.");
        await Throws(() => engine.DeployOrSwitchVersionAsync(detached.InstanceId, resource.ResourceId, old.VersionId));
        Check(File.ReadAllText(copy.TargetEntryFullPath) == "second version", "Refused rollback changed a detached copy.");
        Check((await engine.AnalyzeExternalImportPathsAsync([source])).Single().ConflictKind != VaultEntryConflictKind.ExactDuplicateInVault,
            "A former fingerprint caused the only surviving old bytes to be skipped on reimport.");
        var deploy = await engine.DeployOrSwitchVersionAsync(detached.InstanceId, resource.ResourceId, latest.VersionId);
        Check(Path.GetFileName(deploy.TargetEntryFullPath) == "Bird.ysm", "New version lost the standard deployment name.");

        // Replay an index saved by the old build: both fingerprints pointed at this path.
        var legacyResource = updated with { Versions = updated.Versions.Select(v => v with { MasterEntryOverwritten = false }).ToArray() };
        await VaultStorage.SaveMasterIndexAsync(f.Master, index with { Resources = [legacyResource] });
        var reopened = new ResourceManagerEngine(f.Config);
        var repaired = (await reopened.GetSnapshotAsync()).MasterIndex!.Resources.Single();
        Check(repaired.Versions.Single(v => v.VersionId == old.VersionId).MasterEntryOverwritten
            && repaired.Versions.Count(v => !v.MasterEntryOverwritten && v.MasterEntryName == old.MasterEntryName) == 1,
            "Already-accepted legacy collisions were not reconciled.");
        Check(NtfsDriver.GetFastStat(deploy.TargetEntryFullPath).FileId == NtfsDriver.GetFastStat(physical).FileId,
            "Reopening the ledger rewrote a deployed file.");
    }
}
