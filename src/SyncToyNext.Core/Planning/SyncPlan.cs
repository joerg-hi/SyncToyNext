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
        IReadOnlyDictionary<string, FileEntry> leftState,
        IReadOnlyDictionary<string, FileEntry> rightState,
        IReadOnlyDictionary<string, FileEntry>? leftSnapshot,
        IReadOnlyDictionary<string, FileEntry>? rightSnapshot)
    {
        Pair = pair;
        Operations = operations;
        Warnings = warnings;
        ConfirmationRequired = confirmationRequired;
        LeftState = leftState;
        RightState = rightState;
        LeftSnapshot = leftSnapshot;
        RightSnapshot = rightSnapshot;
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

    public int LeftEntryCount => LeftState.Count;

    public int RightEntryCount => RightState.Count;

    public DateTime CreatedUtc { get; }

    /// <summary>Eingelesener Stand der linken Seite zum Zeitpunkt der Vorschau.</summary>
    internal IReadOnlyDictionary<string, FileEntry> LeftState { get; }

    /// <summary>Eingelesener Stand der rechten Seite zum Zeitpunkt der Vorschau.</summary>
    internal IReadOnlyDictionary<string, FileEntry> RightState { get; }

    internal IReadOnlyDictionary<string, FileEntry>? LeftSnapshot { get; }

    internal IReadOnlyDictionary<string, FileEntry>? RightSnapshot { get; }

    public int Count(OperationKind kind) => Operations.Count(o => o.Kind == kind);

    public long TotalBytesToCopy => Operations
        .Where(o => o.IsSelected && o.Kind is OperationKind.Copy or OperationKind.Overwrite)
        .Sum(o => o.Size);
}
