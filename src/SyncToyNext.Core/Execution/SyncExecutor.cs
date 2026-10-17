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
    private const int CopyBufferSize = 1024 * 1024;

    private readonly IRecycleBin _recycleBin;

    public SyncExecutor(IRecycleBin recycleBin) => _recycleBin = recycleBin;

    public void Execute(
        SyncPlan plan,
        SyncResult result,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var selected = plan.Operations.Where(o => o.IsSelected).ToList();
        foreach (var op in plan.Operations.Where(o => !o.IsSelected))
        {
            op.Status = OperationStatus.Skipped;
            op.Message = "In der Vorschau abgewählt";
        }

        long totalBytes = plan.TotalBytesToCopy;
        int completed = 0;

        foreach (var op in selected)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result.WasCancelled = true;
                break;
            }

            progress?.Report(new SyncProgress(completed, selected.Count, result.BytesCopied, totalBytes, op.DisplayPath));

            try
            {
                ExecuteOne(plan.Pair, op, result, bytes =>
                    progress?.Report(new SyncProgress(completed, selected.Count, bytes, totalBytes, op.DisplayPath)),
                    cancellationToken);
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
        }

        foreach (var op in selected.Where(o => o.Status == OperationStatus.Pending))
        {
            op.Status = OperationStatus.Skipped;
            op.Message = "Abgebrochen";
        }

        progress?.Report(new SyncProgress(completed, selected.Count, result.BytesCopied, totalBytes, null));
    }

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
                string source = PathUtil.ToFullPath(sourceRoot, op.SourcePath!);
                if (!Unchanged(source, op.SourceEntry)) { Skip(op, "Quelldatei wurde seit der Vorschau verändert"); return; }

                if (op.Kind == OperationKind.Copy && (File.Exists(target) || Directory.Exists(target)))
                {
                    Skip(op, "Zieldatei ist inzwischen vorhanden");
                    return;
                }

                if (op.Kind == OperationKind.Overwrite && !Unchanged(target, op.TargetEntry))
                {
                    Skip(op, "Zieldatei wurde seit der Vorschau verändert");
                    return;
                }

                long before = result.BytesCopied;
                CopyFile(source, target, recycle, copied => reportBytes(before + copied), ct);
                result.BytesCopied = before + op.Size;
                Succeed(op);
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
    private void CopyFile(string source, string target, bool recycle, Action<long> reportBytes, CancellationToken ct)
    {
        string directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory,
            $"{Path.GetFileName(target)}.{Guid.NewGuid().ToString("N")[..8]}{FileFilter.TempFileSuffix}");

        var sourceInfo = new FileInfo(source);
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize))
            {
                var buffer = new byte[CopyBufferSize];
                long copied = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    copied += read;
                    reportBytes(copied);
                }
            }

            File.SetLastWriteTimeUtc(temp, sourceInfo.LastWriteTimeUtc);
            if (OperatingSystem.IsWindows())
            {
                File.SetCreationTimeUtc(temp, sourceInfo.CreationTimeUtc);
            }

            if (File.Exists(target))
            {
                ClearReadOnly(target);
                if (recycle) _recycleBin.MoveToRecycleBin(target);
            }

            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        const FileAttributes copiedAttributes = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;
        var attributes = sourceInfo.Attributes & copiedAttributes;
        if (attributes != 0)
        {
            try { File.SetAttributes(target, attributes); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Attribute sind nicht kritisch */ }
        }
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
    private static bool Unchanged(string fullPath, FileEntry? expected)
    {
        if (expected is null) return false;
        var info = new FileInfo(fullPath);
        return info.Exists && info.Length == expected.Size && info.LastWriteTimeUtc == expected.LastWriteTimeUtc;
    }

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
