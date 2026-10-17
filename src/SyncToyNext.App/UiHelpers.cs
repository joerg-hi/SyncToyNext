using System.Diagnostics;
using System.IO;
using System.Windows;
using SyncToyNext.Core.Model;

namespace SyncToyNext.App;

internal static class UiHelpers
{
    public const string AppName = "SyncToyNext";

    public static string ActionTitle(SyncAction action) => action switch
    {
        SyncAction.Synchronize => "Synchronize",
        SyncAction.Echo => "Echo",
        SyncAction.Contribute => "Contribute",
        _ => action.ToString(),
    };

    public static string ActionDescription(SyncAction action) => action switch
    {
        SyncAction.Synchronize =>
            "Neue, geänderte, umbenannte und gelöschte Dateien werden in beide Richtungen übernommen.",
        SyncAction.Echo =>
            "Neue und geänderte Dateien werden von links nach rechts kopiert. Umbenennungen und Löschungen " +
            "links werden rechts wiederholt. Änderungen im rechten Ordner werden nicht beachtet.",
        SyncAction.Contribute =>
            "Neue und geänderte Dateien werden von links nach rechts kopiert. Umbenennungen links werden rechts " +
            "wiederholt. Es wird nichts gelöscht.",
        _ => "",
    };

    /// <summary>Kurze Beschreibungen der aktiven Optionen für die Anzeige als „Chips“.</summary>
    public static List<string> OptionChips(PairOptions o)
    {
        var chips = new List<string>();
        if (o.IncludePatterns.Trim() is { Length: > 0 } inc && inc != "*") chips.Add($"Nur: {inc}");
        if (o.ExcludePatterns.Trim() is { Length: > 0 } exc) chips.Add($"Ohne: {exc}");
        if (o.ExcludedSubfolders.Count == 1) chips.Add($"Ohne Ordner: {PathUtil.ToDisplay(o.ExcludedSubfolders[0])}");
        else if (o.ExcludedSubfolders.Count > 1) chips.Add($"{o.ExcludedSubfolders.Count} Unterordner ausgeschlossen");

        var attributes = new List<string>();
        if (o.ExcludeReadOnly) attributes.Add("schreibgeschützte");
        if (o.ExcludeHidden) attributes.Add("versteckte");
        if (o.ExcludeSystem) attributes.Add("System-");
        if (attributes.Count > 0) chips.Add($"Ohne {string.Join(", ", attributes)} Dateien");

        if (o.CheckFileContents) chips.Add("Dateiinhalte prüfen");
        chips.Add(o.SaveOverwrittenToRecycleBin ? "Papierkorb an" : "Papierkorb aus");
        chips.Add(o.ActiveForRunAll ? "Bei „Alle ausführen“ dabei" : "Nicht bei „Alle ausführen“");
        if (chips.Count == 2 && o.IncludePatterns.Trim() is "*" or "") chips.Insert(0, "Alle Dateien");
        return chips;
    }

    /// <summary>Letzter Teil eines Pfads, bei Laufwerken der Laufwerksbuchstabe (z. B. "E:\").</summary>
    public static string FolderName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string trimmed = Path.TrimEndingDirectorySeparator(path.Trim());
        string name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : path.Trim();
    }

    /// <summary>
    /// Prüft im Hintergrund, ob ein Ordner existiert, damit ein langsames oder getrenntes Netzlaufwerk
    /// die Oberfläche nicht einfriert. Liefert <c>null</c>, wenn innerhalb des Zeitlimits keine Antwort kam.
    /// </summary>
    public static async Task<bool?> DirectoryExistsAsync(string path, int timeoutMs = 5000)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return await Task.Run(() => Directory.Exists(path)).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Ordner, die aus dem Explorer gezogen werden (nur die Pfade, ohne Dateisystemzugriff).</summary>
    public static string[] DroppedPaths(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();

    /// <summary>Behält von den gezogenen Pfaden die vorhandenen Ordner (Prüfung im Hintergrund).</summary>
    public static async Task<List<string>> ExistingFoldersAsync(IEnumerable<string> paths, int max)
    {
        var result = new List<string>();
        foreach (string path in paths)
        {
            if (result.Count >= max) break;
            if (await DirectoryExistsAsync(path) == true) result.Add(path);
        }

        return result;
    }

    public static async Task OpenInExplorerAsync(string path)
    {
        bool? exists = await DirectoryExistsAsync(path);
        if (exists != true)
        {
            MessageBox.Show(exists is null
                    ? $"Der Ordner \"{path}\" antwortet nicht. Ist das Netzlaufwerk verbunden?"
                    : $"Der Ordner \"{path}\" existiert nicht oder ist nicht erreichbar.",
                AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    /// <summary>
    /// Führt eine Funktion auf einem eigenen STA-Thread aus. Der Windows-Papierkorb (SHFileOperation)
    /// arbeitet nur in STA-Threads zuverlässig; Threads aus dem Threadpool sind MTA.
    /// </summary>
    public static Task<T> RunSta<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }
}
