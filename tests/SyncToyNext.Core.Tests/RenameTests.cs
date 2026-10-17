using SyncToyNext.Core.Model;
using SyncToyNext.Core.Planning;
using static SyncToyNext.Core.Model.Side;

namespace SyncToyNext.Core.Tests;

/// <summary>
/// Ordner-Umbenennungen in einem Schritt und reine Änderungen der Groß-/Kleinschreibung.
/// </summary>
public class RenameTests
{
    private static void WriteAlbum(TestFolders t, Side side, string folder)
    {
        t.Write(side, $"{folder}/1.jpg", "one", minutes: 1);
        t.Write(side, $"{folder}/2.jpg", "two!", minutes: 2);
        t.Write(side, $"{folder}/sub/3.jpg", "three", minutes: 3);
    }

    [Fact]
    public void FolderRenamedOnLeft_IsRenamedOnRight_InOneStep()
    {
        using var t = new TestFolders();
        WriteAlbum(t, Left, "Urlaub");
        t.Run();

        Directory.Move(t.PathOf(Left, "Urlaub"), t.PathOf(Left, "Urlaub 2024"));
        var plan = t.Preview();

        var op = Assert.Single(plan.Operations);
        Assert.Equal(OperationKind.RenameFolder, op.Kind);
        Assert.Equal(Right, op.TargetSide);
        Assert.Equal("Urlaub", op.SourcePath);
        Assert.Equal("Urlaub 2024", op.TargetPath);

        t.Run();
        Assert.False(t.DirExists(Right, "Urlaub"));
        Assert.Equal("three", t.Read(Right, "Urlaub 2024/sub/3.jpg"));
        Assert.Empty(t.Preview().Operations);
    }

    [Fact]
    public void FolderMovedIntoOtherFolder_IsMovedOnOtherSide()
    {
        using var t = new TestFolders();
        WriteAlbum(t, Left, "Fotos/2023");
        t.Run();

        t.Mkdir(Right, "Archiv");
        Directory.Move(t.PathOf(Right, "Fotos/2023"), t.PathOf(Right, "Archiv/2023"));
        var plan = t.Preview();

        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.RenameFolder && o.TargetSide == Left);
        Assert.DoesNotContain(plan.Operations, o => o.Kind is OperationKind.Copy or OperationKind.Delete);

        t.Run();
        Assert.Equal("one", t.Read(Left, "Archiv/2023/1.jpg"));
        Assert.False(t.DirExists(Left, "Fotos/2023"));
        Assert.Empty(t.Preview().Operations);
    }

    [Fact]
    public void FolderRenamed_AndFileInsideChanged_RenamesThenCopiesChange()
    {
        using var t = new TestFolders();
        WriteAlbum(t, Left, "A");
        t.Run();

        Directory.Move(t.PathOf(Left, "A"), t.PathOf(Left, "B"));
        t.Write(Left, "B/1.jpg", "one edited", minutes: 50);
        var plan = t.Preview();

        Assert.Equal(OperationKind.RenameFolder, plan.Operations[0].Kind);
        var copy = Assert.Single(plan.Operations, o => o.Kind == OperationKind.Overwrite);
        Assert.Equal("B/1.jpg", copy.TargetPath);

        t.Run();
        Assert.Equal("one edited", t.Read(Right, "B/1.jpg"));
        Assert.False(t.DirExists(Right, "A"));
    }

    [Fact]
    public void Echo_FolderRenamedOnLeft_IsRenamedOnRight()
    {
        using var t = new TestFolders(SyncAction.Echo);
        WriteAlbum(t, Left, "Projekt");
        t.Run();

        Directory.Move(t.PathOf(Left, "Projekt"), t.PathOf(Left, "Projekt (fertig)"));
        Assert.Equal(OperationKind.RenameFolder, Assert.Single(t.Preview().Operations).Kind);

        t.Run();
        Assert.True(t.Exists(Right, "Projekt (fertig)/sub/3.jpg"));
        Assert.False(t.DirExists(Right, "Projekt"));
    }

    [Fact]
    public void Echo_FolderRenamedOnRight_IsIgnored()
    {
        using var t = new TestFolders(SyncAction.Echo);
        WriteAlbum(t, Left, "X");
        t.Run();

        Directory.Move(t.PathOf(Right, "X"), t.PathOf(Right, "Y"));
        Assert.Empty(t.Preview().Operations);
    }

    [Fact]
    public void DeselectedFolderRename_IsOfferedAgain()
    {
        using var t = new TestFolders();
        WriteAlbum(t, Left, "A");
        t.Run();

        Directory.Move(t.PathOf(Left, "A"), t.PathOf(Left, "B"));
        var plan = t.Preview();
        plan.Operations.Single().IsSelected = false;
        t.Engine.Run(plan);

        var again = Assert.Single(t.Preview().Operations);
        Assert.Equal(OperationKind.RenameFolder, again.Kind);
        Assert.Equal("B", again.TargetPath);
    }

    [Fact]
    public void FolderWithAddedFile_FallsBackToRenamingItsParts()
    {
        using var t = new TestFolders();
        WriteAlbum(t, Left, "A");
        t.Run();

        Directory.Move(t.PathOf(Left, "A"), t.PathOf(Left, "B"));
        t.Write(Left, "B/extra.txt", "new file");
        var plan = t.Preview();

        // "B" ist wegen der zusätzlichen Datei keine reine Umbenennung von "A". Der Unterordner und die
        // vorhandenen Dateien werden trotzdem verschoben; kopiert wird nur die neue Datei.
        Assert.DoesNotContain(plan.Operations, o => o.Kind == OperationKind.RenameFolder && o.TargetPath == "B");
        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.RenameFolder && o.TargetPath == "B/sub");
        var copy = Assert.Single(plan.Operations, o => o.Kind is OperationKind.Copy or OperationKind.Overwrite);
        Assert.Equal("B/extra.txt", copy.TargetPath);

        t.Run();
        Assert.True(t.Exists(Right, "B/extra.txt"));
        Assert.True(t.Exists(Right, "B/sub/3.jpg"));
        Assert.False(t.DirExists(Right, "A"));
        Assert.Empty(t.Preview().Operations);
    }

    [Fact]
    public void CaseOnlyFileRename_IsRepeatedOnOtherSide()
    {
        using var t = new TestFolders();
        t.Write(Left, "bericht.txt", "inhalt");
        t.Run();

        t.Move(Left, "bericht.txt", "Bericht.txt");
        var op = Assert.Single(t.Preview().Operations);
        Assert.Equal(OperationKind.Rename, op.Kind);
        Assert.Equal("bericht.txt", op.SourcePath);
        Assert.Equal("Bericht.txt", op.TargetPath);

        t.Run();
        Assert.Contains("Bericht.txt", Directory.GetFiles(t.Right).Select(Path.GetFileName));
        Assert.DoesNotContain("bericht.txt", Directory.GetFiles(t.Right).Select(Path.GetFileName));
        Assert.Empty(t.Preview().Operations);
    }

    [Fact]
    public void CaseOnlyFolderRename_IsRepeatedOnOtherSide()
    {
        using var t = new TestFolders(SyncAction.Echo);
        t.Write(Left, "fotos/a.jpg", "a");
        t.Run();

        Directory.Move(t.PathOf(Left, "fotos"), t.PathOf(Left, "Fotos"));
        var op = Assert.Single(t.Preview().Operations);
        Assert.Equal(OperationKind.RenameFolder, op.Kind);

        t.Run();
        Assert.Contains("Fotos", Directory.GetDirectories(t.Right).Select(Path.GetFileName));
        Assert.Empty(t.Preview().Operations);
    }

    [Fact]
    public void DifferentCaseOnFirstRun_IsLeftAlone()
    {
        using var t = new TestFolders();
        t.Write(Left, "a.txt", "same", minutes: 1);
        t.Write(Right, "A.txt", "same", minutes: 1);

        Assert.Empty(t.Preview().Operations);
    }
}
