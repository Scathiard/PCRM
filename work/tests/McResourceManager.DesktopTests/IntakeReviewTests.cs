using System.IO;
using System.Windows;
using System.Windows.Controls;
using McResourceManager.Core.Engine;
using McResourceManager.Core.FileSystem;
using McResourceManager.Desktop;

namespace McResourceManager.DesktopTests;

internal static partial class Program
{
    private static async Task ReviewPerResource(string launcher)
    {
        var config = Path.Combine(_root, "machine-config.json");
        var engine = new ResourceManagerEngine(config);
        var (machine, _) = await engine.GetSnapshotAsync();
        var b = machine.Instances.Single(i => i.DisplayName == "Pack B");
        var cPath = Path.Combine(launcher, "Pack C");
        Directory.CreateDirectory(cPath);
        await engine.RegisterInstancesAsync([("Pack C", cPath)]);
        var model = Path.Combine(_root, "Bird_v1.0.ysm");
        File.WriteAllText(model, "bird one");
        await engine.CommitVaultIntakeAsync([new(model, true, "ysm_models", null, "Bird", "1.0", [b.InstanceId])]);
        var master = machine.MasterVaultPath!;
        var shader4 = "GenericShader_v4.0.zip";
        File.Copy(Zip(shader4, "version four"), Path.Combine(master, "shaderpacks", shader4));
        var bird2 = "Bird_v2.0.ysm";
        File.WriteAllText(Path.Combine(master, "ysm_models", bird2), "bird two");
        var comet = "Comet_v1.0.ysm";
        File.WriteAllText(Path.Combine(master, "ysm_models", comet), "comet one");
        var paths = new[] { "Pack A", "Pack B", "Pack C" }.Select(name => Path.Combine(launcher, name)).ToArray();
        string Model(int pack, string name) => Path.Combine(paths[pack], "config/yes_steve_model/custom", name);
        string Shader(int pack, string name) => Path.Combine(paths[pack], "shaderpacks", name);

        _main.Close();
        _main = new MainWindow(new ResourceManagerEngine(config));
        _main.Show();
        var review = await Window<VaultChangeReviewDialog>();
        await Wait(() => ((DataGrid)review.FindName("IntakeGrid")).Items.Count == 3 && Find(review, "CommitIntake").IsEnabled);
        var a = Row(review, shader4);
        var bird = Row(review, bird2);
        var fresh = Row(review, comet);
        Check(Targets(a).Select(t => t.Content.ToString()).SequenceEqual(["Pack A · 当前 v2.0", "Pack B · 当前 v1.0"])
            && Targets(bird).Single().Content.ToString() == "Pack B · 当前 1.0"
            && Targets(fresh).Select(t => t.Content.ToString()).SequenceEqual(["Pack A", "Pack B", "Pack C"]),
            "Rows did not show each resource's actual users, versions and optional fresh targets.");
        Check(new[] { a, bird, fresh }.SelectMany(Targets).All(t => t.IsChecked == false), "An upgrade was preselected.");
        Toggle(a, "Pack A"); Toggle(bird, "Pack B"); Toggle(fresh, "Pack C");
        var group = (ComboBox)Find(bird, "IntakeGroup");
        var original = group.SelectedValue;
        group.SelectedValue = "";
        Check(Targets(bird).Count == 3 && Targets(bird).All(t => t.IsChecked == false), "Regrouping carried old selections into a new resource.");
        group.SelectedValue = original;
        Check(Targets(bird).Single().IsChecked == false && Targets(a).Single(t => t.Content.ToString()!.StartsWith("Pack A")).IsChecked == true,
            "Regrouping one resource affected another row.");
        Toggle(bird, "Pack B");
        Check(!File.Exists(Shader(0, shader4)) && !File.Exists(Model(1, bird2)) && !File.Exists(Model(2, comet)), "Preview wrote deployment files.");
        CloseReview(review);
        await Wait(() => !review.IsVisible && ((ProgressBar)_main.FindName("BusyBar")).Visibility == Visibility.Collapsed);
        Check((await new ResourceManagerEngine(config).ScanMasterVaultChangesAsync()).Count == 3, "Cancelling committed part of the batch.");
        await Wait(() => Find(_main, "ReviewButton").IsEnabled);
        Click(_main, "ReviewButton");
        review = await Window<VaultChangeReviewDialog>();
        await Wait(() => ((DataGrid)review.FindName("IntakeGrid")).Items.Count == 3 && Find(review, "CommitIntake").IsEnabled);
        Toggle(Row(review, shader4), "Pack A"); Toggle(Row(review, bird2), "Pack B"); Toggle(Row(review, comet), "Pack C");
        Click(review, "CommitIntake");
        await Wait(() => !review.IsVisible && File.Exists(Model(2, comet)));
        Check(NtfsDriver.GetFastStat(Shader(0, shader4)).FileId == NtfsDriver.GetFastStat(Path.Combine(master, "shaderpacks", shader4)).FileId
            && File.Exists(Shader(1, "GenericShader_v1.0.zip")) && !File.Exists(Shader(1, shader4)) && !File.Exists(Shader(2, shader4))
            && File.ReadAllText(Model(1, bird2)) == "bird two" && !File.Exists(Model(0, bird2)) && !File.Exists(Model(2, bird2))
            && File.ReadAllText(Model(2, comet)) == "comet one" && !File.Exists(Model(0, comet)) && !File.Exists(Model(1, comet)),
            "Submitting a multi-resource review deployed into the wrong instance.");
        Evidence.Add("同一批三项资源按行显示当前版本，取消不提交，重新归组清除旧选择，确认后光影仅升级 A、模型仅升级 B、新资源仅部署到 C。");

        var source = Path.Combine(_root, "Bird_v3.0.ysm");
        File.WriteAllText(source, "bird three");
        await Wait(() => Find(_main, "ReviewButton").IsEnabled);
        Click(_main, "LibraryTab");
        await Wait(() => ((TextBlock)_main.FindName("ContentTitle")).Text == "全局资源库" && Find(_main, "ReviewButton").IsEnabled);
        Click(_main, "ImportResources");
        var import = await Window<VaultChangeReviewDialog>();
        await Wait(() => Find(import, "ImportPaths").IsEnabled);
        Set(import, "ImportPaths", source); Click(import, "AnalyzeImport");
        await Wait(() => ((DataGrid)import.FindName("IntakeGrid")).Items.Count == 1 && Find(import, "CommitIntake").IsEnabled);
        Check(Targets(Row(import, Path.GetFileName(source))).Single().Content.ToString() == "Pack B · 当前 v2.0", "External import used different targeting from vault review.");
        CloseReview(import);
        await Wait(() => !import.IsVisible && Find(_main, "ReviewButton").IsEnabled);
        Check(File.ReadAllText(source) == "bird three" && !File.Exists(Path.Combine(master, "ysm_models", Path.GetFileName(source))), "Cancelled import modified a file.");
        Evidence.Add("外部导入预览列出同一模型实际使用者及版本，关闭后源文件和主库未变。");

        File.WriteAllText(Path.Combine(master, "ysm_models", bird2), "external overwrite");
        Click(_main, "ReviewButton");
        review = await Window<VaultChangeReviewDialog>();
        await Wait(() => ((DataGrid)review.FindName("IntakeGrid")).Items.Count == 1 && Find(review, "CommitIntake").IsEnabled);
        Check(Targets(Row(review, bird2)).Single().Content.ToString()!.Contains("内容异常"), "Overwritten bytes were presented as a verified current version.");
        Click(review, "CommitIntake");
        await Wait(() => !review.IsVisible && ((ProgressBar)_main.FindName("BusyBar")).Visibility == Visibility.Collapsed);
        ((TextBox)_main.FindName("SearchBox")).Text = "Bird";
        await Wait(() => ((ListBox)_main.FindName("ResourceList")).Items.Count == 1);
        Click(_main, "VersionButton");
        var versions = (ListBox)_main.FindName("VersionList");
        await Wait(() => versions.IsVisible && versions.Items.Count == 3);
        versions.UpdateLayout();
        var disabled = versions.Items.Cast<object>().Select(v => (ListBoxItem)versions.ItemContainerGenerator.ContainerFromItem(v))
            .Single(i => !i.IsEnabled);
        versions.SelectedItem = disabled.Content;
        Check(!Find((FrameworkElement)((System.Windows.Controls.Primitives.Popup)_main.FindName("VersionPopup")).Child, "ConfirmVersion").IsEnabled
            && ((TextBlock)_main.FindName("VersionHistory")).Text.Contains("主库已被覆盖"), "Unavailable history could be selected for rollback.");
        ((System.Windows.Controls.Primitives.Popup)_main.FindName("VersionPopup")).IsOpen = false;
        ((TextBox)_main.FindName("SearchBox")).Clear();
        Evidence.Add("确认外部覆写后旧版本留在历史，版本菜单及切换按钮均禁止用它回退。");
    }

    private static DataGridRow Row(Window window, string entry)
    {
        var grid = (DataGrid)window.FindName("IntakeGrid");
        foreach (var item in grid.Items)
        {
            grid.ScrollIntoView(item);
            grid.UpdateLayout();
            if (grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row &&
                grid.Columns[1].GetCellContent(row) is TextBlock text && text.Text == entry) return row;
        }
        throw new InvalidOperationException("Intake row missing: " + entry);
    }

    private static IReadOnlyList<CheckBox> Targets(DataGridRow row)
    {
        var list = (ItemsControl)Find(row, "IntakeTargets");
        list.UpdateLayout();
        return list.Items.Cast<object>().Select(item => (CheckBox)Find((FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(item), "IntakeTarget")).ToArray();
    }

    private static void Toggle(DataGridRow row, string name) =>
        Targets(row).Single(t => t.IsEnabled && (t.Content.ToString() == name || t.Content.ToString()!.StartsWith(name + " ·"))).IsChecked = true;

    private static void CloseReview(Window dialog) => dialog.Dispatcher.BeginInvoke(
        System.Windows.Threading.DispatcherPriority.Background, new Action(dialog.Close));
}
