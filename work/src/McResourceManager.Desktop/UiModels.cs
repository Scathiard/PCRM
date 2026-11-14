using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using McResourceManager.Core.Domain;

namespace McResourceManager.Desktop;

internal sealed record SidebarItem(string Id, string Title, string Subtitle);
internal sealed record VersionChoice(string Id, string Label, bool CanDeploy);
internal sealed class ResourceRow
{
    public required string Group { get; init; }
    public required string TypeId { get; init; }
    public required string Name { get; init; }
    public required string FileName { get; init; }
    public required string FullPath { get; init; }
    public required string VersionCaption { get; init; }
    public string? ResourceId { get; init; }
    public string? VersionId { get; init; }
    public LogicalResource? Resource { get; init; }
    public MountState? State { get; init; }
    public ImageSource? Cover { get; init; }
    public string StateText => State switch
    {
        MountState.LinkedLatest => "共享中 · 最新", MountState.LinkedOldVersion => "共享中 · 旧版",
        MountState.ControlledCopy => "独立副本", MountState.TamperedAlert => "内容异常",
        MountState.WildUntracked => "未入库", _ => "已入库"
    };
    public Brush StateBrush => (Brush)Application.Current.FindResource("Brush.State." + (State?.ToString() ?? "LinkedLatest"));
    public static ImageSource? LoadCover(string? path)
    {
        if (path is null) return null;
        try
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 300; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        catch (Exception error) when (error is System.IO.IOException or NotSupportedException or UriFormatException) { return null; }
    }
}
internal class EditableRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
internal sealed class SelectableInstance : EditableRow
{
    private bool _selected;
    public bool Selected { get => _selected; set { _selected = value; Changed(); } }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Path { get; init; } = "";
}
