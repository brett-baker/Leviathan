using Leviathan.Core.Paths;
using Leviathan.ViewModels.Commands;

namespace Leviathan.ViewModels.Tests;

/// <summary>
/// Pins the sets of location kinds the window commands used to spell out seven times (docs/simplify/window-commands.md, item 5). Each
/// table lists every kind with the answer the old inline pattern gave, so a new kind has to be placed on purpose.
/// </summary>
public sealed class LocationKindsTests
{
    public static TheoryData<LocationKind, bool, bool, bool> Kinds => new()
    {
        // kind, local or share, file operable, server
        { LocationKind.Drive, true, true, false },
        { LocationKind.Unc, true, true, false },
        { LocationKind.Sftp, false, true, true },
        { LocationKind.Ftp, false, true, true },
        { LocationKind.Ftps, false, true, true },
        { LocationKind.Nfs, false, false, false },
        { LocationKind.Shell, false, false, false },
    };

    [Fact]
    public void Kinds_TableCoversEveryKind()
    {
        Assert.Equal(Enum.GetValues<LocationKind>().Length, Kinds.Count());
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void IsLocalOrShare_EachKind_MatchesTheOldPattern(LocationKind kind, bool localOrShare, bool operable, bool server)
    {
        Assert.Equal(localOrShare, LocationKinds.IsLocalOrShare(kind));
        Assert.Equal(operable, LocationKinds.IsFileOperable(kind));
        Assert.Equal(server, LocationKinds.IsServer(kind));
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"C:\Users", true)]
    [InlineData(@"\\nas\media", true)]
    [InlineData(@"\\nas\media\films", true)]
    [InlineData(@"\\nas", false)]
    [InlineData("sftp://host/dir", false)]
    public void IsFolderOnDriveOrShare_Path_RejectsServerRootsAndRemotes(string path, bool expected)
    {
        Assert.Equal(expected, LocationKinds.IsFolderOnDriveOrShare(LocationPath.Parse(path)));
    }
}
