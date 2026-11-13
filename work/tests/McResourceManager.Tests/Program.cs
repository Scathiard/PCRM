using System.Text.Json;

namespace McResourceManager.Tests;

internal static partial class Program
{
    public static string TestRoot { get; private set; } = "";
    private static async Task<int> Main(string[] args)
    {
        var workspace = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        TestRoot = Path.Combine(workspace, "work/tmp/mcrm-core-tests");
        Directory.CreateDirectory(TestRoot);
        var cases = new (string Name, Func<Task> Run)[]
        {
            ("intake, duplicate, same-name coexistence and persistence", Intake),
            ("hardlink, version switch, configuration and old-version preservation", Versions),
            ("external overwrite, deletion and controlled-copy detection", ExternalChanges),
            ("folder trees, detach, remove and custom types", Folders),
            ("harvest classification, execution, savings and repeated submission", Harvest),
            ("rollback, interrupted recovery and backup preservation", Recovery),
            ("replica cache copy and same-volume links (simulated cross-volume)", Replica),
            ("cancellation, boundaries and unknown resource recognition", Boundaries),
            ("archive/folder metadata, manual details and launcher discovery", Metadata),
            ("library relocation, fresh configuration and tamper rejection", Relocation),
            ("cancellation during a 128 MiB resource copy", CancelDuringCopy),
            ("configuration encoding, invalid JSON and locked-file preservation", CompanionSafety),
            ("version grouping, independent-copy association and instance unregistration", Grouping),
            ("configuration location after workspace relocation and standalone packaging", RuntimeLocation)
        };
        var results = new List<object>();
        var failures = 0;
        foreach (var test in cases)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); results.Add(new { test.Name, Passed = true, Seconds = clock.Elapsed.TotalSeconds }); }
            catch (Exception error) { failures++; Console.WriteLine($"FAIL {test.Name}\n{error}"); results.Add(new { test.Name, Passed = false, Error = error.ToString() }); }
        }
        var report = Path.Combine(workspace, "work/data/core-test-results.json");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { AtUtc = DateTime.UtcNow, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{cases.Length - failures}/{cases.Length} passed; {report}");
        return failures == 0 ? 0 : 1;
    }
    private static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }
    private static async Task Throws(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected operation to fail without damaging existing data.");
    }
}
