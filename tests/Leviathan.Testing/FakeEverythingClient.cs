using Leviathan.Search;

namespace Leviathan.Testing;

/// <summary>
/// A stand-in for Everything (spec §19): answers from a list of items. It does not parse Everything's syntax; it records what was asked, and
/// <see cref="Matches"/> (optional) says which items answer a given search text, so a test can model what the real index would return.
/// </summary>
public sealed class FakeEverythingClient : IEverythingClient
{
    /// <summary>What <see cref="GetStateAsync"/> reports.</summary>
    public EverythingState State { get; set; } = EverythingState.Ready;

    /// <summary>Every item Everything "knows".</summary>
    public List<EverythingHit> Items { get; } = [];

    /// <summary>Chooses the items that answer a search text; by default all of them.</summary>
    public Func<string, EverythingHit, bool>? Matches { get; set; }

    /// <summary>The search texts received, in order.</summary>
    public List<string> Searches { get; } = [];

    /// <summary>The (offset, count) pairs received.</summary>
    public List<(int Offset, int Count)> Pages { get; } = [];

    /// <inheritdoc />
    public ValueTask<EverythingState> GetStateAsync(CancellationToken cancellationToken) => ValueTask.FromResult(State);

    /// <inheritdoc />
    public ValueTask<EverythingPage> QueryAsync(string search, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State != EverythingState.Ready)
        {
            throw new InvalidOperationException("Everything is not ready.");
        }

        Searches.Add(search);
        Pages.Add((offset, count));
        var answers = Items.Where(i => Matches?.Invoke(search, i) ?? true).ToList();
        return ValueTask.FromResult(new EverythingPage([.. answers.Skip(offset).Take(count)], answers.Count));
    }
}
