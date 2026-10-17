namespace SyncToyNext.Core.Model;

/// <summary>
/// Eine Datei oder ein Ordner relativ zum Wurzelordner einer Seite.
/// Relative Pfade verwenden intern immer "/" als Trennzeichen.
/// </summary>
public sealed record FileEntry(
    string RelativePath,
    bool IsDirectory,
    long Size,
    DateTime LastWriteTimeUtc,
    FileAttributes Attributes = FileAttributes.Normal)
{
    /// <summary>
    /// Toleranz beim Vergleich von Zeitstempeln zwischen zwei Datenträgern.
    /// FAT/exFAT speichern Zeiten nur auf 2 Sekunden genau.
    /// </summary>
    public static readonly TimeSpan CrossVolumeTolerance = TimeSpan.FromSeconds(2);

    public FileEntry WithPath(string relativePath) => this with { RelativePath = relativePath };

    /// <summary>
    /// Exakter Vergleich mit einem früheren Stand derselben Seite.
    /// </summary>
    public bool SameAsSnapshot(FileEntry? snapshot) =>
        snapshot is not null
        && snapshot.IsDirectory == IsDirectory
        && (IsDirectory || (snapshot.Size == Size && snapshot.LastWriteTimeUtc == LastWriteTimeUtc));

    /// <summary>
    /// Vergleich von Größe und Zeitstempel zwischen linker und rechter Seite (mit Toleranz).
    /// </summary>
    public bool SameMetadataAs(FileEntry other) =>
        other.Size == Size
        && (LastWriteTimeUtc - other.LastWriteTimeUtc).Duration() <= CrossVolumeTolerance;
}

public static class PathUtil
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static string Normalize(string relativePath) =>
        relativePath.Replace('\\', '/').Trim('/');

    public static string ToFullPath(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public static string ToDisplay(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar);

    public static string? GetParent(string relativePath)
    {
        int idx = relativePath.LastIndexOf('/');
        return idx < 0 ? null : relativePath[..idx];
    }

    public static string GetName(string relativePath)
    {
        int idx = relativePath.LastIndexOf('/');
        return idx < 0 ? relativePath : relativePath[(idx + 1)..];
    }

    public static bool IsSameOrBelow(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    public static int Depth(string relativePath) => relativePath.Count(c => c == '/');
}
