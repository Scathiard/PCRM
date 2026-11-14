using System.Windows;
using McResourceManager.Core.Engine;

namespace McResourceManager.Desktop;

public partial class MainWindow
{
    private bool _checkingChanges;
    private string? _lastChangeNotice;

    private async void OnActivated(object? sender, EventArgs e) => await CheckChangesOnFocusAsync();

    private async Task CheckChangesOnFocusAsync()
    {
        if (!_ready || _checkingChanges || _operation is not null || _index is null
            || Application.Current.Windows.OfType<OperationDialog>().Any()) return;
        _checkingChanges = true;
        ReviewButton.IsEnabled = false;
        try
        {
            IReadOnlyList<VaultIntakeCandidateItem>? changes = null;
            await RunUi(async ct => changes = await _engine.ScanMasterVaultChangesAsync(ct));
            if (changes is null) return;
            if (changes.Count == 0) { _lastChangeNotice = null; return; }
            var signature = string.Join("|", changes.Select(c => $"{c.SourceFullPath}:{c.TotalSizeBytes}:{c.ConflictKind}:{c.GuessedVersionLabel}"));
            if (_lastChangeNotice == signature) return;
            _lastChangeNotice = signature;
            var dialog = new VaultChangeReviewDialog(_engine, [], reviewMaster: true) { Owner = this };
            dialog.ShowDialog();
            await RunUi(RefreshAsync);
            if (dialog.Summary is { } summary) StatusText.Text = summary;
        }
        finally { _checkingChanges = false; ReviewButton.IsEnabled = _index is not null; }
    }
}
