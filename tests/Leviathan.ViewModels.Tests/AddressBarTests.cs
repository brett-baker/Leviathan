using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Testing;
using Leviathan.ViewModels.Navigation;

namespace Leviathan.ViewModels.Tests;

public sealed class AddressBarTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private sealed class FakeMappings : IDriveMappings
    {
        public string? TryGetUncRoot(string driveRoot) => driveRoot.Equals("Z:", StringComparison.OrdinalIgnoreCase) ? @"\\nas\media" : null;
    }

    private sealed class FakeProbe(Dictionary<LocationPath, FileEntryKind> items, Exception? failure = null) : IEntryProbe
    {
        public Task<FileEntryKind?> GetKindAsync(LocationPath location, CancellationToken cancellationToken)
        {
            if (failure is not null)
            {
                throw failure;
            }

            return Task.FromResult<FileEntryKind?>(items.TryGetValue(location, out var kind) ? kind : null);
        }
    }

    [Theory]
    [InlineData(@"C:\My Files\a b", CopyAsFormat.Path, @"C:\My Files\a b")]
    [InlineData(@"C:\My Files\a b", CopyAsFormat.Quoted, "\"C:\\My Files\\a b\"")]
    [InlineData(@"C:\My Files\a b", CopyAsFormat.FileUri, "file:///C:/My%20Files/a%20b")]
    [InlineData(@"C:\", CopyAsFormat.FileUri, "file:///C:/")]
    [InlineData(@"\\nas\media\2026 10", CopyAsFormat.FileUri, "file://nas/media/2026%2010")]
    [InlineData(@"Z:\inbox\x", CopyAsFormat.Unc, @"\\nas\media\inbox\x")]
    [InlineData(@"Z:\", CopyAsFormat.Unc, @"\\nas\media")]
    [InlineData(@"C:\inbox", CopyAsFormat.Unc, @"C:\inbox")]
    [InlineData(@"\\nas\media\x", CopyAsFormat.Unc, @"\\nas\media\x")]
    public void PathFormats_Format_ProducesTheRequestedVariant(string path, CopyAsFormat format, string expected)
    {
        Assert.Equal(expected, PathFormats.Format(P(path), format, new FakeMappings()));
    }

    [Fact]
    public void PathFormats_Unc_WithoutMappings_FallsBackToThePath()
    {
        Assert.Equal(@"Z:\a", PathFormats.Format(P(@"Z:\a"), CopyAsFormat.Unc));
    }

    [Fact]
    public async Task Resolve_Folder_Navigates()
    {
        var resolver = new AddressResolver(new FakeProbe(new() { [P(@"C:\Users")] = FileEntryKind.Folder }));

        var result = await resolver.ResolveAsync(@"C:\Users", null, CancellationToken.None);

        Assert.Equal(AddressOutcome.Navigate, result.Outcome);
        Assert.Equal(P(@"C:\Users"), result.Folder);
    }

    [Fact]
    public async Task Resolve_FilePath_OpensItsFolderAndSelectsTheFile()
    {
        var resolver = new AddressResolver(new FakeProbe(new() { [P(@"C:\Users\a.txt")] = FileEntryKind.File }));

        var result = await resolver.ResolveAsync(@"C:\Users\a.txt", null, CancellationToken.None);

        Assert.Equal(AddressOutcome.NavigateAndSelect, result.Outcome);
        Assert.Equal(P(@"C:\Users"), result.Folder);
        Assert.Equal(P(@"C:\Users\a.txt"), result.SelectItem);
    }

    [Fact]
    public async Task Resolve_RelativePath_IsResolvedAgainstTheCurrentFolder()
    {
        var resolver = new AddressResolver(new FakeProbe(new() { [P(@"C:\a\other")] = FileEntryKind.Folder }));

        var result = await resolver.ResolveAsync(@"..\other", P(@"C:\a\b"), CancellationToken.None);

        Assert.Equal(P(@"C:\a\other"), result.Folder);
    }

    [Fact]
    public async Task Resolve_EnvironmentVariable_IsExpanded()
    {
        var target = P(Environment.ExpandEnvironmentVariables(@"%SystemRoot%"));
        var resolver = new AddressResolver(new FakeProbe(new() { [target] = FileEntryKind.Folder }));

        var result = await resolver.ResolveAsync("%SystemRoot%", null, CancellationToken.None);

        Assert.Equal(AddressOutcome.Navigate, result.Outcome);
        Assert.Equal(target, result.Folder);
    }

    [Fact]
    public async Task Resolve_ShellFolder_NavigatesWithoutProbing()
    {
        var resolver = new AddressResolver(new FakeProbe([], new InvalidOperationException("must not probe")));

        var result = await resolver.ResolveAsync("shell:Downloads", null, CancellationToken.None);

        Assert.Equal(AddressOutcome.Navigate, result.Outcome);
        Assert.Equal(LocationKind.Shell, result.Folder?.Kind);
    }

    [Fact]
    public async Task Resolve_MissingPath_IsNotFound()
    {
        var resolver = new AddressResolver(new FakeProbe([]));

        var result = await resolver.ResolveAsync(@"C:\nope", null, CancellationToken.None);

        Assert.Equal(AddressOutcome.NotFound, result.Outcome);
        Assert.Contains("C:\\nope", result.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("::::")]
    public async Task Resolve_GarbageInput_IsInvalid(string input)
    {
        var resolver = new AddressResolver(new FakeProbe([]));

        var result = await resolver.ResolveAsync(input, P(@"C:\a"), CancellationToken.None);

        Assert.Equal(AddressOutcome.Invalid, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.Message));
    }

    [Fact]
    public async Task Resolve_UnreachableShare_ReportsFailureNotNotFound()
    {
        var failure = new LocationException(LocationError.Unreachable, P(@"\\nas\media"), @"\\nas\media is not responding.");
        var resolver = new AddressResolver(new FakeProbe([], failure));

        var result = await resolver.ResolveAsync(@"\\nas\media\inbox", null, CancellationToken.None);

        Assert.Equal(AddressOutcome.Failed, result.Outcome);
        Assert.Contains("not responding", result.Message);
    }

    private static AddressCompleter Completer(FakeLocationProvider fake) =>
        new(new ProviderFolderLister(new LocationProviderRegistry([fake])));

    private static FakeLocationProvider Tree() => new FakeLocationProvider()
        .AddFolder(@"C:\Users\Brett").AddFolder(@"C:\Users\Bob").AddFolder(@"C:\Users\Public")
        .AddFile(@"C:\Users\boot.txt", 1);

    [Fact]
    public async Task Suggest_TypedDirectory_ListsMatchingSubfoldersOnly_NotFiles()
    {
        var suggestions = await Completer(Tree()).SuggestAsync(@"C:\Users\B", null, [], [], CancellationToken.None);

        Assert.Equal([@"C:\Users\Bob", @"C:\Users\Brett"], suggestions.Select(s => s.Text));
        Assert.All(suggestions, s => Assert.Equal(SuggestionSource.Folder, s.Source));
    }

    [Fact]
    public async Task Suggest_TrailingSeparator_ListsAllChildren()
    {
        var suggestions = await Completer(Tree()).SuggestAsync(@"C:\Users\", null, [], [], CancellationToken.None);

        Assert.Equal(3, suggestions.Count);
    }

    [Fact]
    public async Task Suggest_AddsFavoritesAndHistory_WithoutDuplicates()
    {
        var suggestions = await Completer(Tree()).SuggestAsync(
            @"C:\Users\B",
            null,
            history: [P(@"C:\Users\Brett"), P(@"C:\Users\Brandon\Videos")],
            favorites: [P(@"C:\Users\Bob")],
            CancellationToken.None);

        Assert.Equal(3, suggestions.Count);
        Assert.Contains(suggestions, s => s.Text == @"C:\Users\Brandon\Videos" && s.Source == SuggestionSource.History);
        Assert.Equal(1, suggestions.Count(s => s.Text == @"C:\Users\Bob"));
    }

    [Fact]
    public async Task Suggest_UnknownDirectory_GivesNoSuggestionsAndDoesNotThrow()
    {
        var suggestions = await Completer(Tree()).SuggestAsync(@"C:\Nowhere\x", null, [], [], CancellationToken.None);

        Assert.Empty(suggestions);
    }

    [Fact]
    public async Task Suggest_NoSeparator_UsesOnlyHistoryAndFavorites()
    {
        var fake = Tree();
        var suggestions = await Completer(fake).SuggestAsync("C:", null, [P(@"C:\Users\Brett")], [], CancellationToken.None);

        Assert.Equal([@"C:\Users\Brett"], suggestions.Select(s => s.Text));
        Assert.Empty(fake.ListCalls);
    }

    [Fact]
    public async Task Suggest_ManyMatches_AreCapped()
    {
        var fake = new FakeLocationProvider();
        for (var i = 0; i < 40; i++)
        {
            fake.AddFolder(@"C:\Big\f" + i);
        }

        var suggestions = await Completer(fake).SuggestAsync(@"C:\Big\f", null, [], [], CancellationToken.None);

        Assert.Equal(AddressCompleter.MaxSuggestions, suggestions.Count);
        Assert.Equal(@"C:\Big\f0", suggestions[0].Text);
        Assert.Equal(@"C:\Big\f11", suggestions[^1].Text);
    }

    [Fact]
    public void CommonPrefix_IgnoresCase_AndHandlesNoneAndOne()
    {
        Assert.Equal(@"C:\Users\B", AddressCompleter.CommonPrefix([new(@"C:\Users\Bob", SuggestionSource.Folder), new(@"c:\users\Brett", SuggestionSource.Folder)]));
        Assert.Equal(string.Empty, AddressCompleter.CommonPrefix([]));
        Assert.Equal(@"C:\only", AddressCompleter.CommonPrefix([new(@"C:\only", SuggestionSource.Folder)]));
    }

    [Fact]
    public void BeginEdit_FillsThePath_OrTheTypedCharacter_AndCancelLeaves()
    {
        var bar = new AddressBarViewModel(P(@"C:\a\b"));

        bar.BeginEdit();
        Assert.True(bar.IsEditing);
        Assert.Equal(@"C:\a\b", bar.EditText);

        bar.BeginEdit("x");
        Assert.Equal("x", bar.EditText);

        bar.CancelEdit();
        Assert.False(bar.IsEditing);
        Assert.Equal(string.Empty, bar.EditText);
    }

    [Fact]
    public void Show_UpdatesBreadcrumbs_AndLeavesEditMode()
    {
        var bar = new AddressBarViewModel(P(@"C:\a"));
        bar.BeginEdit();

        bar.Show(P(@"C:\a\b\c"));

        Assert.False(bar.IsEditing);
        Assert.Equal(["C:", "a", "b", "c"], bar.Breadcrumbs.Select(c => c.Label.TrimEnd('\\')));
    }

    [Fact]
    public void Layout_CollapsesLeadingSegments_KeepsTheCurrentFolderVisible()
    {
        var bar = new AddressBarViewModel(P(@"C:\a\b\c\d"));

        var layout = bar.Layout(2);

        Assert.Equal(["C:", "a", "b"], layout.Collapsed.Select(c => c.Label.TrimEnd('\\')));
        Assert.Equal(["c", "d"], layout.Visible.Select(c => c.Label));
        Assert.Empty(bar.Layout(99).Collapsed);
        Assert.Equal(["d"], bar.Layout(0).Visible.Select(c => c.Label));
    }
}
