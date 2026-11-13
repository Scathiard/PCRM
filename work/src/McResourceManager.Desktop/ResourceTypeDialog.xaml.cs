using System.Windows;
using McResourceManager.Core.Domain;
using McResourceManager.Core.Engine;

namespace McResourceManager.Desktop;

public partial class ResourceTypeDialog : OperationDialog
{
    private readonly IResourceManagerEngine _engine;
    public ResourceTypeDialog(IResourceManagerEngine engine) { _engine = engine; InitializeComponent(); }
    private async void Save(object sender, RoutedEventArgs e) => await Perform(async ct =>
    {
        await _engine.UpsertResourceTypeAsync(new("custom-" + Guid.NewGuid().ToString("N")[..8], TypeName.Text.Trim(), RelativePath.Text.Trim(),
            FolderName.Text.Trim(), false, IgnorePatterns.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), CompanionReferenceKind.None), ct);
        Finish();
    });
}
