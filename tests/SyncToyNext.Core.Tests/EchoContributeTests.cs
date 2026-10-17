using SyncToyNext.Core.Model;
using SyncToyNext.Core.Planning;
using static SyncToyNext.Core.Model.Side;

namespace SyncToyNext.Core.Tests;

public class EchoContributeTests
{
    [Fact]
    public void Echo_CopiesLeftToRight_AndLeavesRightOnlyFilesAlone()
    {
        using var t = new TestFolders(SyncAction.Echo);
        t.Write(Left, "a.txt", "A");
        t.Write(Right, "only-right.txt", "R");

        t.Run();

        Assert.Equal("A", t.Read(Right, "a.txt"));
        Assert.False(t.Exists(Left, "only-right.txt"));
        Assert.True(t.Exists(Right, "only-right.txt"));
        Assert.Empty(t.Preview().Operations);
    }

    [Fact]
    public void Echo_FirstRun_LeftVersionWins()
    {
        using var t = new TestFolders(SyncAction.Echo);
        t.Write(Left, "a.txt", "left", minutes: 1);
        t.Write(Right, "a.txt", "right-newer", minutes: 50);

        t.Run();

        Assert.Equal("left", t.Read(Right, "a.txt"));
    }

    [Fact]
    public void Echo_DeleteOnLeft_IsRepeatedOnRight()
    {
        using var t = new TestFolders(SyncAction.Echo);
        t.Write(Left, "a.txt", "A");
        t.Write(Left, "dir/b.txt", "B");
        t.Run();

        t.Delete(Left, "a.txt");
        Directory.Delete(t.PathOf(Left, "dir"), recursive: true);
        t.Run();

        Assert.False(t.Exists(Right, "a.txt"));
        Assert.False(t.DirExists(Right, "dir"));
    }

    [Fact]
    public void Echo_RenameOnLeft_IsRepeatedOnRight()
    {
        using var t = new TestFolders(SyncAction.Echo);
        t.Write(Left, "a.txt", "A");
        t.Run();

        t.Move(Left, "a.txt", "b.txt");
        var plan = t.Preview();
        Assert.Single(plan.Operations, o => o.Kind == OperationKind.Rename);

        t.Run();
        Assert.False(t.Exists(Right, "a.txt"));
        Assert.Equal("A", t.Read(Right, "b.txt"));
    }

    [Fact]
    public void Echo_ChangesOnRight_AreIgnored()
    {
        using var t = new TestFolders(SyncAction.Echo);
        t.Write(Left, "edited.txt", "orig");
        t.Write(Left, "deleted.txt", "orig");
        t.Run();

        t.Write(Right, "edited.txt", "edited on right", minutes: 30);
        t.Delete(Right, "deleted.txt");

        Assert.Empty(t.Preview().Operations);
        t.Run();
        Assert.Equal("edited on right", t.Read(Right, "edited.txt"));
        Assert.False(t.Exists(Right, "deleted.txt"));

        // Auch bei weiteren Läufen bleibt es dabei.
        t.Run();
        Assert.False(t.Exists(Right, "deleted.txt"));
        Assert.Empty(t.Preview().Operations);
    }

    [Fact]
    public void Echo_FolderDeletedOnRight_IsNotRecreated()
    {
        using var t = new TestFolders(SyncAction.Echo);
        t.Mkdir(Left, "dir");
        t.Run();
        Assert.True(t.DirExists(Right, "dir"));

        Directory.Delete(t.PathOf(Right, "dir"));
        t.Run();
        t.Run();

        Assert.False(t.DirExists(Right, "dir"));
    }

    [Fact]
    public void Echo_FailedCopy_IsRetriedNextTime()
    {
        using var t = new TestFolders(SyncAction.Echo);
        t.Write(Left, "a.txt", "A");
        t.Write(Left, "b.txt", "B");
        var plan = t.Preview();
        plan.Operations.Single(o => o.TargetPath == "b.txt").IsSelected = false;
        t.Engine.Run(plan);

        // Auch nach mehreren Läufen ohne b.txt wird die Datei weiter angeboten.
        var next = t.Preview();
        Assert.Equal("b.txt", Assert.Single(next.Operations).TargetPath);
    }

    [Fact]
    public void Echo_LeftChangeOverwritesRightEdit()
    {
        using var t = new TestFolders(SyncAction.Echo);
        t.Write(Left, "a.txt", "orig");
        t.Run();

        t.Write(Right, "a.txt", "right edit", minutes: 30);
        t.Write(Left, "a.txt", "left edit", minutes: 10);
        t.Run();

        Assert.Equal("left edit", t.Read(Right, "a.txt"));
    }

    [Fact]
    public void Echo_ModifiedRightFile_IsNotDeleted_WhenDeletedOnLeft()
    {
        using var t = new TestFolders(SyncAction.Echo);
        t.Write(Left, "a.txt", "orig");
        t.Run();

        t.Delete(Left, "a.txt");
        t.Write(Right, "a.txt", "changed on right", minutes: 30);
        t.Run();

        Assert.Equal("changed on right", t.Read(Right, "a.txt"));
    }

    [Fact]
    public void Contribute_DoesNotDelete_ButCopiesAndRenames()
    {
        using var t = new TestFolders(SyncAction.Contribute);
        t.Write(Left, "keep.txt", "K", minutes: 1);
        t.Write(Left, "rename.txt", "R", minutes: 2);
        t.Run();

        t.Delete(Left, "keep.txt");
        t.Move(Left, "rename.txt", "renamed.txt");
        t.Write(Left, "new.txt", "N");
        var plan = t.Preview();
        Assert.DoesNotContain(plan.Operations, o => o.Kind is OperationKind.Delete or OperationKind.DeleteFolder);

        t.Run();
        Assert.True(t.Exists(Right, "keep.txt"));
        Assert.False(t.Exists(Right, "rename.txt"));
        Assert.Equal("R", t.Read(Right, "renamed.txt"));
        Assert.Equal("N", t.Read(Right, "new.txt"));
    }
}
