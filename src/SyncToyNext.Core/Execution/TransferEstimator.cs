namespace SyncToyNext.Core.Execution;

/// <summary>
/// Schätzt Übertragungsrate und Restzeit aus den Fortschrittsmeldungen der letzten Sekunden.
/// </summary>
/// <remarks>
/// Gemessen wird über ein gleitendes Zeitfenster statt über die ganze Laufzeit, damit sich die Anzeige
/// anpasst, wenn z. B. nach vielen kleinen Dateien eine große kommt (oder das Netz langsamer wird).
/// </remarks>
public sealed class TransferEstimator
{
    private readonly TimeSpan _window;
    private readonly Queue<Sample> _samples = new();

    /// <param name="window">Länge des Messfensters; Standard 8 Sekunden.</param>
    public TransferEstimator(TimeSpan? window = null) => _window = window ?? TimeSpan.FromSeconds(8);

    /// <summary>Kürzeste Laufzeit, ab der eine Restzeit angezeigt wird (vorher ist sie zu ungenau).</summary>
    public static TimeSpan MinimumElapsedForEstimate { get; } = TimeSpan.FromSeconds(3);

    /// <summary>Bytes pro Sekunde im Messfenster, <c>null</c> solange zu wenig gemessen wurde.</summary>
    public double? BytesPerSecond { get; private set; }

    /// <summary>Geschätzte Restzeit, <c>null</c> solange keine sinnvolle Schätzung möglich ist.</summary>
    public TimeSpan? Remaining { get; private set; }

    /// <param name="elapsed">Zeit seit dem Start der Ausführung.</param>
    /// <param name="bytes">Bisher abgearbeitete Bytes.</param>
    /// <param name="fraction">Gesamtfortschritt zwischen 0 und 1.</param>
    public void Add(TimeSpan elapsed, long bytes, double fraction)
    {
        var sample = new Sample(elapsed, bytes, Math.Clamp(fraction, 0, 1));
        _samples.Enqueue(sample);
        while (_samples.Count > 2 && elapsed - _samples.Peek().Elapsed > _window) _samples.Dequeue();

        var first = _samples.Peek();
        double seconds = (elapsed - first.Elapsed).TotalSeconds;
        if (seconds < 1)
        {
            return; // bisherige Werte behalten, bis wieder genug gemessen wurde
        }

        BytesPerSecond = Math.Max(0, (bytes - first.Bytes) / seconds);

        double rate = (sample.Fraction - first.Fraction) / seconds;
        Remaining = elapsed >= MinimumElapsedForEstimate && rate > 0
            ? TimeSpan.FromSeconds(Math.Min((1 - sample.Fraction) / rate, TimeSpan.FromDays(1).TotalSeconds))
            : null;
    }

    /// <summary>Restzeit in Worten, gerundet: „etwa 45 Sek.“, „etwa 3 Min.“, „etwa 1 Std. 20 Min.“.</summary>
    public static string FormatRemaining(TimeSpan remaining)
    {
        double seconds = remaining.TotalSeconds;
        if (seconds < 10) return "wenige Sekunden";
        if (seconds < 60) return $"etwa {Math.Ceiling(seconds / 5) * 5:0} Sek.";

        int minutes = (int)Math.Round(seconds / 60);
        if (minutes < 60) return $"etwa {minutes} Min.";

        int hours = minutes / 60;
        int rest = minutes % 60;
        return rest == 0 ? $"etwa {hours} Std." : $"etwa {hours} Std. {rest} Min.";
    }

    /// <summary>Dauer als „0:42“, „12:05“ oder „1:02:33“.</summary>
    public static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");

    private readonly record struct Sample(TimeSpan Elapsed, long Bytes, double Fraction);
}
