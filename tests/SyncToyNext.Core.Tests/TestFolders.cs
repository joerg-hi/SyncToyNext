using SyncToyNext.Core.Execution;
using SyncToyNext.Core.Model;
using SyncToyNext.Core.Planning;

namespace SyncToyNext.Core.Tests;

/// <summary>
/// Legt für jeden Test einen linken und rechten Ordner sowie einen Ersatz-Papierkorb an.
/// </summary>
public sealed class TestFolders : IDisposable
{
    private static readonly DateTime BaseTime = new(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public TestFolders(SyncAction action = SyncAction.Synchronize)
    {
        Root = Path.Combine(Path.GetTempPath(), "stn-tests", Guid.NewGuid().ToString("N"));
        Left = Path.Combine(Root, "left");
        Right = Path.Combine(Root, "right");
        Trash = Path.Combine(Root, "trash");
        Directory.CreateDirectory(Left);
        Directory.CreateDirectory(Right);
        Directory.CreateDirectory(Trash);

        RecycleBin = new FakeRecycleBin(Trash);
        Engine = new SyncEngine(RecycleBin);
        Pair = new FolderPair { Name = "Test", LeftPath = Left, RightPath = Right, Action = action };
    }

    public string Root { get; }
    public string Left { get; }
    public string Right { get; }
    public string Trash { get; }
    public FakeRecycleBin RecycleBin { get; }
    public SyncEngine Engine { get; }
    public FolderPair Pair { get; }

    public string PathOf(Side side, string relative) =>
        PathUtil.ToFullPath(side == Side.Left ? Left : Right, relative);

    /// <summary>Schreibt eine Datei mit festem Inhalt und Zeitstempel (Minuten nach dem Basiszeitpunkt).</summary>
    public void Write(Side side, string relative, string content, int minutes = 0)
    {
        string path = PathOf(side, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, BaseTime.AddMinutes(minutes));
    }

    public void Mkdir(Side side, string relative) => Directory.CreateDirectory(PathOf(side, relative));

    public string? Read(Side side, string relative)
    {
        string path = PathOf(side, relative);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public bool Exists(Side side, string relative) => File.Exists(PathOf(side, relative));

    public bool DirExists(Side side, string relative) => Directory.Exists(PathOf(side, relative));

    public void Delete(Side side, string relative) => File.Delete(PathOf(side, relative));

    public void Move(Side side, string from, string to)
    {
        string target = PathOf(side, to);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(PathOf(side, from), target);
    }

    public SyncPlan Preview() => Engine.Preview(Pair);

    public SyncResult Run()
    {
        var result = Engine.PreviewAndRun(Pair);
        Assert.Equal(0, result.Failed);
        return result;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class FakeRecycleBin : IRecycleBin
{
    private readonly string _trash;

    public FakeRecycleBin(string trash) => _trash = trash;

    public List<string> Recycled { get; } = new();

    public void MoveToRecycleBin(string fullPath)
    {
        Recycled.Add(fullPath);
        File.Move(fullPath, Path.Combine(_trash, $"{Recycled.Count}_{Path.GetFileName(fullPath)}"));
    }
}
