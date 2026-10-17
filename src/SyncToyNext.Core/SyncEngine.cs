using SyncToyNext.Core.Execution;
using SyncToyNext.Core.Model;
using SyncToyNext.Core.Planning;
using SyncToyNext.Core.Scanning;
using SyncToyNext.Core.Snapshots;

namespace SyncToyNext.Core;

/// <summary>
/// Einstiegspunkt für Oberfläche und Kommandozeile: Vorschau erstellen und ausführen.
/// </summary>
public sealed class SyncEngine
{
    private readonly SyncExecutor _executor;

    public SyncEngine(IRecycleBin? recycleBin = null)
    {
        _executor = new SyncExecutor(recycleBin ?? new WindowsRecycleBin());
    }

    /// <summary>
    /// Prüft die Einstellungen eines Ordnerpaars. Gibt eine Fehlermeldung zurück oder <c>null</c>.
    /// </summary>
    public static string? Validate(FolderPair pair)
    {
        if (string.IsNullOrWhiteSpace(pair.Name)) return "Bitte einen Namen für das Ordnerpaar angeben.";
        if (string.IsNullOrWhiteSpace(pair.LeftPath)) return "Bitte den linken Ordner angeben.";
        if (string.IsNullOrWhiteSpace(pair.RightPath)) return "Bitte den rechten Ordner angeben.";

        string left, right;
        try
        {
            left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pair.LeftPath));
            right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pair.RightPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"Ungültiger Pfad: {ex.Message}";
        }

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return "Linker und rechter Ordner dürfen nicht identisch sein.";

        if (IsBelow(left, right) || IsBelow(right, left))
            return "Ein Ordner darf nicht innerhalb des anderen liegen.";

        return null;
    }

    private static bool IsBelow(string path, string folder) =>
        path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Liest beide Ordner ein und berechnet, was bei der Ausführung passieren würde. Ändert nichts.
    /// </summary>
    public SyncPlan Preview(FolderPair pair, CancellationToken cancellationToken = default)
    {
        string? error = Validate(pair);
        if (error is not null) throw new InvalidOperationException(error);

        var warnings = new List<string>();
        var filter = new FileFilter(pair.Options);
        var left = FolderScanner.Scan(pair.LeftPath, filter, warnings, cancellationToken);
        var right = FolderScanner.Scan(pair.RightPath, filter, warnings, cancellationToken);
        var leftSnapshot = SnapshotStore.Load(pair.LeftPath, pair.Id);
        var rightSnapshot = SnapshotStore.Load(pair.RightPath, pair.Id);

        var plan = SyncPlanner.CreatePlan(pair, left, right, leftSnapshot, rightSnapshot,
            (l, r) => ContentComparer.AreEqual(
                PathUtil.ToFullPath(pair.LeftPath, l.RelativePath),
                PathUtil.ToFullPath(pair.RightPath, r.RelativePath),
                cancellationToken),
            warnings);

        return plan;
    }

    /// <summary>
    /// Führt die ausgewählten Aktionen eines Plans aus und schreibt danach die Statusdateien fort.
    /// </summary>
    public SyncResult Run(SyncPlan plan, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = new SyncResult(plan) { StartedUtc = DateTime.UtcNow };
        _executor.Execute(plan, result, progress, cancellationToken);
        UpdateSnapshots(plan, result);
        result.FinishedUtc = DateTime.UtcNow;
        return result;
    }

    /// <summary>Vorschau und Ausführung in einem Schritt (z. B. für die Kommandozeile).</summary>
    public SyncResult PreviewAndRun(FolderPair pair, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Run(Preview(pair, cancellationToken), progress, cancellationToken);

    /// <summary>
    /// Neuer Stand je Seite = tatsächlicher Inhalt nach der Ausführung. Für Pfade, deren Aktion nicht
    /// erfolgreich war (Fehler, abgewählt, abgebrochen), bleibt der alte Stand erhalten. So gilt die
    /// Änderung beim nächsten Lauf weiterhin als offen und wird erneut angeboten.
    /// </summary>
    private static void UpdateSnapshots(SyncPlan plan, SyncResult result)
    {
        var pair = plan.Pair;
        var filter = new FileFilter(pair.Options);
        Dictionary<string, FileEntry> left, right;
        try
        {
            left = FolderScanner.Scan(pair.LeftPath, filter);
            right = FolderScanner.Scan(pair.RightPath, filter);
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Der Stand konnte nach der Ausführung nicht eingelesen werden: {ex.Message}");
            return;
        }

        foreach (var op in plan.Operations.Where(o => o.Status != OperationStatus.Succeeded))
        {
            foreach (string path in op.AffectedPaths())
            {
                if (op.Kind == OperationKind.RenameFolder)
                {
                    // Ein nicht umbenannter Ordner betrifft auch alles darin.
                    RevertTree(left, plan.LeftSnapshot, path);
                    RevertTree(right, plan.RightSnapshot, path);
                }
                else
                {
                    Revert(left, plan.LeftSnapshot, path);
                    Revert(right, plan.RightSnapshot, path);
                }
            }
        }

        Save(pair.LeftPath, pair.Id, left.Values, result);
        Save(pair.RightPath, pair.Id, right.Values, result);
    }

    private static void Revert(Dictionary<string, FileEntry> state, IReadOnlyDictionary<string, FileEntry>? oldSnapshot, string path)
    {
        if (oldSnapshot is not null && oldSnapshot.TryGetValue(path, out var old))
            state[path] = old;
        else
            state.Remove(path);
    }

    private static void RevertTree(Dictionary<string, FileEntry> state, IReadOnlyDictionary<string, FileEntry>? oldSnapshot, string folder)
    {
        foreach (string key in state.Keys.Where(k => PathUtil.IsSameOrBelow(k, folder)).ToList())
        {
            state.Remove(key);
        }

        if (oldSnapshot is null) return;
        foreach (var entry in oldSnapshot.Values.Where(e => PathUtil.IsSameOrBelow(e.RelativePath, folder)))
        {
            state[entry.RelativePath] = entry;
        }
    }

    private static void Save(string root, Guid pairId, IEnumerable<FileEntry> entries, SyncResult result)
    {
        try
        {
            SnapshotStore.Save(root, pairId, entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Warnings.Add($"Die Statusdatei in \"{root}\" konnte nicht geschrieben werden: {ex.Message}");
        }
    }
}
