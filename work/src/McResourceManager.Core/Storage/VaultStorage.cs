using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using McResourceManager.Core.Domain;

namespace McResourceManager.Core.Storage;

/// <summary>
/// 轻量 JSON 账本持久化、忽略伴生文件过滤与文件名词干相似度提取示范实现。
/// 【硬约束】所有 JSON 写入均采用“写同目录临时文件 + File.Move(..., overwrite: true)”原子替换，禁止引入复杂数据库。
/// </summary>
public static partial class VaultStorage
{
    public const string MasterMetaFolderName = ".mcrm";
    public const string MasterIndexFileName = "index.json";
    public const string MasterCoversFolderName = "covers";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// 获取内置的常用 Minecraft 资源类型预设模板（含伴生文件忽略规则与配置引用联动策略）。
    /// </summary>
    public static IReadOnlyList<ResourceTypeDefinition> GetBuiltInTypePresets() =>
    [
        new(
            TypeId: "shaderpacks",
            Name: "光影 (Shaderpacks)",
            InstanceRelativePath: "shaderpacks",
            MasterFolderName: "shaderpacks",
            IsBuiltInPreset: true,
            IgnoredFilePatterns: ["*.txt"],
            CompanionKind: CompanionReferenceKind.ShaderPackWithTxtSettings),
        new(
            TypeId: "resourcepacks",
            Name: "资源包 (Resourcepacks)",
            InstanceRelativePath: "resourcepacks",
            MasterFolderName: "resourcepacks",
            IsBuiltInPreset: true,
            IgnoredFilePatterns: [".DS_Store", "thumbs.db"],
            CompanionKind: CompanionReferenceKind.MinecraftResourcePack),
        new(
            TypeId: "ysm_models",
            Name: "YSM 模型",
            InstanceRelativePath: "config/yes_steve_model/custom",
            MasterFolderName: "ysm_models",
            IsBuiltInPreset: true,
            IgnoredFilePatterns: [],
            CompanionKind: CompanionReferenceKind.None),
        new(
            TypeId: "tacz_guns",
            Name: "TaCZ 枪包",
            InstanceRelativePath: "tacz",
            MasterFolderName: "tacz",
            IsBuiltInPreset: true,
            IgnoredFilePatterns: [],
            CompanionKind: CompanionReferenceKind.None)
    ];

    /// <summary>
    /// 判断某个一级文件是否命中该资源类型的忽略规则（如 shaderpacks 下的 BSL_v8.1.zip.txt 伴生设置文件）。
    /// 命中的文件不作为独立资源扫描或收编入库。
    /// </summary>
    public static bool IsIgnoredCompanionFile(ResourceTypeDefinition typeDef, string entryName, bool isDirectory)
    {
        if (isDirectory || typeDef.IgnoredFilePatterns.Count == 0)
            return false;

        foreach (var pattern in typeDef.IgnoredFilePatterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, entryName, ignoreCase: true))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 读取主库根目录下的 主库/.mcrm/index.json；若不存在则初始化空账本。
    /// </summary>
    public static async Task<MasterVaultIndex> LoadMasterIndexAsync(string masterVaultRoot, CancellationToken ct = default)
    {
        var indexPath = Path.Combine(masterVaultRoot, MasterMetaFolderName, MasterIndexFileName);
        if (!File.Exists(indexPath))
        {
            return new MasterVaultIndex(
                SchemaVersion: 1,
                ResourceTypes: GetBuiltInTypePresets(),
                Resources: []);
        }

        await using var stream = File.OpenRead(indexPath);
        return await JsonSerializer.DeserializeAsync<MasterVaultIndex>(stream, JsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException($"无法解析主库索引文件：{indexPath}");
    }

    /// <summary>
    /// 原子保存 主库/.mcrm/index.json，并确保各资源类型的明文物理目录存在。
    /// </summary>
    public static async Task SaveMasterIndexAsync(string masterVaultRoot, MasterVaultIndex index, CancellationToken ct = default)
    {
        var metaDir = Path.Combine(masterVaultRoot, MasterMetaFolderName);
        Directory.CreateDirectory(metaDir);
        Directory.CreateDirectory(Path.Combine(metaDir, MasterCoversFolderName));

        foreach (var type in index.ResourceTypes)
        {
            Directory.CreateDirectory(Path.Combine(masterVaultRoot, type.MasterFolderName));
        }

        var indexPath = Path.Combine(metaDir, MasterIndexFileName);
        await WriteJsonAtomicallyAsync(indexPath, index, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 原子写入任意 JSON 配置，断电或中途异常不会破坏原文件。
    /// </summary>
    public static async Task WriteJsonAtomicallyAsync<T>(string filePath, T value, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath))
            ?? throw new InvalidOperationException($"无效文件路径：{filePath}");
        Directory.CreateDirectory(dir);

        var tempPath = Path.Combine(dir, $".tmp-{Path.GetFileName(filePath)}-{Guid.NewGuid():N}");
        try
        {
            var json = JsonSerializer.Serialize(value, JsonOptions);
            await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, ct).ConfigureAwait(false);
            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    /// <summary>
    /// 从文件名或文件夹名中拆解出“归一化词干（用于猜测是否属于同一资源）”与“猜测的版本号标签”。
    /// 例如："BSL_v8.2.1.zip" -> Stem: "bsl", VersionLabel: "8.2.1"
    ///       "ComplementaryReimagined_r5.2.zip" -> Stem: "complementaryreimagined", VersionLabel: "r5.2"
    /// </summary>
    public static (string NormalizedStem, string GuessedVersionLabel) ExtractStemAndVersion(string entryName, bool isDirectory)
    {
        var baseName = isDirectory ? entryName : Path.GetFileNameWithoutExtension(entryName);
        var match = VersionPatternRegex().Match(baseName);

        if (match.Success)
        {
            var versionLabel = match.Groups["ver"].Value;
            var prefix = baseName[..match.Index];
            var normalized = NonAlphaNumRegex().Replace(prefix, "").ToLowerInvariant();
            if (!string.IsNullOrEmpty(normalized))
                return (normalized, versionLabel);
        }

        var fallbackStem = NonAlphaNumRegex().Replace(baseName, "").ToLowerInvariant();
        return (fallbackStem, "default");
    }

    [GeneratedRegex(@"[_\-\s]*(?<ver>[vr]?\d+(?:\.\d+)+[a-zA-Z0-9\-]*)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPatternRegex();

    [GeneratedRegex(@"[\s_\-\.]+")]
    private static partial Regex NonAlphaNumRegex();
}
