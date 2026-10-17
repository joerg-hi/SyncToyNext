using SyncToyNext.Core.Planning;

namespace SyncToyNext.Core.Execution;

public sealed class SyncResult
{
    public SyncResult(SyncPlan plan) => Plan = plan;

    public SyncPlan Plan { get; }

    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;

    public DateTime FinishedUtc { get; set; }

    public TimeSpan Duration => FinishedUtc - StartedUtc;

    public bool WasCancelled { get; set; }

    public long BytesCopied { get; set; }

    /// <summary>Hinweise nach der Ausführung, z. B. wenn eine Statusdatei nicht geschrieben werden konnte.</summary>
    public List<string> Warnings { get; } = new();

    public int Succeeded => Plan.Operations.Count(o => o.Status == OperationStatus.Succeeded);

    public int Failed => Plan.Operations.Count(o => o.Status == OperationStatus.Failed);

    /// <summary>Abgewählte, wegen Änderungen übersprungene oder durch Abbruch nicht ausgeführte Aktionen.</summary>
    public int Skipped => Plan.Operations.Count(o => o.Status is OperationStatus.Skipped or OperationStatus.Pending);

    public bool HasErrors => Failed > 0;
}

public readonly record struct SyncProgress(
    int CompletedOperations,
    int TotalOperations,
    long BytesCopied,
    long TotalBytes,
    string? CurrentItem);
