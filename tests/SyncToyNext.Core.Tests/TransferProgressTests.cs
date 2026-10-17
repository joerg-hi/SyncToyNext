using SyncToyNext.Core.Execution;
using SyncToyNext.Core.Planning;
using static SyncToyNext.Core.Model.Side;

namespace SyncToyNext.Core.Tests;

/// <summary>
/// Fortschrittsanzeige beim Ausführen: Meldungen je Datei sowie Tempo und Restzeit.
/// </summary>
public class TransferProgressTests
{
    [Fact]
    public void Run_ReportsCurrentOperationAndReachesTotal()
    {
        using var t = new TestFolders();
        t.Write(Left, "gross.bin", new string('x', 3 * 1024 * 1024));
        t.Write(Left, "klein.txt", "abc");

        var plan = t.Preview();
        var reports = new List<SyncProgress>();
        t.Engine.Run(plan, new SynchronousProgress(reports.Add));

        long total = plan.TotalBytesToCopy;
        Assert.Contains(reports, r => r.CurrentOperation?.TargetPath == "gross.bin" && r.CurrentItem == "gross.bin");
        Assert.All(reports.Where(r => r.CurrentOperation is not null),
            r => Assert.InRange(r.CurrentBytes, 0, r.CurrentOperation!.Size));
        var last = reports.Last();
        Assert.Null(last.CurrentOperation);
        Assert.Equal(total, last.BytesCopied);
        Assert.Equal(2, last.CompletedOperations);
    }

    [Fact]
    public void Run_CountsSkippedCopiesAsDone_SoProgressReachesTheEnd()
    {
        using var t = new TestFolders();
        t.Write(Left, "a.txt", "aaaa");
        t.Write(Left, "b.txt", "bbbbbbbb");

        var plan = t.Preview();
        t.Write(Left, "a.txt", "geändert nach der Vorschau", minutes: 5); // wird übersprungen
        var reports = new List<SyncProgress>();
        var result = t.Engine.Run(plan, new SynchronousProgress(reports.Add));

        Assert.Equal(1, result.Skipped);
        Assert.Equal(8, result.BytesCopied); // nur tatsächlich kopiert
        Assert.Equal(plan.TotalBytesToCopy, reports.Last().BytesCopied); // Fortschritt trotzdem vollständig
    }

    [Fact]
    public void Estimator_ComputesSpeedAndRemainingTime()
    {
        var estimator = new TransferEstimator(TimeSpan.FromSeconds(8));
        const long total = 1000L * 1024 * 1024;
        // gleichmäßig 10 MB/s
        for (int second = 0; second <= 5; second++)
        {
            long bytes = second * 10L * 1024 * 1024;
            estimator.Add(TimeSpan.FromSeconds(second), bytes, bytes / (double)total);
        }

        Assert.Equal(10.0 * 1024 * 1024, estimator.BytesPerSecond!.Value, 1);
        Assert.Equal(95, estimator.Remaining!.Value.TotalSeconds, 1);
    }

    [Fact]
    public void Estimator_NoRemainingTimeAtTheStart()
    {
        var estimator = new TransferEstimator();
        estimator.Add(TimeSpan.Zero, 0, 0);
        estimator.Add(TimeSpan.FromSeconds(1.5), 1000, 0.1);

        Assert.NotNull(estimator.BytesPerSecond);
        Assert.Null(estimator.Remaining);
    }

    [Fact]
    public void Estimator_FollowsChangingSpeedWithinWindow()
    {
        var estimator = new TransferEstimator(TimeSpan.FromSeconds(4));
        long bytes = 0;
        for (int second = 0; second <= 20; second++)
        {
            bytes += second <= 10 ? 100 : 10; // erst schnell, dann langsam
            estimator.Add(TimeSpan.FromSeconds(second), bytes, Math.Min(1, bytes / 5000.0));
        }

        Assert.Equal(10, estimator.BytesPerSecond!.Value, 1);
    }

    [Theory]
    [InlineData(4, "wenige Sekunden")]
    [InlineData(42, "etwa 45 Sek.")]
    [InlineData(170, "etwa 3 Min.")]
    [InlineData(3600, "etwa 1 Std.")]
    [InlineData(4800, "etwa 1 Std. 20 Min.")]
    public void FormatRemaining_RoundsSensibly(int seconds, string expected) =>
        Assert.Equal(expected, TransferEstimator.FormatRemaining(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(42, "0:42")]
    [InlineData(725, "12:05")]
    [InlineData(3753, "1:02:33")]
    public void FormatDuration_ShowsClockStyle(int seconds, string expected) =>
        Assert.Equal(expected, TransferEstimator.FormatDuration(TimeSpan.FromSeconds(seconds)));

    private sealed class SynchronousProgress : IProgress<SyncProgress>
    {
        private readonly Action<SyncProgress> _action;

        public SynchronousProgress(Action<SyncProgress> action) => _action = action;

        public void Report(SyncProgress value) => _action(value);
    }
}
