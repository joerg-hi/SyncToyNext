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

    public static string OptionsSummary(PairOptions o)
    {
        var parts = new List<string>();
        if (o.IncludePatterns.Trim() is { Length: > 0 } inc && inc != "*") parts.Add($"nur {inc}");
        if (o.ExcludePatterns.Trim() is { Length: > 0 } exc) parts.Add($"ohne {exc}");
        if (o.ExcludedSubfolders.Count > 0) parts.Add($"{o.ExcludedSubfolders.Count} Unterordner ausgeschlossen");

        var attributes = new List<string>();
        if (o.ExcludeReadOnly) attributes.Add("schreibgeschützte");
        if (o.ExcludeHidden) attributes.Add("versteckte");
        if (o.ExcludeSystem) attributes.Add("System-");
        if (attributes.Count > 0) parts.Add($"ohne {string.Join(", ", attributes)} Dateien");

        if (o.CheckFileContents) parts.Add("Dateiinhalte prüfen");
        parts.Add(o.SaveOverwrittenToRecycleBin ? "Papierkorb" : "kein Papierkorb");
        if (!o.ActiveForRunAll) parts.Add("nicht bei „Alle ausführen“");
        return string.Join(" · ", parts);
    }

    public static void OpenInExplorer(string path)
    {
        if (!Directory.Exists(path))
        {
            MessageBox.Show($"Der Ordner \"{path}\" existiert nicht oder ist nicht erreichbar.", AppName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
