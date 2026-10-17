using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using SyncToyNext.Core;
using SyncToyNext.Core.Config;
using SyncToyNext.Core.Logging;
using SyncToyNext.Core.Model;
using SyncToyNext.Core.Snapshots;

namespace SyncToyNext.App;

public partial class MainWindow : Window
{
    private readonly PairStore _store;
    private readonly SyncLog _log = new();
    private readonly SyncEngine _engine = new();
    private readonly ObservableCollection<PairItem> _items = new();

    public MainWindow()
        : this(new PairStore())
    {
    }

    internal MainWindow(PairStore store)
    {
        _store = store;
        InitializeComponent();
        WindowPlacement.Attach(this, "Main", restorePosition: true);
        PairList.ItemsSource = _items;
        LoadPairs();
    }

    private FolderPair? SelectedPair => (PairList.SelectedItem as PairItem)?.Pair;

    private IEnumerable<FolderPair> AllPairs => _items.Select(i => i.Pair);

    // ------------------------------------------------------------------ Laden / Speichern

    private void LoadPairs()
    {
        try
        {
            foreach (var pair in _store.Load().OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                _items.Add(new PairItem(pair));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Die Ordnerpaare konnten nicht geladen werden:\n{_store.FilePath}\n\n{ex.Message}",
                UiHelpers.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        if (_items.Count > 0) PairList.SelectedIndex = 0;
        UpdateDetails();
    }

    private void SavePairs()
    {
        try
        {
            _store.Save(AllPairs);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Die Ordnerpaare konnten nicht gespeichert werden:\n\n{ex.Message}",
                UiHelpers.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------ Anzeige

    private void UpdateDetails()
    {
        var pair = SelectedPair;
        int active = AllPairs.Count(p => p.Options.ActiveForRunAll);

        EmptyPanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailsPanel.Visibility = pair is not null ? Visibility.Visible : Visibility.Collapsed;
        RunAllButton.IsEnabled = active > 0;
        PairCountText.Text = _items.Count.ToString();
        StatusText.Text = _items.Count switch
        {
            0 => "Noch keine Ordnerpaare",
            1 => $"1 Ordnerpaar · {active} aktiv für „Alle ausführen“",
            _ => $"{_items.Count} Ordnerpaare · {active} aktiv für „Alle ausführen“",
        };

        if (pair is null) return;

        PairNameText.Text = pair.Name;
        LeftNameText.Text = UiHelpers.FolderName(pair.LeftPath);
        LeftPathText.Text = pair.LeftPath;
        RightNameText.Text = UiHelpers.FolderName(pair.RightPath);
        RightPathText.Text = pair.RightPath;
        ActionGlyphText.Text = pair.Action == SyncAction.Synchronize ? "" : Glyphs.Forward;
        ActionText.Text = UiHelpers.ActionTitle(pair.Action);
        ActionDescriptionText.Text = UiHelpers.ActionDescription(pair.Action);
        OptionChips.ItemsSource = UiHelpers.OptionChips(pair.Options);
        LastRunText.Text = pair.LastRunUtc is { } last
            ? $"Zuletzt ausgeführt: {last.ToLocalTime():dddd, dd.MM.yyyy 'um' HH:mm} Uhr"
            : "Noch nie ausgeführt";

        LeftMissingPanel.Visibility = Visibility.Collapsed;
        RightMissingPanel.Visibility = Visibility.Collapsed;
        _ = CheckFoldersAsync(pair);
    }

    /// <summary>
    /// Prüft im Hintergrund, ob beide Ordner erreichbar sind (z. B. USB-Laufwerk angeschlossen).
    /// Bei Netzlaufwerken kann das einige Sekunden dauern, daher nicht im UI-Thread.
    /// </summary>
    private async Task CheckFoldersAsync(FolderPair pair)
    {
        var (left, right) = await Task.Run(() => (Directory.Exists(pair.LeftPath), Directory.Exists(pair.RightPath)));
        if (SelectedPair != pair) return;
        LeftMissingPanel.Visibility = left ? Visibility.Collapsed : Visibility.Visible;
        RightMissingPanel.Visibility = right ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PairList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateDetails();

    private void PairList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedPair is not null) EditPair_Click(sender, e);
    }

    private void PairContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        foreach (var item in PairContextMenu.Items.OfType<System.Windows.Controls.MenuItem>())
        {
            item.IsEnabled = SelectedPair is not null;
        }
    }

    private void OpenLeft_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPair is { } pair) UiHelpers.OpenInExplorer(pair.LeftPath);
    }

    private void OpenRight_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPair is { } pair) UiHelpers.OpenInExplorer(pair.RightPath);
    }

    // ------------------------------------------------------------------ Ordnerpaare verwalten

    private void NewPair_Click(object sender, RoutedEventArgs e) => CreatePair(null);

    private void CreatePair(FolderPair? template)
    {
        var dialog = new FolderPairWindow(null, AllPairs, template) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;

        var item = new PairItem(dialog.Result);
        _items.Add(item);
        SavePairs();
        PairList.SelectedItem = item;
        PairList.ScrollIntoView(item);
        UpdateDetails();
    }

    private void EditPair_Click(object sender, RoutedEventArgs e)
    {
        var pair = SelectedPair;
        if (pair is null) return;

        var dialog = new FolderPairWindow(pair, AllPairs) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;

        int index = PairList.SelectedIndex;
        _items[index] = new PairItem(dialog.Result);
        SavePairs();
        PairList.SelectedIndex = index;
        UpdateDetails();
    }

    private void DuplicatePair_Click(object sender, RoutedEventArgs e)
    {
        var pair = SelectedPair;
        if (pair is null) return;

        var copy = pair.Clone();
        copy.Name = UniqueName($"{pair.Name} (Kopie)");
        CreatePair(copy);
    }

    private string UniqueName(string name)
    {
        string candidate = name;
        for (int i = 2; AllPairs.Any(p => string.Equals(p.Name, candidate, StringComparison.CurrentCultureIgnoreCase)); i++)
        {
            candidate = $"{name} {i}";
        }

        return candidate;
    }

    private void DeletePair_Click(object sender, RoutedEventArgs e)
    {
        var pair = SelectedPair;
        if (pair is null) return;

        var answer = MessageBox.Show(this,
            $"Soll das Ordnerpaar „{pair.Name}“ gelöscht werden?\n\n" +
            "Die Ordner und Dateien selbst bleiben unverändert. Nur die Statusdateien " +
            $"({SnapshotStore.GetFileName(pair.Id)}) in beiden Ordnern werden entfernt.",
            UiHelpers.AppName, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        foreach (string root in new[] { pair.LeftPath, pair.RightPath })
        {
            try { SnapshotStore.Delete(root, pair.Id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Ordner ggf. nicht erreichbar */ }
        }

        int index = PairList.SelectedIndex;
        _items.RemoveAt(index);
        SavePairs();
        if (_items.Count > 0) PairList.SelectedIndex = Math.Min(index, _items.Count - 1);
        UpdateDetails();
    }

    // ------------------------------------------------------------------ Drag & Drop

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFolders(e).Count > 0 ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        var folders = DroppedFolders(e);
        if (folders.Count == 0) return;

        var template = new FolderPair
        {
            LeftPath = folders[0],
            RightPath = folders.Count > 1 ? folders[1] : "",
        };
        CreatePair(template);
    }

    private static List<string> DroppedFolders(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] paths
            ? paths.Where(Directory.Exists).Take(2).ToList()
            : new List<string>();

    // ------------------------------------------------------------------ Vorschau / Ausführen

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPair is { } pair) ShowRunWindow(new[] { pair }, autoRun: false);
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPair is { } pair) ShowRunWindow(new[] { pair }, autoRun: true);
    }

    private void RunAll_Click(object sender, RoutedEventArgs e)
    {
        var active = AllPairs.Where(p => p.Options.ActiveForRunAll).ToList();
        if (active.Count == 0) return;
        ShowRunWindow(active, autoRun: true);
    }

    private void ShowRunWindow(IReadOnlyList<FolderPair> pairs, bool autoRun)
    {
        var window = new PreviewWindow(pairs, autoRun, _engine, _log, OnPairRun) { Owner = this };
        window.ShowDialog();
        UpdateDetails();
    }

    private void OnPairRun(FolderPair pair)
    {
        pair.LastRunUtc = DateTime.UtcNow;
        SavePairs();
    }

    // ------------------------------------------------------------------ Tastatur

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        switch (key)
        {
            case Key.N when ctrl: NewPair_Click(sender, e); break;
            case Key.F2:
            case Key.E when ctrl: EditPair_Click(sender, e); break;
            case Key.Delete: DeletePair_Click(sender, e); break;
            case Key.P when ctrl: Preview_Click(sender, e); break;
            case Key.F5 when ctrl: RunAll_Click(sender, e); break;
            case Key.F5: Run_Click(sender, e); break;
            case Key.L when ctrl: OpenLog_Click(sender, e); break;
            case Key.F1: About_Click(sender, e); break;
            default: return;
        }

        e.Handled = true;
    }

    // ------------------------------------------------------------------ Menü „Weitere Funktionen“

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = MoreButton.ContextMenu!;
        menu.PlacementTarget = MoreButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_log.FilePath))
        {
            MessageBox.Show(this, "Es wurde noch kein Protokoll geschrieben.", UiHelpers.AppName,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo(_log.FilePath) { UseShellExecute = true });
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(PairStore.DefaultDataDirectory);
        UiHelpers.OpenInExplorer(PairStore.DefaultDataDirectory);
    }

    private void Scheduler_Click(object sender, RoutedEventArgs e)
    {
        new SchedulerInfoWindow(SelectedPair?.Name) { Owner = this }.ShowDialog();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow(_store.FilePath, _log.FilePath) { Owner = this }.ShowDialog();
    }
}
