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
    private List<OperationRow> _rows = new();
    private readonly ObservableCollection<string> _warnings = new();
    private readonly List<(FolderPair Pair, SyncPlan Plan)> _plans = new();
    private ICollectionView _view;
    private readonly DispatcherTimer _searchTimer;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _hasRun;
    private bool _suppressSummary;

    /// <summary>Ab dieser Größe wird für die laufende Datei ein eigener Fortschrittsbalken gezeigt.</summary>
    private const long LargeFileBytes = 8L * 1024 * 1024;

    public PreviewWindow(IReadOnlyList<FolderPair> pairs, bool autoRun, SyncEngine engine, SyncLog log, Action<FolderPair> onPairRun)
    {
        InitializeComponent();
        WindowPlacement.Attach(this, "Preview", restorePosition: false);
        _pairs = pairs;
        _autoRun = autoRun;
        _engine = engine;
        _log = log;
        _onPairRun = onPairRun;

        _view = CreateView(_rows);
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
        PairFoldersList.ItemsSource = pairs.Select(p => new PairFolders(
            p.Name,
            p.LeftPath,
            UiHelpers.FolderName(p.LeftPath),
            p.RightPath,
            UiHelpers.FolderName(p.RightPath),
            p.Action == SyncAction.Synchronize ? "\uE8AB" : Glyphs.Forward,
            UiHelpers.ActionTitle(p.Action),
            pairs.Count > 1 ? Visibility.Visible : Visibility.Collapsed)).ToList();

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
        SetWorking(true);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var rows = new List<OperationRow>();
        for (int index = 0; index < _pairs.Count; index++)
        {
            var pair = _pairs[index];
            if (token.IsCancellationRequested) break;
            ProgressText.Text = $"Lese „{pair.Name}“ ein …";

            // Schritte für dieses Ordnerpaar erklären
            var steps = CreateSteps(pair);
            StepsList.ItemsSource = steps.All;
            WorkingTitle.Text = _pairs.Count == 1
                ? "Was passiert gerade?"
                : $"Was passiert gerade? (Ordnerpaar {index + 1} von {_pairs.Count}: {pair.Name})";
            steps.Left.State = StepState.Running;
            steps.Right.State = StepState.Running;

            try
            {
                string name = pair.Name;
                var progress = new Progress<PreviewProgress>(p =>
                {
                    if (token.IsCancellationRequested) return; // späte Meldungen nach dem Abbruch ignorieren
                    UpdateSteps(steps, pair, p);
                    ProgressText.Text = p.Stage switch
                    {
                        PreviewStage.Scanning => $"Lese „{name}“ ein … {p.Count:N0} Einträge",
                        PreviewStage.Comparing => $"Vergleiche „{name}“ ({p.Count:N0} Einträge) …",
                        PreviewStage.CheckingContents => $"Prüfe Dateiinhalte von „{name}“ … {p.Count:N0} Dateien verglichen",
                        _ => ProgressText.Text,
                    };
                });
                // WaitAsync: Bei „Abbrechen“ sofort weiter, auch wenn ein Netzlaufwerk gerade noch antwortet.
                // Das Einlesen im Hintergrund beendet sich dann von selbst beim nächsten Prüfpunkt.
                var plan = await Task.Run(() => _engine.Preview(pair, token, progress), token).WaitAsync(token);
                _plans.Add((pair, plan));
                steps.Compare.State = StepState.Done;
                steps.Compare.Detail = plan.Operations.Count == 0
                    ? "Keine Unterschiede gefunden."
                    : $"{plan.Operations.Count:N0} Aktionen nötig.";
                if (steps.Contents is not null) steps.Contents.State = StepState.Done;

                // Zeilen im Hintergrund vorbereiten; die Tabelle wird am Ende in einem Schritt befüllt
                // (zeilenweises Einfügen ist bei sehr vielen Aktionen extrem langsam).
                steps.List.State = StepState.Running;
                if (plan.Operations.Count > 0)
                {
                    ProgressText.Text = $"Bereite {plan.Operations.Count:N0} Aktionen für die Anzeige vor …";
                    rows.AddRange(await Task.Run(() => plan.Operations.Select(op =>
                    {
                        op.PropertyChanged += Operation_PropertyChanged;
                        return new OperationRow(pair, op);
                    }).ToList(), token).WaitAsync(token));
                }

                steps.List.State = StepState.Done;

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

        if (rows.Count > 0)
        {
            ProgressText.Text = $"Zeige {rows.Count:N0} Aktionen an …";
            await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            _rows = rows;
            _view = CreateView(_rows);
        }

        SetWorking(false);

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

    private sealed record Steps(PreviewStep Left, PreviewStep Right, PreviewStep Compare, PreviewStep? Contents, PreviewStep List)
    {
        public List<PreviewStep> All => new[] { Left, Right, Compare, Contents, List }.OfType<PreviewStep>().ToList();
    }

    private static Steps CreateSteps(FolderPair pair)
    {
        var contents = pair.Options.CheckFileContents
            ? new PreviewStep("Dateiinhalte prüfen",
                "Dateien, die auf beiden Seiten gleich groß sind, werden Byte für Byte verglichen (Option „Dateiinhalte prüfen“). Über das Netzwerk dauert das lange.")
            : null;
        return new Steps(
            new PreviewStep($"Linken Ordner einlesen: {UiHelpers.FolderName(pair.LeftPath)}", pair.LeftPath),
            new PreviewStep($"Rechten Ordner einlesen: {UiHelpers.FolderName(pair.RightPath)}", pair.RightPath),
            new PreviewStep("Mit dem Stand des letzten Laufs vergleichen",
                "Welche Dateien und Ordner sind neu, geändert, umbenannt oder gelöscht? Grundlage sind die Statusdateien SyncToyNext_….dat in beiden Ordnern."),
            contents,
            new PreviewStep("Liste der Aktionen aufbauen", "Die gefundenen Aktionen erscheinen danach in der Tabelle."));
    }

    private void UpdateSteps(Steps steps, FolderPair pair, PreviewProgress p)
    {
        steps.Left.Detail = $"{p.LeftCount:N0} Einträge{(p.LeftDone ? " – fertig" : " bisher")} · {pair.LeftPath}";
        steps.Right.Detail = $"{p.RightCount:N0} Einträge{(p.RightDone ? " – fertig" : " bisher")} · {pair.RightPath}";
        steps.Left.State = p.LeftDone ? StepState.Done : StepState.Running;
        steps.Right.State = p.RightDone ? StepState.Done : StepState.Running;

        if (p.Stage is PreviewStage.Comparing or PreviewStage.CheckingContents)
        {
            steps.Compare.State = StepState.Running;
        }

        if (p.Stage == PreviewStage.CheckingContents && steps.Contents is not null)
        {
            steps.Contents.State = StepState.Running;
            steps.Contents.Detail = $"{p.Count:N0} Dateien verglichen …";
        }

    }

    /// <summary>
    /// Für den Starttest: zeigt die Erklärung mit einem typischen Zwischenstand, damit sie auf einem
    /// Bildschirmfoto geprüft werden kann (die echte Vorschau ist im Test zu schnell fertig).
    /// </summary>
    internal void ShowWorkingStateForScreenshot(FolderPair pair)
    {
        var steps = CreateSteps(pair);
        StepsList.ItemsSource = steps.All;
        UpdateSteps(steps, pair, new PreviewProgress(PreviewStage.Scanning, 274852, 120345, 154507, LeftDone: true));
        WorkingTitle.Text = "Was passiert gerade?";
        SetWorking(true);
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        ProgressText.Text = $"Lese „{pair.Name}“ ein … 274.852 Einträge";
    }

    /// <summary>
    /// Für den Starttest: zeigt die Fortschrittsanzeige einer laufenden Übertragung mit typischen Werten,
    /// damit das Layout auf einem Bildschirmfoto geprüft werden kann.
    /// </summary>
    internal void ShowTransferStateForScreenshot()
    {
        // Nur das Aussehen, ohne _busy zu setzen (sonst fragt das Schließen nach).
        CancelButton.Visibility = Visibility.Visible;
        CloseButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = false;
        Progress.Value = 0.35;
        SetTransferLayout(true);
        RunButton.IsEnabled = false;
        TransferText.Text = "Aktion 255 von 7.339 · 1,21 GB von 3,45 GB (35 %)";
        TransferTimeText.Text = "24,6 MB/s · noch etwa 2 Min. · 0:51 vergangen";
        CurrentFileText.Text = @"Thunderbird\Mail\pop.gmx.net\Inbox";
        CurrentFileAmountText.Text = "820 MB von 1,75 GB (46 %)";
        CurrentFileProgress.Value = 0.46;
        CurrentFileProgress.Visibility = Visibility.Visible;
        if (_rows.Count > 0) _rows[^1].RunningText = "Wird kopiert … 46 %";
    }

    /// <summary>Zeigt statt der Tabelle die Erklärung der Arbeitsschritte (oder umgekehrt).</summary>
    private void SetWorking(bool working)
    {
        WorkingPanel.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        OperationsGrid.Visibility = working ? Visibility.Hidden : Visibility.Visible;
    }

    private ICollectionView CreateView(List<OperationRow> rows)
    {
        var view = new ListCollectionView(rows) { Filter = FilterRow };
        OperationsGrid.ItemsSource = view;
        return view;
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

        var rowByOperation = new Dictionary<SyncOperation, OperationRow>(ReferenceEqualityComparer.Instance);
        foreach (var row in _rows) rowByOperation[row.Operation] = row;
        var clock = Stopwatch.StartNew();
        var estimator = new TransferEstimator();
        OperationRow? activeRow = null;
        long lastFollow = 0;
        SetTransferLayout(true);

        void ShowTransfer(FolderPair pair, SyncProgress p, long baseBytes, int baseOps)
        {
            if (!_busy) return; // verspätete Meldung nach dem Ende

            long doneBytes = baseBytes + p.BytesCopied;
            int doneOps = baseOps + p.CompletedOperations;
            double value = totalBytes > 0
                ? Math.Min(1.0, doneBytes / (double)totalBytes)
                : totalOps > 0 ? Math.Min(1.0, doneOps / (double)totalOps) : 1.0;
            Progress.Value = value;
            SetTaskbar(TaskbarItemProgressState.Normal, value);

            estimator.Add(clock.Elapsed, doneBytes, value);
            int current = Math.Min(totalOps, doneOps + (p.CurrentOperation is null ? 0 : 1));
            TransferText.Text = totalBytes > 0
                ? $"Aktion {current:N0} von {totalOps:N0} · {SyncLog.FormatBytes(doneBytes)} von {SyncLog.FormatBytes(totalBytes)} ({value:P0})"
                : $"Aktion {current:N0} von {totalOps:N0} ({value:P0})";

            var times = new List<string>();
            if (totalBytes > 0 && estimator.BytesPerSecond is { } speed) times.Add($"{SyncLog.FormatBytes((long)speed)}/s");
            if (estimator.Remaining is { } remaining) times.Add($"noch {TransferEstimator.FormatRemaining(remaining)}");
            times.Add($"{TransferEstimator.FormatDuration(clock.Elapsed)} vergangen");
            TransferTimeText.Text = string.Join(" · ", times);

            var op = p.CurrentOperation;
            var row = op is not null && rowByOperation.TryGetValue(op, out var r) ? r : null;
            if (!ReferenceEquals(row, activeRow))
            {
                if (activeRow is not null) activeRow.RunningText = null;
                activeRow = row;
                long now = Environment.TickCount64;
                if (row is not null && FollowCheckBox.IsChecked == true && now - lastFollow >= 250 && _view.Contains(row))
                {
                    lastFollow = now;
                    OperationsGrid.ScrollIntoView(row);
                }
            }

            if (op is null)
            {
                CurrentFileText.Text = "";
                CurrentFileAmountText.Text = "";
                CurrentFileProgress.Visibility = Visibility.Hidden;
                return;
            }

            CurrentFileText.Text = (_pairs.Count > 1 ? pair.Name + ": " : "") + op.DisplayPath;
            bool isCopy = op.Kind is OperationKind.Copy or OperationKind.Overwrite;
            bool large = isCopy && op.Size >= LargeFileBytes;
            if (large)
            {
                double fileValue = Math.Min(1.0, p.CurrentBytes / (double)op.Size);
                CurrentFileProgress.Value = fileValue;
                CurrentFileAmountText.Text = $"{SyncLog.FormatBytes(p.CurrentBytes)} von {SyncLog.FormatBytes(op.Size)} ({fileValue:P0})";
                if (row is not null) row.RunningText = $"Wird kopiert … {fileValue:P0}";
            }
            else
            {
                CurrentFileAmountText.Text = isCopy ? SyncLog.FormatBytes(op.Size) : op.KindText;
                if (row is not null) row.RunningText = isCopy ? "Wird kopiert …" : "Läuft …";
            }

            CurrentFileProgress.Visibility = large ? Visibility.Visible : Visibility.Hidden;
        }

        foreach (var (pair, plan) in toRun)
        {
            if (token.IsCancellationRequested) { cancelled = true; break; }

            long baseBytes = bytesBefore;
            int baseOps = opsBefore;
            var progress = new Progress<SyncProgress>(p => ShowTransfer(pair, p, baseBytes, baseOps));

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

        if (activeRow is not null) activeRow.RunningText = null;
        SetTransferLayout(false);
        SetBusy(false, "");
        Progress.Visibility = Visibility.Visible;
        if (!cancelled) Progress.Value = 1;
        ShowResult(succeeded, failed, skipped, copied, cancelled, clock.Elapsed);
        UpdateEmptyState();
    }

    private void ShowResult(int succeeded, int failed, int skipped, long copied, bool cancelled, TimeSpan duration)
    {
        string details = $"{succeeded} erledigt, {failed} fehlgeschlagen, {skipped} übersprungen, " +
                         $"{SyncLog.FormatBytes(copied)} kopiert in {TransferEstimator.FormatDuration(duration)}.";

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

    /// <summary>
    /// Während der Ausführung steht der Fortschritt über die volle Breite oberhalb der Schaltflächen,
    /// damit Menge, Tempo, Restzeit und Dateiname auch bei schmalem Fenster lesbar bleiben.
    /// </summary>
    private void SetTransferLayout(bool transfer)
    {
        System.Windows.Controls.Grid.SetRow(ProgressArea, transfer ? 0 : 1);
        System.Windows.Controls.Grid.SetColumnSpan(ProgressArea, transfer ? 2 : 1);
        ProgressArea.Margin = transfer ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 20, 0);
        var visibility = transfer ? Visibility.Visible : Visibility.Collapsed;
        TransferHeader.Visibility = visibility;
        TransferFile.Visibility = visibility;
        FollowCheckBox.Visibility = visibility;
        ProgressText.Visibility = transfer ? Visibility.Collapsed : Visibility.Visible;
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

    private sealed record PairFolders(
        string Name, string Left, string LeftName, string Right, string RightName,
        string ArrowGlyph, string ActionTitle, Visibility NameVisibility);
}
