using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using McResourceManager.Core.Engine;
using McResourceManager.Core.FileSystem;
using McResourceManager.Desktop;

namespace McResourceManager.DesktopTests;

internal static partial class Program
{
    private static string _workspace = "";
    private static string _root = "";
    private static Application _app = null!;
    private static MainWindow _main = null!;
    private static int _result = 1;
    private static Exception? _uiFailure;
    private static readonly List<string> Evidence = [];
    [STAThread]
    private static int Main(string[] args)
    {
        _workspace = Path.GetFullPath(args[0]);
        _root = Path.Combine(_workspace, "work/tmp/mcrm-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Console.WriteLine("UI_FIXTURE_ROOT " + _root);
        _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        _app.DispatcherUnhandledException += (_, e) => { _uiFailure = e.Exception; e.Handled = true; };
        _app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/PCRM;component/Themes/Sky.xaml", UriKind.Relative) });
        _main = new MainWindow(new ResourceManagerEngine(Path.Combine(_root, "machine-config.json")));
        _app.Dispatcher.BeginInvoke(async () => await Run(args.Contains("--hold")));
        _app.Run(_main);
        return _result;
    }
    private static async Task Run(bool hold)
    {
        try
        {
            var source1 = Zip("GenericShader_v1.0.zip", "version one");
            var source2 = Zip("GenericShader_v2.0.zip", "version two");
            var launcher = Path.Combine(_root, "launcher/.minecraft/versions");
            foreach (var name in new[] { "Pack A", "Pack B" })
            {
                var instance = Path.Combine(launcher, name);
                Directory.CreateDirectory(Path.Combine(instance, "shaderpacks"));
                Directory.CreateDirectory(Path.Combine(instance, "config"));
                File.Copy(source1, Path.Combine(instance, "shaderpacks/GenericShader_v1.0.zip"));
                File.WriteAllText(Path.Combine(instance, "config/iris.properties"), "shaderPack=GenericShader_v1.0.zip\n");
            }
            var onboarding = await Window<FirstRunOnboardingDialog>();
            await Wait(() => Find(onboarding, "MasterPath").IsEnabled);
            Set(onboarding, "MasterPath", Path.Combine(_root, "library")); Click(onboarding, "ConfigureLibrary");
            await Wait(() => Find(onboarding, "InstancePath").IsVisible);
            Set(onboarding, "InstancePath", Path.GetDirectoryName(Path.GetDirectoryName(launcher)!)!); Click(onboarding, "ProbeInstances");
            await Wait(() => ((DataGrid)onboarding.FindName("InstanceCandidates")).Items.Count == 2);
            Click(onboarding, "RegisterInstances");
            var harvest = await Window<HarvestDialog>();
            await Wait(() => ((DataGrid)harvest.FindName("HarvestGrid")).Items.Count == 2 && Find(harvest, "ExecuteHarvest").IsEnabled);
            Click(harvest, "ExecuteHarvest");
            await Wait(() => !harvest.IsVisible && Find(onboarding, "EnterWorkspace").IsVisible);
            Click(onboarding, "EnterWorkspace"); await Wait(() => !onboarding.IsVisible);
            var master1 = Path.Combine(_root, "library/shaderpacks/GenericShader_v1.0.zip");
            var a1 = Path.Combine(launcher, "Pack A/shaderpacks/GenericShader_v1.0.zip");
            var b1 = Path.Combine(launcher, "Pack B/shaderpacks/GenericShader_v1.0.zip");
            await Wait(() => File.Exists(master1) && NtfsDriver.GetFastStat(master1).NumberOfLinks == 3);
            Evidence.Add("首次启动：主库配置、批量探测两实例、确认收编；三个路径的 FileID 相同。");
            await Wait(() => ((ListBox)_main.FindName("ResourceList")).Items.Count > 0);
            Click(_main, "LibraryTab"); Click(_main, "ImportResources");
            var intake = await Window<VaultChangeReviewDialog>();
            await Wait(() => Find(intake, "ImportPaths").IsEnabled);
            Set(intake, "ImportPaths", source2); Click(intake, "AnalyzeImport");
            await Wait(() => ((DataGrid)intake.FindName("IntakeGrid")).Items.Count == 1 && Find(intake, "CommitIntake").IsEnabled);
            Click(intake, "CommitIntake"); await Wait(() => !intake.IsVisible);
            await Wait(() => File.Exists(Path.Combine(_root, "library/shaderpacks/GenericShader_v2.0.zip")));
            Check(File.Exists(a1) && File.Exists(b1), "Import changed unselected instances.");
            Evidence.Add("软件内导入：识别光影与版本归组；未勾选部署时两实例保持原文件。");
            Click(_main, "InstancesTab");
            await Wait(() => ((TextBlock)_main.FindName("ContentTitle")).Text == "Pack A"
                && ((ProgressBar)_main.FindName("BusyBar")).Visibility == Visibility.Collapsed
                && ((ListBox)_main.FindName("ResourceList")).Items.Count > 0);
            Click(_main, "VersionButton");
            var versions = (ListBox)_main.FindName("VersionList");
            await Wait(() => versions.IsVisible && versions.Items.Count == 2);
            versions.SelectedItem = versions.Items.Cast<object>().First(item => item.GetType().GetProperty("Label")!.GetValue(item)!.ToString()!.Contains("2.0"));
            Check(File.Exists(a1), "Selecting a version modified files before confirmation.");
            Click((FrameworkElement)((System.Windows.Controls.Primitives.Popup)_main.FindName("VersionPopup")).Child, "ConfirmVersion");
            var a2 = Path.Combine(launcher, "Pack A/shaderpacks/GenericShader_v2.0.zip");
            await Wait(() => File.Exists(a2) && !File.Exists(a1) && ((ProgressBar)_main.FindName("BusyBar")).Visibility == Visibility.Collapsed);
            Check(File.Exists(b1) && File.ReadAllText(Path.Combine(launcher, "Pack A/config/iris.properties")).Contains("v2.0"), "Confirmed switch damaged old instance or companion config.");
            Evidence.Add("版本弹窗：仅选版本不会写文件；确认后 A 升级、B 保旧，Iris 引用随 A 更新。");
            Click(_main, "LibraryTab");
            await Wait(() => ((TextBlock)_main.FindName("ContentTitle")).Text == "全局资源库" && ((ProgressBar)_main.FindName("BusyBar")).Visibility == Visibility.Collapsed);
            Click(_main, "AddTypeButton");
            var typeDialog = await Window<ResourceTypeDialog>();
            Set(typeDialog, "TypeName", "蓝图"); Set(typeDialog, "TypePath", "schematics"); Set(typeDialog, "TypeFolder", "schematics");
            Click(typeDialog, "SaveType"); await Wait(() => !typeDialog.IsVisible);
            await Wait(() => ((ListBox)_main.FindName("Sidebar")).Items.Count == 6 && ((ProgressBar)_main.FindName("BusyBar")).Visibility == Visibility.Collapsed);
            Evidence.Add("自定义资源类型从实际弹窗添加后出现在资源库分类中。");
            Set(_main, "ResourceName", "通用光影 · 已编辑"); Click(_main, "SaveDetails");
            await Wait(() => ((TextBlock)_main.FindName("StatusText")).Text == "详情已保存。");
            Click(_main, "ToolsTab"); Click(_main, "HealthCheck");
            await Wait(() => ((TextBox)_main.FindName("HealthReport")).Text.Contains("Pack B"));
            File.Delete(b1); File.Copy(source1, b1);
            Click(_main, "HealthCheck");
            await Wait(() => ((TextBox)_main.FindName("HealthReport")).Text.Contains("可恢复共享"));
            var repair = new ResourceManagerEngine(Path.Combine(_root, "machine-config.json"));
            var repairState = await repair.GetSnapshotAsync();
            var repairInstance = repairState.MachineConfig.Instances.Single(i => i.DisplayName == "Pack B");
            var repairCandidate = (await repair.ScanInstanceForHarvestAsync(repairInstance.InstanceId, ["shaderpacks"])).Single();
            await repair.ExecuteHarvestAsync([new(repairInstance.InstanceId, "shaderpacks", repairCandidate.EntryName,
                HarvestActionKind.ConvertSameHashToLink, repairCandidate.MatchedResourceId)]);
            Check(NtfsDriver.GetFastStat(b1).FileId == NtfsDriver.GetFastStat(master1).FileId, "Health recovery did not restore sharing.");
            Evidence.Add("外部替换使共享关系失效时，健康检查提示可恢复共享；收编接口重建了真实硬链接。");
            Click(_main, "SettingsTab");
            await Wait(() => ((FrameworkElement)_main.FindName("SettingsPage")).IsVisible);
            Check(((TextBlock)_main.FindName("MasterPathText")).Text == Path.GetFullPath(Path.Combine(_root, "library")), "Settings did not reflect configured library.");
            Click(_main, "InstancesTab");
            await Wait(() => ((TextBlock)_main.FindName("ContentTitle")).Text == "Pack A" && ((FrameworkElement)_main.FindName("WorkspaceGrid")).IsVisible);
            SaveScreenshot("ui-workspace.png");
            Evidence.Add("四页签、详情保存、健康检查与设置入口均通过实际 WPF 控件操作。");
            var source3 = Zip("GenericShader_v3.0.zip", "version three");
            File.Copy(source3, Path.Combine(_root, "library/shaderpacks/GenericShader_v3.0.zip"));
            _main.Close();
            _main = new MainWindow(new ResourceManagerEngine(Path.Combine(_root, "machine-config.json"))); _main.Show();
            await Wait(() => ((ListBox)_main.FindName("ResourceList")).Items.Count > 0);
            Check(!_app.Windows.OfType<FirstRunOnboardingDialog>().Any(), "Configured workspace unexpectedly reopened onboarding.");
            var review = await Window<VaultChangeReviewDialog>();
            await Wait(() => ((DataGrid)review.FindName("IntakeGrid")).Items.Count == 1 && Find(review, "CommitIntake").IsEnabled);
            Check(File.Exists(a2) && File.Exists(b1), "Automatic change detection modified instances before confirmation.");
            Click(review, "CommitIntake"); await Wait(() => !review.IsVisible);
            Evidence.Add("重新打开工作台自动发现主库新增版本并弹出确认；确认前及未勾选的实例均保持原文件。");
            var saved = await new ResourceManagerEngine(Path.Combine(_root, "machine-config.json")).GetSnapshotAsync();
            Check(saved.MasterIndex!.Resources.Single().DisplayName == "通用光影 · 已编辑" && saved.MasterIndex.Resources.Single().Versions.Count == 3,
                "Details or the confirmed master version did not persist across window/engine restart.");
            Evidence.Add("重建主窗口与引擎后，实例、资源、版本和详情持久化有效，未重走首启。");
            Click(_main, "LibraryTab");
            await Wait(() => ((TextBlock)_main.FindName("ContentTitle")).Text == "全局资源库" && ((ProgressBar)_main.FindName("BusyBar")).Visibility == Visibility.Collapsed);
            Click(_main, "VersionButton");
            var libraryVersions = (ListBox)_main.FindName("VersionList");
            await Wait(() => libraryVersions.IsVisible && libraryVersions.Items.Count == 3);
            string CurrentCaption() => libraryVersions.Items.Cast<object>().Select(v => v.GetType().GetProperty("Label")!.GetValue(v)!.ToString()!)
                .Single(label => label.Contains("当前"));
            Check(CurrentCaption().Contains("v2.0"), "Library popup confused the latest library version with the target instance's current version.");
            var targetPicker = (ComboBox)_main.FindName("DeployInstance");
            targetPicker.SelectedItem = targetPicker.Items.Cast<McResourceManager.Core.Domain.GameInstance>().Single(i => i.DisplayName == "Pack B");
            Check(CurrentCaption().Contains("v1.0") && File.Exists(a2) && File.Exists(b1), "Changing the target instance misreported the current version or wrote files.");
            Evidence.Add("资源库版本浮层按目标实例标出当前版本，切换目标只更新显示而不修改文件。");
            ((System.Windows.Controls.Primitives.Popup)_main.FindName("VersionPopup")).IsOpen = false;
            await ReviewPerResource(launcher);
            _result = 0;
            Console.WriteLine("PASS WPF smoke workflow; " + string.Join(" | ", Evidence));
            if (hold)
            {
                _main.Close();
                var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(_workspace, "releases/dist/native/PCRM.exe")) { WorkingDirectory = _workspace };
                start.ArgumentList.Add("--config"); start.ArgumentList.Add(Path.Combine(_root, "machine-config.json"));
                using var packaged = System.Diagnostics.Process.Start(start) ?? throw new IOException("Packaged executable did not start.");
                await Wait(() => { packaged.Refresh(); return packaged.HasExited || packaged.MainWindowHandle != IntPtr.Zero; });
                Check(!packaged.HasExited, "Packaged executable exited before showing a window.");
                Check(packaged.MainWindowTitle == "PCRM - Plain Craft Resources Manager", "Published program opened under the old name.");
                Console.WriteLine($"WPF_QA_WINDOW_READY pid={packaged.Id} fixture={_root}");
                await Wait(() => packaged.HasExited, 600000);
                Check(packaged.ExitCode == 0, "Packaged executable exited abnormally.");
                Evidence.Add("发布后的 PCRM.exe 已实际启动并正常关闭。");
            }
        }
        catch (Exception error) { _result = 1; Console.WriteLine("FAIL WPF smoke workflow\n" + error); Evidence.Add(error.ToString()); SaveScreenshot("ui-failure.png"); }
        finally
        {
            ((System.Windows.Controls.Primitives.Popup)_main.FindName("VersionPopup")).IsOpen = false;
            foreach (var window in _app.Windows.Cast<Window>().ToArray()) window.Close();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var report = Path.Combine(_workspace, "work/data/ui-test-results.json");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = _result == 0, AtUtc = DateTime.UtcNow, Evidence }, new JsonSerializerOptions { WriteIndented = true }));
            if (!_root.StartsWith(Path.Combine(_workspace, "work/tmp/mcrm-ui-tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid cleanup root.");
            Directory.Delete(_root, true);
            _app.Shutdown(_result);
        }
    }
    private static string Zip(string name, string content)
    {
        var path = Path.Combine(_root, name); using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("shaders/main.fsh").Open()); writer.Write(content); return path;
    }
    private static async Task<T> Window<T>() where T : Window
    { await Wait(() => _app.Windows.OfType<T>().Any(w => w.IsLoaded)); return _app.Windows.OfType<T>().Single(w => w.IsLoaded); }
    private static FrameworkElement Find(FrameworkElement root, string id)
    {
        root.UpdateLayout();
        if (root.FindName(id) is FrameworkElement named) return named;
        FrameworkElement? Visit(DependencyObject node)
        {
            if (node is FrameworkElement element && AutomationProperties.GetAutomationId(element) == id) return element;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) { var match = Visit(VisualTreeHelper.GetChild(node, i)); if (match is not null) return match; }
            return null;
        }
        return Visit(root) ?? throw new InvalidOperationException("UI element not found: " + id);
    }
    private static void Set(FrameworkElement root, string id, string value) => ((TextBox)Find(root, id)).Text = value;
    private static void Click(FrameworkElement root, string id) => _app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
    { try { var button = (Button)Find(root, id); Check(button.IsEnabled, "Attempt to click a disabled button: " + id); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); } catch (Exception error) { _uiFailure = error; } }));
    private static async Task Wait(Func<bool> condition, int timeout = 20000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition()) { if (_uiFailure is not null) throw _uiFailure; if (clock.ElapsedMilliseconds > timeout) throw new TimeoutException("WPF condition timed out; " + string.Join(" | ", _app.Windows.Cast<Window>().Select(w => w.Title + ": " + (w.FindName("Feedback") as TextBlock)?.Text))); await Task.Delay(50); }
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private static void SaveScreenshot(string name)
    {
        _main.UpdateLayout();
        if (_main.ActualWidth < 1) return;
        var bitmap = new RenderTargetBitmap((int)_main.ActualWidth, (int)_main.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(_main);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(_workspace, "work/data", name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
