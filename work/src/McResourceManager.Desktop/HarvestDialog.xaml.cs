using System.Windows;
using McResourceManager.Core.Domain;
using McResourceManager.Core.Engine;

namespace McResourceManager.Desktop;

internal sealed class HarvestRow
{
    public required HarvestCandidateItem Candidate { get; init; }
    public required string InstanceName { get; init; }
    public bool Selected { get; set; } = true;
    public string ActionLabel { get; set; } = "复制入库并共享";
    public string[] Options => Candidate.Category switch
    {
        HarvestCategory.SameHashCanLink => ["相同内容转共享", "保留独立副本", "跳过"],
        HarvestCategory.SameResourceDifferentContent => ["复制入库并共享", "替换为主库最新版", "保留独立副本", "跳过"],
        _ => ["复制入库并共享", "跳过"]
    };
    public string Category => Candidate.Category switch { HarvestCategory.SameHashCanLink => "内容相同，可合并", HarvestCategory.SameResourceDifferentContent => "同系列的不同版本", _ => "未入库的新资源" };
    public HarvestActionKind Action => ActionLabel switch
    { "相同内容转共享" => HarvestActionKind.ConvertSameHashToLink, "替换为主库最新版" => HarvestActionKind.ReplaceWithMasterLatest,
        "保留独立副本" => HarvestActionKind.KeepAsControlledCopy, "跳过" => HarvestActionKind.Skip, _ => HarvestActionKind.ImportToMasterAndLink };
}

public partial class HarvestDialog : OperationDialog
{
    private readonly IResourceManagerEngine _engine;
    private readonly IReadOnlyList<GameInstance> _instances;
    private List<HarvestRow> _rows = [];
    public HarvestExecutionResult? Result { get; private set; }
    public HarvestDialog(IResourceManagerEngine engine, IReadOnlyList<GameInstance> instances)
    { _engine = engine; _instances = instances; InitializeComponent(); }
    private async void InitializeDialog(object sender, RoutedEventArgs e) => await Perform(async ct =>
    {
        foreach (var instance in _instances)
        {
            Message($"正在扫描 {instance.DisplayName}…");
            var candidates = await _engine.ScanInstanceForHarvestAsync(instance.InstanceId, instance.TrackedTypeIds, ct);
            _rows.AddRange(candidates.Select(c => new HarvestRow { Candidate = c, InstanceName = instance.DisplayName,
                ActionLabel = c.Category == HarvestCategory.SameHashCanLink ? "相同内容转共享" : "复制入库并共享" }));
        }
        HarvestGrid.ItemsSource = _rows;
        Message(_rows.Count == 0 ? "没有需要收编的资源。可以关闭并进入工作台。" : $"发现 {_rows.Count} 项资源，请确认各项操作。");
    });
    private async void Execute(object sender, RoutedEventArgs e)
    {
        HarvestGrid.CommitEdit(); HarvestGrid.CommitEdit();
        if (_rows.Any(r => r.Selected && r.Action == HarvestActionKind.ReplaceWithMasterLatest)
            && MessageBox.Show(this, "已选择用主库最新版替换部分实例文件，确认继续？", "确认替换", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Perform(async ct =>
        {
            Result = await _engine.ExecuteHarvestAsync(_rows.Where(r => r.Selected).Select(r => new HarvestExecutionDecision(r.Candidate.InstanceId,
                r.Candidate.TypeId, r.Candidate.EntryName, r.Action, r.Candidate.MatchedResourceId)).ToArray(), ct);
            Finish();
        });
    }
}
