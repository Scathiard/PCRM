using System.Text;
using System.Text.Json;
using McResourceManager.Core.FileSystem;
using McResourceManager.Core.Storage;

namespace McResourceManager.Tests;

internal static partial class Program
{
    private static async Task CompanionSafety()
    {
        using var f = new Fixture(TestRoot);
        await f.Engine.ConfigureMasterVaultAsync(f.Master);
        var instance = await f.Instance("A");
        var config = Path.Combine(instance.RootPath, "options.txt");
        var packType = VaultStorage.GetBuiltInTypePresets().Single(t => t.TypeId == "resourcepacks");
        var original = "resourcePacks:[\"vanilla\",\"file/旧包_v1.zip\"]\nincompatibleResourcePacks:[\"旧包_v1.zip\"]\nother:保留原样";
        await File.WriteAllTextAsync(config, original, new UTF8Encoding(true));
        await CompanionConfigUpdater.UpdateCompanionReferencesOnSwitchAsync(instance.RootPath, packType, "旧包_v1.zip", "新包_v2.zip");
        var updated = await File.ReadAllTextAsync(config);
        Check(File.ReadAllBytes(config).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "UTF-8 BOM was removed.");
        Check(!updated.Contains('\r') && updated.EndsWith("other:保留原样", StringComparison.Ordinal), "Newlines or unrelated options were changed.");
        Check(JsonSerializer.Deserialize<string[]>(updated.Split('\n')[0][14..])!.Contains("file/新包_v2.zip"), "JSON escaped Unicode pack reference was not updated.");
        Check(JsonSerializer.Deserialize<string[]>(updated.Split('\n')[1][26..])!.Contains("新包_v2.zip"), "Legacy pack reference was not updated.");
        await File.WriteAllTextAsync(config, "resourcePacks:malformed\r\nother:1");
        var malformed = await File.ReadAllBytesAsync(config);
        await Throws(() => CompanionConfigUpdater.UpdateCompanionReferencesOnSwitchAsync(instance.RootPath, packType, "old.zip", "new.zip"));
        var afterMalformed = await File.ReadAllBytesAsync(config);
        Check(malformed.SequenceEqual(afterMalformed), "Malformed settings were overwritten.");

        var shader = await f.Import(f.Zip("Guard_v1.0.zip", ("shaders/main.fsh", "v1")), "shaderpacks");
        await f.Engine.DeployOrSwitchVersionAsync(instance.InstanceId, shader.ResourceId, shader.LatestVersionId);
        var iris = Path.Combine(instance.RootPath, "config/iris.properties");
        Fixture.Write(iris, "shaderPack=Guard_v1.0.zip\r\nother=保留");
        var originalIris = File.ReadAllBytes(iris);
        shader = await f.Import(f.Zip("Guard_v2.0.zip", ("shaders/main.fsh", "v2")), "shaderpacks", shader.ResourceId, "2.0");
        using (var handle = new FileStream(iris, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await f.Engine.DeployOrSwitchVersionAsync(instance.InstanceId, shader.ResourceId, shader.LatestVersionId);
            Check(result.Success && result.CompanionWarning is not null, "Configuration failure was not reported as a partial success.");
        }
        Check(originalIris.SequenceEqual(File.ReadAllBytes(iris)), "Failed configuration replacement truncated the original.");
        Check(File.Exists(Path.Combine(instance.RootPath, "shaderpacks/Guard_v2.0.zip")), "Config failure rolled back the resource contrary to the contract.");
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(iris)!, ".pcrm-config.*").Any(), "Configuration temp file was not cleaned up.");
    }
}
