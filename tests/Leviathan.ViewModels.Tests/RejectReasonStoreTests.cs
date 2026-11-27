using System.IO.Abstractions.TestingHelpers;
using Leviathan.ViewModels.Triage.Learning;

namespace Leviathan.ViewModels.Tests;

/// <summary>The user's reject reasons: they start empty, survive a restart, and a bad file never stops triage.</summary>
public sealed class RejectReasonStoreTests
{
    private const string Path = @"C:\data\reject-reasons.json";

    [Fact]
    public void Reasons_StartEmpty()
    {
        Assert.Empty(new RejectReasonStore(new MockFileSystem(), Path).Reasons);
    }

    [Fact]
    public void Add_KeepsTitleAndDescription_AndTrimsThem()
    {
        var store = new RejectReasonStore(new MockFileSystem(), Path);

        var added = store.Add("  Too short  ", "  Under two minutes  ");

        Assert.Equal("Too short", added.Title);
        Assert.Equal("Under two minutes", added.Description);
        Assert.Equal(added, Assert.Single(store.Reasons));
    }

    [Fact]
    public void Add_IsStillThereForTheNextRun()
    {
        var disk = new MockFileSystem();
        var first = new RejectReasonStore(disk, Path);
        first.Add("Bad quality", "Blurry or low resolution");
        first.Add("Seen it", string.Empty);

        var second = new RejectReasonStore(disk, Path);

        Assert.Equal(["Bad quality", "Seen it"], second.Reasons.Select(r => r.Title));
        Assert.Equal("Blurry or low resolution", second.Reasons[0].Description);
        Assert.False(disk.FileExists(Path + ".tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Add_WithoutATitle_IsRefused(string title)
    {
        var store = new RejectReasonStore(new MockFileSystem(), Path);

        Assert.Throws<ArgumentException>(() => store.Add(title, "x"));
        Assert.Empty(store.Reasons);
    }

    [Fact]
    public void Add_ATitleAlreadyThere_IsRefused_WhateverTheCase()
    {
        var store = new RejectReasonStore(new MockFileSystem(), Path);
        store.Add("Seen it", null);

        Assert.Throws<InvalidOperationException>(() => store.Add("seen IT", null));
        Assert.Single(store.Reasons);
    }

    [Fact]
    public void Update_ChangesTheWords_ButKeepsTheId()
    {
        var store = new RejectReasonStore(new MockFileSystem(), Path);
        var added = store.Add("Short", null);

        var updated = store.Update(added.Id, "Too short", "Under two minutes");

        Assert.Equal(added.Id, updated.Id);
        Assert.Equal("Too short", store.Find(added.Id)!.Title);
    }

    [Fact]
    public void Update_ToItsOwnTitle_IsNotADuplicate()
    {
        var store = new RejectReasonStore(new MockFileSystem(), Path);
        var added = store.Add("Short", null);

        var updated = store.Update(added.Id, "short", "now with a description");

        Assert.Equal("short", updated.Title);
    }

    [Fact]
    public void Update_AnUnknownId_Throws()
    {
        var store = new RejectReasonStore(new MockFileSystem(), Path);

        Assert.Throws<KeyNotFoundException>(() => store.Update("nope", "x", null));
    }

    [Fact]
    public void Remove_TakesItOffTheList_AndSaysSo()
    {
        var store = new RejectReasonStore(new MockFileSystem(), Path);
        var added = store.Add("Short", null);

        Assert.True(store.Remove(added.Id));
        Assert.False(store.Remove(added.Id));
        Assert.Empty(store.Reasons);
        Assert.Null(store.Find(added.Id));
    }

    [Fact]
    public void Changed_IsRaised_ForEveryEdit()
    {
        var store = new RejectReasonStore(new MockFileSystem(), Path);
        var raised = 0;
        store.Changed += (_, _) => raised++;

        var added = store.Add("Short", null);
        store.Update(added.Id, "Shorter", null);
        store.Remove(added.Id);

        Assert.Equal(3, raised);
    }

    [Fact]
    public void AFileThatCannotBeRead_IsAnEmptyList_AndTheNextSaveReplacesIt()
    {
        var disk = new MockFileSystem();
        disk.AddFile(Path, new MockFileData("{ this is not json"));
        var store = new RejectReasonStore(disk, Path);

        Assert.Empty(store.Reasons);
        store.Add("Short", null);

        Assert.Equal("Short", Assert.Single(new RejectReasonStore(disk, Path).Reasons).Title);
    }
}
