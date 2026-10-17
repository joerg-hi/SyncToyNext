using System.Diagnostics;
using SyncToyNext.Core.Scanning;

namespace SyncToyNext.Core.Execution;

/// <summary>
/// Misst, wie schnell eine Datei in einen Zielordner kopiert wird, einmal mit der Windows-Kopierfunktion
/// (so kopiert SyncToyNext normalerweise) und einmal blockweise (der Ersatzweg nach Fehlern).
/// Zum Vergleich mit dem Explorer, um zu sehen, ob das Netz oder das Kopierverfahren bremst.
/// </summary>
public static class CopySpeedTest
{
    /// <summary>Ergebnis eines Verfahrens.</summary>
    /// <param name="Method">Name des Verfahrens.</param>
    /// <param name="Duration">Dauer; ohne Bedeutung, wenn <paramref name="Error"/> gesetzt ist.</param>
    /// <param name="Error">Fehlermeldung, wenn das Kopieren fehlschlug.</param>
    public sealed record Measurement(string Method, long Bytes, TimeSpan Duration, string? Error)
    {
        public double BytesPerSecond => Duration.TotalSeconds > 0 ? Bytes / Duration.TotalSeconds : 0;
    }

    public static IReadOnlyList<Measurement> Run(string sourceFile, string targetFolder, CancellationToken ct = default)
    {
        var source = new FileInfo(sourceFile);
        if (!source.Exists) throw new FileNotFoundException($"Die Datei \"{sourceFile}\" existiert nicht.", sourceFile);
        if (!Directory.Exists(targetFolder)) throw new DirectoryNotFoundException($"Der Ordner \"{targetFolder}\" existiert nicht.");

        var methods = new List<(string Name, Action<string, string, Action<long>, CancellationToken> Copy)>();
        if (OperatingSystem.IsWindows())
        {
            methods.Add(("Windows-Kopierfunktion (normaler Weg)", FileCopier.CopyWindows));
        }

        methods.Add(("Blockweise (Ersatzweg nach Fehlern)", FileCopier.CopyBlockwise));

        var results = new List<Measurement>();
        foreach (var (name, copy) in methods)
        {
            ct.ThrowIfCancellationRequested();
            // Endung wie die temporären Dateien beim Synchronisieren: wird beim Einlesen übersprungen.
            string target = Path.Combine(targetFolder,
                $"{source.Name}.geschwindigkeitstest-{Guid.NewGuid().ToString("N")[..8]}{FileFilter.TempFileSuffix}");
            var clock = Stopwatch.StartNew();
            try
            {
                copy(source.FullName, target, _ => { }, ct);
                clock.Stop();
                results.Add(new Measurement(name, source.Length, clock.Elapsed, null));
            }
            catch (IOException ex)
            {
                results.Add(new Measurement(name, source.Length, clock.Elapsed, ex.Message));
            }
            finally
            {
                try
                {
                    if (File.Exists(target))
                    {
                        File.SetAttributes(target, FileAttributes.Normal);
                        File.Delete(target);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return results;
    }
}
