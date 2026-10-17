using System.ComponentModel;
using SyncToyNext.Core.Logging;
using SyncToyNext.Core.Planning;

namespace SyncToyNext.App;

/// <summary>
/// Zeile in der Vorschau-Tabelle: eine Aktion samt Name des Ordnerpaars.
/// </summary>
public sealed class OperationRow : INotifyPropertyChanged
{
    private bool _isEditable = true;

    public OperationRow(string pairName, SyncOperation operation)
    {
        PairName = pairName;
        Operation = operation;
        Operation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SyncOperation.Status) or nameof(SyncOperation.Message))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
            }
        };
    }

    public string PairName { get; }

    public SyncOperation Operation { get; }

    public string SizeText => Operation.Kind is OperationKind.Copy or OperationKind.Overwrite or OperationKind.Delete
        ? SyncLog.FormatBytes(Operation.Size)
        : "";

    public string StatusText => Operation.Status switch
    {
        OperationStatus.Pending => "",
        OperationStatus.Succeeded => string.IsNullOrEmpty(Operation.Message) ? "OK" : $"OK – {Operation.Message}",
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

    public event PropertyChangedEventHandler? PropertyChanged;
}
