using Leviathan.Core.Paths;
using Leviathan.Core.Platform;

namespace Leviathan.Core.Tests;

/// <summary>The Delete vs Shift+Delete table of spec §12.5.</summary>
public sealed class DeletePolicyTests
{
    private static DriveDetails Drive(DriveKind kind, string root = @"E:\") =>
        new(LocationPath.Parse(root), "x", kind, StorageMedium.Unknown, "NTFS", 100, 50);

    [Theory]
    [InlineData(DriveKind.Fixed, DeleteBehavior.RecycleBin)]
    [InlineData(DriveKind.Network, DeleteBehavior.SoftDelete)]
    [InlineData(DriveKind.Removable, DeleteBehavior.PermanentAfterConfirmation)]
    [InlineData(DriveKind.Optical, DeleteBehavior.PermanentAfterConfirmation)]
    [InlineData(DriveKind.RamDisk, DeleteBehavior.PermanentAfterConfirmation)]
    [InlineData(DriveKind.Unknown, DeleteBehavior.PermanentAfterConfirmation)]
    public void ADriveLetter_IsDecidedByWhatKindOfDriveItIs(DriveKind kind, DeleteBehavior expected) =>
        Assert.Equal(expected, DeletePolicy.Decide(LocationPath.Parse(@"E:\a\b.txt"), Drive(kind)));

    [Fact]
    public void ADriveNothingIsKnownAbout_AsksFirst_RatherThanTrustingAnUnknownBin() =>
        Assert.Equal(DeleteBehavior.PermanentAfterConfirmation, DeletePolicy.Decide(LocationPath.Parse(@"E:\a\b.txt"), null));

    [Fact]
    public void AShare_IsAlwaysASoftDelete_WhateverIsKnown() =>
        Assert.Equal(DeleteBehavior.SoftDelete, DeletePolicy.Decide(LocationPath.Parse(@"\\nas\media\a.mkv"), null));

    [Theory]
    [InlineData(DeleteBehavior.RecycleBin, DeleteBehavior.SoftDelete, DeleteBehavior.SoftDelete)]
    [InlineData(DeleteBehavior.SoftDelete, DeleteBehavior.PermanentAfterConfirmation, DeleteBehavior.PermanentAfterConfirmation)]
    [InlineData(DeleteBehavior.PermanentAfterConfirmation, DeleteBehavior.RecycleBin, DeleteBehavior.PermanentAfterConfirmation)]
    [InlineData(DeleteBehavior.RecycleBin, DeleteBehavior.RecycleBin, DeleteBehavior.RecycleBin)]
    public void Strictest_PrefersAskingFirst_ThenSoftDelete(DeleteBehavior a, DeleteBehavior b, DeleteBehavior expected) =>
        Assert.Equal(expected, DeletePolicy.Strictest([a, b]));
}
