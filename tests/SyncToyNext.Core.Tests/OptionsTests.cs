using SyncToyNext.Core.Config;
using SyncToyNext.Core.Model;
using SyncToyNext.Core.Planning;
using SyncToyNext.Core.Scanning;
using static SyncToyNext.Core.Model.Side;

namespace SyncToyNext.Core.Tests;

public class OptionsTests
{
    [Fact]
    public void IncludeAndExcludePatterns_AreApplied()
    {
        using var t = new TestFolders();
        t.Pair.Options.IncludePatterns = "*.txt; *.doc*";
        t.Pair.Options.ExcludePatterns = "~$*, *.bak.txt";
        t.Write(Left, "a.txt", "1");
        t.Write(Left, "b.docx", "2");
        t.Write(Left, "c.jpg", "3");
        t.Write(Left, "~$b.docx", "4");
        t.Write(Left, "d.bak.txt", "5");

        t.Run();

        Assert.True(t.Exists(Right, "a.txt"));
        Assert.True(t.Exists(Right, "b.docx"));
        Assert.False(t.Exists(Right, "c.jpg"));
        Assert.False(t.Exists(Right, "~$b.docx"));
        Assert.False(t.Exists(Right, "d.bak.txt"));
    }

    [Fact]
    public void ExcludedFiles_AreNeverDeleted()
    {
        using var t = new TestFolders(SyncAction.Echo);
        t.Pair.Options.ExcludePatterns = "*.tmp";
        t.Write(Right, "work.tmp", "keep me");
        t.Write(Left, "a.txt", "A");

        t.Run();

        Assert.True(t.Exists(Right, "work.tmp"));
    }

    [Fact]
    public void ExcludedSubfolders_AreIgnored()
    {
        using var t = new TestFolders();
        t.Pair.Options.ExcludedSubfolders.Add("cache");
        t.Pair.Options.ExcludedSubfolders.Add(@"photos\raw");
        t.Write(Left, "cache/x.bin", "x");
        t.Write(Left, "photos/raw/1.raw", "r");
        t.Write(Left, "photos/1.jpg", "j");

        t.Run();

        Assert.False(t.DirExists(Right, "cache"));
        Assert.False(t.DirExists(Right, "photos/raw"));
        Assert.True(t.Exists(Right, "photos/1.jpg"));
    }

    [Fact]
    public void HiddenFiles_CanBeExcluded()
    {
        using var t = new TestFolders();
        t.Pair.Options.ExcludeHidden = true;
        t.Write(Left, ".secret", "s");
        File.SetAttributes(t.PathOf(Left, ".secret"), FileAttributes.Hidden);
        t.Write(Left, "visible.txt", "v");

        t.Run();

        Assert.False(t.Exists(Right, ".secret"));
        Assert.True(t.Exists(Right, "visible.txt"));
    }

    [Fact]
    public void ReadOnlyFiles_CanBeExcluded()
    {
        using var t = new TestFolders();
        t.Pair.Options.ExcludeReadOnly = true;
        t.Write(Left, "ro.txt", "r");
        File.SetAttributes(t.PathOf(Left, "ro.txt"), FileAttributes.ReadOnly);

        t.Run();

        Assert.False(t.Exists(Right, "ro.txt"));
    }

    [Fact]
    public void CheckFileContents_SkipsIdenticalFilesWithDifferentTimestamps()
    {
        using var t = new TestFolders();
        t.Pair.Options.CheckFileContents = true;
        t.Write(Left, "a.txt", "same", minutes: 1);
        t.Write(Right, "a.txt", "same", minutes: 99);

        Assert.Empty(t.Preview().Operations);
    }

    [Fact]
    public void CheckFileContents_DetectsDifferentContentWithSameSizeAndTimestamp()
    {
        using var t = new TestFolders(SyncAction.Echo);
        t.Pair.Options.CheckFileContents = true;
        t.Write(Left, "a.txt", "AAAA", minutes: 1);
        t.Write(Right, "a.txt", "BBBB", minutes: 1);

        var op = Assert.Single(t.Preview().Operations);
        Assert.Equal(OperationKind.Overwrite, op.Kind);
    }

    [Fact]
    public void WithoutCheckFileContents_SameSizeAndTimestampCountsAsIdentical()
    {
        using var t = new TestFolders(SyncAction.Echo);
        t.Write(Left, "a.txt", "AAAA", minutes: 1);
        t.Write(Right, "a.txt", "BBBB", minutes: 1);

        Assert.Empty(t.Preview().Operations);
    }

    [Theory]
    [InlineData("*", "anything.txt", true)]
    [InlineData("*.txt", "notes.TXT", true)]
    [InlineData("*.txt", "notes.txt.bak", false)]
    [InlineData("file?.doc", "file1.doc", true)]
    [InlineData("file?.doc", "file10.doc", false)]
    [InlineData("a+b(1).txt", "a+b(1).txt", true)]
    public void Wildcards_MatchLikeWindows(string pattern, string name, bool expected)
    {
        Assert.Equal(expected, FileFilter.WildcardToRegex(pattern).IsMatch(name));
    }

    [Fact]
    public void Validate_RejectsNestedAndIdenticalFolders()
    {
        string root = Path.GetTempPath();
        var pair = new FolderPair { Name = "x", LeftPath = Path.Combine(root, "a"), RightPath = Path.Combine(root, "a", "b") };
        Assert.NotNull(SyncEngine.Validate(pair));

        pair.RightPath = Path.Combine(root, "a") + Path.DirectorySeparatorChar;
        Assert.NotNull(SyncEngine.Validate(pair));

        pair.RightPath = Path.Combine(root, "ab");
        Assert.Null(SyncEngine.Validate(pair));
    }

    [Fact]
    public void Preview_FailsWhenFolderIsMissing()
    {
        using var t = new TestFolders();
        Directory.Delete(t.Right);
        Assert.Throws<DirectoryNotFoundException>(() => t.Preview());
    }

    [Fact]
    public void PairStore_RoundTripsPairs()
    {
        using var t = new TestFolders();
        var store = new PairStore(Path.Combine(t.Root, "cfg", "pairs.json"));
        t.Pair.Options.ExcludedSubfolders.Add("x/y");
        t.Pair.Action = SyncAction.Contribute;
        store.Save(new[] { t.Pair });

        var loaded = Assert.Single(store.Load());
        Assert.Equal(t.Pair.Id, loaded.Id);
        Assert.Equal(SyncAction.Contribute, loaded.Action);
        Assert.Equal("x/y", Assert.Single(loaded.Options.ExcludedSubfolders));
    }
}
