using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Paths;
using Leviathan.Engine.Transfers;

namespace Leviathan.Engine.Tests;

/// <summary>Where a soft delete on a share puts things (spec §12.5): the NAS's own bin when it has one, else a hidden folder of ours.</summary>
public sealed class RecycleFoldersTests
{
    private static LocationPath P(string text) => LocationPath.Parse(text);

    private static MockFileSystem Share()
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"\\nas\media\show\ep1.mkv", new MockFileData("v"));
        return fs;
    }

    [Fact]
    public void WithoutAnyBin_ItIsLeviathansOwnHiddenFolder_AtTheShareRoot()
    {
        var fs = Share();

        var folder = new RecycleFolders(fs).Resolve(P(@"\\nas\media\show\ep1.mkv"));

        Assert.Equal(RecycleKind.Leviathan, folder.Kind);
        Assert.Equal(@"\\nas\media\.leviathan-trash", folder.Folder.Value);
        Assert.Equal(folder.Folder, folder.TrashRoot);
        Assert.True(fs.DirectoryInfo.New(@"\\nas\media\.leviathan-trash").Attributes.HasFlag(FileAttributes.Hidden));
    }

    [Theory]
    [InlineData("#recycle", RecycleKind.Synology)]
    [InlineData("@Recycle", RecycleKind.Qnap)]
    public void AShareBin_IsPreferred_ButTheManifestStaysInOurFolder(string name, RecycleKind kind)
    {
        var fs = Share();
        fs.AddDirectory($@"\\nas\media\{name}");

        var folder = new RecycleFolders(fs).Resolve(P(@"\\nas\media\show\ep1.mkv"));

        Assert.Equal(kind, folder.Kind);
        Assert.Equal($@"\\nas\media\{name}", folder.Folder.Value);
        Assert.Equal(@"\\nas\media\.leviathan-trash", folder.TrashRoot!.Value);
    }

    [Fact]
    public void FindShareBin_ReturnsTheNasBin_WithoutCreatingAnythingOfOurs()
    {
        var fs = Share();
        fs.AddDirectory(@"\\nas\media\#recycle");

        var folder = new RecycleFolders(fs).FindShareBin(P(@"\\nas\media\show\ep1.mkv"));

        Assert.Equal(RecycleKind.Synology, folder!.Kind);
        Assert.Null(folder.TrashRoot);
        Assert.False(fs.Directory.Exists(@"\\nas\media\.leviathan-trash"));
    }

    [Fact]
    public void FindShareBin_WhenTheBinIsHiddenFromThisUser_IsNull_AndStillCreatesNothing()
    {
        var fs = Share();

        Assert.Null(new RecycleFolders(fs).FindShareBin(P(@"\\nas\media\show\ep1.mkv")));
        Assert.False(fs.Directory.Exists(@"\\nas\media\.leviathan-trash"));
    }

    [Fact]
    public void WhatWasFound_IsRemembered_SoALaterDeleteDoesNotLookAgain()
    {
        var fs = Share();
        var folders = new RecycleFolders(fs);
        var first = folders.Resolve(P(@"\\nas\media\show\ep1.mkv"));

        // A bin turned on afterwards is not noticed until the remembered folder disappears or the share is forgotten.
        fs.AddDirectory(@"\\nas\media\#recycle");
        Assert.Same(first, folders.Resolve(P(@"\\nas\media\show\other.mkv")));

        folders.Forget(P(@"\\nas\media"));
        Assert.Equal(RecycleKind.Synology, folders.Resolve(P(@"\\nas\media\show\other.mkv")).Kind);
    }

    [Fact]
    public void ARememberedBin_ThatWasSwitchedOff_IsDetectedAgain()
    {
        var fs = Share();
        fs.AddDirectory(@"\\nas\media\#recycle");
        var folders = new RecycleFolders(fs);
        Assert.Equal(RecycleKind.Synology, folders.Resolve(P(@"\\nas\media\show\ep1.mkv")).Kind);

        fs.Directory.Delete(@"\\nas\media\#recycle");

        Assert.Equal(RecycleKind.Leviathan, folders.Resolve(P(@"\\nas\media\show\ep1.mkv")).Kind);
    }

    [Fact]
    public void EachShareIsRememberedOnItsOwn_IgnoringCase()
    {
        var fs = Share();
        fs.AddFile(@"\\nas\photos\a.jpg", new MockFileData("p"));
        fs.AddDirectory(@"\\nas\photos\#recycle");
        var folders = new RecycleFolders(fs);

        Assert.Equal(RecycleKind.Leviathan, folders.Resolve(P(@"\\nas\media\show\ep1.mkv")).Kind);
        Assert.Equal(RecycleKind.Synology, folders.Resolve(P(@"\\NAS\Photos\a.jpg")).Kind);
    }

    [Fact]
    public void WhenTheShareRootCannotBeWrittenTo_TheHighestFolderThatCanIsUsed_AndNotRemembered()
    {
        var fs = Share();

        // A file in the way makes creating the folder at the root fail, as a read-only root does on a server.
        fs.AddFile(@"\\nas\media\.leviathan-trash", new MockFileData("blocked"));
        var folders = new RecycleFolders(fs);

        var folder = folders.Resolve(P(@"\\nas\media\show\ep1.mkv"));

        Assert.Equal(RecycleKind.Leviathan, folder.Kind);
        Assert.Equal(@"\\nas\media\show\.leviathan-trash", folder.Folder.Value);
    }

    [Fact]
    public void WhenNothingCanBeWrittenTo_ItSaysSo_InsteadOfLosingTheItem()
    {
        var fs = Share();
        fs.AddFile(@"\\nas\media\.leviathan-trash", new MockFileData("blocked"));
        fs.AddFile(@"\\nas\media\show\.leviathan-trash", new MockFileData("blocked"));

        var error = Assert.Throws<IOException>(() => new RecycleFolders(fs).Resolve(P(@"\\nas\media\show\ep1.mkv")));

        Assert.Contains("Shift+Delete", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"\\nas\media\#recycle\show\ep1.mkv", true)]
    [InlineData(@"\\nas\media\@recycle", true)]
    [InlineData(@"\\nas\media\.Leviathan-Trash\20261103-1\ep1.mkv", true)]
    [InlineData(@"\\nas\media\show\ep1.mkv", false)]
    [InlineData(@"\\nas\media\recycle\ep1.mkv", false)]
    public void IsInsideBin_RecognisesTheThreeFolders(string path, bool inside) =>
        Assert.Equal(inside, RecycleFolders.IsInsideBin(P(path)));
}
