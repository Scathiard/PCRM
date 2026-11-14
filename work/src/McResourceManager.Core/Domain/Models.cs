namespace McResourceManager.Core.Domain;

/// <summary>
/// 实例中某个资源条目的实时挂载状态（由 NTFS FileID + 两级哈希哨兵推导）。
/// 【硬约束】后续实现禁止拆分为多层状态类或新增模糊状态；UI 层严禁直接展示底层枚举名，必须展示下列玩家友好文案。
/// </summary>
public enum MountState
{
    /// <summary>UI 文案：[🟢 共享中 · 最新] — FileID 与同盘库（主库或跨盘共享缓存区）最新版本一致，且指纹完好。</summary>
    LinkedLatest,

    /// <summary>UI 文案：[🔵 共享中 · 旧版 vX] — FileID 与同盘库中某历史旧版本一致，且指纹完好。</summary>
    LinkedOldVersion,

    /// <summary>UI 文案：[🟡 独立副本] — 非库内硬链，但在本地配置中登记为该资源的保留分支（持续监控，支持单向重置回主库共享）。</summary>
    ControlledCopy,

    /// <summary>UI 文案：[🔴 内容异常] — FileID 仍相连，但文件大小/修改时间/SHA256 与入库原始哈希哨兵不符。</summary>
    TamperedAlert,

    /// <summary>UI 文案：[⚪ 未入库] — 既无 FileID 匹配，也未登记为独立副本的未知本地文件/文件夹。</summary>
    WildUntracked
}

/// <summary>
/// 资源版本切换时的游戏配置引用与伴生设置同步策略（P0 内置能力）。
/// </summary>
public enum CompanionReferenceKind
{
    /// <summary>无特殊伴生配置引用（如普通模型、枪包）。</summary>
    None = 0,

    /// <summary>Minecraft 资源包：切换版本文件名时，顺带更新实例 options.txt 中 resourcePacks 与 incompatibleResourcePacks 的引用。</summary>
    MinecraftResourcePack = 1,

    /// <summary>
    /// 光影包：
    /// 1. 扫描目录时忽略 *.txt（Iris/OptiFine 生成的同名光影设置文件，跟实例走不入主库）；
    /// 2. 切换版本文件名时，顺带更新 config/iris.properties 与 optionsshaders.txt 中的 shaderPack= 引用；
    /// 3. 若实例下存在 &lt;旧文件名&gt;.txt 且不存在 &lt;新文件名&gt;.txt，自动将其重命名继承为 &lt;新文件名&gt;.txt。
    /// </summary>
    ShaderPackWithTxtSettings = 2
}

/// <summary>
/// 单个文件的物理指纹与卷内锚点（用于单文件资源，或文件夹资源的内部子文件）。
/// </summary>
public sealed record FileEntryFingerprint(
    string RelativePath,
    long Size,
    long LastWriteTimeUtcTicks,
    string Sha256,
    string MasterFileId,
    Dictionary<string, string> ReplicaFileIdsByVolume
);

/// <summary>
/// 逻辑资源下的一个具体版本（对应主库/<类型>/ 下的一个单文件或子文件夹）。
/// </summary>
public sealed record ResourceVersion(
    string VersionId,
    string VersionLabel,
    string MasterEntryName,
    bool IsDirectory,
    bool RestoreStandardNameOnDeploy,
    long TotalSizeBytes,
    long CreatedAtUtcTicks,
    IReadOnlyList<FileEntryFingerprint> Files
)
{
    // 保留实例的版本归属与缓存指纹。失效后的文件名只用于历史和旧缓存定位。
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool MasterEntryOverwritten { get; init; }
}

/// <summary>
/// 一个逻辑资源条目（如 "BSL 光影"），主显 LatestVersionId，其余版本在同行版本胶囊弹出面板中展示。
/// </summary>
public sealed record LogicalResource(
    string ResourceId,
    string TypeId,
    string DisplayName,
    string StandardTargetName,
    string LatestVersionId,
    string Description,
    string? CustomCoverRelativePath,
    IReadOnlyList<ResourceVersion> Versions
);

/// <summary>
/// 资源类型定义（由用途名称、实例内相对路径、忽略文件通配符及伴生引用策略定义）。
/// </summary>
public sealed record ResourceTypeDefinition(
    string TypeId,
    string Name,
    string InstanceRelativePath,
    string MasterFolderName,
    bool IsBuiltInPreset,
    IReadOnlyList<string> IgnoredFilePatterns,
    CompanionReferenceKind CompanionKind
);

/// <summary>
/// 存储在【主库/.pcrm/index.json】中的随库账本（重装系统或迁移主库硬盘时完整保留）。
/// </summary>
public sealed record MasterVaultIndex(
    int SchemaVersion,
    IReadOnlyList<ResourceTypeDefinition> ResourceTypes,
    IReadOnlyList<LogicalResource> Resources
);

/// <summary>
/// 已导入的 Minecraft 实例记录。
/// </summary>
public sealed record GameInstance(
    string InstanceId,
    string DisplayName,
    string RootPath,
    string VolumeRoot,
    IReadOnlyList<string> TrackedTypeIds
);

/// <summary>
/// 实例本地“独立副本（受控副本）”跟踪记录（当实例选择不跟随主库更新且脱离硬链、或手动解绑转独立文件时登记）。
/// </summary>
public sealed record ControlledCopyRecord(
    string InstanceId,
    string ResourceId,
    string TypeId,
    string InstanceEntryName,
    string? SourceVersionId,
    long RecordedAtUtcTicks
);

/// <summary>
/// 全家桶预设（Profile）：保存一组常用资源的引用，采用增量叠加（只加不删）套用。
/// </summary>
public sealed record ResourceProfile(
    string ProfileId,
    string Name,
    string Description,
    IReadOnlyList<string> ResourceIds
);

/// <summary>
/// 存储在【work/data/machine-config.json】中的本机环境配置（与具体电脑盘符强相关）。
/// </summary>
public sealed record LocalMachineConfig(
    string? MasterVaultPath,
    Dictionary<string, string> ReplicaVaultPathsByVolume,
    IReadOnlyList<GameInstance> Instances,
    IReadOnlyList<ControlledCopyRecord> ControlledCopies,
    IReadOnlyList<ResourceProfile> Profiles
);
