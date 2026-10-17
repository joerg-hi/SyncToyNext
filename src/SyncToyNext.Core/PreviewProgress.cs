namespace SyncToyNext.Core;

public enum PreviewStage
{
    /// <summary>Beide Ordner werden eingelesen.</summary>
    Scanning,

    /// <summary>Beide Seiten werden mit dem gespeicherten Stand verglichen.</summary>
    Comparing,

    /// <summary>Dateiinhalte werden verglichen (Option „Dateiinhalte prüfen“).</summary>
    CheckingContents,
}

/// <summary>
/// Fortschritt der Vorschau.
/// </summary>
/// <param name="Stage">Aktuelle Phase.</param>
/// <param name="Count">Beim Einlesen: Einträge beider Seiten zusammen; beim Inhaltsvergleich: verglichene Dateien.</param>
/// <param name="LeftCount">Bisher eingelesene Einträge des linken Ordners.</param>
/// <param name="RightCount">Bisher eingelesene Einträge des rechten Ordners.</param>
/// <param name="LeftDone">Der linke Ordner ist vollständig eingelesen.</param>
/// <param name="RightDone">Der rechte Ordner ist vollständig eingelesen.</param>
/// <param name="Left">Beim Einlesen: was links gerade gelesen wird.</param>
/// <param name="Right">Beim Einlesen: was rechts gerade gelesen wird.</param>
public readonly record struct PreviewProgress(
    PreviewStage Stage,
    int Count,
    int LeftCount = 0,
    int RightCount = 0,
    bool LeftDone = false,
    bool RightDone = false,
    ScanActivity? Left = null,
    ScanActivity? Right = null);

/// <summary>Was auf einer Seite gerade gelesen wird.</summary>
/// <param name="Folder">Ordner, der schon am längsten gelesen wird (relativ, mit „/“; leer = Wurzelordner), oder <c>null</c>.</param>
/// <param name="SinceLastActivity">Zeit seit der letzten Rückmeldung des Laufwerks.</param>
public readonly record struct ScanActivity(string? Folder, TimeSpan SinceLastActivity);
