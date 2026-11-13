using System.Windows;
using McResourceManager.Core.Domain;
using McResourceManager.Core.Engine;

namespace McResourceManager.Desktop;

public partial class VersionGroupingDialog : OperationDialog
{
    private readonly IResourceManagerEngine _engine;
    private readonly LogicalResource _resource;
    public VersionGroupingDialog(IResourceManagerEngine engine, LogicalResource resource, MasterVaultIndex index)
    {
        _engine = engine; _resource = resource; InitializeComponent();
        SourceVersion.ItemsSource = resource.Versions; SourceVersion.SelectedValue = resource.LatestVersionId;
        Destination.ItemsSource = new[] { new MergeChoice(null, "拆分为独立资源") }.Concat(index.Resources.Where(r => r.TypeId == resource.TypeId
            && r.ResourceId != resource.ResourceId).Select(r => new MergeChoice(r.ResourceId, r.DisplayName))).ToArray();
        Destination.SelectedIndex = 0;
    }
    private async void Save(object sender, RoutedEventArgs e) => await Perform(async ct =>
    {
        if (SourceVersion.SelectedValue is not string version || Destination.SelectedItem is not MergeChoice choice) throw new ArgumentException("请选择版本与归组目标。");
        await _engine.MergeOrSplitVersionAsync(_resource.ResourceId, version, choice.Id, ct); Finish();
    });
}
