using System.ComponentModel;
using SyncToyNext.Core.Logging;
using SyncToyNext.Core.Model;
using SyncToyNext.Core.Planning;

namespace SyncToyNext.App;

/// <summary>
/// Zeile in der Vorschau-Tabelle: eine Aktion samt zugehörigem Ordnerpaar.
/// </summary>
public sealed class OperationRow : INotifyPropertyChanged
{
    private bool _isEditable = true;

    public OperationRow(FolderPair pair, SyncOperation operation)
    {
        Pair = pair;
        Operation = operation;
        Operation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SyncOperation.Status) or nameof(SyncOperation.Message))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusGlyph)));
            }
        };
    }

    public FolderPair Pair { get; }

    public string PairName => Pair.Name;

    public SyncOperation Operation { get; }

    public string KindGlyph => Glyphs.ForOperation(Operation.Kind);

    public string TargetText => Operation.Kind is OperationKind.Copy or OperationKind.Overwrite
        ? Operation.TargetSide == Side.Right ? "→ rechts" : "← links"
        : Operation.TargetSide == Side.Right ? "rechts" : "links";

    public string SizeText => Operation.Kind is OperationKind.Copy or OperationKind.Overwrite or OperationKind.Delete
        ? SyncLog.FormatBytes(Operation.Size)
        : "";

    public string StatusGlyph => Glyphs.ForStatus(Operation.Status);

    public string StatusText => Operation.Status switch
    {
        OperationStatus.Pending => "",
        OperationStatus.Succeeded => string.IsNullOrEmpty(Operation.Message) ? "Erledigt" : $"Erledigt – {Operation.Message}",
        OperationStatus.Failed => $"Fehler: {Operation.Message}",
        OperationStatus.Skipped => $"Übersprungen: {Operation.Message}",
        _ => "",
    };

    /// <summary>Nach dem Start der Ausführung kann die Auswahl nicht mehr geändert werden.</summary>
    public bool IsEditable
    {
        get => _isEditable;
        set
        {
            if (_isEditable == value) return;
            _isEditable = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditable)));
        }
    }

    public string FullPath(Side side) =>
        PathUtil.ToFullPath(side == Side.Left ? Pair.LeftPath : Pair.RightPath, Operation.TargetPath);

    public event PropertyChangedEventHandler? PropertyChanged;
}
