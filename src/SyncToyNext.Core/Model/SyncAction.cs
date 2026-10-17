namespace SyncToyNext.Core.Model;

/// <summary>
/// Die drei Synchronisationsarten von SyncToy.
/// </summary>
public enum SyncAction
{
    /// <summary>
    /// Neue, geänderte, umbenannte und gelöschte Dateien werden in beide Richtungen übernommen.
    /// </summary>
    Synchronize,

    /// <summary>
    /// Neue und geänderte Dateien werden von links nach rechts kopiert.
    /// Umbenennungen und Löschungen links werden rechts wiederholt.
    /// </summary>
    Echo,

    /// <summary>
    /// Wie <see cref="Echo"/>, aber ohne Löschungen.
    /// </summary>
    Contribute,
}

public enum Side
{
    Left,
    Right,
}

public static class SideExtensions
{
    public static Side Other(this Side side) => side == Side.Left ? Side.Right : Side.Left;
}
