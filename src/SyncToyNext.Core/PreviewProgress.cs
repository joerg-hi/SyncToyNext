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

/// <summary>Fortschritt der Vorschau: aktuelle Phase und Anzahl der bisher bearbeiteten Einträge bzw. Dateien.</summary>
public readonly record struct PreviewProgress(PreviewStage Stage, int Count);
