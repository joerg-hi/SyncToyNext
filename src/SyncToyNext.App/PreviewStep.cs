using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SyncToyNext.App;

public enum StepState
{
    Waiting,
    Running,
    Done,
}

/// <summary>
/// Ein Arbeitsschritt der Vorschau, wie er während des Einlesens erklärt wird.
/// </summary>
public sealed class PreviewStep : INotifyPropertyChanged
{
    private StepState _state;
    private string _detail;

    public PreviewStep(string title, string detail)
    {
        Title = title;
        _detail = detail;
    }

    public string Title { get; }

    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    public StepState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value)) OnPropertyChanged(nameof(Glyph));
        }
    }

    public string Glyph => State switch
    {
        StepState.Done => Glyphs.Checkmark,
        StepState.Running => Glyphs.Sync,
        _ => "", // leerer Kreis
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
