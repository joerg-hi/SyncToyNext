using SyncToyNext.Core.Model;
using SyncToyNext.Core.Planning;
using SyncToyNext.Core.Scanning;
using static SyncToyNext.Core.Model.Side;

namespace SyncToyNext.Core.Tests;

/// <summary>
/// Verhalten der Optimierungen für Netzlaufwerke: paralleles Einlesen und Statusdateien ohne zweites Einlesen.
/// </summary>
public class PerformanceBehaviorTests
{
    [Fact]
    public void ParallelScan_FindsEveryEntryOfALargeTree()
    {
        using var t = new TestFolders();
        int expected = 0;
        for (int a = 0; a < 6; a++)
        {
            for (int b = 0; b < 6; b++)
            {
                for (int f = 0; f < 5; f++)
                {
                    t.Write(Left, $"a{a}/b{b}/c/datei{f}.txt", $"{a}{b}{f}");
                    expected++;
                }
            }
        }

        expected += 6 + 6 * 6 + 6 * 6; // Ordner a*, a*/b*, a*/b*/c
        var result = FolderScanner.Scan(t.Left, new FileFilter(new PairOptions()), parallelism: 8);

        Assert.Equal(expected, result.Count);
        Assert.Equal(6 * 6 * 5, result.Values.Count(e => !e.IsDirectory));
        Assert.True(result.ContainsKey("a5/b5/c/datei4.txt"));
    }

    [Fact]
    public void ParallelScan_DeepAndUnevenTree_SameResultAsSequential()
    {
        using var t = new TestFolders();
        string deep = string.Join("/", Enumerable.Range(0, 12).Select(i => $"tief{i}"));
        t.Write(Left, deep + "/ende.txt", "x");
        for (int i = 0; i < 600; i++) t.Write(Left, $"gross/datei{i}.txt", "y"); // ein großer Ordner
        for (int i = 0; i < 30; i++) t.Write(Left, $"breit/o{i}/u/datei.txt", "z");

        var filter = new FileFilter(new PairOptions());
        var sequential = FolderScanner.Scan(t.Left, filter, parallelism: 1);
        var monitor = new ScanMonitor();
        var parallel = FolderScanner.Scan(t.Left, filter, parallelism: 8, monitor: monitor);

        Assert.Equal(sequential.Keys.OrderBy(k => k), parallel.Keys.OrderBy(k => k));
        Assert.True(parallel.ContainsKey(deep + "/ende.txt"));
        Assert.Equal(parallel.Count, monitor.Count);
        Assert.Null(monitor.LongestReadingFolder); // nichts mehr in Arbeit
    }

    [Fact]
    public void ParallelScan_ReportsProgress()
    {
        using var t = new TestFolders();
        for (int i = 0; i < 20; i++) t.Write(Left, $"ordner{i}/datei.txt", "x");

        int last = 0;
        FolderScanner.Scan(t.Left, new FileFilter(new PairOptions()), progress: n => last = n);

        Assert.Equal(40, last);
    }

    [Fact]
    public void Preview_ReportsProgressOfBothSides()
    {
        using var t = new TestFolders();
        t.Write(Left, "a.txt", "A");
        t.Write(Right, "b.txt", "B");
        t.Write(Right, "c.txt", "C");

        var reports = new List<PreviewProgress>();
        t.Engine.Preview(t.Pair, progress: new SynchronousProgress(reports.Add));

        var lastScan = reports.Last(r => r.Stage == PreviewStage.Scanning);
        Assert.Equal(3, lastScan.Count);
        Assert.Contains(reports, r => r.Stage == PreviewStage.Scanning && r.LeftDone && r.LeftCount == 1);
        Assert.Contains(reports, r => r.Stage == PreviewStage.Scanning && r.RightDone && r.RightCount == 2);
        Assert.Contains(reports, r => r.Stage == PreviewStage.Comparing && r.Count == 3);
    }

    [Fact]
    public void Preview_ReportsContentChecks()
    {
        using var t = new TestFolders();
        t.Pair.Options.CheckFileContents = true;
        t.Write(Left, "a.txt", "same", minutes: 1);
        t.Write(Right, "a.txt", "same", minutes: 9);

        var reports = new List<PreviewProgress>();
        t.Engine.Preview(t.Pair, progress: new SynchronousProgress(reports.Add));

        Assert.Contains(reports, r => r.Stage == PreviewStage.CheckingContents && r.Count == 1);
    }

    [Fact]
    public void Scan_StopsPromptly_WhenCancelled()
    {
        using var t = new TestFolders();
        for (int i = 0; i < 50; i++) t.Write(Left, $"ordner{i}/datei.txt", "x");

        using var cts = new CancellationTokenSource();
        int reports = 0;
        var ex = Record.Exception(() => FolderScanner.Scan(t.Left, new FileFilter(new PairOptions()), null, cts.Token,
            progress: _ => { if (++reports == 1) cts.Cancel(); }, parallelism: 1));

        Assert.IsAssignableFrom<OperationCanceledException>(ex is AggregateException agg ? agg.Flatten().InnerExceptions[0] : ex);
    }

    [Fact]
    public void Preview_ThrowsCancellation_WhenCancelledBeforeStart()
    {
        using var t = new TestFolders();
        t.Write(Left, "a.txt", "A");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = Record.Exception(() => t.Engine.Preview(t.Pair, cts.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    [Fact]
    public void Preview_FailsWithClearError_WhenOneSideIsMissing()
    {
        using var t = new TestFolders();
        Directory.Delete(t.Left);

        var ex = Assert.Throws<DirectoryNotFoundException>(() => t.Preview());
        Assert.Contains(t.Left, ex.Message);
    }

    [Fact]
    public void Echo_ChangeMadeDuringRun_IsNotRecordedAsSynchronized()
    {
        using var t = new TestFolders(SyncAction.Echo);
        t.Write(Left, "a.txt", "v1");
        t.Run();

        // Vorschau ohne Aktionen, dann ändert jemand die Datei, bevor ausgeführt wird.
        var plan = t.Preview();
        Assert.Empty(plan.Operations);
        t.Write(Left, "a.txt", "v2 geändert", minutes: 10);
        t.Engine.Run(plan);

        // Die Änderung muss beim nächsten Lauf weiterhin auffallen.
        var op = Assert.Single(t.Preview().Operations);
        Assert.Equal(OperationKind.Overwrite, op.Kind);
    }

    [Fact]
    public void SnapshotAfterRun_ContainsCopiedFilesAndNewParentFolders()
    {
        using var t = new TestFolders();
        t.Write(Left, "neu/tief/verschachtelt/datei.txt", "x");
        t.Write(Right, "rechts/nur.txt", "y");
        t.Run();

        // Ohne erneutes Einlesen muss der Stand trotzdem vollständig sein: nichts mehr zu tun,
        // und eine Löschung rechts wird danach korrekt als Löschung erkannt.
        Assert.Empty(t.Preview().Operations);

        Directory.Delete(t.PathOf(Right, "neu"), recursive: true);
        var plan = t.Preview();
        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Delete && o.TargetSide == Left);
        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.DeleteFolder && o.TargetPath == "neu");
    }

    [Fact]
    public void SnapshotAfterRun_ReflectsRenamesAndDeletes()
    {
        using var t = new TestFolders();
        t.Write(Left, "alt/datei.txt", "inhalt", minutes: 1);
        t.Write(Left, "weg.txt", "weg", minutes: 2);
        t.Run();

        Directory.Move(t.PathOf(Left, "alt"), t.PathOf(Left, "neu"));
        t.Delete(Left, "weg.txt");
        t.Run();

        Assert.Empty(t.Preview().Operations);
        Assert.True(t.Exists(Right, "neu/datei.txt"));
        Assert.False(t.Exists(Right, "weg.txt"));
    }

    /// <summary>IProgress, das sofort im aufrufenden Thread meldet (Progress&lt;T&gt; würde das verzögern).</summary>
    private sealed class SynchronousProgress : IProgress<PreviewProgress>
    {
        private readonly Action<PreviewProgress> _action;
        private readonly object _gate = new();

        public SynchronousProgress(Action<PreviewProgress> action) => _action = action;

        public void Report(PreviewProgress value)
        {
            lock (_gate) _action(value);
        }
    }
}
