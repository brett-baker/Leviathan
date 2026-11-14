using Leviathan.ViewModels.Notes;

namespace Leviathan.ViewModels.Tests;

/// <summary>Folder notes (spec §15.6): kept per folder path, trimmed, following renames and going with deleted folders.</summary>
public sealed class FolderNotesTests
{
    private sealed class MemoryStorage(Dictionary<string, string>? start = null) : IFolderNotesStorage
    {
        public Dictionary<string, string> Saved { get; private set; } = start ?? [];

        public int Saves { get; private set; }

        public bool Fail { get; set; }

        public IReadOnlyDictionary<string, string> Load() => Saved;

        public void Save(IReadOnlyDictionary<string, string> notes)
        {
            if (Fail)
            {
                throw new IOException("disk full");
            }

            Saves++;
            Saved = new Dictionary<string, string>(notes);
        }
    }

    [Fact]
    public void ANote_IsKept_PerFolder_WithoutCaseOrTrailingSlash()
    {
        var storage = new MemoryStorage();
        var notes = new FolderNotes(storage);

        notes.Set(@"D:\Work\Taxes\", "  Receipts for 2025  ");

        Assert.Equal("Receipts for 2025", notes.Get(@"d:\work\taxes"));
        Assert.True(notes.Has(@"D:\Work\Taxes"));
        Assert.Equal(string.Empty, notes.Get(@"D:\Work"));
        Assert.Single(storage.Saved);
    }

    [Fact]
    public void ABlankNote_RemovesIt_AndSettingTheSameTextAgainSavesNothing()
    {
        var storage = new MemoryStorage();
        var notes = new FolderNotes(storage);
        notes.Set(@"D:\A", "x");
        notes.Set(@"D:\A", "x");
        Assert.Equal(1, storage.Saves);

        notes.Set(@"D:\A", "   ");

        Assert.False(notes.Has(@"D:\A"));
        Assert.Empty(storage.Saved);
        notes.Set(@"D:\Never", string.Empty);
        Assert.Equal(2, storage.Saves);
    }

    [Fact]
    public void ALongNote_IsCutToTheLimit()
    {
        var notes = new FolderNotes(new MemoryStorage());

        notes.Set(@"D:\A", new string('x', FolderNotes.MaxLength + 500));

        Assert.Equal(FolderNotes.MaxLength, notes.Get(@"D:\A").Length);
    }

    [Fact]
    public void Notes_AreLoadedFromStorage()
    {
        var notes = new FolderNotes(new MemoryStorage(new() { [@"D:\A"] = "hello" }));

        Assert.Equal("hello", notes.Get(@"d:\a\"));
        Assert.Equal(1, notes.Count);
    }

    [Fact]
    public void ARenamedFolder_TakesItsNote_AndThoseOfItsSubfolders()
    {
        var storage = new MemoryStorage();
        var notes = new FolderNotes(storage);
        notes.Set(@"D:\Old", "top");
        notes.Set(@"D:\Old\Sub", "inner");
        notes.Set(@"D:\Older", "other");

        notes.Move(@"D:\Old", @"D:\New");

        Assert.Equal("top", notes.Get(@"D:\New"));
        Assert.Equal("inner", notes.Get(@"D:\New\Sub"));
        Assert.Equal("other", notes.Get(@"D:\Older"));
        Assert.False(notes.Has(@"D:\Old"));
    }

    [Fact]
    public void ADeletedFolder_TakesItsNotes_ButNotASiblingWithTheSamePrefix()
    {
        var notes = new FolderNotes(new MemoryStorage());
        notes.Set(@"D:\Old", "a");
        notes.Set(@"D:\Old\Sub", "b");
        notes.Set(@"D:\Older", "c");

        notes.Remove(@"D:\Old");

        Assert.Equal(1, notes.Count);
        Assert.Equal("c", notes.Get(@"D:\Older"));
    }

    [Fact]
    public void ChangedIsRaised_OnlyWhenSomethingChanged()
    {
        var notes = new FolderNotes(new MemoryStorage());
        var raised = 0;
        notes.Changed += (_, _) => raised++;

        notes.Set(@"D:\A", "x");
        notes.Set(@"D:\A", "x");
        notes.Move(@"D:\Nothing", @"D:\Else");
        notes.Remove(@"D:\Nothing");
        notes.Remove(@"D:\A");

        Assert.Equal(2, raised);
    }

    [Fact]
    public void AStorageThatCannotWrite_DoesNotLoseTheNoteForThisRun()
    {
        var notes = new FolderNotes(new MemoryStorage { Fail = true });

        notes.Set(@"D:\A", "kept in memory");

        Assert.Equal("kept in memory", notes.Get(@"D:\A"));
    }
}
