using System.Text.Json.Serialization;

namespace SyncToyNext.Core.Model;

/// <summary>
/// Ein benanntes Ordnerpaar mit Synchronisationsart und Optionen.
/// </summary>
public sealed class FolderPair
{
    /// <summary>
    /// Eindeutige Kennung. Sie bestimmt auch den Namen der Statusdateien in beiden Ordnern.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public string LeftPath { get; set; } = "";

    public string RightPath { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SyncAction Action { get; set; } = SyncAction.Synchronize;

    public PairOptions Options { get; set; } = new();

    public DateTime? LastRunUtc { get; set; }

    public FolderPair Clone()
    {
        var copy = (FolderPair)MemberwiseClone();
        copy.Options = Options.Clone();
        return copy;
    }
}

/// <summary>
/// Optionen eines Ordnerpaars, entsprechend dem Dialog „Change Options“ von SyncToy 2.1.
/// </summary>
public sealed class PairOptions
{
    /// <summary>
    /// Einzuschließende Dateien, z. B. "*" oder "*.docx; *.xlsx". Trennzeichen: Semikolon oder Komma.
    /// </summary>
    public string IncludePatterns { get; set; } = "*";

    /// <summary>
    /// Auszuschließende Dateien, z. B. "*.tmp; ~$*". Trennzeichen: Semikolon oder Komma.
    /// </summary>
    public string ExcludePatterns { get; set; } = "";

    /// <summary>
    /// Ausgeschlossene Unterordner, relativ zum jeweiligen Wurzelordner (z. B. "Temp/Cache").
    /// </summary>
    public List<string> ExcludedSubfolders { get; set; } = new();

    public bool ExcludeReadOnly { get; set; }

    public bool ExcludeHidden { get; set; }

    public bool ExcludeSystem { get; set; }

    /// <summary>
    /// Überschriebene und gelöschte Dateien in den Papierkorb verschieben (in SyncToy standardmäßig an).
    /// </summary>
    public bool SaveOverwrittenToRecycleBin { get; set; } = true;

    /// <summary>
    /// Dateiinhalte per Prüfsumme vergleichen, statt sich nur auf Größe und Änderungsdatum zu verlassen.
    /// </summary>
    public bool CheckFileContents { get; set; }

    /// <summary>
    /// Ordnerpaar bei „Alle ausführen“ berücksichtigen.
    /// </summary>
    public bool ActiveForRunAll { get; set; } = true;

    public PairOptions Clone()
    {
        var copy = (PairOptions)MemberwiseClone();
        copy.ExcludedSubfolders = new List<string>(ExcludedSubfolders);
        return copy;
    }
}
