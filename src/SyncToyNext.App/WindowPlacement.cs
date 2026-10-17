using System.IO;
using System.Text.Json;
using System.Windows;
using SyncToyNext.Core.Config;

namespace SyncToyNext.App;

/// <summary>
/// Merkt sich Größe und Position der Fenster in <c>%LOCALAPPDATA%\SyncToyNext\Windows.json</c>.
/// </summary>
internal static class WindowPlacement
{
    private static readonly string FilePath = Path.Combine(PairStore.DefaultDataDirectory, "Windows.json");
    private static Dictionary<string, Placement>? s_cache;

    /// <summary>Stellt die gespeicherte Größe/Position wieder her und speichert sie beim Schließen.</summary>
    public static void Attach(Window window, string key, bool restorePosition)
    {
        var placements = Load();
        if (placements.TryGetValue(key, out var p) && p.Width >= window.MinWidth && p.Height >= window.MinHeight)
        {
            window.Width = p.Width;
            window.Height = p.Height;

            if (restorePosition && IsOnScreen(p))
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = p.Left;
                window.Top = p.Top;
            }

            if (p.Maximized) window.WindowState = WindowState.Maximized;
        }

        window.Closing += (_, _) =>
        {
            var bounds = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
                : window.RestoreBounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            var all = Load();
            all[key] = new Placement(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                window.WindowState == WindowState.Maximized);
            Save(all);
        };
    }

    private static bool IsOnScreen(Placement p)
    {
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        // Mindestens die Titelleiste muss sichtbar sein.
        return screen.IntersectsWith(new Rect(p.Left + 40, p.Top, Math.Max(1, p.Width - 80), 30));
    }

    private static Dictionary<string, Placement> Load()
    {
        if (s_cache is not null) return s_cache;
        try
        {
            if (File.Exists(FilePath))
            {
                s_cache = JsonSerializer.Deserialize<Dictionary<string, Placement>>(File.ReadAllText(FilePath));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Dann eben mit Standardgröße starten.
        }

        return s_cache ??= new Dictionary<string, Placement>();
    }

    private static void Save(Dictionary<string, Placement> placements)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(placements));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nicht kritisch.
        }
    }

    private sealed record Placement(double Left, double Top, double Width, double Height, bool Maximized);
}
