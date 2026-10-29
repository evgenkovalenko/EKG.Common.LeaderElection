using EKG.Common.LeaderElection.Locks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace EKG.Common.LeaderElection.Tests.Unit;

// The Kubernetes API server's part, in memory: one Lease with a resourceVersion, written optimistically.
internal sealed class FakeLeaseServer
{
    private readonly Lock _lock = new();

    public LeaderElectionRecord? Record { get; private set; }

    public long Version { get; private set; }

    public (LeaderElectionRecord? Record, long Version) Read()
    {
        lock (_lock)
            return (Record, Version);
    }

    public bool Create(LeaderElectionRecord record, out long version)
    {
        lock (_lock)
        {
            version = Version;
            if (Record is not null)
                return false;
            Record = record;
            version = ++Version;
            return true;
        }
    }

    public bool Replace(LeaderElectionRecord record, long expectedVersion, out long version)
    {
        lock (_lock)
        {
            version = Version;
            if (Record is null || Version != expectedVersion)
                return false;
            Record = record;
            version = ++Version;
            return true;
        }
    }

    // Another writer (a test step), bumping the version as Kubernetes would.
    public void Overwrite(LeaderElectionRecord record)
    {
        lock (_lock)
        {
            Record = record;
            Version++;
        }
    }
}

// One instance's view of the server, as LeaseLock: replace sends the version last seen.
internal sealed class FakeLock(FakeLeaseServer server, string identity) : ILock
{
    private long _lastSeen = -1;

    public string Identity { get; } = identity;

    public Func<Exception?> FailWith { get; set; } = () => null;

    public string Describe() => "test/lease";

    public Task<LeaderElectionRecord?> GetAsync(CancellationToken cancellationToken)
    {
        if (FailWith() is { } e)
            return Task.FromException<LeaderElectionRecord?>(e);
        var (record, version) = server.Read();
        _lastSeen = record is null ? -1 : version;
        return Task.FromResult(record);
    }

    public Task<bool> CreateAsync(LeaderElectionRecord record, CancellationToken cancellationToken)
    {
        if (FailWith() is { } e)
            return Task.FromException<bool>(e);
        var created = server.Create(record, out var version);
        if (created)
            _lastSeen = version;
        return Task.FromResult(created);
    }

    public Task<bool> UpdateAsync(LeaderElectionRecord record, CancellationToken cancellationToken)
    {
        if (FailWith() is { } e)
            return Task.FromException<bool>(e);
        var replaced = server.Replace(record, _lastSeen, out var version);
        if (replaced)
            _lastSeen = version;
        return Task.FromResult(replaced);
    }
}

internal sealed class SwitchableHealth : ILeaderHealth
{
    public Func<Task<bool>> Answer { get; set; } = () => Task.FromResult(true);

    public Task<bool> IsHealthy() => Answer();
}

// One elector on the shared server, with handlers that record what ran.
internal sealed class Instance
{
    public Instance(string identity, FakeLeaseServer server, FakeTimeProvider time, LeaderElectionOptions? options = null)
    {
        Lock = new FakeLock(server, identity);
        Options = options ?? new LeaderElectionOptions { LockName = "test" };
        Elector = new LeaderElector(Microsoft.Extensions.Options.Options.Create(Options), Lock, Health, time,
            NullLogger<LeaderElector>.Instance);
    }

    public FakeLock Lock { get; }

    public LeaderElectionOptions Options { get; }

    public SwitchableHealth Health { get; } = new();

    public LeaderElector Elector { get; }

    public List<LeaderElectionEventArgs> Acquired { get; } = [];

    public List<LeaderElectionEventArgs> Released { get; } = [];

    public Instance WithHandlers(Func<LeaderElectionEventArgs, Task>? onAcquire = null, Func<LeaderElectionEventArgs, Task>? onRelease = null)
    {
        Elector.OnLeadAcquire += e =>
        {
            lock (Acquired)
                Acquired.Add(e);
            return onAcquire?.Invoke(e) ?? Task.CompletedTask;
        };
        Elector.OnLeadRelease += e =>
        {
            lock (Released)
                Released.Add(e);
            return onRelease?.Invoke(e) ?? Task.CompletedTask;
        };
        return this;
    }

    public Task CycleAsync() => Elector.RunOneCycleAsync();
}

internal static class Drive
{
    // Advances fake time until the task completes: timeouts registered on fake time fire as the test moves the clock.
    public static async Task UntilDoneAsync(Task task, FakeTimeProvider time, TimeSpan step, int maxSteps = 200)
    {
        for (var i = 0; i < maxSteps && !task.IsCompleted; i++)
        {
            await Task.Delay(5);
            if (!task.IsCompleted)
                time.Advance(step);
        }
        await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public static async Task UntilAsync(Func<bool> condition, int maxMs = 5000)
    {
        for (var waited = 0; waited < maxMs && !condition(); waited += 10)
            await Task.Delay(10);
        Assert.True(condition(), "condition not met in time");
    }
}
