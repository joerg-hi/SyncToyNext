namespace SyncToyNext.Core.Model;

/// <summary>
/// Hilfen für Bestände (relativer Pfad → Eintrag), z. B. beim Nachführen von Umbenennungen.
/// </summary>
internal static class EntryTree
{
    /// <summary>
    /// Verschiebt mehrere Teilbäume in einem einzigen Durchlauf (alter Ordnerpfad → neuer Ordnerpfad).
    /// Liegt ein Eintrag unter mehreren Quellordnern, gilt der am tiefsten liegende.
    /// </summary>
    public static void MoveTrees(Dictionary<string, FileEntry> entries, IReadOnlyDictionary<string, string> moves)
    {
        if (moves.Count == 0) return;
        var changes = new List<(string OldKey, string NewKey, FileEntry Entry)>();
        foreach (var (key, entry) in entries)
        {
            for (string? p = key; p is not null; p = PathUtil.GetParent(p))
            {
                if (moves.TryGetValue(p, out string? to))
                {
                    string newKey = to + key[p.Length..];
                    changes.Add((key, newKey, entry.WithPath(newKey)));
                    break;
                }
            }
        }

        foreach (var change in changes) entries.Remove(change.OldKey);
        foreach (var change in changes) entries[change.NewKey] = change.Entry;
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
