using SyncToyNext.Core.Model;

namespace SyncToyNext.Core.Planning;

/// <summary>
/// Vergleicht die aktuellen Stände beider Seiten mit den Ständen der letzten Synchronisation
/// und leitet daraus die nötigen Aktionen ab. Der Planner verändert keine Dateien.
/// </summary>
/// <remarks>
/// Eine Datei gilt auf einer Seite als „geändert“, wenn sie dort neu ist oder sich Größe bzw.
/// Änderungszeit gegenüber dem gespeicherten Stand dieser Seite verändert haben.
/// Ohne gespeicherten Stand (erste Synchronisation) gilt jede Datei als geändert.
/// </remarks>
public static class SyncPlanner
{
    /// <param name="scanWarnings">Hinweise aus dem Einlesen der Ordner, die in den Plan übernommen werden.</param>
    /// <param name="contentEqual">
    /// Vergleicht die Inhalte einer linken und einer rechten Datei. Wird nur verwendet,
    /// wenn die Option „Dateiinhalte prüfen“ aktiv ist.
    /// </param>
    public static SyncPlan CreatePlan(
        FolderPair pair,
        IReadOnlyDictionary<string, FileEntry> left,
        IReadOnlyDictionary<string, FileEntry> right,
        IReadOnlyDictionary<string, FileEntry>? leftSnapshot,
        IReadOnlyDictionary<string, FileEntry>? rightSnapshot,
        Func<FileEntry, FileEntry, bool>? contentEqual = null,
        IEnumerable<string>? scanWarnings = null)
    {
        var builder = new Builder(pair, left, right, leftSnapshot, rightSnapshot,
            pair.Options.CheckFileContents ? contentEqual : null);
        if (scanWarnings is not null) builder.AddWarnings(scanWarnings);
        return builder.Build();
    }

    private sealed class Builder
    {
        private readonly FolderPair _pair;
        private readonly SyncAction _action;
        private readonly Func<FileEntry, FileEntry, bool>? _contentEqual;
        private readonly IReadOnlyDictionary<string, FileEntry> _origLeft;
        private readonly IReadOnlyDictionary<string, FileEntry> _origRight;
        private readonly IReadOnlyDictionary<string, FileEntry>? _origLeftSnap;
        private readonly IReadOnlyDictionary<string, FileEntry>? _origRightSnap;

        // Arbeitskopien. Sie werden bei Umbenennungen angepasst, damit der anschließende
        // Pfad-für-Pfad-Vergleich den Zustand nach der Umbenennung sieht.
        private readonly Dictionary<string, FileEntry> _left;
        private readonly Dictionary<string, FileEntry> _right;
        private readonly Dictionary<string, FileEntry> _leftSnap;
        private readonly Dictionary<string, FileEntry> _rightSnap;

        private readonly List<SyncOperation> _createFolders = new();
        private readonly List<SyncOperation> _renames = new();
        private readonly List<SyncOperation> _deletes = new();
        private readonly List<SyncOperation> _copies = new();
        private readonly List<SyncOperation> _deleteFolders = new();
        private readonly List<string> _warnings = new();

        public Builder(
            FolderPair pair,
            IReadOnlyDictionary<string, FileEntry> left,
            IReadOnlyDictionary<string, FileEntry> right,
            IReadOnlyDictionary<string, FileEntry>? leftSnapshot,
            IReadOnlyDictionary<string, FileEntry>? rightSnapshot,
            Func<FileEntry, FileEntry, bool>? contentEqual)
        {
            _pair = pair.Clone();
            _action = pair.Action;
            _contentEqual = contentEqual;
            _origLeft = left;
            _origRight = right;
            _origLeftSnap = leftSnapshot;
            _origRightSnap = rightSnapshot;
            _left = new Dictionary<string, FileEntry>(left, PathUtil.Comparer);
            _right = new Dictionary<string, FileEntry>(right, PathUtil.Comparer);
            _leftSnap = leftSnapshot is null
                ? new Dictionary<string, FileEntry>(PathUtil.Comparer)
                : new Dictionary<string, FileEntry>(leftSnapshot, PathUtil.Comparer);
            _rightSnap = rightSnapshot is null
                ? new Dictionary<string, FileEntry>(PathUtil.Comparer)
                : new Dictionary<string, FileEntry>(rightSnapshot, PathUtil.Comparer);
        }

        public void AddWarnings(IEnumerable<string> warnings) => _warnings.AddRange(warnings);

        public SyncPlan Build()
        {
            PlanRenames(Side.Left);
            if (_action == SyncAction.Synchronize)
            {
                PlanRenames(Side.Right);
            }

            PlanFiles();
            PlanFolders();

            var operations = new List<SyncOperation>();
            operations.AddRange(_createFolders.OrderBy(o => PathUtil.Depth(o.TargetPath))
                .ThenBy(o => o.TargetPath, StringComparer.OrdinalIgnoreCase));
            operations.AddRange(_renames);
            operations.AddRange(_deletes.OrderBy(o => o.TargetPath, StringComparer.OrdinalIgnoreCase));
            operations.AddRange(_copies.OrderBy(o => o.TargetPath, StringComparer.OrdinalIgnoreCase));
            operations.AddRange(_deleteFolders.OrderByDescending(o => PathUtil.Depth(o.TargetPath))
                .ThenBy(o => o.TargetPath, StringComparer.OrdinalIgnoreCase));

            return new SyncPlan(_pair, operations, _warnings, CheckMassDeletion(operations),
                _origLeftSnap, _origRightSnap, _origLeft.Count, _origRight.Count);
        }

        // ------------------------------------------------------------------ Umbenennungen

        /// <summary>
        /// Erkennt auf der Seite <paramref name="side"/> umbenannte oder verschobene Dateien:
        /// Eine Datei fehlt gegenüber dem gespeicherten Stand und es gibt genau eine neue Datei
        /// mit identischer Größe und Änderungszeit. Leere Dateien werden nicht zugeordnet,
        /// weil sie nicht eindeutig sind.
        /// </summary>
        private void PlanRenames(Side side)
        {
            var current = Files(side);
            var snap = Snap(side);
            if (snap.Count == 0) return;

            static (long, long) Key(FileEntry e) => (e.Size, e.LastWriteTimeUtc.Ticks);

            var removed = snap.Values
                .Where(e => !e.IsDirectory && e.Size > 0 && !current.ContainsKey(e.RelativePath))
                .GroupBy(Key)
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.Single());

            var added = current.Values
                .Where(e => !e.IsDirectory && e.Size > 0 && !snap.ContainsKey(e.RelativePath))
                .GroupBy(Key)
                .Where(g => g.Count() == 1)
                .Select(g => g.Single())
                .ToList();

            foreach (var newEntry in added)
            {
                if (removed.TryGetValue(Key(newEntry), out var oldEntry))
                {
                    TryPropagateRename(side, oldEntry.RelativePath, newEntry.RelativePath);
                }
            }
        }

        private void TryPropagateRename(Side side, string from, string to)
        {
            Side target = side.Other();
            var targetFiles = Files(target);
            var targetSnap = Snap(target);
            var sourceFiles = Files(side);
            var sourceSnap = Snap(side);

            // Die andere Seite muss die Datei noch unverändert unter dem alten Namen haben.
            if (!targetFiles.TryGetValue(from, out var targetEntry) || targetEntry.IsDirectory) return;
            if (targetFiles.ContainsKey(to)) return;
            if (!targetEntry.SameAsSnapshot(Get(targetSnap, from))) return;
            if (!targetEntry.SameMetadataAs(sourceFiles[to])) return;

            _renames.Add(new SyncOperation(OperationKind.Rename, target, to)
            {
                SourcePath = from,
                TargetEntry = targetEntry,
                SourceEntry = sourceFiles[to],
                Reason = side == Side.Left ? "Links umbenannt" : "Rechts umbenannt",
            });

            targetFiles.Remove(from);
            targetFiles[to] = targetEntry.WithPath(to);
            MoveSnapshotEntry(targetSnap, from, to);
            MoveSnapshotEntry(sourceSnap, from, to);
        }

        private static void MoveSnapshotEntry(Dictionary<string, FileEntry> snap, string from, string to)
        {
            if (snap.Remove(from, out var entry))
            {
                snap[to] = entry.WithPath(to);
            }
        }

        // ------------------------------------------------------------------ Dateien

        private void PlanFiles()
        {
            var paths = new HashSet<string>(PathUtil.Comparer);
            paths.UnionWith(_left.Where(kv => !kv.Value.IsDirectory).Select(kv => kv.Key));
            paths.UnionWith(_right.Where(kv => !kv.Value.IsDirectory).Select(kv => kv.Key));

            foreach (string path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var l = Get(_left, path);
                var r = Get(_right, path);

                if (l is { IsDirectory: true } || r is { IsDirectory: true })
                {
                    _warnings.Add($"Konflikt: \"{PathUtil.ToDisplay(path)}\" ist auf einer Seite eine Datei und auf der anderen ein Ordner. Wird übersprungen.");
                    continue;
                }

                var sl = FileOnly(Get(_leftSnap, path));
                var sr = FileOnly(Get(_rightSnap, path));

                if (_action == SyncAction.Synchronize)
                {
                    PlanSynchronize(path, l, r, sl, sr);
                }
                else
                {
                    PlanEchoOrContribute(path, l, r, sl, sr);
                }
            }
        }

        private void PlanSynchronize(string path, FileEntry? l, FileEntry? r, FileEntry? sl, FileEntry? sr)
        {
            bool lChanged = l is not null && !l.SameAsSnapshot(sl);
            bool rChanged = r is not null && !r.SameAsSnapshot(sr);

            if (l is not null && r is not null)
            {
                if (Identical(l, r, sl, sr)) return;

                if (lChanged && !rChanged)
                {
                    AddCopy(Side.Right, l, r, "Links geändert");
                }
                else if (rChanged && !lChanged)
                {
                    AddCopy(Side.Left, r, l, "Rechts geändert");
                }
                else
                {
                    // Beide geändert (oder beide unverändert, aber verschieden): Die neuere Datei gewinnt.
                    var diff = l.LastWriteTimeUtc - r.LastWriteTimeUtc;
                    if (diff.Duration() <= FileEntry.CrossVolumeTolerance)
                    {
                        _warnings.Add($"Konflikt: \"{PathUtil.ToDisplay(path)}\" ist auf beiden Seiten verschieden, hat aber dieselbe Änderungszeit. Wird übersprungen.");
                    }
                    else if (diff > TimeSpan.Zero)
                    {
                        AddCopy(Side.Right, l, r, lChanged ? "Beide geändert, links neuer" : "Verschieden, links neuer");
                    }
                    else
                    {
                        AddCopy(Side.Left, r, l, rChanged ? "Beide geändert, rechts neuer" : "Verschieden, rechts neuer");
                    }
                }
            }
            else if (l is not null)
            {
                if (sr is not null && !lChanged)
                {
                    AddDelete(Side.Left, l, "Rechts gelöscht");
                }
                else
                {
                    AddCopy(Side.Right, l, null, sr is not null ? "Rechts gelöscht, aber links geändert" : "Neu links");
                }
            }
            else if (r is not null)
            {
                if (sl is not null && !rChanged)
                {
                    AddDelete(Side.Right, r, "Links gelöscht");
                }
                else
                {
                    AddCopy(Side.Left, r, null, sl is not null ? "Links gelöscht, aber rechts geändert" : "Neu rechts");
                }
            }
        }

        /// <summary>
        /// Echo und Contribute reagieren nur auf Änderungen im linken Ordner.
        /// Rechts geänderte oder gelöschte Dateien werden nicht angetastet bzw. nicht wiederhergestellt.
        /// </summary>
        private void PlanEchoOrContribute(string path, FileEntry? l, FileEntry? r, FileEntry? sl, FileEntry? sr)
        {
            bool lChanged = l is not null && !l.SameAsSnapshot(sl);
            bool rChanged = r is not null && !r.SameAsSnapshot(sr);

            if (l is not null && r is not null)
            {
                if (Identical(l, r, sl, sr)) return;
                if (lChanged)
                {
                    AddCopy(Side.Right, l, r, sl is null ? "Links neu bzw. erste Synchronisation" : "Links geändert");
                }
            }
            else if (l is not null)
            {
                // Nur kopieren, wenn die Datei links neu oder geändert ist. Hat jemand sie rechts gelöscht
                // und links ist sie unverändert, bleibt es dabei. Fehlgeschlagene oder abgewählte Kopien
                // gelten weiter als "links geändert", weil ihr alter Stand erhalten bleibt (siehe SyncEngine).
                if (lChanged)
                {
                    AddCopy(Side.Right, l, null, "Neu links");
                }
            }
            else if (r is not null)
            {
                if (_action == SyncAction.Echo && sl is not null && !rChanged)
                {
                    AddDelete(Side.Right, r, "Links gelöscht");
                }
            }
        }

        private bool Identical(FileEntry l, FileEntry r, FileEntry? sl, FileEntry? sr)
        {
            if (l.Size != r.Size) return false;
            if (_contentEqual is null) return l.SameMetadataAs(r);

            // Beide seit dem letzten Lauf unverändert und gleich datiert: kein erneuter Inhaltsvergleich nötig.
            if (l.SameMetadataAs(r) && l.SameAsSnapshot(sl) && r.SameAsSnapshot(sr)) return true;
            return _contentEqual(l, r);
        }

        private void AddCopy(Side target, FileEntry source, FileEntry? existingTarget, string reason)
        {
            _copies.Add(new SyncOperation(existingTarget is null ? OperationKind.Copy : OperationKind.Overwrite,
                target, source.RelativePath)
            {
                SourcePath = source.RelativePath,
                SourceEntry = source,
                TargetEntry = existingTarget,
                Reason = reason,
            });
        }

        private void AddDelete(Side target, FileEntry existing, string reason)
        {
            _deletes.Add(new SyncOperation(OperationKind.Delete, target, existing.RelativePath)
            {
                TargetEntry = existing,
                Reason = reason,
            });
        }

        // ------------------------------------------------------------------ Ordner

        private void PlanFolders()
        {
            var paths = new HashSet<string>(PathUtil.Comparer);
            paths.UnionWith(_left.Where(kv => kv.Value.IsDirectory).Select(kv => kv.Key));
            paths.UnionWith(_right.Where(kv => kv.Value.IsDirectory).Select(kv => kv.Key));

            var deleteCandidates = new List<(Side Side, string Path, string Reason)>();

            foreach (string path in paths)
            {
                var l = Get(_left, path);
                var r = Get(_right, path);
                if (l is not null && r is not null) continue; // vorhanden oder Typkonflikt (siehe PlanFiles)

                bool inLeftSnap = Get(_leftSnap, path) is { IsDirectory: true };
                bool inRightSnap = Get(_rightSnap, path) is { IsDirectory: true };
                bool wasSynced = inLeftSnap && inRightSnap;

                if (l is not null)
                {
                    if (_action == SyncAction.Synchronize)
                    {
                        if (wasSynced) deleteCandidates.Add((Side.Left, path, "Rechts gelöscht"));
                        else AddCreateFolder(Side.Right, path, "Neu links");
                    }
                    else if (!inLeftSnap)
                    {
                        // Echo/Contribute: nur links neue Ordner anlegen, rechts gelöschte nicht wiederherstellen.
                        AddCreateFolder(Side.Right, path, "Neu links");
                    }
                }
                else if (r is not null)
                {
                    if (_action == SyncAction.Synchronize)
                    {
                        if (wasSynced) deleteCandidates.Add((Side.Right, path, "Links gelöscht"));
                        else AddCreateFolder(Side.Left, path, "Neu rechts");
                    }
                    else if (_action == SyncAction.Echo && wasSynced)
                    {
                        deleteCandidates.Add((Side.Right, path, "Links gelöscht"));
                    }
                }
            }

            // Ein Ordner wird nur gelöscht, wenn nach allen Dateiaktionen nichts mehr darin übrig bleibt.
            // "Blockiert" sind alle Ordner, in denen danach noch eine Datei oder ein Unterordner liegt.
            var blockedLeft = BlockedFolders(Side.Left, deleteCandidates);
            var blockedRight = BlockedFolders(Side.Right, deleteCandidates);

            foreach (var (side, path, reason) in deleteCandidates.OrderByDescending(c => PathUtil.Depth(c.Path)))
            {
                var blocked = side == Side.Left ? blockedLeft : blockedRight;

                if (!blocked.Contains(path))
                {
                    _deleteFolders.Add(new SyncOperation(OperationKind.DeleteFolder, side, path)
                    {
                        TargetEntry = Files(side)[path],
                        Reason = reason,
                    });
                }
                else
                {
                    // Der Ordner bleibt, also bleiben auch alle übergeordneten Ordner.
                    AddAncestors(blocked, path);
                    if (_action == SyncAction.Synchronize)
                    {
                        AddCreateFolder(side.Other(), path, "Ordner enthält noch Dateien");
                    }
                }
            }
        }

        private HashSet<string> BlockedFolders(Side side, List<(Side Side, string Path, string Reason)> candidates)
        {
            var candidatePaths = new HashSet<string>(
                candidates.Where(c => c.Side == side).Select(c => c.Path), PathUtil.Comparer);
            var blocked = new HashSet<string>(PathUtil.Comparer);

            foreach (string file in RemainingFiles(side))
            {
                AddAncestors(blocked, file);
            }

            foreach (var entry in Files(side).Values)
            {
                if (entry.IsDirectory && !candidatePaths.Contains(entry.RelativePath))
                {
                    AddAncestors(blocked, entry.RelativePath);
                }
            }

            return blocked;
        }

        private static void AddAncestors(HashSet<string> set, string path)
        {
            for (string? parent = PathUtil.GetParent(path); parent is not null; parent = PathUtil.GetParent(parent))
            {
                if (!set.Add(parent)) break; // übergeordnete Ordner sind dann bereits enthalten
            }
        }

        private void AddCreateFolder(Side target, string path, string reason)
        {
            _createFolders.Add(new SyncOperation(OperationKind.CreateFolder, target, path)
            {
                SourcePath = path,
                Reason = reason,
            });
        }

        private HashSet<string> RemainingFiles(Side side)
        {
            var result = new HashSet<string>(
                Files(side).Values.Where(e => !e.IsDirectory).Select(e => e.RelativePath), PathUtil.Comparer);
            foreach (var op in _deletes.Where(o => o.TargetSide == side)) result.Remove(op.TargetPath);
            foreach (var op in _copies.Where(o => o.TargetSide == side)) result.Add(op.TargetPath);
            return result;
        }

        // ------------------------------------------------------------------ Sicherheitsprüfung

        /// <summary>
        /// Ist eine Seite komplett leer, obwohl sie beim letzten Lauf Inhalt hatte, deutet das eher auf
        /// ein falsches oder leeres Laufwerk hin als auf eine gewollte Löschung. Dann muss der Benutzer bestätigen.
        /// </summary>
        private string? CheckMassDeletion(List<SyncOperation> operations)
        {
            foreach (Side side in new[] { Side.Left, Side.Right })
            {
                var current = side == Side.Left ? _origLeft : _origRight;
                var snap = side == Side.Left ? _origLeftSnap : _origRightSnap;
                if (current.Count > 0 || snap is null || snap.Count == 0) continue;

                int deletes = operations.Count(o =>
                    o.TargetSide == side.Other() && o.Kind is OperationKind.Delete or OperationKind.DeleteFolder);
                if (deletes == 0) continue;

                string folder = side == Side.Left ? _pair.LeftPath : _pair.RightPath;
                return $"Der {(side == Side.Left ? "linke" : "rechte")} Ordner \"{folder}\" ist leer, " +
                       $"enthielt beim letzten Lauf aber {snap.Count} Einträge. " +
                       $"Die Ausführung würde {deletes} Einträge auf der anderen Seite löschen.";
            }

            return null;
        }

        // ------------------------------------------------------------------ Hilfen

        private Dictionary<string, FileEntry> Files(Side side) => side == Side.Left ? _left : _right;

        private Dictionary<string, FileEntry> Snap(Side side) => side == Side.Left ? _leftSnap : _rightSnap;

        private static FileEntry? Get(IReadOnlyDictionary<string, FileEntry> dict, string path) =>
            dict.TryGetValue(path, out var e) ? e : null;

        private static FileEntry? FileOnly(FileEntry? e) => e is { IsDirectory: false } ? e : null;
    }
}
