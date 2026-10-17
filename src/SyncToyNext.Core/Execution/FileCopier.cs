using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SyncToyNext.Core.Execution;

/// <summary>
/// Kopiert eine Datei samt Änderungszeit und Attributen.
/// </summary>
/// <remarks>
/// Unter Windows wird <c>CopyFileEx</c> verwendet, wie vom Explorer: Es arbeitet über das Netzwerk mit großen,
/// überlappenden Blöcken, übernimmt Zeitstempel und Attribute selbst (keine Extra-Abfragen) und meldet den
/// Fortschritt. Auf anderen Systemen (nur für die Tests) wird blockweise kopiert.
/// </remarks>
internal static class FileCopier
{
    private const int StreamBufferSize = 1024 * 1024;
    private const long NoBufferingThreshold = 256L * 1024 * 1024;

    public static void Copy(string source, string target, long expectedSize, Action<long> reportBytes, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            CopyWindows(source, target, expectedSize, reportBytes, ct);
        }
        else
        {
            CopyStream(source, target, reportBytes, ct);
        }
    }

    // ------------------------------------------------------------------ Windows

    private const uint COPY_FILE_FAIL_IF_EXISTS = 0x00000001;
    private const uint COPY_FILE_NO_BUFFERING = 0x00001000;
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

    private static void CopyWindows(string source, string target, long expectedSize, Action<long> reportBytes, CancellationToken ct)
    {
        int cancel = 0;
        CopyProgressRoutine callback = (total, transferred, _, _, _, _, _, _, _) =>
        {
            reportBytes(transferred);
            return ct.IsCancellationRequested ? PROGRESS_CANCEL : PROGRESS_CONTINUE;
        };

        uint flags = COPY_FILE_FAIL_IF_EXISTS;
        if (expectedSize >= NoBufferingThreshold) flags |= COPY_FILE_NO_BUFFERING; // empfohlen für sehr große Dateien

        bool ok = CopyFileEx(ToExtendedPath(source), ToExtendedPath(target), callback, IntPtr.Zero, ref cancel, flags);
        int error = Marshal.GetLastPInvokeError();
        GC.KeepAlive(callback);

        if (ok) return;
        if (error == ERROR_REQUEST_ABORTED || ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        throw new IOException(new Win32Exception(error).Message, error);
    }

    /// <summary>Ergänzt bei langen Pfaden das Präfix \\?\, das Windows-Funktionen dafür verlangen.</summary>
    private static string ToExtendedPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.Length < 250 || full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    // ------------------------------------------------------------------ andere Systeme

    private static void CopyStream(string source, string target, Action<long> reportBytes, CancellationToken ct)
    {
        var sourceInfo = new FileInfo(source);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.SequentialScan))
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, StreamBufferSize))
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
