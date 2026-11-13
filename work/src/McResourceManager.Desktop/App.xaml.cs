using System.Security.Cryptography;
using System.Text;
using System.Windows;
using McResourceManager.Core.Engine;

namespace McResourceManager.Desktop;

public partial class App : Application
{
    private Mutex? _instanceLock;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var position = Array.IndexOf(e.Args, "--config");
        var config = position >= 0 && position + 1 < e.Args.Length ? e.Args[position + 1] : RuntimePaths.DefaultConfigPath();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(config).ToUpperInvariant())));
        _instanceLock = new Mutex(true, "Local\\MCRM-" + key, out var first);
        if (!first) { MessageBox.Show("此资源管理工作台已经打开。", "MCRM"); Shutdown(); return; }
        new MainWindow(new ResourceManagerEngine(config)).Show();
    }
    protected override void OnExit(ExitEventArgs e) { _instanceLock?.Dispose(); base.OnExit(e); }
}
