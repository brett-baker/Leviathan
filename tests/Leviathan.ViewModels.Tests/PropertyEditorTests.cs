using System.Runtime.InteropServices;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Preview;

namespace Leviathan.ViewModels.Tests;

/// <summary>In-place editing of title, comments and rating (spec §9.2).</summary>
public sealed class PropertyEditorTests
{
    private sealed class Writer(Exception? fail = null) : IPropertyWriter
    {
        public Dictionary<string, object>? Written { get; private set; }

        public string? Path { get; private set; }

        public Task WriteAsync(string path, IReadOnlyDictionary<string, object> values, CancellationToken cancellationToken)
        {
            if (fail is not null)
            {
                throw fail;
            }

            Path = path;
            Written = new Dictionary<string, object>(values);
            return Task.CompletedTask;
        }
    }

    private static PropertyEditor Editor(string? title = "Song", string? comment = null, uint? rating = null)
    {
        var values = new Dictionary<string, object?>();
        if (title is not null)
        {
            values["System.Title"] = title;
        }

        if (comment is not null)
        {
            values["System.Comment"] = comment;
        }

        if (rating is not null)
        {
            values["System.Rating"] = rating;
        }

        return new PropertyEditor(values);
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(1u, 1)]
    [InlineData(12u, 1)]
    [InlineData(13u, 2)]
    [InlineData(25u, 2)]
    [InlineData(50u, 3)]
    [InlineData(75u, 4)]
    [InlineData(87u, 4)]
    [InlineData(88u, 5)]
    [InlineData(99u, 5)]
    public void StarsOf_ReadsWindowsBands(uint stored, int stars)
    {
        Assert.Equal(stars, PropertyEditor.StarsOf(stored));
    }

    [Theory]
    [InlineData(0, 0u)]
    [InlineData(1, 1u)]
    [InlineData(2, 25u)]
    [InlineData(3, 50u)]
    [InlineData(4, 75u)]
    [InlineData(5, 99u)]
    public void StoredRating_UsesTheValuesWindowsWrites_AndRoundTrips(int stars, uint stored)
    {
        Assert.Equal(stored, PropertyEditor.StoredRating(stars));
        Assert.Equal(stars, PropertyEditor.StarsOf(PropertyEditor.StoredRating(stars)));
    }

    [Fact]
    public void ANewEditor_HasNothingToSave()
    {
        var editor = Editor(comment: "hello", rating: 50);

        Assert.False(editor.IsDirty);
        Assert.Equal("Song", editor.Title);
        Assert.Equal("hello", editor.Comment);
        Assert.Equal(3, editor.Stars);
    }

    [Fact]
    public async Task Save_SendsOnlyWhatChanged()
    {
        var editor = Editor(comment: "old", rating: 25);
        var writer = new Writer();
        editor.Comment = "  new  ";
        editor.Stars = 5;

        var result = await editor.SaveAsync(@"C:\a\song.mp3", writer, CancellationToken.None);

        Assert.True(result.Saved);
        Assert.Equal(@"C:\a\song.mp3", writer.Path);
        Assert.Equal(["System.Comment", "System.Rating"], writer.Written!.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("new", writer.Written["System.Comment"]);
        Assert.Equal(99u, writer.Written["System.Rating"]);
    }

    [Fact]
    public async Task EmptyingAText_WritesAnEmptyString_AndClearingStarsWritesZero()
    {
        var editor = Editor(title: "Song", rating: 99);
        var writer = new Writer();
        editor.Title = " ";
        editor.Stars = 0;

        await editor.SaveAsync(@"C:\a\song.mp3", writer, CancellationToken.None);

        Assert.Equal(string.Empty, writer.Written!["System.Title"]);
        Assert.Equal(0u, writer.Written["System.Rating"]);
    }

    [Fact]
    public async Task Save_WithNoChange_WritesNothing()
    {
        var writer = new Writer();

        var result = await Editor().SaveAsync(@"C:\a\song.mp3", writer, CancellationToken.None);

        Assert.True(result.Saved);
        Assert.Null(writer.Written);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(COMException))]
    [InlineData(typeof(NotSupportedException))]
    public async Task AFileThatRefuses_GivesAMessage_NotAnException(Type kind)
    {
        var editor = Editor();
        editor.Title = "Other";

        var result = await editor.SaveAsync(@"C:\a\song.txt", new Writer((Exception)Activator.CreateInstance(kind, "no handler")!), CancellationToken.None);

        Assert.False(result.Saved);
        Assert.Contains("did not take the change", result.Message);
        Assert.Contains("no handler", result.Message);
    }

    [Fact]
    public void TheDetailsRatingText_AgreesWithTheEditorsBands()
    {
        Assert.Equal("2 of 5", WindowsProperties.Format("System.Rating", 13u));
        Assert.Equal("5 of 5", WindowsProperties.Format("System.Rating", 99u));
        Assert.Null(WindowsProperties.Format("System.Rating", 0u));
    }
}
