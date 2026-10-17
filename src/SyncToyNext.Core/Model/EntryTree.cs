namespace SyncToyNext.Core.Model;

/// <summary>
/// Hilfen für Bestände (relativer Pfad → Eintrag), z. B. beim Nachführen von Umbenennungen.
/// </summary>
internal static class EntryTree
{
    /// <summary>Verschiebt einen Ordner samt allem darunter auf einen neuen Pfad.</summary>
    public static void MoveTree(Dictionary<string, FileEntry> entries, string from, string to)
    {
        var keys = entries.Keys.Where(k => PathUtil.IsSameOrBelow(k, from)).ToList();
        var moved = keys.Select(k => entries[k]).ToList();
        foreach (string key in keys) entries.Remove(key);
        foreach (var entry in moved)
        {
            string newPath = to + entry.RelativePath[from.Length..];
            entries[newPath] = entry.WithPath(newPath);
        }
    }

    /// <summary>Ergänzt fehlende übergeordnete Ordner eines Pfads.</summary>
    public static void EnsureAncestors(Dictionary<string, FileEntry> entries, string path)
    {
        for (string? parent = PathUtil.GetParent(path); parent is not null; parent = PathUtil.GetParent(parent))
        {
            if (entries.ContainsKey(parent)) break;
            entries[parent] = new FileEntry(parent, true, 0, default, FileAttributes.Directory);
        }
    }
}
