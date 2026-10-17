using System.Collections.Concurrent;
using System.IO.Enumeration;
using SyncToyNext.Core.Model;

namespace SyncToyNext.Core.Scanning;

/// <summary>
/// Liest den aktuellen Inhalt eines Wurzelordners rekursiv ein.
/// </summary>
/// <remarks>
/// Auf Netzlaufwerken (NAS, Freigaben) ist die Wartezeit je Anfrage der Engpass, nicht die Bandbreite.
/// Deshalb werden mehrere Ordner gleichzeitig gelesen, mit großem Puffer (weniger Anfragen je Ordner),
/// und Größe, Datum und Attribute direkt aus dem Verzeichnis-Listing übernommen (keine Abfrage je Datei).
/// Gefundene Unterordner kommen in eine gemeinsame Warteschlange; ein langsamer oder sehr großer Ordner
/// hält die übrigen also nicht auf.
/// </remarks>
public static class FolderScanner
{
    /// <summary>Anzahl der Ordner, die gleichzeitig gelesen werden.</summary>
    public const int DefaultParallelism = 8;

    private const int BufferSize = 64 * 1024;
    private const int ProgressIntervalMs = 200;
    private const int BatchSize = 256;

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        BufferSize = BufferSize,
    };

    /// <param name="progress">Wird regelmäßig mit der Anzahl der bisher gelesenen Einträge aufgerufen (aus beliebigen Threads).</param>
    /// <param name="monitor">Zeigt laufend, wie viel gelesen ist und welcher Ordner gerade gelesen wird.</param>
    public static Dictionary<string, FileEntry> Scan(
        string root,
        FileFilter filter,
        ICollection<string>? warnings = null,
        CancellationToken cancellationToken = default,
        Action<int>? progress = null,
        int parallelism = DefaultParallelism,
        ScanMonitor? monitor = null)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Der Ordner \"{root}\" existiert nicht oder ist nicht erreichbar.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var scan = new ScanState(root, filter, progress, monitor, cts.Token);

        // Der Wurzelordner muss lesbar sein; ein Fehler dort bricht das Einlesen ab.
        var queue = new ConcurrentQueue<string>(scan.ReadDirectory("", isRoot: true));
        int pending = queue.Count; // Ordner in der Warteschlange oder in Arbeit
        if (pending > 0)
        {
            int workers = Math.Max(1, parallelism);
            using var available = new SemaphoreSlim(pending);
            int finished = 0;

            void Work()
            {
                try
                {
                    while (true)
                    {
                        available.Wait(cts.Token);
                        if (Volatile.Read(ref finished) == 1) return;
                        if (!queue.TryDequeue(out var relative)) continue;

                        foreach (string sub in scan.ReadDirectory(relative, isRoot: false))
                        {
                            Interlocked.Increment(ref pending);
                            queue.Enqueue(sub);
                            available.Release();
                        }

                        if (Interlocked.Decrement(ref pending) == 0)
                        {
                            Volatile.Write(ref finished, 1);
                            available.Release(workers); // alle wartenden Threads beenden
                            return;
                        }
                    }
                }
                catch
                {
                    cts.Cancel(); // die übrigen Threads nicht ewig warten lassen
                    throw;
                }
            }

            var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(Work)).ToArray();
            try
            {
                Task.WaitAll(tasks);
            }
            catch (AggregateException ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = ex.Flatten().InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException)
                            ?? ex.Flatten().InnerExceptions[0];
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke(scan.Count);
        if (warnings is not null)
        {
            foreach (string warning in scan.Warnings.OrderBy(w => w, StringComparer.CurrentCulture)) warnings.Add(warning);
        }

        return new Dictionary<string, FileEntry>(scan.Result, PathUtil.Comparer);
    }

    private sealed class ScanState
    {
        private readonly string _root;
        private readonly FileFilter _filter;
        private readonly Action<int>? _progress;
        private readonly ScanMonitor? _monitor;
        private readonly CancellationToken _cancellationToken;
        private int _count;
        private long _lastReport;

        public ScanState(string root, FileFilter filter, Action<int>? progress, ScanMonitor? monitor, CancellationToken cancellationToken)
        {
            _root = root;
            _filter = filter;
            _progress = progress;
            _monitor = monitor;
            _cancellationToken = cancellationToken;
        }

        public ConcurrentDictionary<string, FileEntry> Result { get; } = new(PathUtil.Comparer);

        public ConcurrentQueue<string> Warnings { get; } = new();

        public int Count => Volatile.Read(ref _count);

        /// <summary>Liest einen Ordner und gibt die (nicht ausgefilterten) Unterordner zurück.</summary>
        public List<string> ReadDirectory(string relative, bool isRoot)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var subfolders = new List<string>();
            List<RawEntry> children;
            _monitor?.Begin(relative);
            try
            {
                string full = relative.Length == 0 ? _root : PathUtil.ToFullPath(_root, relative);
                // Auch innerhalb eines großen Ordners regelmäßig auf „Abbrechen“ prüfen und Fortschritt melden.
                children = new List<RawEntry>();
                foreach (var child in new FileSystemEnumerable<RawEntry>(full, Transform, Options))
                {
                    children.Add(child);
                    if (children.Count % BatchSize == 0)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        ReportProgress(BatchSize);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                if (isRoot) throw;
                Warnings.Enqueue($"Ordner \"{PathUtil.ToDisplay(relative)}\" konnte nicht gelesen werden: {ex.Message}");
                return subfolders;
            }
            finally
            {
                _monitor?.End(relative);
            }

            foreach (var child in children)
            {
                string childRelative = relative.Length == 0 ? child.Name : relative + "/" + child.Name;

                if (child.IsDirectory)
                {
                    if (!_filter.IncludeDirectory(childRelative, child.Attributes)) continue;
                    if (!TryAdd(new FileEntry(childRelative, true, 0, default, child.Attributes))) continue;
                    subfolders.Add(childRelative);
                }
                else
                {
                    if (!_filter.IncludeFile(childRelative, child.Attributes)) continue;
                    TryAdd(new FileEntry(childRelative, false, child.Length, child.LastWriteTimeUtc, child.Attributes));
                }
            }

            ReportProgress(children.Count % BatchSize);
            return subfolders;
        }

        private bool TryAdd(FileEntry entry)
        {
            if (Result.TryAdd(entry.RelativePath, entry)) return true;

            // Nur auf Dateisystemen mit Unterscheidung der Groß-/Kleinschreibung möglich.
            Warnings.Enqueue($"\"{PathUtil.ToDisplay(entry.RelativePath)}\" existiert in mehreren Schreibweisen und wird nur einmal berücksichtigt.");
            return false;
        }

        private void ReportProgress(int added)
        {
            int total = Interlocked.Add(ref _count, added);
            _monitor?.SetCount(total);
            if (_progress is null) return;

            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref _lastReport);
            if (now - last >= ProgressIntervalMs && Interlocked.CompareExchange(ref _lastReport, now, last) == last)
            {
                _progress(total);
            }
        }

        private static RawEntry Transform(ref FileSystemEntry entry) => new(
            entry.FileName.ToString(),
            entry.IsDirectory,
            entry.IsDirectory ? 0 : entry.Length,
            entry.IsDirectory ? default : entry.LastWriteTimeUtc.UtcDateTime,
            entry.Attributes);
    }

    private readonly record struct RawEntry(string Name, bool IsDirectory, long Length, DateTime LastWriteTimeUtc, FileAttributes Attributes);
}
