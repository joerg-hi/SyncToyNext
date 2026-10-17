using SyncToyNext.Core.Model;

namespace SyncToyNext.Core.Scanning;

/// <summary>
/// Liest den aktuellen Inhalt eines Wurzelordners rekursiv ein.
/// </summary>
public static class FolderScanner
{
    public static Dictionary<string, FileEntry> Scan(
        string root,
        FileFilter filter,
        ICollection<string>? warnings = null,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, FileEntry>(PathUtil.Comparer);
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists)
        {
            throw new DirectoryNotFoundException($"Der Ordner \"{root}\" existiert nicht oder ist nicht erreichbar.");
        }

        var pending = new Stack<(DirectoryInfo Dir, string Relative)>();
        pending.Push((rootInfo, ""));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, relative) = pending.Pop();

            IEnumerable<FileSystemInfo> children;
            try
            {
                // Die Liste wird sofort vollständig gelesen, damit Zugriffsfehler hier abgefangen werden.
                children = dir.EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    AttributesToSkip = 0,
                    IgnoreInaccessible = false,
                    ReturnSpecialDirectories = false,
                }).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                if (relative.Length == 0) throw;
                warnings?.Add($"Ordner \"{PathUtil.ToDisplay(relative)}\" konnte nicht gelesen werden: {ex.Message}");
                continue;
            }

            foreach (var child in children)
            {
                string childRelative = relative.Length == 0 ? child.Name : relative + "/" + child.Name;
                FileAttributes attributes = child.Attributes;

                if (child is DirectoryInfo childDir)
                {
                    if (!filter.IncludeDirectory(childRelative, attributes)) continue;
                    result[childRelative] = new FileEntry(childRelative, true, 0, default, attributes);
                    pending.Push((childDir, childRelative));
                }
                else if (child is FileInfo file)
                {
                    if (!filter.IncludeFile(childRelative, attributes)) continue;
                    result[childRelative] = new FileEntry(
                        childRelative, false, file.Length, file.LastWriteTimeUtc, attributes);
                }
            }
        }

        return result;
    }
}
