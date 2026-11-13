using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using McResourceManager.Core.Domain;

namespace McResourceManager.Desktop;

public partial class MainWindow
{
    private void ResourceSelected(object sender, SelectionChangedEventArgs e)
    {
        var row = ResourceList.SelectedItem as ResourceRow;
        SaveDetailsButton.IsEnabled = ChangeCoverButton.IsEnabled = GroupVersionButton.IsEnabled = row?.Resource is not null;
        DetachButton.IsEnabled = _page == "Instances" && row?.State is MountState.LinkedLatest or MountState.LinkedOldVersion;
        RemoveButton.IsEnabled = _page == "Instances" && row is not null;
        if (row is null)
        {
            DetailTitle.Text = "选择一个资源"; DetailCover.Source = null; DetailState.Text = "在这里查看版本、简介和使用情况。";
            NameEditor.Text = DescriptionEditor.Text = VersionHistory.Text = UsageText.Text = AdvancedInfo.Text = "";
            return;
        }
        DetailTitle.Text = row.Name; DetailState.Text = row.StateText; DetailCover.Source = row.Cover;
        NameEditor.Text = row.Name; DescriptionEditor.Text = row.Resource?.Description ?? "";
        NameEditor.IsEnabled = DescriptionEditor.IsEnabled = row.Resource is not null;
        VersionHistory.Text = row.Resource is null ? "收编后即可记录与切换版本。" : string.Join(Environment.NewLine, row.Resource.Versions
            .OrderByDescending(v => v.CreatedAtUtcTicks).Select(v => $"{v.VersionLabel}   ·   {new DateTime(v.CreatedAtUtcTicks, DateTimeKind.Utc).ToLocalTime():yyyy-MM-dd} {(v.VersionId == row.Resource.LatestVersionId ? "· 最新" : "")}"));
        var users = _machine?.Instances.Where(i => _states.TryGetValue(i.InstanceId, out var views) && views.Any(v => v.MatchedResourceId == row.ResourceId)).Select(i => i.DisplayName) ?? [];
        UsageText.Text = row.Resource is null ? "" : $"使用实例：{string.Join("、", users.DefaultIfEmpty("暂无"))}";
        var version = row.Resource?.Versions.FirstOrDefault(v => v.VersionId == row.VersionId);
        AdvancedInfo.Text = $"路径：{row.FullPath}\n" + (version is null ? "" : $"大小：{version.TotalSizeBytes:N0} 字节\n" + string.Join("\n", version.Files.Take(8).Select(f => $"{f.RelativePath}\nFileID：{f.MasterFileId}\nSHA256：{f.Sha256}")));
    }
    private void OpenVersions(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.Tag is not ResourceRow row || row.Resource is null) { HarvestInstances(sender, e); return; }
        _versionRow = row; ResourceList.SelectedItem = row;
        DeployInstance.SelectedValue = _instanceId;
        RenderVersionChoices(row.VersionId);
        VersionPopup.PlacementTarget = button; VersionPopup.IsOpen = true;
    }
    private void VersionTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VersionPopup?.IsOpen == true) RenderVersionChoices((VersionList.SelectedItem as VersionChoice)?.Id);
    }
    private void RenderVersionChoices(string? selectedVersion)
    {
        if (_versionRow?.Resource is not { } resource) return;
        var current = DeployInstance.SelectedItem is GameInstance instance && _states.TryGetValue(instance.InstanceId, out var states)
            ? states.Where(v => v.MatchedResourceId == resource.ResourceId).Select(v => v.MatchedVersionId).Distinct().ToArray() : [];
        var currentId = current.Length == 1 ? current[0] : null;
        VersionList.ItemsSource = resource.Versions.OrderByDescending(v => v.CreatedAtUtcTicks)
            .Select(v => new VersionChoice(v.VersionId, $"{v.VersionLabel}{(v.VersionId == currentId ? " · 当前" : "")}{(v.VersionId == resource.LatestVersionId ? " · 最新" : "")}", "", v.VersionId == resource.LatestVersionId)).ToArray();
        VersionList.SelectedItem = VersionList.Items.Cast<VersionChoice>().FirstOrDefault(v => v.Id == selectedVersion);
    }
    private async void SwitchVersion(object sender, RoutedEventArgs e)
    {
        if (_versionRow?.ResourceId is not { } resourceId || VersionList.SelectedItem is not VersionChoice version || DeployInstance.SelectedItem is not GameInstance instance)
        { StatusText.Text = "请选择版本和目标实例。"; return; }
        VersionPopup.IsOpen = false;
        await RunUi(async ct =>
        {
            var result = await _engine.DeployOrSwitchVersionAsync(instance.InstanceId, resourceId, version.Id, UpdateCompanion.IsChecked == true, ct);
            await RefreshAsync(ct); StatusText.Text = result.CompanionWarning ?? "版本已切换；其他实例保持原版本。";
        });
    }
    private async void SaveDetails(object sender, RoutedEventArgs e)
    {
        if (ResourceList.SelectedItem is not ResourceRow { ResourceId: { } id }) return;
        var name = NameEditor.Text; var description = DescriptionEditor.Text;
        await RunUi(async ct => { await _engine.UpdateResourceMetadataAsync(id, name, description, null, ct); await RefreshAsync(ct); StatusText.Text = "详情已保存。"; });
    }
    private async void GroupVersion(object sender, RoutedEventArgs e)
    {
        if (_index is null || ResourceList.SelectedItem is not ResourceRow { Resource: { } resource }) return;
        new VersionGroupingDialog(_engine, resource, _index) { Owner = this }.ShowDialog(); await RunUi(RefreshAsync);
    }
    private async void ChangeCover(object sender, RoutedEventArgs e)
    {
        if (ResourceList.SelectedItem is not ResourceRow { ResourceId: { } id }) return;
        var picker = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp", Title = "选择封面" };
        if (picker.ShowDialog(this) != true) return;
        var name = NameEditor.Text; var description = DescriptionEditor.Text;
        await RunUi(async ct => { await _engine.UpdateResourceMetadataAsync(id, name, description, picker.FileName, ct); await RefreshAsync(ct); });
    }
    private void LocateFile(object sender, RoutedEventArgs e)
    {
        if (ResourceList.SelectedItem is not ResourceRow row) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = $"/select,\"{row.FullPath}\"" }); }
        catch (Exception error) { StatusText.Text = error.Message; }
    }
    private async void DetachResource(object sender, RoutedEventArgs e)
    {
        if (_page != "Instances" || _instanceId is null || ResourceList.SelectedItem is not ResourceRow row) return;
        await RunUi(async ct => { await _engine.DetachToControlledCopyAsync(_instanceId, row.TypeId, row.FileName, ct); await RefreshAsync(ct); StatusText.Text = "已转为独立副本，此实例可单独保留。"; });
    }
    private async void RemoveResource(object sender, RoutedEventArgs e)
    {
        if (_page != "Instances" || _instanceId is null || ResourceList.SelectedItem is not ResourceRow row) return;
        if (MessageBox.Show(this, $"从当前实例移除“{row.Name}”？主库中的资源保留。", "确认移除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunUi(async ct => { await _engine.RemoveFromInstanceAsync(_instanceId, row.TypeId, row.FileName, ct); await RefreshAsync(ct); StatusText.Text = "已从当前实例移除。"; });
    }
}
