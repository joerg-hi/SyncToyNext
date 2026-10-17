using System.Collections.Concurrent;

namespace SyncToyNext.Core.Scanning;

/// <summary>
/// Beobachtet ein laufendes Einlesen: Anzahl der Einträge, gerade gelesene Ordner und wann zuletzt
/// etwas vorwärtsging. Damit lässt sich anzeigen, ob ein Netzlaufwerk nur langsam ist oder gar nicht antwortet.
/// </summary>
/// <remarks>Wird von mehreren Threads gleichzeitig beschrieben und gelesen.</remarks>
public sealed class ScanMonitor
{
    private readonly ConcurrentDictionary<string, long> _reading = new(StringComparer.Ordinal);
    private int _count;
    private long _lastActivity = Environment.TickCount64;

    /// <summary>Bisher gelesene Einträge.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Zeit seit der letzten Rückmeldung des Laufwerks (neuer Ordner, neue Einträge oder Ordner fertig).</summary>
    public TimeSpan SinceLastActivity => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastActivity));

    /// <summary>
    /// Der Ordner, der schon am längsten gelesen wird (relativ, mit „/“; leer = Wurzelordner),
    /// oder <c>null</c>, wenn gerade keiner gelesen wird.
    /// </summary>
    public string? LongestReadingFolder
    {
        get
        {
            string? folder = null;
            long oldest = long.MaxValue;
            foreach (var (path, started) in _reading)
            {
                if (started < oldest)
                {
                    oldest = started;
                    folder = path;
                }
            }

            return folder;
        }
    }

    internal void Begin(string relative)
    {
        _reading[relative] = Environment.TickCount64;
        Touch();
    }

    internal void End(string relative)
    {
        _reading.TryRemove(relative, out _);
        Touch();
    }

    internal void SetCount(int count)
    {
        Volatile.Write(ref _count, count);
        Touch();
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
}
