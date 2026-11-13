using System.Windows;
using McResourceManager.Core.Domain;
using McResourceManager.Core.Engine;

namespace McResourceManager.Desktop;

public partial class MainWindow
{
    private async void SetupClicked(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        new FirstRunOnboardingDialog(_engine) { Owner = this }.ShowDialog(); await RunUi(RefreshAsync);
    }
    private async void AddInstances(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        new FirstRunOnboardingDialog(_engine, _machine?.MasterVaultPath is not null) { Owner = this }.ShowDialog(); await RunUi(RefreshAsync);
    }
    private async void SetupReplica(object sender, RoutedEventArgs e)
    {
        if (_machine?.MasterVaultPath is null) { StatusText.Text = "请先配置主库。"; return; }
        new ReplicaSetupDialog(_engine) { Owner = this }.ShowDialog(); await RunUi(RefreshAsync);
    }
    private async void AddType(object sender, RoutedEventArgs e)
    {
        if (_index is null) { StatusText.Text = "请先配置主库。"; return; }
        new ResourceTypeDialog(_engine) { Owner = this }.ShowDialog(); await RunUi(RefreshAsync);
    }
    private async void ImportFiles(object sender, RoutedEventArgs e) => await ShowImportAsync([]);
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) { e.Handled = true; await ShowImportAsync(paths); }
    }
    private async Task ShowImportAsync(string[] paths)
    {
        if (_index is null) { StatusText.Text = "请先配置主库，再导入资源。"; return; }
        if (_operation is not null) return;
        var dialog = new VaultChangeReviewDialog(_engine, paths, _page == "Library" ? _typeId : null,
            _page == "Instances" ? _instanceId : null) { Owner = this };
        dialog.ShowDialog(); await RunUi(RefreshAsync);
        if (dialog.Summary is { } summary) StatusText.Text = summary;
    }
    private async void ReviewChanges(object sender, RoutedEventArgs e)
    {
        if (_index is null || _operation is not null) return;
        var dialog = new VaultChangeReviewDialog(_engine, [], reviewMaster: true) { Owner = this };
        dialog.ShowDialog(); await RunUi(RefreshAsync);
        if (dialog.Summary is { } summary) StatusText.Text = summary;
    }
    private async void HarvestInstances(object sender, RoutedEventArgs e)
    {
        if (_machine is null || _index is null || _operation is not null) return;
        var instances = _page == "Instances" && _instanceId is not null ? _machine.Instances.Where(i => i.InstanceId == _instanceId).ToArray() : _machine.Instances;
        var dialog = new HarvestDialog(_engine, instances) { Owner = this }; dialog.ShowDialog();
        await RunUi(RefreshAsync); if (dialog.Result is { } result) StatusText.Text = $"已收编 {result.LinkedCount} 项，本次节省 {result.SavedBytes / 1048576d:0.00} MB。" + string.Join("；", result.NonFatalWarnings);
    }
    private async void HealthCheck(object sender, RoutedEventArgs e) => await RunUi(async ct =>
    {
        await RefreshAsync(ct);
        if (_index is null) { HealthReport.Text = "请先配置主库。"; return; }
        var changes = await _engine.ScanMasterVaultChangesAsync(ct);
        var lines = new List<string> { $"检查时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}", $"主库待确认变动：{changes.Count} 项", SavingsLabel.Text };
        lines.AddRange(_scanWarnings.Select(w => $"未完成检查：{w}"));
        foreach (var instance in _machine!.Instances)
        {
            var states = _states[instance.InstanceId];
            lines.Add($"\n{instance.DisplayName}：{states.Count} 项资源");
            lines.AddRange(states.Where(v => v.State is MountState.TamperedAlert or MountState.ControlledCopy or MountState.WildUntracked)
                .Select(v => $"  {(v.State == MountState.TamperedAlert ? "内容异常" : v.State == MountState.ControlledCopy ? "独立副本" : "未入库")} · {v.InstanceEntryName}"));
            if (states.Any(v => v.State is MountState.ControlledCopy or MountState.WildUntracked))
            {
                try
                {
                    var harvest = await _engine.ScanInstanceForHarvestAsync(instance.InstanceId, instance.TrackedTypeIds, ct);
                    lines.AddRange(harvest.Where(c => c.Category == HarvestCategory.SameHashCanLink)
                        .Select(c => $"  可恢复共享 · {c.EntryName}（内容与主库一致，可通过收编向导重新连接）"));
                }
                catch (System.IO.IOException error) { lines.Add($"  未完成共享恢复检查：{error.Message}"); }
            }
        }
        HealthReport.Text = string.Join(Environment.NewLine, lines);
        StatusText.Text = "检查完成；主库变动可在右上角确认，实例异常可从版本菜单切换或重新收编。";
    });
}
