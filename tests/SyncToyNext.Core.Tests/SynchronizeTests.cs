using SyncToyNext.Core.Model;
using SyncToyNext.Core.Planning;
using static SyncToyNext.Core.Model.Side;

namespace SyncToyNext.Core.Tests;

public class SynchronizeTests
{
    [Fact]
    public void FirstRun_CopiesNewFilesInBothDirections()
    {
        using var t = new TestFolders();
        t.Write(Left, "a.txt", "A");
        t.Write(Right, "sub/b.txt", "B");

        t.Run();

        Assert.Equal("A", t.Read(Right, "a.txt"));
        Assert.Equal("B", t.Read(Left, "sub/b.txt"));
        Assert.Empty(t.Preview().Operations);
    }

    [Fact]
    public void CopiedFile_KeepsTimestamp()
    {
        using var t = new TestFolders();
        t.Write(Left, "a.txt", "A", minutes: 42);

        t.Run();

        Assert.Equal(File.GetLastWriteTimeUtc(t.PathOf(Left, "a.txt")), File.GetLastWriteTimeUtc(t.PathOf(Right, "a.txt")));
    }

    [Fact]
    public void ModifiedOnLeft_IsCopiedToRight_OldVersionGoesToRecycleBin()
    {
        using var t = new TestFolders();
        t.Write(Left, "a.txt", "v1");
        t.Run();

        t.Write(Left, "a.txt", "v2-longer", minutes: 10);
        var plan = t.Preview();
        var op = Assert.Single(plan.Operations);
        Assert.Equal(OperationKind.Overwrite, op.Kind);
        Assert.Equal(Right, op.TargetSide);

        t.Run();
        Assert.Equal("v2-longer", t.Read(Right, "a.txt"));
        Assert.Single(t.RecycleBin.Recycled);
    }

    [Fact]
    public void ModifiedOnRight_IsCopiedToLeft_EvenIfOlder()
    {
        using var t = new TestFolders();
        t.Write(Left, "a.txt", "v1", minutes: 100);
        t.Run();

        // Rechts geändert, aber mit älterem Zeitstempel (z. B. aus einem Archiv wiederhergestellt).
        t.Write(Right, "a.txt", "restored", minutes: 5);
        t.Run();

        Assert.Equal("restored", t.Read(Left, "a.txt"));
    }

    [Fact]
    public void DeletedOnRight_IsDeletedOnLeft()
    {
        using var t = new TestFolders();
        t.Write(Left, "a.txt", "A");
        t.Run();

        t.Delete(Right, "a.txt");
        var op = Assert.Single(t.Preview().Operations);
        Assert.Equal(OperationKind.Delete, op.Kind);
        Assert.Equal(Left, op.TargetSide);

        t.Run();
        Assert.False(t.Exists(Left, "a.txt"));
        Assert.Single(t.RecycleBin.Recycled);
    }

    [Fact]
    public void DeletedOnOneSide_ButModifiedOnOther_IsRestored()
    {
        using var t = new TestFolders();
        t.Write(Left, "a.txt", "v1");
        t.Run();

        t.Delete(Right, "a.txt");
        t.Write(Left, "a.txt", "v2", minutes: 10);
        t.Run();

        Assert.Equal("v2", t.Read(Right, "a.txt"));
    }

    [Fact]
    public void BothModified_NewerWins()
    {
        using var t = new TestFolders();
        t.Write(Left, "a.txt", "v1");
        t.Run();

        t.Write(Left, "a.txt", "left", minutes: 10);
        t.Write(Right, "a.txt", "right!", minutes: 20);
        t.Run();

        Assert.Equal("right!", t.Read(Left, "a.txt"));
        Assert.Equal("right!", t.Read(Right, "a.txt"));
    }

    [Fact]
    public void BothModified_SameTimestamp_IsReportedAsConflict()
    {
        using var t = new TestFolders();
        t.Write(Left, "a.txt", "v1");
        t.Run();

        t.Write(Left, "a.txt", "left", minutes: 10);
        t.Write(Right, "a.txt", "right!", minutes: 10);
        var plan = t.Preview();

        Assert.Empty(plan.Operations);
        Assert.Contains(plan.Warnings, w => w.Contains("Konflikt"));
    }

    [Fact]
    public void RenameOnLeft_IsRepeatedOnRight_WithoutCopying()
    {
        using var t = new TestFolders();
        t.Write(Left, "docs/report.txt", "content");
        t.Run();

        t.Move(Left, "docs/report.txt", "archive/report-2024.txt");
        Directory.Delete(t.PathOf(Left, "docs"));
        var plan = t.Preview();

        var rename = Assert.Single(plan.Operations, o => o.Kind == OperationKind.Rename);
        Assert.Equal(Right, rename.TargetSide);
        Assert.DoesNotContain(plan.Operations, o => o.Kind is OperationKind.Copy or OperationKind.Overwrite);

        t.Run();
        Assert.Equal("content", t.Read(Right, "archive/report-2024.txt"));
        Assert.False(t.Exists(Right, "docs/report.txt"));
        Assert.False(t.DirExists(Right, "docs"));
        Assert.Empty(t.Preview().Operations);
    }

    [Fact]
    public void RenameOnRight_IsRepeatedOnLeft()
    {
        using var t = new TestFolders();
        t.Write(Left, "a.txt", "content");
        t.Run();

        t.Move(Right, "a.txt", "b.txt");
        t.Run();

        Assert.False(t.Exists(Left, "a.txt"));
        Assert.Equal("content", t.Read(Left, "b.txt"));
    }

    [Fact]
    public void NewFolders_AreCreated_AndDeletedFolders_AreRemoved()
    {
        using var t = new TestFolders();
        t.Mkdir(Left, "empty");
        t.Write(Left, "full/x.txt", "x");
        t.Run();
        Assert.True(t.DirExists(Right, "empty"));
        Assert.True(t.Exists(Right, "full/x.txt"));

        Directory.Delete(t.PathOf(Right, "empty"));
        Directory.Delete(t.PathOf(Right, "full"), recursive: true);
        t.Run();

        Assert.False(t.DirExists(Left, "empty"));
        Assert.False(t.DirExists(Left, "full"));
    }

    [Fact]
    public void DeletedFolder_IsKept_WhenOtherSideAddedNewFileInside()
    {
        using var t = new TestFolders();
        t.Write(Left, "f/old.txt", "old");
        t.Run();

        Directory.Delete(t.PathOf(Right, "f"), recursive: true);
        t.Write(Left, "f/new.txt", "new", minutes: 10);
        t.Run();

        Assert.False(t.Exists(Left, "f/old.txt"));
        Assert.Equal("new", t.Read(Right, "f/new.txt"));
        Assert.Empty(t.Preview().Operations);
    }

    [Fact]
    public void DeselectedOperation_IsOfferedAgainNextTime()
    {
        using var t = new TestFolders();
        t.Write(Left, "a.txt", "A");
        t.Write(Left, "b.txt", "B");

        var plan = t.Preview();
        plan.Operations.Single(o => o.TargetPath == "b.txt").IsSelected = false;
        var result = t.Engine.Run(plan);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.Skipped);
        Assert.False(t.Exists(Right, "b.txt"));

        var next = Assert.Single(t.Preview().Operations);
        Assert.Equal("b.txt", next.TargetPath);
    }

    [Fact]
    public void SourceChangedAfterPreview_OperationIsSkipped()
    {
        using var t = new TestFolders();
        t.Write(Left, "a.txt", "A");
        var plan = t.Preview();

        t.Write(Left, "a.txt", "changed", minutes: 5);
        var result = t.Engine.Run(plan);

        Assert.Equal(1, result.Skipped);
        Assert.False(t.Exists(Right, "a.txt"));
        Assert.Single(t.Preview().Operations);
    }

    [Fact]
    public void EmptiedSide_RequiresConfirmation()
    {
        using var t = new TestFolders();
        t.Write(Left, "a.txt", "A");
        t.Write(Left, "b.txt", "B");
        t.Run();

        t.Delete(Right, "a.txt");
        t.Delete(Right, "b.txt");

        var plan = t.Preview();
        Assert.NotNull(plan.ConfirmationRequired);
        Assert.Equal(2, plan.Count(OperationKind.Delete));
    }

    [Fact]
    public void SnapshotFiles_AreNotSynchronized()
    {
        using var t = new TestFolders();
        t.Write(Left, "a.txt", "A");
        t.Run();

        string snapshotName = Snapshots.SnapshotStore.GetFileName(t.Pair.Id);
        Assert.True(File.Exists(Path.Combine(t.Left, snapshotName)));
        Assert.True(File.Exists(Path.Combine(t.Right, snapshotName)));
        Assert.Empty(t.Preview().Operations);
    }

    [Fact]
    public void WithoutRecycleBinOption_FilesAreDeletedPermanently()
    {
        using var t = new TestFolders();
        t.Pair.Options.SaveOverwrittenToRecycleBin = false;
        t.Write(Left, "a.txt", "A");
        t.Run();

        t.Delete(Right, "a.txt");
        t.Run();

        Assert.False(t.Exists(Left, "a.txt"));
        Assert.Empty(t.RecycleBin.Recycled);
    }

    [Fact]
    public void ReadOnlySourceFile_IsCopied_AndCanBeOverwrittenLater()
    {
        using var t = new TestFolders();
        t.Write(Left, "ro.txt", "v1");
        File.SetAttributes(t.PathOf(Left, "ro.txt"), FileAttributes.ReadOnly);
        t.Run();
        Assert.True((File.GetAttributes(t.PathOf(Right, "ro.txt")) & FileAttributes.ReadOnly) != 0);

        File.SetAttributes(t.PathOf(Left, "ro.txt"), FileAttributes.Normal);
        t.Write(Left, "ro.txt", "v2", minutes: 10);
        t.Run();

        Assert.Equal("v2", t.Read(Right, "ro.txt"));
    }
}
