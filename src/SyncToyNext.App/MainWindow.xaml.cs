using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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
    private readonly PairStore _store = new();
    private readonly SyncLog _log = new();
    private readonly SyncEngine _engine = new();
    private readonly ObservableCollection<FolderPair> _pairs = new();

    public MainWindow()
    {
        InitializeComponent();
        PairList.ItemsSource = _pairs;
        LoadPairs();
    }

    private FolderPair? SelectedPair => PairList.SelectedItem as FolderPair;

    // ------------------------------------------------------------------ Laden / Speichern

    private void LoadPairs()
    {
        try
        {
            foreach (var pair in _store.Load().OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                _pairs.Add(pair);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Die Ordnerpaare konnten nicht geladen werden:\n{_store.FilePath}\n\n{ex.Message}",
                UiHelpers.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        if (_pairs.Count > 0) PairList.SelectedIndex = 0;
        UpdateDetails();
    }

    private void SavePairs()
    {
        try
        {
            _store.Save(_pairs);
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
        bool hasPair = pair is not null;

        EmptyText.Visibility = _pairs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailsPanel.Visibility = hasPair ? Visibility.Visible : Visibility.Collapsed;
        EditButton.IsEnabled = hasPair;
        DeleteButton.IsEnabled = hasPair;
        RunAllButton.IsEnabled = _pairs.Any(p => p.Options.ActiveForRunAll);

        if (pair is null) return;

        PairNameText.Text = pair.Name;
        LeftPathRun.Text = pair.LeftPath;
        RightPathRun.Text = pair.RightPath;
        ActionText.Text = UiHelpers.ActionTitle(pair.Action);
        ActionDescriptionText.Text = UiHelpers.ActionDescription(pair.Action);
        OptionsText.Text = UiHelpers.OptionsSummary(pair.Options);
        LastRunText.Text = pair.LastRunUtc is { } last
            ? last.ToLocalTime().ToString("dddd, dd.MM.yyyy HH:mm")
            : "noch nie";
    }

    private void PairList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateDetails();

    private void PairList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedPair is not null) EditPair_Click(sender, e);
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

    private void NewPair_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new FolderPairWindow(null, _pairs) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;

        _pairs.Add(dialog.Result);
        SavePairs();
        PairList.SelectedItem = dialog.Result;
        UpdateDetails();
    }

    private void EditPair_Click(object sender, RoutedEventArgs e)
    {
        var pair = SelectedPair;
        if (pair is null) return;

        var dialog = new FolderPairWindow(pair, _pairs) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;

        int index = _pairs.IndexOf(pair);
        _pairs[index] = dialog.Result;
        SavePairs();
        PairList.SelectedIndex = index;
        UpdateDetails();
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

        int index = _pairs.IndexOf(pair);
        _pairs.Remove(pair);
        SavePairs();
        if (_pairs.Count > 0) PairList.SelectedIndex = Math.Min(index, _pairs.Count - 1);
        UpdateDetails();
    }

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
        var active = _pairs.Where(p => p.Options.ActiveForRunAll).ToList();
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

    // ------------------------------------------------------------------ Menü

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

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

    private void Scheduler_Click(object sender, RoutedEventArgs e)
    {
        new SchedulerInfoWindow(SelectedPair?.Name) { Owner = this }.ShowDialog();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
        MessageBox.Show(this,
            $"SyncToyNext {version}\n\n" +
            "Ein freier Nachbau von Microsoft SyncToy 2.1.\n" +
            "Synchronize, Echo und Contribute mit Vorschau, Filtern, Umbenennungserkennung und Papierkorb.\n\n" +
            $"Ordnerpaare: {_store.FilePath}\n" +
            $"Protokoll: {_log.FilePath}\n\n" +
            "Lizenz: MIT",
            "Über SyncToyNext", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
