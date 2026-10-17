using System.Text.Json;
using SyncToyNext.Core.Model;

namespace SyncToyNext.Core.Snapshots;

/// <summary>
/// Speichert den Stand eines Ordners nach der letzten Synchronisation in einer Datei
/// <c>SyncToyNext_{Paar-ID}.dat</c> im Wurzelordner, so wie SyncToy seine <c>SyncToy_*.dat</c>.
/// Nur damit lassen sich Löschungen und Umbenennungen von neuen Dateien unterscheiden.
/// </summary>
public static class SnapshotStore
{
    private const string Prefix = "SyncToyNext_";
    private const string Extension = ".dat";
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static string GetFileName(Guid pairId) => $"{Prefix}{pairId:N}{Extension}";

    public static bool IsSnapshotFileName(string name) =>
        name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
        && (name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(Extension + ".new", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Lädt den gespeicherten Stand. Gibt <c>null</c> zurück, wenn es keinen (lesbaren) Stand gibt,
    /// das Paar also zum ersten Mal synchronisiert wird.
    /// </summary>
    public static Dictionary<string, FileEntry>? Load(string root, Guid pairId)
    {
        string path = Path.Combine(root, GetFileName(pairId));
        if (!File.Exists(path)) return null;

        try
        {
            var data = JsonSerializer.Deserialize<SnapshotData>(File.ReadAllText(path), JsonOptions);
            if (data is null || data.Version != CurrentVersion || data.PairId != pairId) return null;

            var result = new Dictionary<string, FileEntry>(PathUtil.Comparer);
            foreach (var e in data.Entries)
            {
                string rel = PathUtil.Normalize(e.P);
                result[rel] = new FileEntry(rel, e.D, e.S, new DateTime(e.T, DateTimeKind.Utc));
            }

            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(string root, Guid pairId, IEnumerable<FileEntry> entries)
    {
        var data = new SnapshotData
        {
            Version = CurrentVersion,
            PairId = pairId,
            CreatedUtc = DateTime.UtcNow,
            Entries = entries
                .OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(e => new SnapshotEntry
                {
                    P = e.RelativePath,
                    D = e.IsDirectory,
                    S = e.IsDirectory ? 0 : e.Size,
                    T = e.IsDirectory ? 0 : e.LastWriteTimeUtc.Ticks,
                })
                .ToList(),
        };

        string path = Path.Combine(root, GetFileName(pairId));
        string temp = path + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));

        // Eine versteckte Datei lässt sich unter Windows nicht direkt ersetzen.
        if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        File.Move(temp, path, overwrite: true);

        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, FileAttributes.Hidden);
        }
    }

    public static void Delete(string root, Guid pairId)
    {
        string path = Path.Combine(root, GetFileName(pairId));
        if (!File.Exists(path)) return;
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    private sealed class SnapshotData
    {
        public int Version { get; set; }
        public Guid PairId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public List<SnapshotEntry> Entries { get; set; } = new();
    }

    // Kurze Namen halten die Datei bei vielen Einträgen klein.
    private sealed class SnapshotEntry
    {
        public string P { get; set; } = "";
        public bool D { get; set; }
        public long S { get; set; }
        public long T { get; set; }
    }
}
