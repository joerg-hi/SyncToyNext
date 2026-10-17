using System.ComponentModel;
using System.Runtime.CompilerServices;
using SyncToyNext.Core.Model;

namespace SyncToyNext.Core.Planning;

public enum OperationKind
{
    /// <summary>Ordner auf der Zielseite anlegen.</summary>
    CreateFolder,

    /// <summary>Ordner samt Inhalt auf der Zielseite umbenennen bzw. verschieben.</summary>
    RenameFolder,

    /// <summary>Datei auf der Zielseite umbenennen bzw. verschieben.</summary>
    Rename,

    /// <summary>Datei auf der Zielseite löschen.</summary>
    Delete,

    /// <summary>Neue Datei auf die Zielseite kopieren.</summary>
    Copy,

    /// <summary>Vorhandene Datei auf der Zielseite überschreiben.</summary>
    Overwrite,

    /// <summary>Ordner auf der Zielseite löschen, sofern er dann leer ist.</summary>
    DeleteFolder,
}

public enum OperationStatus
{
    Pending,
    Succeeded,
    Failed,
    Skipped,
}

/// <summary>
/// Eine geplante Aktion. In der Vorschau kann der Benutzer einzelne Aktionen abwählen.
/// </summary>
public sealed class SyncOperation : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private OperationStatus _status = OperationStatus.Pending;
    private string? _message;

    public SyncOperation(OperationKind kind, Side targetSide, string targetPath)
    {
        Kind = kind;
        TargetSide = targetSide;
        TargetPath = targetPath;
    }

    public OperationKind Kind { get; }

    /// <summary>Seite, auf der die Aktion etwas verändert.</summary>
    public Side TargetSide { get; }

    /// <summary>Relativer Pfad auf der Zielseite (bei Umbenennung: der neue Pfad).</summary>
    public string TargetPath { get; }

    /// <summary>
    /// Kopieren/Überschreiben: relativer Pfad auf der Quellseite (der anderen Seite).
    /// Umbenennen: alter relativer Pfad auf der Zielseite.
    /// </summary>
    public string? SourcePath { get; init; }

    /// <summary>Stand der Quelldatei bei der Planung (zum Erkennen späterer Änderungen).</summary>
    public FileEntry? SourceEntry { get; init; }

    /// <summary>Stand der Zieldatei bei der Planung, falls sie überschrieben, gelöscht oder umbenannt wird.</summary>
    public FileEntry? TargetEntry { get; init; }

    /// <summary>
    /// Tatsächlicher Stand der Zieldatei nach erfolgreichem Kopieren (zurückgelesen, weil manche
    /// Datenträger Zeitstempel runden). Wird zum Fortschreiben der Statusdatei verwendet.
    /// </summary>
    public FileEntry? ResultEntry { get; internal set; }

    /// <summary>Kurze Begründung für die Vorschau.</summary>
    public string Reason { get; init; } = "";

    public long Size => SourceEntry is { IsDirectory: false } s ? s.Size : TargetEntry is { IsDirectory: false } t ? t.Size : 0;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public OperationStatus Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public string? Message
    {
        get => _message;
        set => Set(ref _message, value);
    }

    public string DirectionSymbol => Kind switch
    {
        OperationKind.Copy or OperationKind.Overwrite => TargetSide == Side.Right ? "→" : "←",
        _ => TargetSide == Side.Right ? "rechts" : "links",
    };

    public string KindText => Kind switch
    {
        OperationKind.CreateFolder => "Neuer Ordner",
        OperationKind.RenameFolder => "Ordner umbenennen",
        OperationKind.Rename => "Umbenennen",
        OperationKind.Delete => "Löschen",
        OperationKind.Copy => "Neu",
        OperationKind.Overwrite => "Überschreiben",
        OperationKind.DeleteFolder => "Ordner löschen",
        _ => Kind.ToString(),
    };

    public string DisplayPath => Kind is OperationKind.Rename or OperationKind.RenameFolder && SourcePath is not null
        ? $"{PathUtil.ToDisplay(SourcePath)} → {PathUtil.ToDisplay(TargetPath)}"
        : PathUtil.ToDisplay(TargetPath);

    /// <summary>Alle relativen Pfade, die von dieser Aktion betroffen sind.</summary>
    public IEnumerable<string> AffectedPaths()
    {
        yield return TargetPath;
        if (SourcePath is not null && !string.Equals(SourcePath, TargetPath, StringComparison.Ordinal))
        {
            yield return SourcePath;
        }
    }

    public override string ToString() => $"{KindText} [{TargetSide}] {DisplayPath}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
