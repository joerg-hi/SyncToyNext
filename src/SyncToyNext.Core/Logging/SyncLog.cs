using System.Text;
using SyncToyNext.Core.Config;
using SyncToyNext.Core.Execution;
using SyncToyNext.Core.Planning;

namespace SyncToyNext.Core.Logging;

/// <summary>
/// Schreibt ein fortlaufendes Textprotokoll, standardmäßig
/// <c>%LOCALAPPDATA%\SyncToyNext\SyncToyNextLog.txt</c>. Ab 5 MB wird die alte Datei umbenannt.
/// </summary>
public sealed class SyncLog
{
    private const long MaxSize = 5 * 1024 * 1024;
    private static readonly object Gate = new();

    public SyncLog(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(PairStore.DefaultDataDirectory, "SyncToyNextLog.txt");
    }

    public string FilePath { get; }

    public void Write(SyncResult result)
    {
        var plan = result.Plan;
        var sb = new StringBuilder();
        sb.AppendLine(new string('=', 70));
        sb.AppendLine($"{result.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  Ordnerpaar \"{plan.Pair.Name}\" ({plan.Pair.Action})");
        sb.AppendLine($"Links:  {plan.Pair.LeftPath}");
        sb.AppendLine($"Rechts: {plan.Pair.RightPath}");

        foreach (string warning in plan.Warnings.Concat(result.Warnings))
        {
            sb.AppendLine($"Hinweis: {warning}");
        }

        foreach (var op in plan.Operations)
        {
            string status = op.Status switch
            {
                OperationStatus.Succeeded => "OK    ",
                OperationStatus.Failed => "FEHLER",
                _ => "ÜBERSP",
            };
            sb.Append($"  {status} {op.KindText,-15} {op.DirectionSymbol,-6} {op.DisplayPath}");
            if (!string.IsNullOrEmpty(op.Message)) sb.Append($"  ({op.Message})");
            sb.AppendLine();
        }

        sb.AppendLine($"Ergebnis: {result.Succeeded} erfolgreich, {result.Failed} fehlgeschlagen, {result.Skipped} übersprungen, " +
                      $"{FormatBytes(result.BytesCopied)} kopiert, Dauer {result.Duration:hh\\:mm\\:ss}" +
                      (result.WasCancelled ? ", ABGEBROCHEN" : ""));
        Append(sb.ToString());
    }

    public void WriteError(string pairName, string message) =>
        Append($"{new string('=', 70)}{Environment.NewLine}{DateTime.Now:yyyy-MM-dd HH:mm:ss}  Ordnerpaar \"{pairName}\": FEHLER {message}{Environment.NewLine}");

    private void Append(string text)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxSize)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }

                File.AppendAllText(FilePath, text, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ein fehlendes Protokoll darf die Synchronisation nicht verhindern.
            }
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}
