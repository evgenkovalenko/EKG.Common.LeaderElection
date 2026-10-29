using EKG.Common.LeaderElection.Locks;
using Microsoft.Extensions.Time.Testing;

namespace EKG.Common.LeaderElection.Tests.Unit;

// PRM-78 plan D4: the election cycle, two instances on one in-memory Lease, on fake time.
public class LeaderElectorTests
{
    private readonly FakeLeaseServer _server = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    private Instance NewInstance(string identity, LeaderElectionOptions? options = null) =>
        new Instance(identity, _server, _time, options).WithHandlers();

    [Fact]
    public async Task The_first_instance_creates_the_Lease_and_leads_once_OnLeadAcquire_completed()
    {
        var a = NewInstance("pod-a");

        await a.CycleAsync();

        Assert.True(a.Elector.IsLeader);
        Assert.True(a.Elector.IsHealthy);
        var acquired = Assert.Single(a.Acquired);
        Assert.True(acquired.IsLeader);
        Assert.Equal("pod-a", _server.Record!.HolderIdentity);
        Assert.Equal(0, _server.Record.LeaderTransitions);
        Assert.Equal(36, _server.Record.LeaseDurationSeconds);
        Assert.Equal(_time.GetUtcNow(), _server.Record.AcquireTime);
    }

    [Fact]
    public async Task IsLeader_turns_true_only_after_the_acquire_handlers()
    {
        var a = new Instance("pod-a", _server, _time);
        bool? leaderDuringHandler = null;
        a.Elector.OnLeadAcquire += _ =>
        {
            leaderDuringHandler = a.Elector.IsLeader;
            return Task.CompletedTask;
        };

        await a.CycleAsync();

        Assert.False(leaderDuringHandler);
        Assert.True(a.Elector.IsLeader);
    }

    [Fact]
    public async Task A_second_instance_does_not_take_a_live_Lease()
    {
        var a = NewInstance("pod-a");
        var b = NewInstance("pod-b");
        await a.CycleAsync();

        _time.Advance(TimeSpan.FromSeconds(30));
        await b.CycleAsync();

        Assert.False(b.Elector.IsLeader);
        Assert.Empty(b.Acquired);
        Assert.Equal("pod-a", _server.Record!.HolderIdentity);
    }

    [Fact]
    public async Task The_leader_renews_the_Lease()
    {
        var a = NewInstance("pod-a");
        await a.CycleAsync();

        _time.Advance(TimeSpan.FromSeconds(2));
        await a.CycleAsync();

        Assert.True(a.Elector.IsLeader);
        Assert.Single(a.Acquired);
        Assert.Equal(_time.GetUtcNow(), _server.Record!.RenewTime);
    }

    [Fact]
    public async Task An_expired_Lease_is_taken_over_and_the_old_leader_steps_down_at_its_next_cycle()
    {
        var a = NewInstance("pod-a");
        var b = NewInstance("pod-b");
        await a.CycleAsync();

        _time.Advance(TimeSpan.FromSeconds(37)); // a stopped renewing (e.g. paused) for longer than the 36 s lease
        await b.CycleAsync();

        Assert.True(b.Elector.IsLeader);
        Assert.Equal("pod-b", _server.Record!.HolderIdentity);
        Assert.Equal(1, _server.Record.LeaderTransitions);

        await a.CycleAsync();

        Assert.False(a.Elector.IsLeader);
        Assert.Single(a.Released);
        Assert.Equal("pod-b", _server.Record.HolderIdentity); // a's release leaves b's Lease alone
    }

    [Fact]
    public async Task A_Lease_with_a_holder_but_no_renewal_time_counts_as_expired()
    {
        _server.Overwrite(new LeaderElectionRecord { HolderIdentity = "gone", LeaseDurationSeconds = 36, LeaderTransitions = 4 });
        var b = NewInstance("pod-b");

        await b.CycleAsync();

        Assert.True(b.Elector.IsLeader);
        Assert.Equal(5, _server.Record!.LeaderTransitions);
    }

    [Fact]
    public async Task The_Lease_holders_own_duration_decides_expiry()
    {
        _server.Overwrite(new LeaderElectionRecord { HolderIdentity = "other", LeaseDurationSeconds = 60, RenewTime = _time.GetUtcNow() });
        var b = NewInstance("pod-b");

        _time.Advance(TimeSpan.FromSeconds(45)); // past our 36 s, within the holder's 60 s
        await b.CycleAsync();
        Assert.False(b.Elector.IsLeader);

        _time.Advance(TimeSpan.FromSeconds(20));
        await b.CycleAsync();
        Assert.True(b.Elector.IsLeader);
    }

    [Fact]
    public async Task A_write_that_loses_the_race_does_not_lead()
    {
        var a = NewInstance("pod-a");
        await a.CycleAsync();
        _time.Advance(TimeSpan.FromSeconds(40));
        var b = NewInstance("pod-b");
        var bRead = b.Lock.GetAsync(default); // b has read the version...
        await bRead;
        _server.Overwrite(_server.Record! with { HolderIdentity = "pod-c", RenewTime = _time.GetUtcNow() }); // ...c wrote meanwhile

        var won = await b.Lock.UpdateAsync(_server.Record with { HolderIdentity = "pod-b" }, default);

        Assert.False(won);
        Assert.Equal("pod-c", _server.Record.HolderIdentity);
    }

    [Fact]
    public async Task An_unhealthy_leader_steps_down_and_releases_the_Lease_into_the_past()
    {
        var a = NewInstance("pod-a");
        await a.CycleAsync();
        a.Health.Answer = () => Task.FromResult(false);

        _time.Advance(TimeSpan.FromSeconds(2));
        await a.CycleAsync();

        Assert.False(a.Elector.IsLeader);
        Assert.False(a.Elector.IsHealthy);
        var released = Assert.Single(a.Released);
        Assert.False(released.IsLeader);
        Assert.False(released.IsLeaderElectionStopping);
        Assert.Null(_server.Record!.HolderIdentity);
        Assert.True(_server.Record.RenewTime < _time.GetUtcNow() - TimeSpan.FromSeconds(36));
        Assert.False(a.Elector.LeaderHandlerTimeoutToken.IsCancellationRequested);

        var b = NewInstance("pod-b"); // the next instance needn't wait for expiry
        await b.CycleAsync();
        Assert.True(b.Elector.IsLeader);
    }

    [Fact]
    public async Task An_unhealthy_follower_does_not_compete()
    {
        var a = NewInstance("pod-a");
        a.Health.Answer = () => Task.FromResult(false);

        await a.CycleAsync();

        Assert.False(a.Elector.IsLeader);
        Assert.Null(_server.Record);
    }

    [Fact]
    public async Task A_health_check_slower_than_its_timeout_counts_as_unhealthy()
    {
        var a = NewInstance("pod-a");
        a.Health.Answer = () => new TaskCompletionSource<bool>().Task;

        await Drive.UntilDoneAsync(a.CycleAsync(), _time, TimeSpan.FromMilliseconds(500));

        Assert.False(a.Elector.IsHealthy);
        Assert.Null(_server.Record);
    }

    [Fact]
    public async Task A_failing_health_check_counts_as_unhealthy()
    {
        var a = NewInstance("pod-a");
        a.Health.Answer = () => Task.FromException<bool>(new InvalidOperationException("db down"));

        await a.CycleAsync();

        Assert.False(a.Elector.IsHealthy);
        Assert.False(a.Elector.IsLeader);
    }

    [Fact]
    public async Task A_renewal_that_fails_demotes_the_leader()
    {
        var a = NewInstance("pod-a");
        await a.CycleAsync();
        a.Lock.FailWith = () => new HttpRequestException("api server unreachable");

        _time.Advance(TimeSpan.FromSeconds(2));
        await a.CycleAsync();

        Assert.False(a.Elector.IsLeader);
        Assert.Single(a.Released);
        Assert.False(a.Elector.LeaderHandlerTimeoutToken.IsCancellationRequested); // a failed release isn't fatal
    }

    [Fact]
    public async Task A_renewal_slower_than_RenewalTimeout_demotes_the_leader()
    {
        var slow = new SlowLock(new FakeLock(_server, "pod-a"));
        var elector = new LeaderElector(Microsoft.Extensions.Options.Options.Create(new LeaderElectionOptions { LockName = "test" }), slow,
            new AlwaysHealthy(), _time, Microsoft.Extensions.Logging.Abstractions.NullLogger<LeaderElector>.Instance);
        await elector.RunOneCycleAsync();
        Assert.True(elector.IsLeader);
        slow.Hang = true; // the API server stops answering

        await Drive.UntilDoneAsync(elector.RunOneCycleAsync(), _time, TimeSpan.FromSeconds(1));

        Assert.False(elector.IsLeader);
    }

    [Fact]
    public async Task An_acquire_handler_that_throws_demotes_and_runs_the_release_handlers()
    {
        var a = new Instance("pod-a", _server, _time).WithHandlers(onAcquire: _ => throw new InvalidOperationException("boom"));

        await a.CycleAsync();

        Assert.False(a.Elector.IsLeader);
        Assert.Single(a.Released);
        Assert.Null(_server.Record!.HolderIdentity);
        Assert.False(a.Elector.LeaderHandlerTimeoutToken.IsCancellationRequested);
    }

    [Fact]
    public async Task An_acquire_handler_that_times_out_cancels_the_handler_token()
    {
        var a = new Instance("pod-a", _server, _time).WithHandlers(onAcquire: _ => new TaskCompletionSource().Task);

        await Drive.UntilDoneAsync(a.CycleAsync(), _time, TimeSpan.FromSeconds(1));

        Assert.False(a.Elector.IsLeader);
        Assert.True(a.Elector.LeaderHandlerTimeoutToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_release_handler_that_times_out_cancels_the_handler_token_but_still_releases()
    {
        var a = new Instance("pod-a", _server, _time).WithHandlers(onRelease: _ => new TaskCompletionSource().Task);
        await a.CycleAsync();

        await Drive.UntilDoneAsync(a.Elector.DemoteAsync(), _time, TimeSpan.FromSeconds(1));

        Assert.False(a.Elector.IsLeader);
        Assert.True(a.Elector.LeaderHandlerTimeoutToken.IsCancellationRequested);
        Assert.Null(_server.Record!.HolderIdentity);
    }

    [Fact]
    public async Task A_release_handler_that_throws_cancels_the_handler_token()
    {
        var a = new Instance("pod-a", _server, _time).WithHandlers(onRelease: _ => throw new InvalidOperationException("boom"));
        await a.CycleAsync();

        await a.Elector.DemoteAsync();

        Assert.True(a.Elector.LeaderHandlerTimeoutToken.IsCancellationRequested);
        Assert.False(a.Elector.IsLeader);
    }

    [Fact]
    public async Task Handlers_are_optional()
    {
        var a = new Instance("pod-a", _server, _time);

        await a.CycleAsync();
        await a.Elector.DemoteAsync();

        Assert.False(a.Elector.IsLeader);
        Assert.Null(_server.Record!.HolderIdentity);
    }

    [Fact]
    public async Task DemoteAsync_steps_down_and_the_instance_can_lead_again()
    {
        var a = NewInstance("pod-a");
        await a.CycleAsync();

        await a.Elector.DemoteAsync();
        Assert.False(a.Elector.IsLeader);
        Assert.Null(_server.Record!.HolderIdentity);

        _time.Advance(TimeSpan.FromSeconds(2));
        await a.CycleAsync();
        Assert.True(a.Elector.IsLeader);
        Assert.Equal(2, a.Acquired.Count);
    }

    [Fact]
    public async Task DemoteAsync_does_nothing_for_a_follower()
    {
        var a = NewInstance("pod-a");

        await a.Elector.DemoteAsync();

        Assert.Empty(a.Released);
    }

    [Fact]
    public async Task Stopping_releases_the_Lease_with_IsLeaderElectionStopping_and_a_failing_release_handler_is_not_fatal()
    {
        var a = new Instance("pod-a", _server, _time).WithHandlers(onRelease: _ => throw new InvalidOperationException("boom"));
        a.Elector.Start();
        await Drive.UntilAsync(() => a.Elector.IsLeader);

        await a.Elector.StopAsync();

        Assert.False(a.Elector.IsLeader);
        var released = Assert.Single(a.Released);
        Assert.True(released.IsLeaderElectionStopping);
        Assert.Null(_server.Record!.HolderIdentity);
        Assert.False(a.Elector.LeaderHandlerTimeoutToken.IsCancellationRequested);
    }

    [Fact]
    public async Task The_loop_runs_a_cycle_every_RenewalPeriod()
    {
        var a = NewInstance("pod-a");
        a.Elector.Start();
        await Drive.UntilAsync(() => a.Elector.IsLeader);
        var firstRenew = _server.Record!.RenewTime;

        for (var i = 0; i < 5 && _server.Record!.RenewTime == firstRenew; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(50);
        }

        Assert.True(_server.Record!.RenewTime > firstRenew);
        await a.Elector.StopAsync();
    }

    [Fact]
    public async Task Stop_before_start_and_a_second_start_are_harmless()
    {
        var a = NewInstance("pod-a");
        await a.Elector.StopAsync();

        a.Elector.Start();
        a.Elector.Start();
        await Drive.UntilAsync(() => a.Elector.IsLeader);
        await a.Elector.StopAsync();
        await a.Elector.StopAsync();

        Assert.Single(a.Acquired);
    }

    [Fact]
    public async Task The_handlers_journal_is_kept_per_transition()
    {
        var a = new Instance("pod-a", _server, _time).WithHandlers(onAcquire: e =>
        {
            e.Journal.Add("consumer started");
            return Task.CompletedTask;
        });

        await a.CycleAsync();

        var text = a.Acquired[0].Journal.Format(isAcquire: true);
        Assert.Contains("Leader acquisition journal", text);
        Assert.Contains("consumer started", text);
        Assert.Equal(string.Empty, new LeaderElectionJournal().Format(isAcquire: false));
    }

    [Fact]
    public async Task The_in_process_lock_makes_a_single_instance_the_leader()
    {
        var elector = new LeaderElector(Microsoft.Extensions.Options.Options.Create(new LeaderElectionOptions { LockName = "x", Enabled = false }),
            new LocalLock("local"), new AlwaysHealthy(), _time, Microsoft.Extensions.Logging.Abstractions.NullLogger<LeaderElector>.Instance);

        await elector.RunOneCycleAsync();
        Assert.True(elector.IsLeader);
        _time.Advance(TimeSpan.FromSeconds(2));
        await elector.RunOneCycleAsync();
        Assert.True(elector.IsLeader);

        await elector.DemoteAsync();
        Assert.False(elector.IsLeader);
        await elector.RunOneCycleAsync();
        Assert.True(elector.IsLeader);
    }

    // Passes through to the inner lock until Hang; then reads never answer (until cancelled).
    private sealed class SlowLock(ILock inner) : ILock
    {
        public bool Hang { get; set; }

        public string Identity => inner.Identity;

        public string Describe() => inner.Describe();

        public Task<LeaderElectionRecord?> GetAsync(CancellationToken cancellationToken) =>
            Hang ? Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => (LeaderElectionRecord?)null, TaskScheduler.Default) : inner.GetAsync(cancellationToken);

        public Task<bool> CreateAsync(LeaderElectionRecord record, CancellationToken cancellationToken) => inner.CreateAsync(record, cancellationToken);

        public Task<bool> UpdateAsync(LeaderElectionRecord record, CancellationToken cancellationToken) => inner.UpdateAsync(record, cancellationToken);
    }
}
