using System.Windows;
using McResourceManager.Core.Domain;
using McResourceManager.Core.Engine;
using Microsoft.Win32;

namespace McResourceManager.Desktop;

public partial class FirstRunOnboardingDialog : OperationDialog
{
    private readonly IResourceManagerEngine _engine;
    private readonly bool _instancesOnly;
    private List<SelectableInstance> _candidates = [];
    public FirstRunOnboardingDialog(IResourceManagerEngine engine, bool instancesOnly = false)
    { _engine = engine; _instancesOnly = instancesOnly; InitializeComponent(); }
    private async void InitializeDialog(object sender, RoutedEventArgs e) => await Perform(async ct =>
    {
        var snapshot = await _engine.GetSnapshotAsync(ct);
        LibraryPath.Text = snapshot.MachineConfig.MasterVaultPath ?? "";
        if (_instancesOnly && snapshot.MasterIndex is not null) ShowInstances();
    });
    private void PickLibrary(object sender, RoutedEventArgs e)
    { var picker = new OpenFolderDialog { Title = "选择主资源库文件夹" }; if (picker.ShowDialog(this) == true) LibraryPath.Text = picker.FolderName; }
    private async void ConfigureLibrary(object sender, RoutedEventArgs e) => await Perform(async ct =>
    { await _engine.ConfigureMasterVaultAsync(LibraryPath.Text, ct); ShowInstances(); Message("主库已准备好，接下来添加实例。"); });
    private void ShowInstances() { LibraryStep.Visibility = Visibility.Collapsed; InstancesStep.Visibility = Visibility.Visible; }
    private void PickInstances(object sender, RoutedEventArgs e)
    { var picker = new OpenFolderDialog { Title = "选择实例或启动器根目录" }; if (picker.ShowDialog(this) == true) InstancePath.Text = picker.FolderName; }
    private async void ProbeInstances(object sender, RoutedEventArgs e) => await Perform(async ct =>
    {
        var candidates = await _engine.ProbeInstanceDirectoryAsync(InstancePath.Text, ct);
        _candidates = candidates.Where(c => !c.AlreadyRegistered).Select(c => new SelectableInstance { Id = c.RootPath, Name = c.DisplayName, Path = c.RootPath, Selected = true }).ToList();
        InstanceCandidates.ItemsSource = _candidates;
        Message(_candidates.Count == 0 ? "这些实例已经登记。" : $"找到 {_candidates.Count} 个待添加实例，请确认勾选。");
    });
    private async void RegisterInstances(object sender, RoutedEventArgs e)
    {
        InstanceCandidates.CommitEdit(); InstanceCandidates.CommitEdit();
        var selected = _candidates.Where(c => c.Selected).ToArray();
        if (selected.Length == 0) { Message("请至少选择一个实例。"); return; }
        IReadOnlyList<GameInstance>? registered = null;
        await Perform(async ct =>
        {
            var snapshot = await _engine.GetSnapshotAsync(ct);
            var masterVolume = System.IO.Path.GetPathRoot(snapshot.MachineConfig.MasterVaultPath!);
            foreach (var volume in selected.Select(c => System.IO.Path.GetPathRoot(c.Path)!).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!string.Equals(masterVolume, volume, StringComparison.OrdinalIgnoreCase)
                    && !snapshot.MachineConfig.ReplicaVaultPathsByVolume.ContainsKey(volume))
                {
                    if (new ReplicaSetupDialog(_engine, volume) { Owner = this }.ShowDialog() != true)
                        throw new OperationCanceledException();
                }
            registered = await _engine.RegisterInstancesAsync(selected.Select(c => (c.Name, c.Path)).ToArray(), ct);
        });
        if (registered is null) return;
        var harvest = new HarvestDialog(_engine, registered) { Owner = this }; harvest.ShowDialog();
        InstancesStep.Visibility = Visibility.Collapsed; CompleteStep.Visibility = Visibility.Visible;
        OnboardingSummary.Text = harvest.Result is { } result
            ? $"已添加 {registered.Count} 个实例，收编 {result.LinkedCount} 项资源，立省 {result.SavedBytes / 1073741824d:0.000} GB。" + string.Join("；", result.NonFatalWarnings)
            : $"已添加 {registered.Count} 个实例。可随时在“工具”中继续扫描收编。";
    }
    private void EnterWorkspace(object sender, RoutedEventArgs e) => DialogResult = true;
}
