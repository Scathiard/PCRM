using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using McResourceManager.Core.Domain;

namespace McResourceManager.Core.FileSystem;

/// <summary>
/// 版本切换时的伴生配置引用更新与设置文件继承器（P0 核心能力，且与 P1 的全局 options.txt 工具共享读写逻辑）。
/// 解决痛点：光影或资源包切换版本后文件名改变（如 BSL_v8.1.zip -> BSL_v8.2.zip），导致游戏内光影/资源包自动关闭及光影参数丢失。
/// </summary>
public static class CompanionConfigUpdater
{
    /// <summary>
    /// 当实例中的某项资源从 oldEntryName 替换为 newEntryName 时，按类型的 CompanionKind 自动更新启用引用与伴生设置文件。
    /// </summary>
    public static async Task UpdateCompanionReferencesOnSwitchAsync(
        string instanceRootPath,
        ResourceTypeDefinition typeDef,
        string? oldEntryName,
        string newEntryName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(oldEntryName) ||
            string.Equals(oldEntryName, newEntryName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        switch (typeDef.CompanionKind)
        {
            case CompanionReferenceKind.MinecraftResourcePack:
                await UpdateResourcePackInOptionsTxtAsync(instanceRootPath, oldEntryName, newEntryName, ct)
                    .ConfigureAwait(false);
                break;

            case CompanionReferenceKind.ShaderPackWithTxtSettings:
                await UpdateShaderPackSelectionAsync(instanceRootPath, oldEntryName, newEntryName, ct)
                    .ConfigureAwait(false);
                InheritShaderTxtSettings(instanceRootPath, typeDef.InstanceRelativePath, oldEntryName, newEntryName);
                break;
        }
    }

    /// <summary>
    /// 替换实例 options.txt 中 resourcePacks 与 incompatibleResourcePacks 列表里的 "file/旧文件名" 为 "file/新文件名"。
    /// </summary>
    private static async Task UpdateResourcePackInOptionsTxtAsync(
        string instanceRootPath,
        string oldEntryName,
        string newEntryName,
        CancellationToken ct)
    {
        var optionsPath = ResourceFiles.Child(instanceRootPath, "options.txt");
        await RewriteAsync(optionsPath, text => Regex.Replace(text,
            @"(?m)^(resourcePacks:|incompatibleResourcePacks:)([^\r\n]*)", match =>
            {
                string[] packs;
                try { packs = JsonSerializer.Deserialize<string[]>(match.Groups[2].Value) ?? throw new JsonException(); }
                catch (JsonException error) { throw new IOException("资源包启用列表无法解析，已保留原配置。", error); }
                var changed = false;
                for (var i = 0; i < packs.Length; i++)
                {
                    if (packs[i] == $"file/{oldEntryName}") { packs[i] = $"file/{newEntryName}"; changed = true; }
                    else if (packs[i] == oldEntryName) { packs[i] = newEntryName; changed = true; }
                }
                return changed ? match.Groups[1].Value + JsonSerializer.Serialize(packs) : match.Value;
            }), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 替换 Iris (config/iris.properties) 与 OptiFine (optionsshaders.txt) 中的 shaderPack=旧文件名 为 shaderPack=新文件名。
    /// </summary>
    private static async Task UpdateShaderPackSelectionAsync(
        string instanceRootPath,
        string oldEntryName,
        string newEntryName,
        CancellationToken ct)
    {
        string[] candidateFiles =
        [
            ResourceFiles.Child(instanceRootPath, "config/iris.properties"),
            ResourceFiles.Child(instanceRootPath, "optionsshaders.txt")
        ];

        foreach (var configFile in candidateFiles)
        {
            await RewriteAsync(configFile, text => Regex.Replace(text, @"(?m)^[^\r\n]*", match =>
                match.Value.Trim() == $"shaderPack={oldEntryName}"
                    ? match.Value.Replace($"shaderPack={oldEntryName}", $"shaderPack={newEntryName}", StringComparison.Ordinal)
                    : match.Value), ct).ConfigureAwait(false);
        }
    }

    private static async Task RewriteAsync(string path, Func<string, string> transform, CancellationToken ct)
    {
        ResourceFiles.AssertNoLinks(path);
        if (!File.Exists(path)) return;
        var original = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var hasBom = original.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
        var utf8 = new UTF8Encoding(false, true);
        string text;
        try { text = utf8.GetString(original, hasBom ? 3 : 0, original.Length - (hasBom ? 3 : 0)); }
        catch (DecoderFallbackException error) { throw new IOException("配置编码不是 UTF-8，已保留原配置。", error); }
        var changed = transform(text);
        if (changed == text) return;
        var bytes = utf8.GetBytes(changed);
        if (hasBom) bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray();
        var stage = ResourceFiles.Child(Path.GetDirectoryName(path)!, $".mcrm-config.{Path.GetFileName(path)}.{Guid.NewGuid():N}");
        try
        {
            await using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            var current = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (!original.SequenceEqual(current))
                throw new IOException("配置在处理期间发生变化，已保留外部修改。");
            ct.ThrowIfCancellationRequested();
            File.Move(stage, path, overwrite: true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    /// <summary>
    /// 光影参数文件继承：若 shaderpacks/&lt;旧光影名&gt;.txt 存在且 &lt;新光影名&gt;.txt 不存在，自动将其重命名继承为 &lt;新光影名&gt;.txt。
    /// </summary>
    private static void InheritShaderTxtSettings(
        string instanceRootPath,
        string shaderRelativeDir,
        string oldEntryName,
        string newEntryName)
    {
        var shaderDir = ResourceFiles.Child(instanceRootPath, shaderRelativeDir);
        var oldTxt = ResourceFiles.Child(shaderDir, $"{oldEntryName}.txt");
        var newTxt = ResourceFiles.Child(shaderDir, $"{newEntryName}.txt");

        if (File.Exists(oldTxt) && !File.Exists(newTxt))
        {
            File.Move(oldTxt, newTxt);
        }
    }
}
