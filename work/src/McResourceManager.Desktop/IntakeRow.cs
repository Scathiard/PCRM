using McResourceManager.Core.Domain;
using McResourceManager.Core.Engine;

namespace McResourceManager.Desktop;

internal sealed record MergeChoice(string? Id, string Name);
internal sealed record IntakeTarget(string Id, string Caption, bool CanSelect = true)
{
    public bool Selected { get; set; }
}

internal sealed class IntakeRow : EditableRow
{
    private readonly IReadOnlyList<GameInstance> _instances;
    private readonly IReadOnlyList<LogicalResource> _resources;
    private readonly ILookup<string, IntakeTarget> _users;
    private string? _typeId;
    private string _groupId;
    private bool _selected;

    public IntakeRow(VaultIntakeCandidateItem candidate, IReadOnlyList<GameInstance> instances,
        IReadOnlyList<LogicalResource> resources, ILookup<string, IntakeTarget> users)
    {
        Candidate = candidate;
        _instances = instances;
        _resources = resources;
        _users = users;
        _typeId = candidate.RecognizedTypeId;
        _groupId = candidate.SuggestedResourceId ?? "";
        _selected = candidate.ConflictKind != VaultEntryConflictKind.ExactDuplicateInVault;
        Name = candidate.SuggestedResourceName ?? candidate.EntryName;
        Version = candidate.GuessedVersionLabel;
        RefreshTargets(candidate.PreselectedInstanceIds);
    }

    public VaultIntakeCandidateItem Candidate { get; }
    public string Name { get; set; }
    public string Version { get; set; }
    public bool Selected { get => _selected; set { _selected = value; Changed(); } }
    public string? TypeId
    {
        get => _typeId;
        set
        {
            if (_typeId == value) return;
            _typeId = value;
            _groupId = "";
            Changed();
            Changed(nameof(Groups));
            Changed(nameof(GroupId));
            RefreshTargets([]);
        }
    }
    public IEnumerable<MergeChoice> Groups => new[] { new MergeChoice("", "作为新资源") }
        .Concat(_resources.Where(r => r.TypeId == TypeId).Select(r => new MergeChoice(r.ResourceId, r.DisplayName)));
    public string GroupId
    {
        get => _groupId;
        set
        {
            // WPF temporarily clears SelectedValue while replacing the combo box's ItemsSource.
            if (value is null || _groupId == value) return;
            _groupId = value;
            Changed();
            RefreshTargets([]);
        }
    }
    public IReadOnlyList<IntakeTarget> Targets { get; private set; } = [];
    public string TargetHeading => TypeId is null ? "选择类型后可指定部署实例" : GroupId.Length == 0 ? "可选部署到实例"
        : Targets.Count == 0 ? "暂无实例使用此资源" : "选择要升级的实例";
    public string Conflict => Candidate.ConflictKind switch
    {
        VaultEntryConflictKind.ExactDuplicateInVault => "已存在 · 默认跳过",
        VaultEntryConflictKind.SameNameDifferentContentWillCoexist => "同名不同内容 · 保留两版",
        VaultEntryConflictKind.ExternalInPlaceOverwrite => $"外部覆盖 · 影响 {Candidate.AffectedInstanceIds.Count} 个实例",
        _ => TypeId is null ? "需手动指定类型" : "可入库"
    };

    private void RefreshTargets(IReadOnlyList<string> defaults)
    {
        Targets = TypeId is null ? [] : GroupId.Length == 0
            ? _instances.Where(i => i.TrackedTypeIds.Contains(TypeId)).Select(i =>
                new IntakeTarget(i.InstanceId, i.DisplayName) { Selected = defaults.Contains(i.InstanceId) }).ToArray()
            : _users[GroupId].Select(target => target with { Selected = false }).ToArray();
        Changed(nameof(Targets));
        Changed(nameof(TargetHeading));
        Changed(nameof(Conflict));
    }

    public VaultIntakeCommitDecision Decision() => new(Candidate.SourceFullPath, Candidate.IsExternalImport, TypeId!,
        GroupId.Length == 0 ? null : GroupId, Name, Version, Targets.Where(t => t.CanSelect && t.Selected).Select(t => t.Id).ToArray());
}
