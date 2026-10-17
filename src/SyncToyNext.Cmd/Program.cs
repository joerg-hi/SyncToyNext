using SyncToyNext.Core;
using SyncToyNext.Core.Config;
using SyncToyNext.Core.Execution;
using SyncToyNext.Core.Logging;
using SyncToyNext.Core.Model;
using SyncToyNext.Core.Planning;

namespace SyncToyNext.Cmd;

/// <summary>
/// Kommandozeile, angelehnt an SyncToyCmd.exe. Gedacht vor allem für die Windows-Aufgabenplanung.
/// </summary>
public static class Program
{
    private const int ExitOk = 0;
    private const int ExitErrors = 1;
    private const int ExitUsage = 2;

    // Der Windows-Papierkorb (SHFileOperation) erwartet einen STA-Thread.
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            PrintUsage();
            return ExitUsage;
        }

        if (options.Mode == Mode.Help)
        {
            PrintUsage();
            return ExitOk;
        }

        if (options.Mode == Mode.SpeedTest)
        {
            return RunSpeedTest(options.SpeedTestFile!, options.SpeedTestFolder!);
        }

        var store = new PairStore(options.ConfigPath);
        List<FolderPair> pairs;
        try
        {
            pairs = store.Load();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Die Ordnerpaare konnten nicht aus \"{store.FilePath}\" geladen werden: {ex.Message}");
            return ExitUsage;
        }

        if (options.Mode == Mode.List)
        {
            ListPairs(pairs, store.FilePath);
            return ExitOk;
        }

        List<FolderPair> selected;
        if (options.PairName is null)
        {
            selected = pairs.Where(p => p.Options.ActiveForRunAll).ToList();
            if (selected.Count == 0)
            {
                Console.WriteLine("Es gibt keine Ordnerpaare, die für \"Alle ausführen\" aktiv sind.");
                return ExitOk;
            }
        }
        else
        {
            var pair = PairStore.Find(pairs, options.PairName);
            if (pair is null)
            {
                Console.Error.WriteLine($"Das Ordnerpaar \"{options.PairName}\" wurde nicht gefunden. Vorhandene Paare: " +
                                        string.Join(", ", pairs.Select(p => $"\"{p.Name}\"")));
                return ExitUsage;
            }

            selected = new List<FolderPair> { pair };
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine("Abbruch angefordert ...");
        };

        var engine = new SyncEngine();
        var log = options.NoLog ? null : new SyncLog();
        bool anyErrors = false;

        foreach (var pair in selected)
        {
            if (cts.IsCancellationRequested) break;
            anyErrors |= !ProcessPair(engine, log, pair, options, cts.Token);

            if (options.Mode == Mode.Run)
            {
                pair.LastRunUtc = DateTime.UtcNow;
            }
        }

        if (options.Mode == Mode.Run)
        {
            TrySaveLastRun(store, selected);
        }

        return anyErrors || cts.IsCancellationRequested ? ExitErrors : ExitOk;
    }

    /// <summary>Kopiert eine Datei mit beiden Verfahren in den Zielordner und zeigt das Tempo.</summary>
    private static int RunSpeedTest(string file, string folder)
    {
        Console.WriteLine($"Geschwindigkeitstest: \"{file}\" nach \"{folder}\"");
        Console.WriteLine("Die Testkopien werden danach wieder gelöscht.");
        Console.WriteLine();

        IReadOnlyList<CopySpeedTest.Measurement> results;
        try
        {
            results = CopySpeedTest.Run(file, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"FEHLER: {ex.Message}");
            return ExitErrors;
        }

        foreach (var m in results)
        {
            Console.WriteLine(m.Error is null
                ? $"  {m.Method,-40} {SyncLog.FormatBytes(m.Bytes)} in {TransferEstimator.FormatDuration(m.Duration)}  =  {SyncLog.FormatBytes((long)m.BytesPerSecond)}/s"
                : $"  {m.Method,-40} FEHLER: {m.Error}");
        }

        Console.WriteLine();
        Console.WriteLine("Zum Vergleich dieselbe Datei mit dem Explorer in denselben Ordner kopieren.");
        return results.Any(r => r.Error is not null) ? ExitErrors : ExitOk;
    }

    /// <returns><c>false</c>, wenn Fehler aufgetreten sind.</returns>
    private static bool ProcessPair(SyncEngine engine, SyncLog? log, FolderPair pair, Options options, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {pair.Name} ({ActionName(pair.Action)}) ===");
        Console.WriteLine($"    Links:  {pair.LeftPath}");
        Console.WriteLine($"    Rechts: {pair.RightPath}");

        SyncPlan plan;
        try
        {
            plan = engine.Preview(pair, ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"    FEHLER: {ex.Message}");
            log?.WriteError(pair.Name, ex.Message);
            return false;
        }

        foreach (string warning in plan.Warnings)
        {
            Console.WriteLine($"    Hinweis: {warning}");
        }

        if (options.Mode == Mode.Preview)
        {
            PrintPlan(plan);
            return true;
        }

        if (plan.ConfirmationRequired is not null && !options.Force)
        {
            Console.Error.WriteLine($"    ÜBERSPRUNGEN: {plan.ConfirmationRequired}");
            Console.Error.WriteLine("    Mit --force wird trotzdem ausgeführt.");
            log?.WriteError(pair.Name, "Übersprungen: " + plan.ConfirmationRequired);
            return false;
        }

        if (plan.Operations.Count == 0)
        {
            Console.WriteLine("    Keine Änderungen nötig.");
        }

        var result = engine.Run(plan, null, ct);
        log?.Write(result);

        foreach (var op in plan.Operations.Where(o => o.Status == OperationStatus.Failed))
        {
            Console.Error.WriteLine($"    FEHLER {op.KindText} {op.DisplayPath}: {op.Message}");
        }

        foreach (string warning in result.Warnings)
        {
            Console.WriteLine($"    Hinweis: {warning}");
        }

        Console.WriteLine($"    Ergebnis: {result.Succeeded} erfolgreich, {result.Failed} fehlgeschlagen, " +
                          $"{result.Skipped} übersprungen, {SyncLog.FormatBytes(result.BytesCopied)} kopiert, " +
                          $"Dauer {result.Duration:hh\\:mm\\:ss}{(result.WasCancelled ? ", ABGEBROCHEN" : "")}");
        return !result.HasErrors && !result.WasCancelled;
    }

    private static void PrintPlan(SyncPlan plan)
    {
        if (plan.Operations.Count == 0)
        {
            Console.WriteLine("    Keine Änderungen nötig.");
            return;
        }

        foreach (var op in plan.Operations)
        {
            Console.WriteLine($"    {op.KindText,-15} {op.DirectionSymbol,-6} {op.DisplayPath}   [{op.Reason}]");
        }

        Console.WriteLine($"    {plan.Operations.Count} Aktionen, {SyncLog.FormatBytes(plan.TotalBytesToCopy)} zu kopieren.");
        if (plan.ConfirmationRequired is not null)
        {
            Console.WriteLine($"    ACHTUNG: {plan.ConfirmationRequired}");
        }
    }

    private static void ListPairs(List<FolderPair> pairs, string path)
    {
        Console.WriteLine($"Ordnerpaare aus \"{path}\":");
        if (pairs.Count == 0)
        {
            Console.WriteLine("  (keine)");
            return;
        }

        foreach (var p in pairs)
        {
            string active = p.Options.ActiveForRunAll ? "" : "  [nicht bei \"Alle ausführen\"]";
            Console.WriteLine($"  {p.Name}  ({ActionName(p.Action)}){active}");
            Console.WriteLine($"      {p.LeftPath}  ->  {p.RightPath}");
        }
    }

    private static void TrySaveLastRun(PairStore store, List<FolderPair> ran)
    {
        try
        {
            // Neu laden, damit gleichzeitige Änderungen aus der Oberfläche nicht überschrieben werden.
            var current = store.Load();
            foreach (var pair in current)
            {
                var updated = ran.FirstOrDefault(r => r.Id == pair.Id);
                if (updated is not null) pair.LastRunUtc = updated.LastRunUtc;
            }

            store.Save(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Hinweis: Zeitpunkt des letzten Laufs konnte nicht gespeichert werden: {ex.Message}");
        }
    }

    private static string ActionName(SyncAction action) => action switch
    {
        SyncAction.Synchronize => "Synchronize",
        SyncAction.Echo => "Echo",
        SyncAction.Contribute => "Contribute",
        _ => action.ToString(),
    };

    private static void PrintUsage()
    {
        Console.WriteLine("""
            SyncToyNextCmd – Ordnerpaare von SyncToyNext ohne Oberfläche ausführen

            Aufruf:
              SyncToyNextCmd -R                 Alle Ordnerpaare ausführen, die für "Alle ausführen" aktiv sind
              SyncToyNextCmd -R "Name"          Ein bestimmtes Ordnerpaar ausführen (auch: -R"Name")
              SyncToyNextCmd -P ["Name"]        Nur Vorschau anzeigen, nichts ändern
              SyncToyNextCmd -L                 Alle Ordnerpaare auflisten
              SyncToyNextCmd --speedtest "Datei" "Zielordner"
                                                Kopiertempo messen (z. B. zum NAS), beide Kopierverfahren
              SyncToyNextCmd -?                 Diese Hilfe

            Zusätzliche Optionen:
              --force                           Auch ausführen, wenn eine Seite plötzlich leer ist
                                                (sonst wird das Paar zur Sicherheit übersprungen)
              --config "Datei"                  Andere Datei mit Ordnerpaaren verwenden
              --no-log                          Kein Protokoll schreiben

            Ordnerpaare werden in der Oberfläche (SyncToyNext.exe) angelegt.
            Rückgabewert: 0 = erfolgreich, 1 = Fehler bei mindestens einem Paar, 2 = falscher Aufruf.
            """);
    }

    private enum Mode
    {
        Help,
        Run,
        Preview,
        List,
        SpeedTest,
    }

    private sealed class Options
    {
        public Mode Mode { get; private set; } = Mode.Help;
        public string? PairName { get; private set; }
        public bool Force { get; private set; }
        public bool NoLog { get; private set; }
        public string? ConfigPath { get; private set; }
        public string? SpeedTestFile { get; private set; }
        public string? SpeedTestFolder { get; private set; }

        public static Options Parse(string[] args)
        {
            var o = new Options();
            bool modeSet = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string lower = arg.ToLowerInvariant();

                if (lower is "-?" or "/?" or "-h" or "--help")
                {
                    o.Mode = Mode.Help;
                    return o;
                }

                if (lower is "--force" or "-force" or "/force") { o.Force = true; continue; }
                if (lower is "--no-log" or "/nolog") { o.NoLog = true; continue; }

                if (lower is "--speedtest" or "/speedtest")
                {
                    if (i + 2 >= args.Length) throw new ArgumentException("Nach --speedtest fehlen Datei und Zielordner.");
                    o.Mode = Mode.SpeedTest;
                    o.SpeedTestFile = args[++i];
                    o.SpeedTestFolder = args[++i];
                    return o;
                }

                if (lower is "--config" or "/config")
                {
                    if (i + 1 >= args.Length) throw new ArgumentException("Nach --config fehlt der Dateiname.");
                    o.ConfigPath = args[++i];
                    continue;
                }

                if (arg.Length >= 2 && (arg[0] == '-' || arg[0] == '/') && char.ToUpperInvariant(arg[1]) is 'R' or 'P' or 'L')
                {
                    if (modeSet) throw new ArgumentException("Bitte nur einen der Schalter -R, -P oder -L angeben.");
                    modeSet = true;
                    o.Mode = char.ToUpperInvariant(arg[1]) switch
                    {
                        'R' => Mode.Run,
                        'P' => Mode.Preview,
                        _ => Mode.List,
                    };

                    if (o.Mode == Mode.List) continue;

                    // Name direkt angehängt (-R"Name") oder als nächstes Argument (-R "Name").
                    if (arg.Length > 2)
                    {
                        o.PairName = arg[2..].Trim('"');
                    }
                    else if (i + 1 < args.Length && !args[i + 1].StartsWith('-') && !args[i + 1].StartsWith('/'))
                    {
                        o.PairName = args[++i];
                    }

                    continue;
                }

                throw new ArgumentException($"Unbekanntes Argument: {arg}");
            }

            return o;
        }
    }
}
