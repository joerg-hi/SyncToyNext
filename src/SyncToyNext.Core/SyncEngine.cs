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
    /// <param name="progress">Meldet die Anzahl der bisher eingelesenen Einträge beider Seiten.</param>
    public SyncPlan Preview(FolderPair pair, CancellationToken cancellationToken = default, IProgress<int>? progress = null)
    {
        string? error = Validate(pair);
        if (error is not null) throw new InvalidOperationException(error);

        var filter = new FileFilter(pair.Options);
        var leftWarnings = new List<string>();
        var rightWarnings = new List<string>();
        int leftCount = 0, rightCount = 0;
        void Report() => progress?.Report(Volatile.Read(ref leftCount) + Volatile.Read(ref rightCount));

        // Beide Seiten gleichzeitig einlesen; schlägt eine fehl, wird die andere abgebrochen.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leftTask = Task.Run(() => ScanSide(pair.LeftPath, filter, leftWarnings, cts, n => { Volatile.Write(ref leftCount, n); Report(); }));
        var rightTask = Task.Run(() => ScanSide(pair.RightPath, filter, rightWarnings, cts, n => { Volatile.Write(ref rightCount, n); Report(); }));
        var leftSnapshotTask = Task.Run(() => SnapshotStore.Load(pair.LeftPath, pair.Id));
        var rightSnapshotTask = Task.Run(() => SnapshotStore.Load(pair.RightPath, pair.Id));

        try
        {
            Task.WaitAll(leftTask, rightTask, leftSnapshotTask, rightSnapshotTask);
        }
        catch (AggregateException ex)
        {
            // Den eigentlichen Fehler weitergeben, nicht den Folge-Abbruch der anderen Seite.
            var first = ex.Flatten().InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException)
                        ?? ex.Flatten().InnerExceptions[0];
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var warnings = leftWarnings.Concat(rightWarnings).ToList();

        return SyncPlanner.CreatePlan(pair, leftTask.Result, rightTask.Result, leftSnapshotTask.Result, rightSnapshotTask.Result,
            (l, r) => ContentComparer.AreEqual(
                PathUtil.ToFullPath(pair.LeftPath, l.RelativePath),
                PathUtil.ToFullPath(pair.RightPath, r.RelativePath),
                cancellationToken),
            warnings);
    }

    private static Dictionary<string, FileEntry> ScanSide(
        string root, FileFilter filter, List<string> warnings, CancellationTokenSource cts, Action<int> progress)
    {
        try
        {
            return FolderScanner.Scan(root, filter, warnings, cts.Token, progress);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            cts.Cancel();
            throw;
        }
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
    /// Neuer Stand je Seite = eingelesener Stand aus der Vorschau plus die Wirkung der erfolgreichen Aktionen.
    /// Die Ordner werden dafür nicht erneut eingelesen (auf Netzlaufwerken würde das die Laufzeit verdoppeln).
    /// Für Pfade, deren Aktion nicht erfolgreich war (Fehler, abgewählt, abgebrochen), bleibt der alte Stand
    /// erhalten. So gilt die Änderung beim nächsten Lauf weiterhin als offen und wird erneut angeboten.
    /// Änderungen, die jemand während des Laufs außerhalb der Aktionen macht, werden so ebenfalls erst beim
    /// nächsten Lauf als Änderung erkannt und nicht stillschweigend als „abgeglichen“ vermerkt.
    /// </summary>
    private static void UpdateSnapshots(SyncPlan plan, SyncResult result)
    {
        var pair = plan.Pair;
        var left = new Dictionary<string, FileEntry>(plan.LeftState, PathUtil.Comparer);
        var right = new Dictionary<string, FileEntry>(plan.RightState, PathUtil.Comparer);

        foreach (var op in plan.Operations.Where(o => o.Status == OperationStatus.Succeeded))
        {
            Apply(op.TargetSide == Side.Left ? left : right, op);
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

    /// <summary>Überträgt die Wirkung einer erfolgreichen Aktion auf den Stand der Zielseite.</summary>
    private static void Apply(Dictionary<string, FileEntry> state, SyncOperation op)
    {
        switch (op.Kind)
        {
            case OperationKind.CreateFolder:
                state[op.TargetPath] = new FileEntry(op.TargetPath, true, 0, default, FileAttributes.Directory);
                EntryTree.EnsureAncestors(state, op.TargetPath);
                break;

            case OperationKind.Rename:
            {
                var entry = state.Remove(op.SourcePath!, out var old) ? old : op.TargetEntry;
                if (entry is not null) state[op.TargetPath] = entry.WithPath(op.TargetPath);
                EntryTree.EnsureAncestors(state, op.TargetPath);
                break;
            }

            case OperationKind.RenameFolder:
                EntryTree.MoveTree(state, op.SourcePath!, op.TargetPath);
                EntryTree.EnsureAncestors(state, op.TargetPath);
                break;

            case OperationKind.Copy:
            case OperationKind.Overwrite:
                state[op.TargetPath] = op.ResultEntry ?? op.SourceEntry!.WithPath(op.TargetPath);
                EntryTree.EnsureAncestors(state, op.TargetPath);
                break;

            case OperationKind.Delete:
            case OperationKind.DeleteFolder:
                state.Remove(op.TargetPath);
                break;
        }
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
