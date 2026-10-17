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
public readonly record struct PreviewProgress(
    PreviewStage Stage,
    int Count,
    int LeftCount = 0,
    int RightCount = 0,
    bool LeftDone = false,
    bool RightDone = false);
