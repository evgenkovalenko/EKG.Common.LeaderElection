namespace EKG.Common.LeaderElection.Locks;

/// <summary>The Lease's content, as the election reads and writes it.</summary>
internal sealed record LeaderElectionRecord
{
    public string? HolderIdentity { get; init; }

    public int LeaseDurationSeconds { get; init; }

    public DateTimeOffset? AcquireTime { get; init; }

    public DateTimeOffset? RenewTime { get; init; }

    public int LeaderTransitions { get; init; }
}

/// <summary>
/// The shared record the instances compete for (PRM-78 plan D5). Writes are optimistic: <see cref="UpdateAsync"/>
/// replaces the version last read or created, and answers false when someone else wrote it meanwhile.
/// </summary>
internal interface ILock
{
    /// <summary>This instance's identity, written as the holder.</summary>
    string Identity { get; }

    /// <summary>Where the record lives, for logs: <c>namespace/name</c>.</summary>
    string Describe();

    /// <summary>The record, or null when there's none yet.</summary>
    Task<LeaderElectionRecord?> GetAsync(CancellationToken cancellationToken);

    /// <summary>Creates the record; false when it already exists.</summary>
    Task<bool> CreateAsync(LeaderElectionRecord record, CancellationToken cancellationToken);

    /// <summary>Replaces the version last read or created; false when it changed meanwhile.</summary>
    Task<bool> UpdateAsync(LeaderElectionRecord record, CancellationToken cancellationToken);
}
