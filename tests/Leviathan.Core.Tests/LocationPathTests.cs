using Leviathan.Core.Paths;

namespace Leviathan.Core.Tests;

public class LocationPathTests
{
    [Theory]
    [InlineData(@"C:\Users\brett", @"C:\Users\brett")]
    [InlineData(@"c:/users/brett/", @"C:\users\brett")]
    [InlineData(@"C:", @"C:\")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"""C:\Program Files""", @"C:\Program Files")]
    [InlineData(@"C:\a\.\b\..\c", @"C:\a\c")]
    [InlineData(@"\\?\C:\very\long", @"C:\very\long")]
    public void Parse_DrivePaths_Normalizes(string input, string expected)
    {
        var path = LocationPath.Parse(input);

        Assert.Equal(LocationKind.Drive, path.Kind);
        Assert.Equal(expected, path.Value);
    }

    [Theory]
    [InlineData(@"\\nas\media\inbox", @"\\nas\media\inbox", "nas", "media")]
    [InlineData(@"//nas/media/", @"\\nas\media", "nas", "media")]
    [InlineData(@"\\?\UNC\nas\media\2026-10", @"\\nas\media\2026-10", "nas", "media")]
    public void Parse_UncPaths_SplitsServerAndShare(string input, string expected, string server, string share)
    {
        var path = LocationPath.Parse(input);

        Assert.Equal(LocationKind.Unc, path.Kind);
        Assert.Equal(expected, path.Value);
        Assert.Equal(server, path.Server);
        Assert.Equal(share, path.Share);
        Assert.True(path.IsNetwork);
    }

    [Fact]
    public void Parse_BareServer_HasNoShare()
    {
        var path = LocationPath.Parse(@"\\Diskstation");

        Assert.Equal(LocationKind.Unc, path.Kind);
        Assert.Null(path.Share);
        Assert.True(path.IsRoot);
        Assert.Equal("Diskstation", path.Name);
    }

    [Theory]
    [InlineData("sftp://brett@nas:2222/volume1/media", LocationKind.Sftp, "sftp://brett@nas:2222/volume1/media")]
    [InlineData("SFTP://NAS/volume1/", LocationKind.Sftp, "sftp://nas/volume1")]
    [InlineData("ftp://host/pub/file%20name.txt", LocationKind.Ftp, "ftp://host/pub/file%20name.txt")]
    [InlineData("nfs://nas/volume1/media", LocationKind.Nfs, "nfs://nas/volume1/media")]
    public void Parse_Uris_NormalizesSchemeAndHost(string input, LocationKind kind, string expected)
    {
        var path = LocationPath.Parse(input);

        Assert.Equal(kind, path.Kind);
        Assert.Equal(expected, path.Value);
        Assert.True(path.IsNetwork);
    }

    [Fact]
    public void Parse_UriWithEscapedSegment_DecodesSegment()
    {
        var path = LocationPath.Parse("ftp://host/pub/file%20name.txt");

        Assert.Equal("file name.txt", path.Name);
        Assert.Equal(2222, LocationPath.Parse("sftp://nas:2222/x").Port);
        Assert.Equal("brett", LocationPath.Parse("sftp://brett@nas/x").User);
    }

    [Fact]
    public void Parse_ShellLocation_KeepsName()
    {
        var path = LocationPath.Parse("shell:Downloads");

        Assert.Equal(LocationKind.Shell, path.Kind);
        Assert.Equal("shell:Downloads", path.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative\\path")]
    [InlineData("C:relative")]
    [InlineData(@"C:\..")]
    [InlineData("http://example.com/x")]
    [InlineData(@"C:\bad|name")]
    [InlineData(@"\\")]
    public void TryParse_Invalid_ReturnsFalseWithReason(string input)
    {
        var ok = LocationPath.TryParse(input, out var path, out var error);

        Assert.False(ok);
        Assert.Null(path);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Parent_OfShareRoot_IsServer_AndServerHasNoParent()
    {
        var share = LocationPath.Parse(@"\\nas\media");

        var server = share.Parent;

        Assert.NotNull(server);
        Assert.Equal(@"\\nas", server!.Value);
        Assert.Null(server.Parent);
    }

    [Fact]
    public void Parent_OfDriveRoot_IsNull()
    {
        Assert.Null(LocationPath.Parse(@"D:\").Parent);
        Assert.Equal(@"D:\", LocationPath.Parse(@"D:\x").Parent!.Value);
    }

    [Fact]
    public void Combine_AddsChild_AndOnServerCreatesShare()
    {
        Assert.Equal(@"C:\a\b", LocationPath.Parse(@"C:\a").Combine("b").Value);
        Assert.Equal(@"\\nas\media", LocationPath.Parse(@"\\nas").Combine("media").Value);
        Assert.Equal("sftp://nas/a/b%20c", LocationPath.Parse("sftp://nas/a").Combine("b c").Value);
    }

    [Theory]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("")]
    public void Combine_InvalidName_Throws(string name)
    {
        Assert.Throws<ArgumentException>(() => LocationPath.Parse(@"C:\a").Combine(name));
    }

    [Fact]
    public void Breadcrumbs_Unc_StartAtServer()
    {
        var crumbs = LocationPath.Parse(@"\\Diskstation\media\inbox\2026-10").GetBreadcrumbs();

        Assert.Equal(new[] { "Diskstation", "media", "inbox", "2026-10" }, crumbs.Select(c => c.Label));
        Assert.Equal(@"\\Diskstation", crumbs[0].Location.Value);
        Assert.Equal(@"\\Diskstation\media\inbox", crumbs[2].Location.Value);
    }

    [Fact]
    public void Breadcrumbs_Drive_StartAtDrive()
    {
        var crumbs = LocationPath.Parse(@"D:\Exports\2026").GetBreadcrumbs();

        Assert.Equal(new[] { "D:", "Exports", "2026" }, crumbs.Select(c => c.Label));
    }

    [Fact]
    public void Equality_IgnoresCaseForWindowsPaths_ButNotForSftp()
    {
        Assert.Equal(LocationPath.Parse(@"C:\Users"), LocationPath.Parse(@"c:\USERS"));
        Assert.Equal(LocationPath.Parse(@"\\NAS\Media"), LocationPath.Parse(@"\\nas\media"));
        Assert.NotEqual(LocationPath.Parse("sftp://nas/A"), LocationPath.Parse("sftp://nas/a"));
        Assert.Equal(LocationPath.Parse(@"C:\Users").GetHashCode(), LocationPath.Parse(@"c:\users").GetHashCode());
    }

    [Fact]
    public void Contains_DetectsDescendants()
    {
        var root = LocationPath.Parse(@"\\nas\media");

        Assert.True(root.Contains(LocationPath.Parse(@"\\nas\media\inbox\x")));
        Assert.True(root.Contains(root));
        Assert.False(root.Contains(LocationPath.Parse(@"\\nas\photos")));
        Assert.False(LocationPath.Parse(@"C:\a\b").Contains(LocationPath.Parse(@"C:\a")));
    }

    [Fact]
    public void ToWin32Path_AddsExtendedPrefix_OnlyForLongPaths()
    {
        var shortPath = LocationPath.Parse(@"C:\short");
        var longDrive = LocationPath.Parse(@"C:\" + string.Join('\\', Enumerable.Repeat(new string('a', 50), 6)));
        var longUnc = LocationPath.Parse(@"\\nas\media\" + string.Join('\\', Enumerable.Repeat(new string('b', 50), 6)));

        Assert.Equal(@"C:\short", shortPath.ToWin32Path());
        Assert.StartsWith(@"\\?\C:\", longDrive.ToWin32Path());
        Assert.StartsWith(@"\\?\UNC\nas\media\", longUnc.ToWin32Path());
        Assert.StartsWith(@"\\?\", shortPath.ToWin32Path(forceExtended: true));
    }

    [Fact]
    public void ToWin32Path_ForUri_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => LocationPath.Parse("sftp://nas/x").ToWin32Path());
    }

    [Theory]
    [InlineData("..", @"C:\a")]
    [InlineData(@"..\other", @"C:\a\other")]
    [InlineData("sub", @"C:\a\b\sub")]
    [InlineData(@".\sub\deeper", @"C:\a\b\sub\deeper")]
    [InlineData(@"D:\elsewhere", @"D:\elsewhere")]
    public void TryResolve_RelativeToCurrent(string input, string expected)
    {
        var current = LocationPath.Parse(@"C:\a\b");

        Assert.True(LocationPath.TryResolve(input, current, out var path, out _));
        Assert.Equal(expected, path!.Value);
    }

    [Fact]
    public void TryResolve_AboveRoot_Fails()
    {
        Assert.False(LocationPath.TryResolve(@"..\..", LocationPath.Parse(@"C:\a"), out _, out var error));
        Assert.Contains("above", error);
    }

    [Fact]
    public void TryResolve_ExpandsEnvironmentVariables_InTypedText()
    {
        Environment.SetEnvironmentVariable("LEVIATHAN_TEST_ROOT", @"C:\Data");

        Assert.True(LocationPath.TryResolve(@"%LEVIATHAN_TEST_ROOT%\x", null, out var path, out _));
        Assert.Equal(@"C:\Data\x", path!.Value);
    }

    [Fact]
    public void Parse_FolderNamedLikeAVariable_KeepsItsName()
    {
        // C:\Windows\System32 really contains a folder called %userprofile%; a listing hands its path back to Parse.
        var path = LocationPath.Parse(@"C:\Windows\System32\%userprofile%");

        Assert.Equal(@"C:\Windows\System32\%userprofile%", path.Value);
        Assert.Equal("%userprofile%", path.Name);
    }
}
