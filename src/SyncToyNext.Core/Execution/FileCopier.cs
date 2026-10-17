using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SyncToyNext.Core.Execution;

/// <summary>
/// Kopiert eine Datei samt Änderungszeit und Attributen.
/// </summary>
/// <remarks>
/// Unter Windows wird <c>CopyFileEx</c> verwendet, wie vom Explorer: Es arbeitet über das Netzwerk mit großen,
/// überlappenden Blöcken, übernimmt Zeitstempel und Attribute selbst (keine Extra-Abfragen) und meldet den
/// Fortschritt. Auf anderen Systemen (nur für die Tests) und als zweiter Versuch wird blockweise kopiert.
/// </remarks>
internal static class FileCopier
{
    private const int StreamBufferSize = 1024 * 1024;

    /// <summary>
    /// Kopiert <paramref name="source"/> nach <paramref name="target"/> (eine noch nicht vorhandene, temporäre Datei).
    /// Schlägt <c>CopyFileEx</c> fehl, wird die angefangene Datei gelöscht und einmal blockweise neu kopiert.
    /// </summary>
    /// <returns>Wie kopiert wurde; für das Protokoll.</returns>
    public static CopyOutcome Copy(string source, string target, Action<long> reportBytes, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            CopyBlockwise(source, target, reportBytes, ct);
            return new CopyOutcome(Blockwise: true, FirstError: null);
        }

        try
        {
            CopyWindows(source, target, reportBytes, ct);
            return new CopyOutcome(Blockwise: false, FirstError: null);
        }
        catch (IOException first)
        {
            // z. B. kurze Verbindungsunterbrechung zum NAS bei großen Dateien
            ct.ThrowIfCancellationRequested();
            try { if (File.Exists(target)) File.Delete(target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw first; }

            try
            {
                CopyBlockwise(source, target, reportBytes, ct);
                return new CopyOutcome(Blockwise: true, FirstError: first.Message);
            }
            catch (IOException second)
            {
                throw new IOException($"{first.Message} Zweiter Versuch: {second.Message}", second);
            }
        }
    }

    // ------------------------------------------------------------------ Windows

    private const uint PROGRESS_CONTINUE = 0;
    private const uint PROGRESS_CANCEL = 1;
    private const int ERROR_REQUEST_ABORTED = 1235;

    private delegate uint CopyProgressRoutine(
        long totalFileSize, long totalBytesTransferred, long streamSize, long streamBytesTransferred,
        uint streamNumber, uint callbackReason, IntPtr sourceFile, IntPtr destinationFile, IntPtr data);

    [DllImport("kernel32.dll", EntryPoint = "CopyFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileEx(
        string existingFileName, string newFileName, CopyProgressRoutine? progressRoutine,
        IntPtr data, ref int cancel, uint copyFlags);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelSynchronousIo(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint THREAD_TERMINATE = 0x0001; // von CancelSynchronousIo verlangt
    private const int ERROR_OPERATION_ABORTED = 995;

    internal static void CopyWindows(string source, string target, Action<long> reportBytes, CancellationToken ct)
    {
        // Hängt ein Zugriff auf das Netzlaufwerk (Öffnen, Schreiben, Schließen), ruft Windows die Fortschrittsfunktion
        // nicht mehr auf und „Abbrechen“ käme nie an. Deshalb wird bei einem Abbruch zusätzlich die gerade wartende
        // Ein-/Ausgabe dieses Threads abgebrochen.
        IntPtr thread = OpenThread(THREAD_TERMINATE, false, GetCurrentThreadId());
        var registration = thread == IntPtr.Zero ? default : ct.Register(() => CancelSynchronousIo(thread));
        try
        {
            CopyWindowsCore(source, target, reportBytes, ct);
        }
        finally
        {
            registration.Dispose(); // wartet auf einen laufenden Aufruf, erst danach das Handle schließen
            if (thread != IntPtr.Zero) CloseHandle(thread);
        }
    }

    private static void CopyWindowsCore(string source, string target, Action<long> reportBytes, CancellationToken ct)
    {
        int cancel = 0;
        CopyProgressRoutine callback = (total, transferred, _, _, _, _, _, _, _) =>
        {
            reportBytes(transferred);
            return ct.IsCancellationRequested ? PROGRESS_CANCEL : PROGRESS_CONTINUE;
        };

        // Ohne COPY_FILE_FAIL_IF_EXISTS: Der Name der temporären Datei ist ohnehin eindeutig, und das Flag kann bei
        // großen Dateien auf Netzlaufwerken zu „Die Datei ist vorhanden“ führen, wenn Windows das Anlegen nach einem
        // kurzen Verbindungsabbruch wiederholt. Auch auf COPY_FILE_NO_BUFFERING wird verzichtet (über das Netz unzuverlässig).
        bool ok = CopyFileEx(ToExtendedPath(source), ToExtendedPath(target), callback, IntPtr.Zero, ref cancel, 0);
        int error = Marshal.GetLastPInvokeError();
        GC.KeepAlive(callback);

        if (ok) return;
        if (error is ERROR_REQUEST_ABORTED or ERROR_OPERATION_ABORTED || ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        throw new IOException($"{new Win32Exception(error).Message} (Fehler {error})", error);
    }

    /// <summary>Ergänzt bei langen Pfaden das Präfix \\?\, das Windows-Funktionen dafür verlangen.</summary>
    private static string ToExtendedPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.Length < 250 || full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    // ------------------------------------------------------------------ blockweise (andere Systeme, zweiter Versuch)

    internal static void CopyBlockwise(string source, string target, Action<long> reportBytes, CancellationToken ct)
    {
        var sourceInfo = new FileInfo(source);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, StreamBufferSize, FileOptions.SequentialScan))
        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferSize))
        {
            var buffer = new byte[StreamBufferSize];
            long copied = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                copied += read;
                reportBytes(copied);
            }
        }

        File.SetLastWriteTimeUtc(target, sourceInfo.LastWriteTimeUtc);
        if ((sourceInfo.Attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
        }
    }
}

/// <summary>Wie eine Datei kopiert wurde.</summary>
/// <param name="Blockwise">Blockweise statt mit der Windows-Kopierfunktion (langsamer über das Netz).</param>
/// <param name="FirstError">Fehler der Windows-Kopierfunktion, wegen dem blockweise kopiert wurde.</param>
internal readonly record struct CopyOutcome(bool Blockwise, string? FirstError);
