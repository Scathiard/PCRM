using System.Windows;
using McResourceManager.Core.Engine;
using Microsoft.Win32;

namespace McResourceManager.Desktop;

internal sealed record MergeChoice(string? Id, string Name);
internal sealed class IntakeRow
{
    public required VaultIntakeCandidateItem Candidate { get; init; }
    public bool Selected { get; set; }
    public string? TypeId { get; set; }
    public string? MergeId { get; set; }
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Conflict => Candidate.ConflictKind switch
    {
        VaultEntryConflictKind.ExactDuplicateInVault => "已存在 · 默认跳过",
        VaultEntryConflictKind.SameNameDifferentContentWillCoexist => "同名不同内容 · 保留两版",
        VaultEntryConflictKind.ExternalInPlaceOverwrite => $"外部覆盖 · 影响 {Candidate.AffectedInstanceIds.Count} 个实例",
        _ => Candidate.RecognizedTypeId is null ? "需手动指定类型" : "可安全入库"
    };
}

public partial class VaultChangeReviewDialog : OperationDialog
{
    private readonly IResourceManagerEngine _engine;
    private readonly string? _preselectedType;
    private readonly string? _preselectedInstance;
    private readonly bool _review;
    private List<IntakeRow> _rows = [];
    private List<SelectableInstance> _instances = [];
    public string? Summary { get; private set; }
    public VaultChangeReviewDialog(IResourceManagerEngine engine, string[] paths, string? preselectedType = null,
        string? preselectedInstance = null, bool reviewMaster = false)
    {
        _engine = engine; _preselectedType = string.IsNullOrWhiteSpace(preselectedType) ? null : preselectedType;
        _preselectedInstance = preselectedInstance; _review = reviewMaster;
        InitializeComponent(); SourcePaths.Text = string.Join(Environment.NewLine, paths);
        if (_review) { Heading.Text = "确认主库变动"; SourcePanel.Visibility = Visibility.Collapsed; }
    }
    private async void InitializeDialog(object sender, RoutedEventArgs e) => await Perform(async ct =>
    {
        var (machine, index) = await _engine.GetSnapshotAsync(ct);
        TypeColumn.ItemsSource = index!.ResourceTypes;
        MergeColumn.ItemsSource = new[] { new MergeChoice(null, "作为新资源") }.Concat(index.Resources.Select(r => new MergeChoice(r.ResourceId, r.DisplayName))).ToArray();
        _instances = machine.Instances.Select(i => new SelectableInstance { Id = i.InstanceId, Name = i.DisplayName, Selected = i.InstanceId == _preselectedInstance }).ToList();
        InstanceChoices.ItemsSource = _instances;
        if (_review || !string.IsNullOrWhiteSpace(SourcePaths.Text)) await AnalyzeAsync(ct);
    });
    private void PickFiles(object sender, RoutedEventArgs e)
    { var picker = new OpenFileDialog { Multiselect = true, Title = "选择资源文件", Filter = "所有资源|*.*" }; if (picker.ShowDialog(this) == true) SourcePaths.Text = string.Join(Environment.NewLine, picker.FileNames); }
    private void PickFolder(object sender, RoutedEventArgs e)
    { var picker = new OpenFolderDialog { Title = "选择一个完整资源文件夹" }; if (picker.ShowDialog(this) == true) SourcePaths.Text = picker.FolderName; }
    private async void FilesDropped(object sender, DragEventArgs e)
    {
        if (_review) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        { SourcePaths.Text = string.Join(Environment.NewLine, paths); e.Handled = true; await Perform(AnalyzeAsync); }
    }
    private async void Analyze(object sender, RoutedEventArgs e) => await Perform(AnalyzeAsync);
    private async Task AnalyzeAsync(CancellationToken ct)
    {
        var paths = SourcePaths.Text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!_review && paths.Length == 0) throw new ArgumentException("请选择或拖入资源文件。");
        var candidates = _review ? await _engine.ScanMasterVaultChangesAsync(ct)
            : await _engine.AnalyzeExternalImportPathsAsync(paths, _preselectedType, preselectedInstanceId: _preselectedInstance, ct: ct);
        _rows = candidates.Select(c => new IntakeRow { Candidate = c, Selected = c.ConflictKind != VaultEntryConflictKind.ExactDuplicateInVault,
            TypeId = c.RecognizedTypeId, MergeId = c.SuggestedResourceId, Name = c.SuggestedResourceName ?? c.EntryName, Version = c.GuessedVersionLabel }).ToList();
        IntakeGrid.ItemsSource = _rows;
        Message(candidates.Count == 0 ? "没有待处理的主库变动。" : $"已识别 {candidates.Count} 项；请确认类型、归组和版本，勾选后才会修改文件。"
            + (candidates.Any(c => c.ConflictKind == VaultEntryConflictKind.ExternalInPlaceOverwrite) ? " 外部覆盖前的内容无法自动恢复；确认表示接纳当前内容为新版本。" : ""));
    }
    private async void Commit(object sender, RoutedEventArgs e)
    {
        IntakeGrid.CommitEdit(); IntakeGrid.CommitEdit();
        var selected = _rows.Where(r => r.Selected).ToArray();
        if (selected.Length == 0) { Message("请勾选需要入库的条目；重复项已默认跳过。"); return; }
        if (selected.Any(r => string.IsNullOrWhiteSpace(r.TypeId))) { Message("仍有条目未指定类型，请选择类型或取消勾选这些项。"); return; }
        await Perform(async ct =>
        {
            var instances = _instances.Where(i => i.Selected).Select(i => i.Id).ToArray();
            var decisions = selected.Select(r => new VaultIntakeCommitDecision(r.Candidate.SourceFullPath, r.Candidate.IsExternalImport,
                r.TypeId!, r.MergeId, r.Name, r.Version, instances)).ToArray();
            var results = await _engine.CommitVaultIntakeAsync(decisions, ct);
            var warnings = results.Where(r => !r.Success || r.CompanionWarning is not null).Select(r => r.CompanionWarning).ToArray();
            Summary = $"已处理 {selected.Length} 项资源，部署成功 {results.Count(r => r.Success)} 项。" + string.Join("；", warnings);
            Finish();
        });
    }
}
