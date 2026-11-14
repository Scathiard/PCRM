using System.Windows;
using McResourceManager.Core.Domain;
using McResourceManager.Core.Engine;
using Microsoft.Win32;

namespace McResourceManager.Desktop;

public partial class VaultChangeReviewDialog : OperationDialog
{
    private readonly IResourceManagerEngine _engine;
    private readonly string? _preselectedType;
    private readonly string? _preselectedInstance;
    private readonly bool _review;
    private List<IntakeRow> _rows = [];
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
        _rows.Clear();
        IntakeGrid.ItemsSource = null;
        var paths = SourcePaths.Text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!_review && paths.Length == 0) throw new ArgumentException("请选择或拖入资源文件。");
        var candidates = _review ? await _engine.ScanMasterVaultChangesAsync(ct)
            : await _engine.AnalyzeExternalImportPathsAsync(paths, _preselectedType, preselectedInstanceId: _preselectedInstance, ct: ct);
        var (machine, index) = await _engine.GetSnapshotAsync(ct);
        TypeColumn.ItemsSource = index!.ResourceTypes;
        var users = new List<(string ResourceId, IntakeTarget Target)>();
        var scanWarnings = new List<string>();
        if (candidates.Count > 0 && index.Resources.Count > 0)
            foreach (var instance in machine.Instances)
            {
                try
                {
                    var entries = await _engine.ResolveInstanceResourcesAsync(instance.InstanceId, ct);
                    foreach (var group in entries.Where(v => v.MatchedResourceId is not null).GroupBy(v => v.MatchedResourceId!))
                    {
                        var labels = group.Select(v => v.State switch
                        {
                            MountState.TamperedAlert => $"登记 {v.MatchedVersionLabel} · 内容异常",
                            MountState.ControlledCopy => $"{v.MatchedVersionLabel ?? "版本未记录"} · 独立副本",
                            _ => $"当前 {v.MatchedVersionLabel}"
                        });
                        var single = group.Count() == 1;
                        var caption = $"{instance.DisplayName} · {string.Join(" / ", labels)}" + (single ? "" : " · 有多个同资源条目，请先整理");
                        users.Add((group.Key, new(instance.InstanceId, caption, single)));
                    }
                }
                catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
                { scanWarnings.Add($"{instance.DisplayName}（{error.Message}）"); }
            }
        var usage = users.ToLookup(pair => pair.ResourceId, pair => pair.Target);
        _rows = candidates.Select(c => new IntakeRow(c, machine.Instances, index.Resources, usage)).ToList();
        IntakeGrid.ItemsSource = _rows;
        Message(candidates.Count == 0 ? "没有待处理的主库变动。" : $"已识别 {candidates.Count} 项。"
            + (candidates.Any(c => c.ConflictKind == VaultEntryConflictKind.ExternalInPlaceOverwrite) ? " 已共享的文件可能已被外部改写。确认将接纳当前内容，被覆盖的原版无法从主库回退。" : "")
            + (scanWarnings.Count > 0 ? " 未能读取以下实例的当前版本：" + string.Join("、", scanWarnings) : ""));
    }
    private async void Commit(object sender, RoutedEventArgs e)
    {
        IntakeGrid.CommitEdit(); IntakeGrid.CommitEdit();
        var selected = _rows.Where(r => r.Selected).ToArray();
        if (selected.Length == 0) { Message("请勾选需要入库的条目；重复项已默认跳过。"); return; }
        if (selected.Any(r => string.IsNullOrWhiteSpace(r.TypeId))) { Message("仍有条目未指定类型，请选择类型或取消勾选这些项。"); return; }
        await Perform(async ct =>
        {
            var decisions = selected.Select(row => row.Decision()).ToArray();
            var results = await _engine.CommitVaultIntakeAsync(decisions, ct);
            var warnings = results.Where(r => !r.Success || r.CompanionWarning is not null).Select(r => r.CompanionWarning).ToArray();
            Summary = $"已处理 {selected.Length} 项资源，部署成功 {results.Count(r => r.Success)} 项。" + string.Join("；", warnings);
            Finish();
        });
    }
}
