using McResourceManager.Core.Domain;

namespace McResourceManager.Core.Engine;

/// <summary>
/// 导入或主库变动扫描时的冲突类型（支持【资源导入确认】与【主库变动过目】共用同一弹窗）。
/// </summary>
public enum VaultEntryConflictKind
{
    /// <summary>无冲突：全新文件或不同文件名的同系列新版本。</summary>
    None = 0,

    /// <summary>内容完全相同：主库中已存在相同 SHA256 的版本，默认跳过不重复入库。</summary>
    ExactDuplicateInVault = 1,

    /// <summary>同名但内容不同（经由软件导入）：入库时主库自动加区分后缀并存，投放时还原标准名，绝不覆盖主库已有版本。</summary>
    SameNameDifferentContentWillCoexist = 2,

    /// <summary>外部原地覆盖（用户在资源管理器直接替换了主库同名文件）：FileID 未变但大小/修改时间已变，列出受影响实例。</summary>
    ExternalInPlaceOverwrite = 3
}

/// <summary>
/// 待确认入库条目（统一承载“外部拖入/选择导入”与“主库目录变动检测”两种来源，共用同一确认弹窗）。
/// 当源自外部导入且自动识别未命中时，RecognizedTypeId 为 null，UI 必须要求用户手动指定类型后才允许入库。
/// </summary>
public sealed record VaultIntakeCandidateItem(
    string SourceFullPath,
    string EntryName,
    bool IsDirectory,
    bool IsExternalImport,
    long TotalSizeBytes,
    string? RecognizedTypeId,
    string? RecognitionReason,
    string? SuggestedResourceId,
    string? SuggestedResourceName,
    string GuessedVersionLabel,
    VaultEntryConflictKind ConflictKind,
    IReadOnlyList<string> PreselectedInstanceIds,
    IReadOnlyList<string> AffectedInstanceIds
);

/// <summary>
/// 用户在【导入 / 主库变动确认弹窗】中拍板的单条决定（TypeId 必填，不得为空）。
/// 若 IsExternalImport 为 true，引擎仅复制源文件到主库，绝不移动、改名或删除用户外部源文件。
/// </summary>
public sealed record VaultIntakeCommitDecision(
    string SourceFullPath,
    bool IsExternalImport,
    string TypeId,
    string? MergeIntoResourceId,
    string DisplayResourceName,
    string VersionLabel,
    IReadOnlyList<string> InstanceIdsToDeployOrUpgrade
);

/// <summary>
/// 实例中单个资源条目的实时状态解析结果（供【实例】页签中栏展示）。
/// </summary>
public sealed record InstanceResourceEntryView(
    string TypeId,
    string InstanceEntryName,
    string InstanceEntryFullPath,
    MountState State,
    string? MatchedResourceId,
    string? MatchedResourceName,
    string? MatchedVersionId,
    string? MatchedVersionLabel,
    string? LatestVersionId,
    string? LatestVersionLabel,
    IReadOnlyList<ResourceVersion> AvailableVersions
);

/// <summary>
/// 存量实例扫描向导（首次启动引导 & 工具 1）的分类对比项。
/// </summary>
public enum HarvestCategory
{
    SameHashCanLink,
    SameResourceDifferentContent,
    NewWildResource
}

public enum HarvestActionKind
{
    Skip = 0,
    ConvertSameHashToLink = 1,
    ImportToMasterAndLink = 2,
    ReplaceWithMasterLatest = 3,
    KeepAsControlledCopy = 4
}

public sealed record HarvestCandidateItem(
    string InstanceId,
    string TypeId,
    string EntryName,
    string FullPath,
    bool IsDirectory,
    long TotalSizeBytes,
    HarvestCategory Category,
    string? MatchedResourceId,
    string? MatchedVersionId
);

public sealed record HarvestExecutionDecision(
    string InstanceId,
    string TypeId,
    string EntryName,
    HarvestActionKind Action,
    string? TargetResourceId
);

public sealed record HarvestExecutionResult(
    int LinkedCount,
    int ImportedCount,
    long SavedBytes,
    IReadOnlyList<string> NonFatalWarnings
);

public sealed record ProbedInstanceCandidate(
    string DisplayName,
    string RootPath,
    string VolumeRoot,
    bool AlreadyRegistered
);

public sealed record DeploymentResult(
    bool Success,
    string TargetEntryFullPath,
    string? CompanionWarning
);

/// <summary>
/// 核心业务引擎统一接口（覆盖 P0 首版全部 UI 交互所需入口，含最小资源导入模块）。
/// 【防屎山硬约束】：
/// 1. WPF 界面层（McResourceManager.Desktop）只允许调用此接口，严禁在 UI 层直接调用 File / Directory API。
/// 2. 实现类按职责拆分为小文件，每个实现文件不超过 250 行。
/// 3. 严禁引入数据库框架或 Git 仓库，所有持久化仅通过原子替换读写 主库/.pcrm/index.json 与 work/data/machine-config.json。
/// </summary>
public interface IResourceManagerEngine
{
    // --- A. 状态快照与首次配置 / 盘符缓存区配置 ---

    Task<(LocalMachineConfig MachineConfig, MasterVaultIndex? MasterIndex)> GetSnapshotAsync(CancellationToken ct = default);
    Task<MasterVaultIndex> ConfigureMasterVaultAsync(string masterVaultPath, CancellationToken ct = default);
    Task ConfigureReplicaVaultAsync(string volumeRoot, string replicaVaultPath, CancellationToken ct = default);

    // --- B. 资源类型、详情编辑与手动归组 ---

    Task<ResourceTypeDefinition> UpsertResourceTypeAsync(ResourceTypeDefinition typeDef, CancellationToken ct = default);

    Task UpdateResourceMetadataAsync(
        string resourceId,
        string displayName,
        string description,
        string? newCoverSourceFilePath,
        CancellationToken ct = default);

    Task MergeOrSplitVersionAsync(
        string sourceResourceId,
        string versionId,
        string? targetMergeResourceId,
        CancellationToken ct = default);

    // --- C. 实例探测与导入管理 ---

    Task<IReadOnlyList<ProbedInstanceCandidate>> ProbeInstanceDirectoryAsync(string selectedPath, CancellationToken ct = default);

    Task<IReadOnlyList<GameInstance>> RegisterInstancesAsync(
        IReadOnlyList<(string DisplayName, string RootPath)> instances,
        CancellationToken ct = default);

    Task UnregisterInstanceAsync(string instanceId, CancellationToken ct = default);

    // --- D. 软件内资源导入模块与主库变动检测（共用确认模型与弹窗） ---

    /// <summary>
    /// 识别并分析用户从外部拖入或选择导入的文件/文件夹（不修改源文件）：
    /// 按内置规则（含 shaders/ 为光影、含 pack.mcmeta 为资源包、.ysm 为 YSM 模型、枪包结构为 TaCZ、或匹配已有资源词干）识别类型；
    /// 识别失败时 RecognizedTypeId 返回 null（由用户在确认弹窗中手动指定，禁止随机归类或空类型入库）。
    /// </summary>
    Task<IReadOnlyList<VaultIntakeCandidateItem>> AnalyzeExternalImportPathsAsync(
        IReadOnlyList<string> sourcePaths,
        string? preselectedTypeId = null,
        string? targetResourceId = null,
        string? preselectedInstanceId = null,
        CancellationToken ct = default);

    /// <summary>扫描主库各类型目录（跳过忽略规则文件与内部临时项），返回未登记的新增文件或同 FileID 覆写项。</summary>
    Task<IReadOnlyList<VaultIntakeCandidateItem>> ScanMasterVaultChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// 提交用户在【导入 / 主库变动确认弹窗】中的决定（TypeId 必填）：
    /// - 若 IsExternalImport 为 true：将源文件复制到 主库/&lt;类型&gt;/（同名异版自动加区分后缀并设 RestoreStandardNameOnDeploy = true，绝不覆盖已有版本；不改动外部源文件）；
    /// - 写入 主库/.pcrm/index.json，并对 InstanceIdsToDeployOrUpgrade 中的实例执行硬链接投放或切换（顺带更新光影/材质配置引用与 .txt 参数继承）。
    /// </summary>
    Task<IReadOnlyList<DeploymentResult>> CommitVaultIntakeAsync(
        IReadOnlyList<VaultIntakeCommitDecision> decisions,
        CancellationToken ct = default);

    // --- E. 实例状态实时推导与版本投放/切换 ---

    /// <summary>实时推导指定实例下各资源的挂载状态（先调用 NtfsDriver.RecoverInterruptedSwapsInDirectory 恢复中断残留）。</summary>
    Task<IReadOnlyList<InstanceResourceEntryView>> ResolveInstanceResourcesAsync(string instanceId, CancellationToken ct = default);

    Task<DeploymentResult> DeployOrSwitchVersionAsync(
        string instanceId,
        string resourceId,
        string targetVersionId,
        bool updateCompanionConfig = true,
        CancellationToken ct = default);

    Task DetachToControlledCopyAsync(string instanceId, string typeId, string instanceEntryName, CancellationToken ct = default);
    Task RemoveFromInstanceAsync(string instanceId, string typeId, string instanceEntryName, CancellationToken ct = default);

    // --- F. 存量整合包扫描与批量收编（首次启动引导 & 工具 1） ---

    Task<IReadOnlyList<HarvestCandidateItem>> ScanInstanceForHarvestAsync(
        string instanceId,
        IReadOnlyList<string> typeIds,
        CancellationToken ct = default);

    Task<HarvestExecutionResult> ExecuteHarvestAsync(
        IReadOnlyList<HarvestExecutionDecision> decisions,
        CancellationToken ct = default);
}
