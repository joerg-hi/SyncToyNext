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
