using SyncToyNext.Core.Model;

namespace SyncToyNext.App;

/// <summary>
/// Eintrag in der Liste der Ordnerpaare.
/// </summary>
public sealed class PairItem
{
    public PairItem(FolderPair pair) => Pair = pair;

    public FolderPair Pair { get; }

    public string Name => Pair.Name;

    public string Glyph => Glyphs.ForAction(Pair.Action);

    public string Subtitle => Pair.Options.ActiveForRunAll
        ? UiHelpers.ActionTitle(Pair.Action)
        : $"{UiHelpers.ActionTitle(Pair.Action)} · nicht bei „Alle ausführen“";
}
