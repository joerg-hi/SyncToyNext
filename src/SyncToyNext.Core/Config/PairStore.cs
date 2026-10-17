using System.Text.Json;
using SyncToyNext.Core.Model;

namespace SyncToyNext.Core.Config;

/// <summary>
/// Speichert die Ordnerpaare als JSON, standardmäßig unter
/// <c>%LOCALAPPDATA%\SyncToyNext\FolderPairs.json</c>.
/// </summary>
public sealed class PairStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public PairStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(DefaultDataDirectory, "FolderPairs.json");
    }

    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SyncToyNext");

    public string FilePath { get; }

    public List<FolderPair> Load()
    {
        if (!File.Exists(FilePath)) return new List<FolderPair>();
        var data = JsonSerializer.Deserialize<PairFile>(File.ReadAllText(FilePath), JsonOptions);
        return data?.Pairs ?? new List<FolderPair>();
    }

    public void Save(IEnumerable<FolderPair> pairs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var data = new PairFile { Pairs = pairs.ToList() };
        string temp = FilePath + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }

    public static FolderPair? Find(IEnumerable<FolderPair> pairs, string name) =>
        pairs.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));

    private sealed class PairFile
    {
        public int Version { get; set; } = 1;
        public List<FolderPair> Pairs { get; set; } = new();
    }
}
