using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Leviathan.Core.Paths;

namespace Leviathan.Search;

/// <summary>Which of the three ways of searching answered for some folders (spec §13).</summary>
public enum SearchSourceKind
{
    /// <summary>Everything (voidtools), for local drives it indexes.</summary>
    Everything,

    /// <summary>Leviathan's own name index, for folders it has crawled (the NAS index, or a local disk without Everything).</summary>
    Index,

    /// <summary>Listing the folders now, for anywhere no index covers.</summary>
    Live,
}

/// <summary>Which source searches which folders, and why.</summary>
/// <param name="Kind">The source.</param>
/// <param name="Roots">The folders it searches.</param>
/// <param name="IndexedAt">For the index: when the folders were last listed completely; items changed since are not in it.</param>
/// <param name="Note">A short reason for the user, such as "Everything is not running", or null.</param>
public sealed record SearchSourceChoice(SearchSourceKind Kind, IReadOnlyList<LocationPath> Roots, DateTimeOffset? IndexedAt = null, string? Note = null);

/// <summary>
/// Chooses where each folder of a search is answered from and merges the answers (spec §13, D14): Everything for local drives when it is running
/// and the query can use it, Leviathan's index for folders it has crawled, a live search for everything else. The sources run side by side, results
/// are de-duplicated by path (an item found by two sources appears once), and if Everything fails part-way its folders are searched live instead, so
/// a source going away never turns into "no results". Every source judges items with the same <see cref="QueryMatcher"/>.
/// </summary>
public sealed class SearchCoordinator(LiveSearch live, IEverythingClient? everything = null, EverythingSearch? everythingSearch = null, NameIndex? index = null, NameIndexSearch? indexSearch = null)
{
    /// <summary>Decides which source answers which folder. Cheap: it asks Everything for its state and the index for its coverage.</summary>
    public async ValueTask<IReadOnlyList<SearchSourceChoice>> PlanAsync(Query query, IReadOnlyList<LocationPath> roots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(roots);
        var everythingState = everything is null || everythingSearch is null ? EverythingState.NotInstalled : await everything.GetStateAsync(cancellationToken).ConfigureAwait(false);
        var canUseEverything = everythingState == EverythingState.Ready && EverythingSearch.CanAnswer(query, [.. roots.Where(r => r.Kind == LocationKind.Drive)]);
        var forEverything = new List<LocationPath>();
        var forIndex = new List<LocationPath>();
        var forLive = new List<LocationPath>();
        DateTimeOffset? oldest = null;
        foreach (var root in roots)
        {
            if (canUseEverything && root.Kind == LocationKind.Drive)
            {
                forEverything.Add(root);
            }
            else if (index is not null && indexSearch is not null && index.CoveredSince(root) is { } since)
            {
                forIndex.Add(root);
                oldest = oldest is null || since < oldest ? since : oldest;
            }
            else
            {
                forLive.Add(root);
            }
        }

        var choices = new List<SearchSourceChoice>();
        if (forEverything.Count > 0)
        {
            choices.Add(new SearchSourceChoice(SearchSourceKind.Everything, forEverything));
        }

        if (forIndex.Count > 0)
        {
            choices.Add(new SearchSourceChoice(SearchSourceKind.Index, forIndex, oldest));
        }

        if (forLive.Count > 0)
        {
            var note = everythingState is EverythingState.NotRunning or EverythingState.Loading && forLive.Any(r => r.Kind == LocationKind.Drive)
                ? (everythingState == EverythingState.NotRunning ? "Everything is installed but not running" : "Everything is still loading its index")
                : null;
            choices.Add(new SearchSourceChoice(SearchSourceKind.Live, forLive, null, note));
        }

        return choices;
    }

    /// <summary>Searches <paramref name="roots"/> from the sources <see cref="PlanAsync"/> picks and streams the merged matches. The batches carry the sources used.</summary>
    public async IAsyncEnumerable<SearchBatch> RunAsync(
        Query query,
        IReadOnlyList<LocationPath> roots,
        QueryMatcher matcher,
        ISearchFacts facts,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(query, roots, cancellationToken).ConfigureAwait(false);
        var sources = plan.ToList();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var output = Channel.CreateUnbounded<SearchBatch>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var progress = new Dictionary<int, SearchProgress>();
        var errors = new List<SearchFolderError>();
        var gate = new Lock();

        async Task PumpAsync(int number, SearchSourceChoice choice, IAsyncEnumerable<SearchBatch> stream)
        {
            await foreach (var batch in stream.WithCancellation(linked.Token).ConfigureAwait(false))
            {
                SearchBatch? send = null;
                lock (gate)
                {
                    progress[number] = batch.Progress;
                    errors.AddRange(batch.Errors);
                    var fresh = batch.Matches.Where(m => seen.Add(m.Location.ToString())).ToList();
                    if (fresh.Count > 0)
                    {
                        send = new SearchBatch(fresh, Combine(progress.Values), false, [], [.. sources]);
                    }
                }

                if (send is not null)
                {
                    await output.Writer.WriteAsync(send, linked.Token).ConfigureAwait(false);
                }
            }
        }

        async Task RunSourceAsync(int number, SearchSourceChoice choice)
        {
            switch (choice.Kind)
            {
                case SearchSourceKind.Everything:
                    try
                    {
                        await PumpAsync(number, choice, everythingSearch!.RunAsync(query, choice.Roots, matcher, facts, linked.Token)).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException)
                    {
                        // Everything went away mid-search: its folders are searched live, so the user gets answers, not silence.
                        lock (gate)
                        {
                            var at = sources.IndexOf(choice);
                            if (at >= 0)
                            {
                                sources[at] = choice with { Kind = SearchSourceKind.Live, Note = "Everything stopped answering" };
                            }
                        }

                        await PumpAsync(number, choice, live.RunAsync(choice.Roots, matcher, facts, linked.Token)).ConfigureAwait(false);
                    }

                    break;
                case SearchSourceKind.Index:
                    await PumpAsync(number, choice, indexSearch!.RunAsync(query, choice.Roots, matcher, facts, linked.Token)).ConfigureAwait(false);
                    break;
                default:
                    await PumpAsync(number, choice, live.RunAsync(choice.Roots, matcher, facts, linked.Token)).ConfigureAwait(false);
                    break;
            }
        }

        var work = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(plan.Select((choice, number) => RunSourceAsync(number, choice))).ConfigureAwait(false);
                output.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                output.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var batch in output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return batch;
            }

            await work.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SearchProgress total;
            List<SearchSourceChoice> used;
            lock (gate)
            {
                total = Combine(progress.Values);
                used = [.. sources];
            }

            yield return new SearchBatch([], total, true, errors, used);
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            try
            {
                await work.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopping a search is not an error.
            }
        }
    }

    private static SearchProgress Combine(IEnumerable<SearchProgress> parts)
    {
        var folders = 0;
        var examined = 0L;
        var matches = 0;
        var errors = 0;
        foreach (var part in parts)
        {
            folders += part.FoldersSearched;
            examined += part.ItemsExamined;
            matches += part.Matches;
            errors += part.ErrorCount;
        }

        return new SearchProgress(folders, examined, matches, errors);
    }
}
