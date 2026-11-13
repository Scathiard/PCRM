using System.Windows;
using McResourceManager.Core.Engine;
using Microsoft.Win32;

namespace McResourceManager.Desktop;

public partial class ReplicaSetupDialog : OperationDialog
{
    private readonly IResourceManagerEngine _engine;
    public ReplicaSetupDialog(IResourceManagerEngine engine, string? volume = null)
    { _engine = engine; InitializeComponent(); VolumePath.Text = volume ?? ""; }
    private void PickPath(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择共享缓存区" };
        if (picker.ShowDialog(this) == true) { CachePath.Text = picker.FolderName; VolumePath.Text = System.IO.Path.GetPathRoot(picker.FolderName); }
    }
    private async void Save(object sender, RoutedEventArgs e) => await Perform(async ct =>
    { await _engine.ConfigureReplicaVaultAsync(VolumePath.Text, CachePath.Text, ct); Finish(); });
}
