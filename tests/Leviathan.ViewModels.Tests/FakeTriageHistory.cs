using Leviathan.ViewModels.Triage.Learning;

namespace Leviathan.ViewModels.Tests;

/// <summary>An <see cref="ITriageHistory"/> that keeps the decisions in memory, for tests of everything that records or learns from them.</summary>
public sealed class FakeTriageHistory : ITriageHistory
{
    private readonly List<TriageDecisionRecord> _records = [];
    private readonly Lock _lock = new();

    /// <summary>What has been appended so far.</summary>
    public IReadOnlyList<TriageDecisionRecord> Records
    {
        get
        {
            lock (_lock)
            {
                return [.. _records];
            }
        }
    }

    /// <summary>When set, the next append throws it (a history that cannot be written).</summary>
    public Exception? FailWith { get; set; }

    /// <inheritdoc />
    public Task AppendAsync(TriageDecisionRecord record, CancellationToken cancellationToken)
    {
        if (FailWith is { } failure)
        {
            throw failure;
        }

        lock (_lock)
        {
            _records.Add(record);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TriageDecisionRecord>> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Records);
}
