using SyncToyNext.Core.Model;

namespace SyncToyNext.Core.Planning;

/// <summary>
/// Ergebnis der Vorschau: alle geplanten Aktionen plus die eingelesenen Stände,
/// die nach der Ausführung zum Fortschreiben der Statusdateien gebraucht werden.
/// </summary>
public sealed class SyncPlan
{
    internal SyncPlan(
        FolderPair pair,
        List<SyncOperation> operations,
        List<string> warnings,
        string? confirmationRequired,
        IReadOnlyDictionary<string, FileEntry>? leftSnapshot,
        IReadOnlyDictionary<string, FileEntry>? rightSnapshot,
        int leftCount,
        int rightCount)
    {
        Pair = pair;
        Operations = operations;
        Warnings = warnings;
        ConfirmationRequired = confirmationRequired;
        LeftSnapshot = leftSnapshot;
        RightSnapshot = rightSnapshot;
        LeftEntryCount = leftCount;
        RightEntryCount = rightCount;
        CreatedUtc = DateTime.UtcNow;
    }

    /// <summary>Kopie des Ordnerpaars zum Zeitpunkt der Vorschau.</summary>
    public FolderPair Pair { get; }

    public IReadOnlyList<SyncOperation> Operations { get; }

    /// <summary>Hinweise, z. B. Konflikte, die nicht automatisch aufgelöst werden.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// Ist dieser Text gesetzt, muss der Benutzer die Ausführung ausdrücklich bestätigen
    /// (z. B. weil eine Seite plötzlich leer ist und sonst alles gelöscht würde).
    /// </summary>
    public string? ConfirmationRequired { get; }

    public bool IsFirstRun => LeftSnapshot is null && RightSnapshot is null;

    public int LeftEntryCount { get; }

    public int RightEntryCount { get; }

    public DateTime CreatedUtc { get; }

    internal IReadOnlyDictionary<string, FileEntry>? LeftSnapshot { get; }

    internal IReadOnlyDictionary<string, FileEntry>? RightSnapshot { get; }

    public int Count(OperationKind kind) => Operations.Count(o => o.Kind == kind);

    public long TotalBytesToCopy => Operations
        .Where(o => o.IsSelected && o.Kind is OperationKind.Copy or OperationKind.Overwrite)
        .Sum(o => o.Size);
}
