namespace EKG.Common.LeaderElection.Locks;

// PRM-78 plan D3, Enabled: false: the record lives in this process, so the one instance always wins it. The election
// itself runs unchanged -- health, handlers, DemoteAsync behave as in a cluster -- with no Kubernetes calls.
internal sealed class LocalLock(string identity) : ILock
{
    private readonly Lock _lock = new();
    private LeaderElectionRecord? _record;
    private long _version;
    private long _lastSeen = -1;

    public string Identity { get; } = identity;

    public string Describe() => "in-process (LeaderElection:Enabled=false)";

    public Task<LeaderElectionRecord?> GetAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _lastSeen = _record is null ? -1 : _version;
            return Task.FromResult(_record);
        }
    }

    public Task<bool> CreateAsync(LeaderElectionRecord record, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_record is not null)
                return Task.FromResult(false);
            _record = record;
            _lastSeen = ++_version;
            return Task.FromResult(true);
        }
    }

    public Task<bool> UpdateAsync(LeaderElectionRecord record, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_record is null || _lastSeen != _version)
                return Task.FromResult(false);
            _record = record;
            _lastSeen = ++_version;
            return Task.FromResult(true);
        }
    }
}
