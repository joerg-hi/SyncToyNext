using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using SyncToyNext.Core;
using SyncToyNext.Core.Execution;
using SyncToyNext.Core.Logging;
using SyncToyNext.Core.Model;
using SyncToyNext.Core.Planning;

namespace SyncToyNext.App;

/// <summary>
/// Zeigt die Vorschau für ein oder mehrere Ordnerpaare und führt sie auf Wunsch aus.
/// Bei „Ausführen“ aus dem Hauptfenster startet die Ausführung direkt nach der Vorschau.
/// </summary>
public partial class PreviewWindow : Window
{
    private readonly IReadOnlyList<FolderPair> _pairs;
    private readonly bool _autoRun;
    private readonly SyncEngine _engine;
    private readonly SyncLog _log;
    private readonly Action<FolderPair> _onPairRun;
    private readonly ObservableCollection<OperationRow> _rows = new();
    private readonly List<(FolderPair Pair, SyncPlan Plan)> _plans = new();
    private readonly List<string> _warnings = new();
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _hasRun;
    private bool _suppressSummary;

    public PreviewWindow(IReadOnlyList<FolderPair> pairs, bool autoRun, SyncEngine engine, SyncLog log, Action<FolderPair> onPairRun)
    {
        InitializeComponent();
        _pairs = pairs;
        _autoRun = autoRun;
        _engine = engine;
        _log = log;
        _onPairRun = onPairRun;

        OperationsGrid.ItemsSource = _rows;
        PairColumn.Visibility = pairs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        string subject = pairs.Count == 1 ? $"„{pairs[0].Name}“" : $"{pairs.Count} Ordnerpaare";
        Title = $"{(autoRun ? "Ausführen" : "Vorschau")}: {subject}";
        HeaderText.Text = Title;

        Loaded += async (_, _) => await StartAsync();
        Closing += OnClosing;
    }

    // ------------------------------------------------------------------ Vorschau

    private async Task StartAsync()
    {
        SetBusy(true, "Ordner werden eingelesen …");
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        foreach (var pair in _pairs)
        {
            if (token.IsCancellationRequested) break;
            ProgressText.Text = $"Lese „{pair.Name}“ ein …";
            try
            {
                var plan = await Task.Run(() => _engine.Preview(pair, token), token);
                _plans.Add((pair, plan));
                foreach (var op in plan.Operations)
                {
                    op.PropertyChanged += Operation_PropertyChanged;
                    _rows.Add(new OperationRow(pair.Name, op));
                }
                foreach (string w in plan.Warnings) _warnings.Add(Prefix(pair, w));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _warnings.Add(Prefix(pair, $"FEHLER: {ex.Message}"));
            }
        }

        bool cancelled = token.IsCancellationRequested;
        SetBusy(false, cancelled ? "Vorschau abgebrochen." : "");
        UpdateWarnings();
        UpdateSummary();
        if (cancelled) return;

        RunButton.IsEnabled = _plans.Any(p => p.Plan.Operations.Count > 0);

        // Auch ohne Aktionen wird ausgeführt: Dabei werden der Lauf vermerkt und die Statusdateien geschrieben.
        if (_autoRun) await RunAsync();
    }

    private string Prefix(FolderPair pair, string text) => _pairs.Count > 1 ? $"{pair.Name}: {text}" : text;

    private void UpdateSummary()
    {
        var ops = _plans.SelectMany(p => p.Plan.Operations).ToList();
        if (ops.Count == 0)
        {
            SummaryText.Text = _plans.Count == 0
                ? "Es konnte kein Ordnerpaar eingelesen werden."
                : "Keine Änderungen nötig – die Ordner sind auf dem gleichen Stand.";
            return;
        }

        var parts = new List<string>();
        void Add(OperationKind kind, string text)
        {
            int n = ops.Count(o => o.Kind == kind);
            if (n > 0) parts.Add($"{n} {text}");
        }

        Add(OperationKind.Copy, "neu");
        Add(OperationKind.Overwrite, "überschreiben");
        Add(OperationKind.Rename, "umbenennen");
        Add(OperationKind.Delete, "löschen");
        Add(OperationKind.CreateFolder, "Ordner anlegen");
        Add(OperationKind.DeleteFolder, "Ordner löschen");

        int selected = ops.Count(o => o.IsSelected);
        long bytes = _plans.Sum(p => p.Plan.TotalBytesToCopy);
        SummaryText.Text = $"{ops.Count} Aktionen ({string.Join(", ", parts)}). " +
                           $"{selected} ausgewählt, {SyncLog.FormatBytes(bytes)} zu kopieren.";
    }

    private void UpdateWarnings()
    {
        WarningsList.ItemsSource = null;
        WarningsList.ItemsSource = _warnings;
        WarningsExpander.Header = $"Hinweise ({_warnings.Count})";
        WarningsExpander.Visibility = _warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ Ausführen

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync();

    private async Task RunAsync()
    {
        if (_busy || _hasRun) return;

        // Sicherheitsabfrage, wenn eine Seite plötzlich leer ist.
        foreach (var (pair, plan) in _plans)
        {
            if (plan.ConfirmationRequired is null) continue;
            var answer = MessageBox.Show(this,
                $"Ordnerpaar „{pair.Name}“:\n\n{plan.ConfirmationRequired}\n\n" +
                "Ist vielleicht das falsche Laufwerk angeschlossen?\n\n" +
                "„Ja“ führt das Ordnerpaar trotzdem aus, „Nein“ überspringt es.",
                UiHelpers.AppName, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                _suppressSummary = true;
                foreach (var op in plan.Operations) op.IsSelected = false;
                _suppressSummary = false;
                _warnings.Add(Prefix(pair, "Übersprungen (Sicherheitsabfrage abgelehnt)."));
            }
        }

        _hasRun = true;
        RunButton.IsEnabled = false;
        foreach (var row in _rows) row.IsEditable = false;
        SetBusy(true, "Ausführung läuft …");
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var toRun = _plans
            .Where(p => p.Plan.ConfirmationRequired is null || p.Plan.Operations.Any(o => o.IsSelected))
            .ToList();
        long totalBytes = toRun.Sum(p => p.Plan.TotalBytesToCopy);
        int totalOps = toRun.Sum(p => p.Plan.Operations.Count(o => o.IsSelected));
        long bytesBefore = 0;
        int opsBefore = 0;
        int succeeded = 0, failed = 0, skipped = 0;
        long copied = 0;
        bool cancelled = false;

        foreach (var (pair, plan) in toRun)
        {
            if (token.IsCancellationRequested) { cancelled = true; break; }

            long baseBytes = bytesBefore;
            int baseOps = opsBefore;
            var progress = new Progress<SyncProgress>(p =>
            {
                Progress.Value = totalBytes > 0
                    ? Math.Min(1.0, (baseBytes + p.BytesCopied) / (double)totalBytes)
                    : totalOps > 0 ? Math.Min(1.0, (baseOps + p.CompletedOperations) / (double)totalOps) : 1.0;
                ProgressText.Text = p.CurrentItem is null
                    ? ""
                    : $"{(_pairs.Count > 1 ? pair.Name + ": " : "")}{p.CurrentItem}";
            });

            try
            {
                var result = await UiHelpers.RunSta(() => _engine.Run(plan, progress, token));
                _log.Write(result);
                _onPairRun(pair);
                foreach (string w in result.Warnings) _warnings.Add(Prefix(pair, w));
                succeeded += result.Succeeded;
                failed += result.Failed;
                skipped += result.Skipped;
                copied += result.BytesCopied;
                cancelled |= result.WasCancelled;
            }
            catch (Exception ex)
            {
                _warnings.Add(Prefix(pair, $"FEHLER: {ex.Message}"));
                _log.WriteError(pair.Name, ex.Message);
                failed++;
            }

            bytesBefore += plan.TotalBytesToCopy;
            opsBefore += plan.Operations.Count(o => o.IsSelected);
        }

        SetBusy(false, "");
        Progress.Visibility = Visibility.Visible;
        Progress.Value = cancelled ? Progress.Value : 1;
        UpdateWarnings();

        string status = cancelled ? "Abgebrochen" : failed > 0 ? "Fertig mit Fehlern" : "Fertig";
        HeaderText.Text = $"{status}: {(_pairs.Count == 1 ? $"„{_pairs[0].Name}“" : $"{_pairs.Count} Ordnerpaare")}";
        SummaryText.Text = $"{succeeded} erfolgreich, {failed} fehlgeschlagen, {skipped} übersprungen, " +
                           $"{SyncLog.FormatBytes(copied)} kopiert. Details stehen in der Spalte „Ergebnis“ und im Protokoll.";
    }

    private void SetBusy(bool busy, string text)
    {
        _busy = busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.IsEnabled = !busy;
        SelectionButtons.IsEnabled = !busy && !_hasRun;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        Progress.IsIndeterminate = busy && !_hasRun;
        if (busy) Progress.Value = 0;
        ProgressText.Text = text;
    }

    // ------------------------------------------------------------------ Bedienelemente

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAll(true);

    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool selected)
    {
        _suppressSummary = true;
        foreach (var row in _rows) row.Operation.IsSelected = selected;
        _suppressSummary = false;
        UpdateSummary();
    }

    private void Operation_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_suppressSummary && !_hasRun && e.PropertyName == nameof(SyncOperation.IsSelected)) UpdateSummary();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
        ProgressText.Text = "Wird abgebrochen …";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;

        var answer = MessageBox.Show(this, "Die Verarbeitung läuft noch. Soll sie abgebrochen werden?",
            UiHelpers.AppName, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) Cancel_Click(this, new RoutedEventArgs());
        e.Cancel = true;
    }
}
