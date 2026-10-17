using System.Diagnostics;
using SyncToyNext.Core.Logging;
using SyncToyNext.Core.Model;
using SyncToyNext.Core.Planning;
using SyncToyNext.Core.Scanning;

namespace SyncToyNext.Core.Execution;

/// <summary>
/// Führt die ausgewählten Aktionen eines Plans aus. Vor jeder Aktion wird geprüft, ob sich die
/// betroffenen Dateien seit der Vorschau verändert haben; dann wird die Aktion übersprungen,
/// damit keine zwischenzeitlichen Änderungen verloren gehen.
/// </summary>
public sealed class SyncExecutor
{
    /// <summary>Ab dieser Größe wird der Beginn einer Kopie ohne Drosselung gemeldet.</summary>
    private const long LargeFileBytes = 8L * 1024 * 1024;

    private readonly IRecycleBin _recycleBin;
    private readonly HashSet<string> _knownDirectories = new(StringComparer.OrdinalIgnoreCase);

    public SyncExecutor(IRecycleBin recycleBin) => _recycleBin = recycleBin;

    public void Execute(
        SyncPlan plan,
        SyncResult result,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        _knownDirectories.Clear();
        var selected = plan.Operations.Where(o => o.IsSelected).ToList();
        foreach (var op in plan.Operations.Where(o => !o.IsSelected))
        {
            op.Status = OperationStatus.Skipped;
            op.Message = "In der Vorschau abgewählt";
        }

        long totalBytes = plan.TotalBytesToCopy;
        int completed = 0;
        long doneBytes = 0; // fertig bearbeitete Kopien, auch übersprungene oder fehlgeschlagene

        // Höchstens alle 100 ms melden: Bei Tausenden kleiner Dateien würde die Oberfläche sonst mit
        // Meldungen überflutet und dadurch selbst zur Bremse.
        long lastReport = 0;
        void Report(SyncOperation? op, long currentBytes, bool force = false)
        {
            if (progress is null) return;
            long now = Environment.TickCount64;
            if (!force && now - lastReport < 100) return;
            lastReport = now;
            progress.Report(new SyncProgress(completed, selected.Count, doneBytes + currentBytes, totalBytes,
                op?.DisplayPath, op, currentBytes));
        }

        foreach (var op in selected)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result.WasCancelled = true;
                break;
            }

            // Große Dateien sofort melden, damit ihr Name nicht wegen der Drosselung fehlt.
            Report(op, 0, force: op.Size >= LargeFileBytes);

            try
            {
                ExecuteOne(plan.Pair, op, result, bytes => Report(op, bytes), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                op.Status = OperationStatus.Skipped;
                op.Message = "Abgebrochen";
                result.WasCancelled = true;
                break;
            }
            catch (Exception ex)
            {
                op.Status = OperationStatus.Failed;
                op.Message = ex.Message;
            }

            completed++;
            if (op.Kind is OperationKind.Copy or OperationKind.Overwrite) doneBytes += op.Size;
        }

        foreach (var op in selected.Where(o => o.Status == OperationStatus.Pending))
        {
            op.Status = OperationStatus.Skipped;
            op.Message = "Abgebrochen";
        }

        Report(null, 0, force: true);
    }

    /// <param name="reportBytes">Meldet die bisher kopierten Bytes der laufenden Datei.</param>
    private void ExecuteOne(FolderPair pair, SyncOperation op, SyncResult result, Action<long> reportBytes, CancellationToken ct)
    {
        string targetRoot = op.TargetSide == Side.Left ? pair.LeftPath : pair.RightPath;
        string sourceRoot = op.TargetSide == Side.Left ? pair.RightPath : pair.LeftPath;
        string target = PathUtil.ToFullPath(targetRoot, op.TargetPath);
        bool recycle = pair.Options.SaveOverwrittenToRecycleBin;

        switch (op.Kind)
        {
            case OperationKind.CreateFolder:
                if (File.Exists(target)) throw new IOException("Es existiert bereits eine Datei mit diesem Namen.");
                Directory.CreateDirectory(target);
                Succeed(op);
                break;

            case OperationKind.Rename:
            {
                string oldPath = PathUtil.ToFullPath(targetRoot, op.SourcePath!);
                bool caseOnly = PathUtil.Comparer.Equals(op.SourcePath, op.TargetPath);
                if (!Unchanged(oldPath, op.TargetEntry)) { Skip(op, "Datei wurde seit der Vorschau verändert"); return; }
                if (!caseOnly && (File.Exists(target) || Directory.Exists(target))) { Skip(op, "Ziel existiert bereits"); return; }
                MoveEntry(oldPath, target, isDirectory: false, caseOnly);
                Succeed(op);
                break;
            }

            case OperationKind.RenameFolder:
            {
                string oldPath = PathUtil.ToFullPath(targetRoot, op.SourcePath!);
                bool caseOnly = PathUtil.Comparer.Equals(op.SourcePath, op.TargetPath);
                if (!Directory.Exists(oldPath)) { Skip(op, "Ordner existiert nicht mehr"); return; }
                if (!caseOnly && (File.Exists(target) || Directory.Exists(target))) { Skip(op, "Ziel existiert bereits"); return; }
                MoveEntry(oldPath, target, isDirectory: true, caseOnly);
                Succeed(op);
                break;
            }

            case OperationKind.Delete:
                if (!File.Exists(target)) { Succeed(op, "War bereits gelöscht"); return; }
                if (!Unchanged(target, op.TargetEntry)) { Skip(op, "Datei wurde seit der Vorschau verändert"); return; }
                RemoveFile(target, recycle);
                Succeed(op);
                break;

            case OperationKind.Copy:
            case OperationKind.Overwrite:
            {
                // Je eine Abfrage für Quelle und Ziel liefert alles Nötige (Existenz, Größe, Datum, Attribute).
                var sourceInfo = new FileInfo(PathUtil.ToFullPath(sourceRoot, op.SourcePath!));
                if (!Matches(sourceInfo, op.SourceEntry)) { Skip(op, "Quelldatei wurde seit der Vorschau verändert"); return; }

                var targetInfo = new FileInfo(target);
                if (op.Kind == OperationKind.Copy && targetInfo.Exists)
                {
                    Skip(op, "Zieldatei ist inzwischen vorhanden");
                    return;
                }

                if (op.Kind == OperationKind.Overwrite && !Matches(targetInfo, op.TargetEntry))
                {
                    Skip(op, "Zieldatei wurde seit der Vorschau verändert");
                    return;
                }

                var clock = Stopwatch.StartNew();
                var outcome = CopyFile(sourceInfo, targetInfo, recycle, reportBytes, ct);
                clock.Stop();
                result.BytesCopied += op.Size;

                var written = new FileInfo(target);
                op.ResultEntry = new FileEntry(op.TargetPath, false, written.Length, written.LastWriteTimeUtc, written.Attributes);
                Succeed(op, DescribeCopy(op.Size, clock.Elapsed, outcome));
                break;
            }

            case OperationKind.DeleteFolder:
                if (!Directory.Exists(target)) { Succeed(op, "War bereits gelöscht"); return; }
                if (Directory.EnumerateFileSystemEntries(target).Any()) { Skip(op, "Ordner ist nicht leer"); return; }
                new DirectoryInfo(target).Attributes &= ~FileAttributes.ReadOnly;
                Directory.Delete(target);
                Succeed(op);
                break;

            default:
                throw new NotSupportedException(op.Kind.ToString());
        }
    }

    /// <summary>
    /// Kopiert über eine temporäre Datei im Zielordner, damit bei einem Abbruch nie eine halbe Datei
    /// unter dem echten Namen liegt. Zeitstempel und Attribute werden übernommen.
    /// </summary>
    private CopyOutcome CopyFile(FileInfo source, FileInfo target, bool recycle, Action<long> reportBytes, CancellationToken ct)
    {
        string directory = target.DirectoryName!;
        EnsureDirectory(directory);
        string temp = Path.Combine(directory,
            $"{target.Name}.{Guid.NewGuid().ToString("N")[..8]}{FileFilter.TempFileSuffix}");

        try
        {
            var outcome = FileCopier.Copy(source.FullName, temp, reportBytes, ct);

            if (target.Exists)
            {
                if ((target.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(target.FullName, target.Attributes & ~FileAttributes.ReadOnly);
                }

                if (recycle) _recycleBin.MoveToRecycleBin(target.FullName);
            }

            File.Move(temp, target.FullName, overwrite: true);
            return outcome;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Messwerte für Ergebnis und Protokoll: bei großen Dateien Dauer und Tempo, und immer, wenn die
    /// Windows-Kopierfunktion versagt hat und blockweise kopiert wurde (über das Netz deutlich langsamer).
    /// </summary>
    private static string? DescribeCopy(long size, TimeSpan duration, CopyOutcome outcome)
    {
        bool fallback = outcome.FirstError is not null;
        if (size < LargeFileBytes && !fallback) return null;

        string text = $"{SyncLog.FormatBytes(size)} in {TransferEstimator.FormatDuration(duration)}";
        if (duration.TotalSeconds >= 0.5) text += $", {SyncLog.FormatBytes((long)(size / duration.TotalSeconds))}/s";
        if (fallback) text += $", blockweise kopiert nach Fehler: {outcome.FirstError}";
        return text;
    }

    /// <summary>
    /// Legt einen Ordner an. Bereits angelegte oder vorhandene Ordner werden gemerkt, damit beim Kopieren
    /// vieler Dateien in denselben Ordner nicht jedes Mal nachgefragt wird (spart Netzwerkzugriffe).
    /// </summary>
    private void EnsureDirectory(string directory)
    {
        if (_knownDirectories.Contains(directory)) return;
        Directory.CreateDirectory(directory);
        _knownDirectories.Add(directory);
    }

    /// <summary>
    /// Verschiebt eine Datei oder einen Ordner. Ändert sich nur die Schreibweise, geht es über einen
    /// Zwischennamen, weil Windows sonst Quelle und Ziel als identisch ansieht.
    /// </summary>
    private static void MoveEntry(string oldPath, string newPath, bool isDirectory, bool caseOnly)
    {
        string? parent = Path.GetDirectoryName(newPath);
        if (parent is not null) Directory.CreateDirectory(parent);

        if (caseOnly)
        {
            string temp = $"{newPath}.{Guid.NewGuid().ToString("N")[..8]}{FileFilter.TempFileSuffix}";
            Move(oldPath, temp, isDirectory);
            Move(temp, newPath, isDirectory);
        }
        else
        {
            Move(oldPath, newPath, isDirectory);
        }
    }

    private static void Move(string from, string to, bool isDirectory)
    {
        if (isDirectory) Directory.Move(from, to);
        else File.Move(from, to);
    }

    private void RemoveFile(string path, bool recycle)
    {
        ClearReadOnly(path);
        if (recycle) _recycleBin.MoveToRecycleBin(path);
        else File.Delete(path);
    }

    /// <summary>Prüft, ob die Datei noch dem bei der Planung gesehenen Stand entspricht.</summary>
    private static bool Unchanged(string fullPath, FileEntry? expected) => Matches(new FileInfo(fullPath), expected);

    private static bool Matches(FileInfo info, FileEntry? expected) =>
        expected is not null && info.Exists && info.Length == expected.Size && info.LastWriteTimeUtc == expected.LastWriteTimeUtc;

    private static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Succeed(SyncOperation op, string? message = null)
    {
        op.Status = OperationStatus.Succeeded;
        op.Message = message;
    }

    private static void Skip(SyncOperation op, string message)
    {
        op.Status = OperationStatus.Skipped;
        op.Message = message;
    }
}
