using Leviathan.Interop;

namespace Leviathan.Interop.Tests;

/// <summary>Hard links on a real (NTFS) temp folder (spec §15.2).</summary>
public sealed class FileHardLinkerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-hardlink-" + Guid.NewGuid().ToString("N"));

    public FileHardLinkerTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string Make(string name, string text)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task ReplaceWithLink_MakesTheCopyShareTheKeptFile_AndLeavesNoTempFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Hard links by this API are Windows only.");
        var keep = Make("keep.txt", "same contents");
        var extra = Make("extra.txt", "same contents");
        var linker = new FileHardLinker();

        var problem = await linker.ReplaceWithLinkAsync(keep, extra, CancellationToken.None);

        Assert.Null(problem);
        Assert.Equal("same contents", File.ReadAllText(extra));
        // A write through one name shows through the other: they are one file now.
        File.AppendAllText(keep, "!");
        Assert.Equal("same contents!", File.ReadAllText(extra));
        Assert.Equal(["extra.txt", "keep.txt"], Directory.GetFiles(_folder).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task ReplaceWithLink_ReplacesAReadOnlyCopy()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Hard links by this API are Windows only.");
        var keep = Make("keep.txt", "abc");
        var extra = Make("extra.txt", "abc");
        File.SetAttributes(extra, FileAttributes.ReadOnly);

        Assert.Null(await new FileHardLinker().ReplaceWithLinkAsync(keep, extra, CancellationToken.None));
        Assert.Equal("abc", File.ReadAllText(extra));
    }

    [Fact]
    public async Task ReplaceWithLink_ACopyThatChangedSize_IsNotLinked_AndIsUntouched()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Hard links by this API are Windows only.");
        var keep = Make("keep.txt", "abc");
        var extra = Make("extra.txt", "abcdef");

        var problem = await new FileHardLinker().ReplaceWithLinkAsync(keep, extra, CancellationToken.None);

        Assert.NotNull(problem);
        Assert.Equal("abcdef", File.ReadAllText(extra));
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }

    [Fact]
    public async Task ReplaceWithLink_ALeftoverTempFile_IsReported_AndNothingChanges()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Hard links by this API are Windows only.");
        var keep = Make("keep.txt", "abc");
        var extra = Make("extra.txt", "abc");
        Make("extra.txt.leviathan-link", "old");

        var problem = await new FileHardLinker().ReplaceWithLinkAsync(keep, extra, CancellationToken.None);

        Assert.NotNull(problem);
        Assert.Equal("old", File.ReadAllText(extra + ".leviathan-link"));
    }

    [Fact]
    public async Task MakeIndependent_GivesTheLinkedCopyItsOwnContents_AgainstTheKeptFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Hard links by this API are Windows only.");
        var keep = Make("keep.txt", "abc");
        var extra = Make("extra.txt", "abc");
        var linker = new FileHardLinker();
        await linker.ReplaceWithLinkAsync(keep, extra, CancellationToken.None);

        Assert.Null(await linker.MakeIndependentAsync(extra, CancellationToken.None));

        File.AppendAllText(keep, "!");
        Assert.Equal("abc", File.ReadAllText(extra));
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }
}
