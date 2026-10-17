using System.IO;
using System.Windows;
using System.Windows.Threading;
using SyncToyNext.Core;
using SyncToyNext.Core.Logging;
using SyncToyNext.Core.Model;

namespace SyncToyNext.App;

public partial class App : Application
{
    private string? _smokeTestLog;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int index = Array.FindIndex(e.Args, a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _smokeTestLog = index + 1 < e.Args.Length ? e.Args[index + 1] : Path.Combine(Path.GetTempPath(), "SyncToyNext-smoke.txt");
            RunSmokeTest();
            return;
        }

        new MainWindow().Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (_smokeTestLog is not null)
        {
            FinishSmokeTest(false, e.Exception.ToString());
            return;
        }

        MessageBox.Show(
            $"Ein unerwarteter Fehler ist aufgetreten:\n\n{e.Exception.Message}",
            UiHelpers.AppName,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    /// <summary>
    /// Starttest für den automatischen Build: öffnet alle Fenster mit Testdaten, erstellt eine echte Vorschau
    /// und beendet das Programm mit Rückgabewert 0 (in Ordnung) oder 1 (Fehler). So fallen Fehler in der
    /// Oberfläche (z. B. fehlende Design-Ressourcen) auf, ohne dass jemand das Programm von Hand startet.
    /// </summary>
    private void RunSmokeTest()
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "SyncToyNext-smoke", Guid.NewGuid().ToString("N"));
            string left = Path.Combine(root, "Links");
            string right = Path.Combine(root, "Rechts");
            Directory.CreateDirectory(Path.Combine(left, "Unterordner"));
            Directory.CreateDirectory(right);
            File.WriteAllText(Path.Combine(left, "neu.txt"), "links");
            File.WriteAllText(Path.Combine(left, "Unterordner", "datei.txt"), "links");
            File.WriteAllText(Path.Combine(right, "rechts.txt"), "rechts");

            var pair = new FolderPair { Name = "Starttest", LeftPath = left, RightPath = right };
            pair.Options.ExcludePatterns = "*.tmp";

            var main = new MainWindow();
            main.Show();

            var dialog = new FolderPairWindow(null, Array.Empty<FolderPair>(), pair) { Owner = main };
            dialog.Show();
            dialog.Close();

            var edit = new FolderPairWindow(pair, new[] { pair }) { Owner = main };
            edit.Show();
            edit.Close();

            var about = new AboutWindow("Ordnerpaare.json", "Protokoll.txt") { Owner = main };
            about.Show();
            about.Close();

            var scheduler = new SchedulerInfoWindow(pair.Name) { Owner = main };
            scheduler.Show();
            scheduler.Close();

            var preview = new PreviewWindow(new[] { pair }, autoRun: false, new SyncEngine(),
                new SyncLog(Path.Combine(root, "log.txt")), _ => { }) { Owner = main };
            preview.Show();

            // Die Vorschau wird asynchron erstellt; nach kurzer Zeit prüfen und alles schließen.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                int rows = preview.OperationsGrid.Items.Count;
                preview.Close();
                main.Close();
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }

                // Erwartet: Ordner "Unterordner" anlegen, neu.txt und Unterordner/datei.txt nach rechts, rechts.txt nach links.
                FinishSmokeTest(rows == 4, $"Vorschau-Zeilen: {rows} (erwartet 4)");
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            FinishSmokeTest(false, ex.ToString());
        }
    }

    private void FinishSmokeTest(bool success, string details)
    {
        try
        {
            File.WriteAllText(_smokeTestLog!, $"{(success ? "OK" : "FEHLER")}{Environment.NewLine}{details}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }

        Shutdown(success ? 0 : 1);
    }
}
