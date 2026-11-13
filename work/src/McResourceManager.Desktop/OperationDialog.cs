using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace McResourceManager.Desktop;

public class OperationDialog : Window
{
    private CancellationTokenSource? _cancellation;
    private bool _finish;
    private bool _closeAfterCancel;
    protected OperationDialog() { Style = (Style)Application.Current.FindResource(typeof(Window)); }
    protected void Message(string text) { if (FindName("Feedback") is TextBlock feedback) feedback.Text = text; }
    protected void Finish() => _finish = true;
    protected async Task Perform(Func<CancellationToken, Task> action)
    {
        if (_cancellation is not null) return;
        _cancellation = new();
        if (FindName("Form") is UIElement form) form.IsEnabled = false;
        if (FindName("DialogProgress") is ProgressBar progress) progress.Visibility = Visibility.Visible;
        try { await action(_cancellation.Token); }
        catch (OperationCanceledException) { Message("操作已取消，已经完成的条目保留。"); }
        catch (Exception error) { Message(error.Message); }
        finally
        {
            _cancellation.Dispose(); _cancellation = null;
            if (FindName("Form") is UIElement enabled) enabled.IsEnabled = true;
            if (FindName("DialogProgress") is ProgressBar hidden) hidden.Visibility = Visibility.Collapsed;
            if (_finish) DialogResult = true;
            else if (_closeAfterCancel) Close();
        }
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_cancellation is not null) { e.Cancel = true; _closeAfterCancel = true; _cancellation.Cancel(); }
        base.OnClosing(e);
    }
    protected void CancelDialog(object sender, RoutedEventArgs e) => Close();
}
