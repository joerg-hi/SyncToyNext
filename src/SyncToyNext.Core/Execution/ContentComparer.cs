namespace SyncToyNext.Core.Execution;

/// <summary>
/// Vergleicht zwei Dateien Byte für Byte. Das ist schneller als zwei Prüfsummen zu berechnen,
/// weil der Vergleich beim ersten Unterschied abbricht.
/// </summary>
public static class ContentComparer
{
    private const int BufferSize = 1024 * 1024;

    public static bool AreEqual(string pathA, string pathB, CancellationToken cancellationToken = default)
    {
        using var a = new FileStream(pathA, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.SequentialScan);
        using var b = new FileStream(pathB, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.SequentialScan);
        if (a.Length != b.Length) return false;

        var bufferA = new byte[BufferSize];
        var bufferB = new byte[BufferSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int readA = a.ReadAtLeast(bufferA, BufferSize, throwOnEndOfStream: false);
            int readB = b.ReadAtLeast(bufferB, BufferSize, throwOnEndOfStream: false);
            if (readA != readB) return false;
            if (readA == 0) return true;
            if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB))) return false;
        }
    }
}
