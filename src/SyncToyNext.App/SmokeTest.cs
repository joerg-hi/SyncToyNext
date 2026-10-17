using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SyncToyNext.Core;
using SyncToyNext.Core.Config;
using SyncToyNext.Core.Logging;
using SyncToyNext.Core.Model;

namespace SyncToyNext.App;

/// <summary>
/// Starttest für den automatischen Build (<c>SyncToyNext.exe --smoke-test [Protokoll] [--screenshots Ordner]</c>).
/// Öffnet alle Fenster mit Testdaten, erstellt eine echte Vorschau, führt sie aus und speichert auf Wunsch
/// Bildschirmfotos im hellen und dunklen Modus. Rückgabewert 0 = in Ordnung, 1 = Fehler.
/// </summary>
internal sealed class SmokeTest
{
    private readonly string? _screenshotDir;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SyncToyNext-smoke", Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = new();

    public SmokeTest(string? screenshotDir) => _screenshotDir = screenshotDir;

    public IReadOnlyList<string> Log => _log;

    public async Task<bool> RunAsync()
    {
        try
        {
            var pairs = CreateTestData();
            bool ok = await RunPassAsync(pairs, "hell");

            if (_screenshotDir is not null)
            {
#pragma warning disable WPF0001 // ThemeMode ist in WPF noch als experimentell markiert.
                Application.Current.ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
                ok &= await RunPassAsync(CreateTestData(), "dunkel");
            }

            return ok;
        }
        finally
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private List<FolderPair> CreateTestData()
    {
        string set = Path.Combine(_root, Guid.NewGuid().ToString("N")[..6]);
        string docsLeft = Path.Combine(set, "Dokumente");
        string docsRight = Path.Combine(set, "USB-Stick", "Dokumente");
        Write(docsLeft, "Briefe/Kündigung Fitnessstudio.docx", "a");
        Write(docsLeft, "Briefe/Bewerbung.pdf", "bb");
        Write(docsLeft, "Steuer/2025/Belege.xlsx", "ccc");
        Write(docsLeft, "Urlaub/Packliste.txt", "dddd");
        Write(docsRight, "Notizen.txt", "eeeee");
        Write(docsRight, "Steuer/2024/Belege.xlsx", "ffffff");

        string photosLeft = Path.Combine(set, "Bilder");
        string photosRight = Path.Combine(set, "Sicherung", "Bilder");
        Write(photosLeft, "2026/Sommer/IMG_0001.jpg", "g");
        Directory.CreateDirectory(photosRight);

        var docs = new FolderPair { Name = "Dokumente → USB-Stick", LeftPath = docsLeft, RightPath = docsRight };
        docs.Options.ExcludePatterns = "*.tmp; ~$*";
        docs.Options.ExcludedSubfolders.Add("Cache");

        var photos = new FolderPair
        {
            Name = "Fotos sichern",
            LeftPath = photosLeft,
            RightPath = photosRight,
            Action = SyncAction.Echo,
            LastRunUtc = DateTime.UtcNow.AddDays(-1),
        };
        photos.Options.CheckFileContents = true;

        var music = new FolderPair
        {
            Name = "Musik zum Laptop",
            LeftPath = Path.Combine(set, "Musik"),
            RightPath = Path.Combine(set, "Laptop", "Musik"),
            Action = SyncAction.Contribute,
        };
        music.Options.ActiveForRunAll = false;

        return new List<FolderPair> { docs, photos, music };
    }

    private static void Write(string root, string relative, string content)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<bool> RunPassAsync(List<FolderPair> pairs, string mode)
    {
        bool ok = true;
        var store = new PairStore(Path.Combine(_root, $"pairs-{mode}.json"));
        store.Save(pairs);
        var log = new SyncLog(Path.Combine(_root, $"log-{mode}.txt"));

        // Willkommensseite ohne Ordnerpaare
        var empty = new MainWindow(new PairStore(Path.Combine(_root, $"empty-{mode}.json")));
        empty.Show();
        await Capture(empty, $"{mode}-1-willkommen");
        empty.Close();

        var main = new MainWindow(store);
        main.Show();
        await Capture(main, $"{mode}-2-hauptfenster");

        var edit = new FolderPairWindow(pairs[0], pairs) { Owner = main };
        edit.Show();
        await Capture(edit, $"{mode}-3-ordnerpaar");
        edit.Close();

        var create = new FolderPairWindow(null, pairs) { Owner = main };
        create.Show();
        await Capture(create, $"{mode}-4-neues-ordnerpaar");
        create.Close();

        var preview = new PreviewWindow(new[] { pairs[0] }, autoRun: false, new SyncEngine(), log, _ => { }) { Owner = main };
        preview.Show();
        await WaitUntil(() => preview.RunButton.IsEnabled, TimeSpan.FromSeconds(30));
        int rows = preview.OperationsGrid.Items.Count;
        // 4 Ordner anlegen, 4 Dateien nach rechts, 2 Dateien nach links
        _log.Add($"{mode}: Vorschau-Zeilen {rows} (erwartet 10)");
        ok &= rows == 10;
        await Capture(preview, $"{mode}-5-vorschau");
        preview.Close();

        var run = new PreviewWindow(pairs.Take(2).ToList(), autoRun: true, new SyncEngine(), log, _ => { }) { Owner = main };
        run.Show();
        await WaitUntil(() => run.ResultBar.Visibility == Visibility.Visible, TimeSpan.FromSeconds(60));
        bool finished = run.ResultBar.Visibility == Visibility.Visible;
        bool copied = File.Exists(Path.Combine(pairs[0].RightPath, "Urlaub", "Packliste.txt"))
                      && File.Exists(Path.Combine(pairs[1].RightPath, "2026", "Sommer", "IMG_0001.jpg"));
        _log.Add($"{mode}: Ausführung beendet {finished}, Dateien kopiert {copied}");
        ok &= finished && copied;
        await Capture(run, $"{mode}-6-ergebnis");
        run.Close();

        var scheduler = new SchedulerInfoWindow(pairs[0].Name) { Owner = main };
        scheduler.Show();
        await Capture(scheduler, $"{mode}-7-zeitsteuerung");
        scheduler.Close();

        var about = new AboutWindow(store.FilePath, log.FilePath) { Owner = main };
        about.Show();
        await Capture(about, $"{mode}-8-ueber");
        about.Close();

        main.Close();
        return ok;
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < end)
        {
            await Task.Delay(100);
        }
    }

    /// <summary>Speichert den Fensterinhalt als PNG (ohne Titelleiste), mit dem Fensterhintergrund des Designs.</summary>
    private async Task Capture(Window window, string name)
    {
        await Task.Delay(700); // Layout, Animationen und asynchrone Prüfungen abwarten
        if (_screenshotDir is null || window.Content is not FrameworkElement content) return;

        Directory.CreateDirectory(_screenshotDir);
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        int width = (int)Math.Ceiling(content.ActualWidth * scale);
        int height = (int)Math.Ceiling(content.ActualHeight * scale);
        if (width == 0 || height == 0) return;

        var background = window.TryFindResource("WindowBackground") as Brush ?? Brushes.White;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            dc.DrawRectangle(background, null, rect);
            dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, rect);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var stream = File.Create(Path.Combine(_screenshotDir, name + ".png"));
        encoder.Save(stream);
    }
}
