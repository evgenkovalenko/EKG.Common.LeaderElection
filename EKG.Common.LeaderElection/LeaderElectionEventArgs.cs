namespace EKG.Common.LeaderElection;

/// <summary>Passed to <see cref="ILeaderElection.OnLeadAcquire"/> and <see cref="ILeaderElection.OnLeadRelease"/>.</summary>
public sealed class LeaderElectionEventArgs : EventArgs
{
    /// <summary><see cref="ILeaderElection.LeaderHandlerTimeoutToken"/>.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>True for an acquisition, false for a release.</summary>
    public bool IsLeader { get; init; }

    /// <summary>True when the release is part of the host's shutdown.</summary>
    public bool IsLeaderElectionStopping { get; init; }

    /// <summary>Notes the handler adds; logged as one entry after the handlers finished.</summary>
    public LeaderElectionJournal Journal { get; init; } = new();
}

/// <summary>
/// Timestamped notes of one acquisition or release, logged together once the handlers finished, so a slow or failed
/// transition can be read as one story.
/// </summary>
public sealed class LeaderElectionJournal
{
    private readonly List<(DateTimeOffset At, string Event)> _records = [];
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;

    public LeaderElectionJournal() : this(TimeProvider.System)
    {
    }

    internal LeaderElectionJournal(TimeProvider time)
    {
        _time = time;
        Started = time.GetUtcNow();
    }

    internal DateTimeOffset Started { get; }

    public void Add(string @event)
    {
        lock (_lock)
            _records.Add((_time.GetUtcNow(), @event));
    }

    internal string Format(bool isAcquire)
    {
        lock (_lock)
        {
            if (_records.Count == 0)
                return string.Empty;
            var lines = _records.Select(r => $"  {r.At:HH:mm:ss.fff} {r.Event}");
            return $"{(isAcquire ? "Leader acquisition" : "Leader release")} journal, started {Started:HH:mm:ss.fff}:{Environment.NewLine}"
                   + string.Join(Environment.NewLine, lines);
        }
    }
}
