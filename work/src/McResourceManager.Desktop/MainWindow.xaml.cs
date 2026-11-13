using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using McResourceManager.Core.Domain;
using McResourceManager.Core.Engine;

namespace McResourceManager.Desktop;

public partial class MainWindow : Window
{
    private readonly IResourceManagerEngine _engine;
    private LocalMachineConfig? _machine;
    private MasterVaultIndex? _index;
    private readonly Dictionary<string, IReadOnlyList<InstanceResourceEntryView>> _states = [];
    private readonly List<string> _scanWarnings = [];
    private List<ResourceRow> _rows = [];
    private string _page = "Instances";
    private string? _instanceId;
    private string? _typeId;
    private bool _loading;
    private bool _ready;
    private CancellationTokenSource? _operation;
    private ResourceRow? _versionRow;
    public MainWindow(IResourceManagerEngine engine) { _engine = engine; Style = (Style)Application.Current.FindResource(typeof(Window)); InitializeComponent(); }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RunUi(async ct => await RefreshAsync(ct));
        if (_machine?.MasterVaultPath is null || _machine.Instances.Count == 0)
        {
            new FirstRunOnboardingDialog(_engine) { Owner = this }.ShowDialog();
            await RunUi(async ct => await RefreshAsync(ct));
        }
        _ready = true;
        await CheckChangesOnFocusAsync();
    }

    private async Task RunUi(Func<CancellationToken, Task> work)
    {
        if (_operation is not null) return;
        _operation = new(); BusyBar.Visibility = CancelButton.Visibility = Visibility.Visible;
        try { await work(_operation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "操作已取消；已经完成的条目保留，可刷新查看。"; }
        catch (Exception error) { StatusText.Text = error.Message; StatusText.ToolTip = error.ToString(); }
        finally { _operation.Dispose(); _operation = null; BusyBar.Visibility = CancelButton.Visibility = Visibility.Collapsed; }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        (_machine, _index) = await _engine.GetSnapshotAsync(ct);
        _states.Clear();
        _scanWarnings.Clear();
        if (_index is not null)
            foreach (var instance in _machine.Instances)
            {
                try { _states[instance.InstanceId] = await _engine.ResolveInstanceResourcesAsync(instance.InstanceId, ct); }
                catch (System.IO.IOException error) { _states[instance.InstanceId] = []; _scanWarnings.Add($"{instance.DisplayName}：{error.Message}"); }
            }
        _instanceId = _machine.Instances.Any(i => i.InstanceId == _instanceId) ? _instanceId : _machine.Instances.FirstOrDefault()?.InstanceId;
        MasterPathText.Text = _machine.MasterVaultPath ?? "尚未配置主库";
        DeployInstance.ItemsSource = _machine.Instances;
        if (_scanWarnings.Count > 0) StatusText.Text = string.Join("；", _scanWarnings);
        UpdatePage();
        long savings = _states.Values.SelectMany(s => s).Where(v => v.State is MountState.LinkedLatest or MountState.LinkedOldVersion)
            .Sum(v => v.AvailableVersions.FirstOrDefault(version => version.VersionId == v.MatchedVersionId)?.TotalSizeBytes ?? 0);
        savings -= _index?.Resources.SelectMany(r => r.Versions).SelectMany(v => v.Files).Sum(f => f.Size * f.ReplicaFileIdsByVolume.Count) ?? 0;
        SavingsLabel.Text = $"共享节省约 {Math.Max(0, savings) / 1073741824d:0.00} GB";
        SavingsLabel.ToolTip = "按已登记共享文件的逻辑大小估算，扣除账本中的跨盘缓存；不等于文件系统实际分配空间。";
    }

    private void UpdatePage()
    {
        _loading = true;
        foreach (Button tab in PageTabs.Children)
        {
            var selected = (string)tab.Tag == _page;
            tab.Background = (System.Windows.Media.Brush)FindResource(selected ? "Brush.Panel" : "Brush.Transparent");
            tab.Foreground = (System.Windows.Media.Brush)FindResource(selected ? "Brush.Accent" : "Brush.Panel");
        }
        WorkspaceGrid.Visibility = _page is "Instances" or "Library" ? Visibility.Visible : Visibility.Collapsed;
        ToolsPage.Visibility = _page == "Tools" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = _page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        var library = _page == "Library";
        Breadcrumb.Text = _page switch { "Library" => "资源库 / 全局资源", "Tools" => "工具 / 整理与检查", "Settings" => "设置 / 工作台", _ => "实例 / 我的资源" };
        SidebarTitle.Text = library ? "资源类型" : "我的实例";
        AddInstanceButton.Visibility = library ? Visibility.Collapsed : Visibility.Visible;
        AddTypeButton.Visibility = library ? Visibility.Visible : Visibility.Collapsed;
        var items = library ? new[] { new SidebarItem("", "全部资源", "所有类型，共用同一资源库") }.Concat(_index?.ResourceTypes.Select(t => new SidebarItem(t.TypeId, t.Name, t.InstanceRelativePath)) ?? []).ToArray()
            : _machine?.Instances.Select(i => new SidebarItem(i.InstanceId, i.DisplayName, i.RootPath)).ToArray() ?? [];
        Sidebar.ItemsSource = items;
        Sidebar.SelectedItem = items.FirstOrDefault(i => i.Id == (library ? _typeId ?? "" : _instanceId));
        _loading = false;
        RenderRows();
    }

    private void RenderRows()
    {
        if (_index is null || _machine is null) { _rows = []; ResourceList.ItemsSource = null; return; }
        var library = _page == "Library";
        ContentTitle.Text = library ? "全局资源库" : _machine.Instances.FirstOrDefault(i => i.InstanceId == _instanceId)?.DisplayName ?? "先添加一个实例";
        _rows = library ? _index.Resources.Where(r => string.IsNullOrEmpty(_typeId) || r.TypeId == _typeId).Select(r =>
        {
            var version = r.Versions.Single(v => v.VersionId == r.LatestVersionId);
            return MakeRow(r.TypeId, r.DisplayName, version.MasterEntryName,
                System.IO.Path.Combine(_machine.MasterVaultPath!, _index.ResourceTypes.Single(t => t.TypeId == r.TypeId).MasterFolderName, version.MasterEntryName), r, version.VersionId, null);
        }).ToList() : (_instanceId is not null && _states.TryGetValue(_instanceId, out var states) ? states : [])
            .Select(v => MakeRow(v.TypeId, v.MatchedResourceName ?? v.InstanceEntryName, v.InstanceEntryName, v.InstanceEntryFullPath,
                _index.Resources.FirstOrDefault(r => r.ResourceId == v.MatchedResourceId), v.MatchedVersionId, v.State)).ToList();
        ApplySearch();
    }

    private ResourceRow MakeRow(string typeId, string name, string file, string path, LogicalResource? resource, string? versionId, MountState? state)
    {
        var type = _index!.ResourceTypes.Single(t => t.TypeId == typeId);
        var version = resource?.Versions.FirstOrDefault(v => v.VersionId == versionId);
        return new() { Group = $"{type.Name}   ·   {type.InstanceRelativePath}", TypeId = typeId, Name = name, FileName = file,
            FullPath = path, Resource = resource, ResourceId = resource?.ResourceId, VersionId = versionId, State = state,
            VersionCaption = version is null ? "先收编" : $"{version.VersionLabel} {(version.VersionId == resource!.LatestVersionId ? "最新" : "旧版")} ▾",
            Cover = ResourceRow.LoadCover(resource?.CustomCoverRelativePath is { } cover ? System.IO.Path.Combine(_machine!.MasterVaultPath!, cover) : null) };
    }

    private void ApplySearch()
    {
        var query = SearchBox.Text.Trim();
        var filtered = _rows.Where(row => string.IsNullOrEmpty(query) || $"{row.Name} {row.FileName} {row.StateText}".Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var view = CollectionViewSource.GetDefaultView(filtered); view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ResourceRow.Group)));
        ResourceList.ItemsSource = view; EmptyHint.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ContentSubtitle.Text = $"{filtered.Count} 个资源条目 · 拖入文件或文件夹，确认后安全入库";
        if (filtered.Count > 0) ResourceList.SelectedIndex = 0;
    }
    private void ChangePage(object sender, RoutedEventArgs e) { _page = (string)((Button)sender).Tag; VersionPopup.IsOpen = false; UpdatePage(); }
    private void SidebarChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Sidebar.SelectedItem is not SidebarItem item) return;
        if (_page == "Library") _typeId = item.Id; else _instanceId = item.Id;
        RenderRows();
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (ResourceList is not null) ApplySearch(); }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RunUi(RefreshAsync);
    private void CancelOperation(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void OnKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; } }
    private void DragHeader(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) { if (e.ClickCount == 2) MaximizeWindow(sender, e); else DragMove(); } }
    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object sender, RoutedEventArgs e) { if (_operation is not null) { _operation.Cancel(); StatusText.Text = "正在取消操作，请完成后关闭。"; } else Close(); }
}
