using System.Text.RegularExpressions;
using SyncToyNext.Core.Model;

namespace SyncToyNext.Core.Scanning;

/// <summary>
/// Entscheidet anhand der Optionen eines Ordnerpaars, welche Dateien und Ordner berücksichtigt werden.
/// Ausgefilterte Einträge sind für die Synchronisation unsichtbar: Sie werden weder kopiert noch gelöscht.
/// </summary>
public sealed class FileFilter
{
    /// <summary>Endung der temporären Dateien, die beim Kopieren entstehen.</summary>
    public const string TempFileSuffix = ".stn-tmp";

    private static readonly string[] RootFoldersAlwaysSkipped = { "$RECYCLE.BIN", "System Volume Information" };

    private readonly List<Regex> _include;
    private readonly List<Regex> _exclude;
    private readonly List<string> _excludedFolders;
    private readonly FileAttributes _excludedAttributes;

    public FileFilter(PairOptions options)
    {
        _include = ParsePatterns(options.IncludePatterns);
        if (_include.Count == 0)
        {
            _include.Add(WildcardToRegex("*"));
        }

        _exclude = ParsePatterns(options.ExcludePatterns);
        _excludedFolders = options.ExcludedSubfolders
            .Select(PathUtil.Normalize)
            .Where(p => p.Length > 0)
            .ToList();

        if (options.ExcludeReadOnly) _excludedAttributes |= FileAttributes.ReadOnly;
        if (options.ExcludeHidden) _excludedAttributes |= FileAttributes.Hidden;
        if (options.ExcludeSystem) _excludedAttributes |= FileAttributes.System;
    }

    public bool IncludeDirectory(string relativePath, FileAttributes attributes)
    {
        // Verbindungspunkte und symbolische Links werden nicht verfolgt (Schutz vor Endlosschleifen).
        if ((attributes & FileAttributes.ReparsePoint) != 0) return false;

        if (!relativePath.Contains('/')
            && RootFoldersAlwaysSkipped.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return !_excludedFolders.Any(f => PathUtil.IsSameOrBelow(relativePath, f));
    }

    public bool IncludeFile(string relativePath, FileAttributes attributes)
    {
        string name = PathUtil.GetName(relativePath);

        if (name.EndsWith(TempFileSuffix, StringComparison.OrdinalIgnoreCase)) return false;
        if (!relativePath.Contains('/') && Snapshots.SnapshotStore.IsSnapshotFileName(name)) return false;
        if (IsOriginalSyncToyFile(name)) return false;
        if ((attributes & _excludedAttributes) != 0) return false;

        if (!_include.Any(r => Matches(r, relativePath, name))) return false;
        return !_exclude.Any(r => Matches(r, relativePath, name));
    }

    /// <summary>
    /// Statusdateien des originalen SyncToy („SyncToy_{GUID}.dat“). Sie liegen im Wurzelordner jedes früheren
    /// Ordnerpaars, also auch in Unterordnern, und dürfen nie mitkopiert werden.
    /// </summary>
    internal static bool IsOriginalSyncToyFile(string name) =>
        name.StartsWith("SyncToy_", StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
        && Guid.TryParse(name.AsSpan(8, name.Length - 12), out _);

    private static bool Matches(Regex regex, string relativePath, string name) =>
        regex.IsMatch(name) || regex.IsMatch(relativePath);

    internal static List<Regex> ParsePatterns(string? patterns) =>
        (patterns ?? "")
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(WildcardToRegex)
            .ToList();

    internal static Regex WildcardToRegex(string pattern)
    {
        string normalized = pattern.Replace('\\', '/');
        string regex = "^" + Regex.Escape(normalized).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
