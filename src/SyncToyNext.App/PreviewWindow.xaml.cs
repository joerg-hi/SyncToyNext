using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Shell;
using System.Windows.Threading;
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
    private readonly ObservableCollection<string> _warnings = new();
    private readonly List<(FolderPair Pair, SyncPlan Plan)> _plans = new();
    private readonly ICollectionView _view;
    private readonly DispatcherTimer _searchTimer;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _hasRun;
    private bool _suppressSummary;

    public PreviewWindow(IReadOnlyList<FolderPair> pairs, bool autoRun, SyncEngine engine, SyncLog log, Action<FolderPair> onPairRun)
    {
        InitializeComponent();
        WindowPlacement.Attach(this, "Preview", restorePosition: false);
        _pairs = pairs;
        _autoRun = autoRun;
        _engine = engine;
        _log = log;
        _onPairRun = onPairRun;

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterRow;
        OperationsGrid.ItemsSource = _view;
        WarningsList.ItemsSource = _warnings;
        _warnings.CollectionChanged += (_, _) => UpdateWarnings();

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RefreshFilter();
        };

        PairColumn.Visibility = pairs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Title = $"{(autoRun ? "Ausführen" : "Vorschau")}: {Subject}";
        HeaderText.Text = autoRun ? "Ausführen" : "Vorschau";
        HeaderGlyph.Text = autoRun ? Glyphs.Play : Glyphs.View;
        SubHeaderText.Text = pairs.Count == 1
            ? $"{pairs[0].Name} · {UiHelpers.ActionTitle(pairs[0].Action)}"
            : string.Join(" · ", pairs.Select(p => p.Name));
        SubHeaderText.ToolTip = string.Join(Environment.NewLine,
            pairs.Select(p => $"{p.Name}: {p.LeftPath}  ⇄  {p.RightPath}"));

        Loaded += async (_, _) => await StartAsync();
        Closing += OnClosing;
        Closed += (_, _) => SetTaskbar(TaskbarItemProgressState.None, 0);
    }

    private string Subject => _pairs.Count == 1 ? $"„{_pairs[0].Name}“" : $"{_pairs.Count} Ordnerpaare";

    // ------------------------------------------------------------------ Vorschau

    private async Task StartAsync()
    {
        SetBusy(true, "Ordner werden eingelesen …");
        SetTaskbar(TaskbarItemProgressState.Indeterminate, 0);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        foreach (var pair in _pairs)
        {
            if (token.IsCancellationRequested) break;
            ProgressText.Text = $"Lese „{pair.Name}“ ein …";
            try
            {
                string name = pair.Name;
                var progress = new Progress<int>(n => ProgressText.Text = $"Lese „{name}“ ein … {n:N0} Einträge");
                var plan = await Task.Run(() => _engine.Preview(pair, token, progress), token);
                _plans.Add((pair, plan));
                foreach (var op in plan.Operations)
                {
                    op.PropertyChanged += Operation_PropertyChanged;
                    _rows.Add(new OperationRow(pair, op));
                }

                foreach (string w in plan.Warnings) _warnings.Add(Prefix(pair, w));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _warnings.Add(Prefix(pair, $"Fehler: {ex.Message}"));
            }
        }

        bool cancelled = token.IsCancellationRequested;
        SetBusy(false, cancelled ? "Vorschau abgebrochen." : "");
        SetTaskbar(TaskbarItemProgressState.None, 0);
        UpdateSummary();
        UpdateEmptyState();
        if (cancelled) return;

        RunButton.IsEnabled = _plans.Any(p => p.Plan.Operations.Count > 0);
        if (!_autoRun && RunButton.IsEnabled) RunButton.Focus();

        // Auch ohne Aktionen wird ausgeführt: Dabei werden der Lauf vermerkt und die Statusdateien geschrieben.
        if (_autoRun) await RunAsync();
    }

    private string Prefix(FolderPair pair, string text) => _pairs.Count > 1 ? $"{pair.Name}: {text}" : text;

    private void UpdateSummary()
    {
        var ops = _plans.SelectMany(p => p.Plan.Operations).ToList();
        var chips = new List<SummaryChip>();

        void Add(string glyph, string text, params OperationKind[] kinds)
        {
            int n = ops.Count(o => kinds.Contains(o.Kind));
            if (n > 0) chips.Add(new SummaryChip(glyph, $"{n} {text}"));
        }

        Add(Glyphs.Page, "neu", OperationKind.Copy);
        Add(Glyphs.Refresh, "überschreiben", OperationKind.Overwrite);
        Add(Glyphs.Rename, "umbenennen", OperationKind.Rename, OperationKind.RenameFolder);
        Add(Glyphs.Delete, "löschen", OperationKind.Delete);
        Add(Glyphs.NewFolder, "Ordner anlegen", OperationKind.CreateFolder);
        Add(Glyphs.Delete, "Ordner löschen", OperationKind.DeleteFolder);

        if (ops.Count > 0)
        {
            int selected = ops.Count(o => o.IsSelected);
            long bytes = _plans.Sum(p => p.Plan.TotalBytesToCopy);
            chips.Add(new SummaryChip(Glyphs.Checkmark, $"{selected} von {ops.Count} ausgewählt"));
            chips.Add(new SummaryChip(Glyphs.Copy, $"{SyncLog.FormatBytes(bytes)} zu kopieren"));
        }

        SummaryChips.ItemsSource = chips;
    }

    private void UpdateWarnings()
    {
        WarningsHeader.Text = _warnings.Count == 1 ? "1 Hinweis" : $"{_warnings.Count} Hinweise";
        WarningsBar.Visibility = _warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateEmptyState()
    {
        bool anyRows = _rows.Count > 0;
        bool anyVisible = !_view.IsEmpty;
        EmptyGridPanel.Visibility = anyVisible || _busy ? Visibility.Collapsed : Visibility.Visible;

        if (!anyRows && _plans.Count > 0)
        {
            EmptyGridGlyph.Text = Glyphs.Checkmark;
            EmptyGridGlyph.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush");
            EmptyGridText.Text = "Alles auf dem gleichen Stand – nichts zu tun.";
        }
        else if (!anyRows)
        {
            EmptyGridGlyph.Text = Glyphs.Error;
            EmptyGridGlyph.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
            EmptyGridText.Text = "Es konnte kein Ordnerpaar eingelesen werden.";
        }
        else
        {
            EmptyGridGlyph.Text = Glyphs.Filter;
            EmptyGridGlyph.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            EmptyGridText.Text = "Keine Einträge für diesen Filter.";
        }
    }

    // ------------------------------------------------------------------ Suche und Filter

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void FilterBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded) RefreshFilter();
    }

    private void RefreshFilter()
    {
        _view.Refresh();
        UpdateEmptyState();
    }

    private bool FilterRow(object item)
    {
        if (item is not OperationRow row) return false;
        var op = row.Operation;

        bool kindMatches = FilterBox.SelectedIndex switch
        {
            1 => op.Kind is OperationKind.Copy or OperationKind.Overwrite,
            2 => op.Kind is OperationKind.Rename or OperationKind.RenameFolder,
            3 => op.Kind is OperationKind.Delete,
            4 => op.Kind is OperationKind.CreateFolder or OperationKind.DeleteFolder,
            5 => op.Status == OperationStatus.Failed,
            6 => op.Status == OperationStatus.Skipped,
            _ => true,
        };
        if (!kindMatches) return false;

        string search = SearchBox.Text.Trim();
        return search.Length == 0
               || op.DisplayPath.Contains(search, StringComparison.CurrentCultureIgnoreCase)
               || op.Reason.Contains(search, StringComparison.CurrentCultureIgnoreCase);
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
        HeaderText.Text = "Wird ausgeführt …";
        HeaderGlyph.Text = Glyphs.Play;
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
        SetTaskbar(TaskbarItemProgressState.Normal, 0);

        foreach (var (pair, plan) in toRun)
        {
            if (token.IsCancellationRequested) { cancelled = true; break; }

            long baseBytes = bytesBefore;
            int baseOps = opsBefore;
            var progress = new Progress<SyncProgress>(p =>
            {
                double value = totalBytes > 0
                    ? Math.Min(1.0, (baseBytes + p.BytesCopied) / (double)totalBytes)
                    : totalOps > 0 ? Math.Min(1.0, (baseOps + p.CompletedOperations) / (double)totalOps) : 1.0;
                Progress.Value = value;
                SetTaskbar(TaskbarItemProgressState.Normal, value);
                ProgressText.Text = p.CurrentItem is null
                    ? ""
                    : $"{baseOps + p.CompletedOperations + 1} / {totalOps} · {(_pairs.Count > 1 ? pair.Name + ": " : "")}{p.CurrentItem}";
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
                _warnings.Add(Prefix(pair, $"Fehler: {ex.Message}"));
                _log.WriteError(pair.Name, ex.Message);
                failed++;
            }

            bytesBefore += plan.TotalBytesToCopy;
            opsBefore += plan.Operations.Count(o => o.IsSelected);
        }

        SetBusy(false, "");
        Progress.Visibility = Visibility.Visible;
        if (!cancelled) Progress.Value = 1;
        ShowResult(succeeded, failed, skipped, copied, cancelled);
        UpdateEmptyState();
    }

    private void ShowResult(int succeeded, int failed, int skipped, long copied, bool cancelled)
    {
        string details = $"{succeeded} erledigt, {failed} fehlgeschlagen, {skipped} übersprungen, " +
                         $"{SyncLog.FormatBytes(copied)} kopiert.";

        if (cancelled)
        {
            HeaderText.Text = "Abgebrochen";
            ResultBar.Style = (Style)FindResource("InfoBarWarning");
            ResultGlyph.Text = Glyphs.Warning;
            ResultText.Text = $"Die Ausführung wurde abgebrochen. {details} Nicht ausgeführte Aktionen werden beim nächsten Mal erneut angeboten.";
            SetTaskbar(TaskbarItemProgressState.Paused, Progress.Value);
        }
        else if (failed > 0)
        {
            HeaderText.Text = "Fertig mit Fehlern";
            ResultBar.Style = (Style)FindResource("InfoBarError");
            ResultGlyph.Text = Glyphs.Error;
            ResultText.Text = $"{details} Mit dem Filter „Nur Fehler“ sehen Sie die betroffenen Dateien. Sie werden beim nächsten Lauf erneut versucht.";
            SetTaskbar(TaskbarItemProgressState.Error, 1);
        }
        else
        {
            HeaderText.Text = "Fertig";
            ResultBar.Style = (Style)FindResource("InfoBarSuccess");
            ResultGlyph.Text = Glyphs.Checkmark;
            ResultText.Text = details;
            SetTaskbar(TaskbarItemProgressState.None, 0);
        }

        HeaderGlyph.Text = ResultGlyph.Text;
        ResultBar.Visibility = Visibility.Visible;
        CloseButton.Focus();
    }

    private void SetBusy(bool busy, string text)
    {
        _busy = busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = true;
        CloseButton.IsEnabled = !busy;
        SelectionButtons.IsEnabled = !busy && !_hasRun;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        Progress.IsIndeterminate = busy && !_hasRun;
        if (busy) Progress.Value = 0;
        ProgressText.Text = text;
    }

    private void SetTaskbar(TaskbarItemProgressState state, double value)
    {
        if (Owner?.TaskbarItemInfo is not { } info) return;
        info.ProgressState = state;
        info.ProgressValue = value;
    }

    // ------------------------------------------------------------------ Auswahl

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetVisible(true);

    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetVisible(false);

    /// <summary>Wirkt auf die aktuell angezeigten (gefilterten) Zeilen.</summary>
    private void SetVisible(bool selected)
    {
        _suppressSummary = true;
        foreach (var row in _view.Cast<OperationRow>()) row.Operation.IsSelected = selected;
        _suppressSummary = false;
        UpdateSummary();
    }

    private void Operation_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_suppressSummary && !_hasRun && e.PropertyName == nameof(SyncOperation.IsSelected)) UpdateSummary();
    }

    // ------------------------------------------------------------------ Kontextmenü der Tabelle

    private OperationRow? SelectedRow => OperationsGrid.SelectedItem as OperationRow;

    private void GridContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        bool has = SelectedRow is not null;
        ShowLeftMenuItem.IsEnabled = has;
        ShowRightMenuItem.IsEnabled = has;
        CopyPathMenuItem.IsEnabled = has;
    }

    private void ShowLeft_Click(object sender, RoutedEventArgs e) => ShowInExplorer(Side.Left);

    private void ShowRight_Click(object sender, RoutedEventArgs e) => ShowInExplorer(Side.Right);

    private void OperationsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SelectedRow is { } row) ShowInExplorer(row.Operation.TargetSide);
    }

    /// <summary>Zeigt die Datei im Explorer an; existiert sie nicht (mehr), den nächsten vorhandenen Ordner.</summary>
    private async void ShowInExplorer(Side side)
    {
        if (SelectedRow is not { } row) return;
        string path = row.FullPath(side);

        // Im Hintergrund suchen, damit ein langsames Netzlaufwerk das Fenster nicht einfriert.
        var (exists, folder) = await Task.Run(() =>
        {
            if (File.Exists(path) || Directory.Exists(path)) return (true, (string?)null);
            string? parent = Path.GetDirectoryName(path);
            while (parent is not null && !Directory.Exists(parent)) parent = Path.GetDirectoryName(parent);
            return (false, parent);
        });

        if (exists)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        else if (folder is not null)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row) Clipboard.SetText(row.FullPath(row.Operation.TargetSide));
    }

    // ------------------------------------------------------------------ Schließen / Abbrechen

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

    private sealed record SummaryChip(string Glyph, string Text);
}
