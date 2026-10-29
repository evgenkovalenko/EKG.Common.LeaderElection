using System.Text.Json;
using EKG.Common.LeaderElection.Locks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EKG.Common.LeaderElection;

// PRM-78 plan D4: the election cycle of DraftKings.Feeds.LeaderElection's LeaderElector, every RenewalPeriod, one at a
// time (DemoteAsync shares the semaphore):
//   1. health (bounded by HealthCheckTimeout);
//   2. leader: renew within RenewalTimeout (still ours and unexpired -> new renewTime); unhealthy or not renewed ->
//      demote: OnLeadRelease (bounded), release the Lease, IsLeader = false;
//   3. healthy follower: acquire within RenewalTimeout (no Lease -> create; empty, ours or expired -> take over), then
//      OnLeadAcquire (bounded); IsLeader turns true only once it completed.
// A handler that times out (or a release handler that throws) outside a shutdown leaves the application's state unknown:
// LeaderHandlerTimeoutToken is cancelled, the loop ends, and the host stops the application (LeaderElectionHostedService).
internal sealed class LeaderElector : ILeaderElection, IAsyncDisposable
{
    private readonly LeaderElectionOptions _options;
    private readonly ILock _lock;
    private readonly ILeaderHealth _health;
    private readonly TimeProvider _time;
    private readonly ILogger<LeaderElector> _logger;
    private readonly CancellationTokenSource _handlerTimeout = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _cycle = new(1, 1);
    private readonly Lock _startLock = new();

    private Task? _worker;
    private int _disposed;
    private volatile bool _isLeader;
    private volatile bool _isHealthy;
    private bool _indeterminate; // an acquire handler timed out: undo on the way out

    public LeaderElector(IOptions<LeaderElectionOptions> options, ILock @lock, ILeaderHealth health, TimeProvider time,
        ILogger<LeaderElector> logger)
    {
        _options = options.Value;
        _lock = @lock;
        _health = health;
        _time = time;
        _logger = logger;
    }

    public bool IsLeader => _isLeader;

    public bool IsHealthy => _isHealthy;

    public string Identity => _lock.Identity;

    public CancellationToken LeaderHandlerTimeoutToken => _handlerTimeout.Token;

    public event Func<LeaderElectionEventArgs, Task>? OnLeadAcquire;

    public event Func<LeaderElectionEventArgs, Task>? OnLeadRelease;

    internal bool Started => _worker is not null;

    public void Start()
    {
        lock (_startLock)
        {
            if (_worker is not null)
            {
                _logger.LogWarning("Leader election for {Lease} is already running", _lock.Describe());
                return;
            }
            _logger.LogInformation(
                "Leader election starting: Lease {Lease}, identity {Identity}, lease duration {LeaseDuration}, renewal every {RenewalPeriod}, options {Options}",
                _lock.Describe(), Identity, _options.LeaseDuration, _options.RenewalPeriod, JsonSerializer.Serialize(_options));
            _worker = Task.Run(RunAsync);
        }
    }

    public async Task StopAsync()
    {
        Task? worker;
        lock (_startLock)
            worker = _worker;
        if (worker is null || Volatile.Read(ref _disposed) == 1)
            return;
        if (!_stopping.IsCancellationRequested)
            await _stopping.CancelAsync();
        await worker;
    }

    public async Task DemoteAsync()
    {
        await _cycle.WaitAsync();
        try
        {
            if (_isLeader)
            {
                _logger.LogInformation("Demotion requested");
                await DemoteInternalAsync(stopping: false);
            }
        }
        finally
        {
            _cycle.Release();
        }
    }

    private async Task RunAsync()
    {
        using var loop = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, _handlerTimeout.Token);
        try
        {
            while (!loop.IsCancellationRequested)
            {
                var started = _time.GetTimestamp();
                await _cycle.WaitAsync(CancellationToken.None);
                try
                {
                    await RunCycleAsync(loop.Token);
                }
                catch (OperationCanceledException) when (loop.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Leader election cycle for {Lease} failed", _lock.Describe());
                }
                finally
                {
                    _cycle.Release();
                }

                var elapsed = _time.GetElapsedTime(started);
                if (elapsed > _options.LeaseDuration)
                    _logger.LogError("Leader election cycle took {Elapsed}, longer than the lease duration {LeaseDuration}: the Lease may have expired meanwhile (split-brain risk)",
                        elapsed, _options.LeaseDuration);

                try
                {
                    await Task.Delay(_options.RenewalPeriod, _time, loop.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        finally
        {
            await _cycle.WaitAsync(CancellationToken.None);
            try
            {
                if (_isLeader || _indeterminate)
                {
                    _logger.LogInformation("Leader election stopping: releasing leadership");
                    try
                    {
                        await DemoteInternalAsync(stopping: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Releasing leadership on stop failed");
                        _isLeader = false;
                    }
                }
            }
            finally
            {
                _cycle.Release();
            }
            _logger.LogInformation("Leader election for {Lease} stopped", _lock.Describe());
        }
    }

    // One cycle; internal for the unit tests, which drive cycles one by one.
    internal async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        _isHealthy = await EvaluateHealthAsync(cancellationToken);

        if (_isLeader)
        {
            var renewed = false;
            if (!_isHealthy)
                _logger.LogWarning("The leader is unhealthy: stepping down");
            else
            {
                renewed = await BoundedAsync(RenewAsync, _options.RenewalTimeout, "Renewing the Lease", cancellationToken);
                if (!renewed)
                    _logger.LogWarning("The Lease couldn't be renewed: stepping down");
            }
            if (!renewed)
                await DemoteInternalAsync(stopping: false);
        }
        else if (_isHealthy && await BoundedAsync(TryAcquireAsync, _options.RenewalTimeout, "Acquiring the Lease", cancellationToken))
        {
            await AcquireInternalAsync();
        }
    }

    // A unit test's single cycle under the semaphore, as the loop runs it.
    internal async Task RunOneCycleAsync(CancellationToken cancellationToken = default)
    {
        await _cycle.WaitAsync(cancellationToken);
        try
        {
            await RunCycleAsync(cancellationToken);
        }
        finally
        {
            _cycle.Release();
        }
    }

    private async Task<bool> EvaluateHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _health.IsHealthy().WaitAsync(_options.HealthCheckTimeout, _time, cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("The health check took longer than {Timeout}: treated as unhealthy", _options.HealthCheckTimeout);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "The health check failed: treated as unhealthy");
            return false;
        }
    }

    // Runs a Lease operation within a timeout: false when it overruns or fails (logged); a stop propagates.
    private async Task<bool> BoundedAsync(Func<CancellationToken, Task<bool>> operation, TimeSpan timeout, string what,
        CancellationToken cancellationToken)
    {
        using var timer = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        try
        {
            return await operation(linked.Token).WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("{What} took longer than {Timeout}", what, timeout);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{What} failed", what);
            return false;
        }
    }

    private async Task<bool> TryAcquireAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var record = await _lock.GetAsync(cancellationToken);
        if (record is null)
            return await _lock.CreateAsync(new LeaderElectionRecord
            {
                HolderIdentity = Identity,
                LeaseDurationSeconds = LeaseSeconds,
                AcquireTime = now,
                RenewTime = now,
                LeaderTransitions = 0,
            }, cancellationToken);

        // Take it over when nobody holds it, it's ours (e.g. a release that failed), or its holder stopped renewing.
        var ours = record.HolderIdentity == Identity;
        if (record.HolderIdentity is not null && !ours && !Expired(record, now))
            return false;

        if (!ours && record.HolderIdentity is not null)
            _logger.LogWarning("Lease {Lease} held by {Holder} expired (last renewed {RenewTime}): taking it over",
                _lock.Describe(), record.HolderIdentity, record.RenewTime);

        return await _lock.UpdateAsync(record with
        {
            HolderIdentity = Identity,
            LeaseDurationSeconds = LeaseSeconds,
            AcquireTime = now,
            RenewTime = now,
            LeaderTransitions = record.LeaderTransitions + (ours ? 0 : 1),
        }, cancellationToken);
    }

    private async Task<bool> RenewAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var record = await _lock.GetAsync(cancellationToken);
        if (record is null)
        {
            _logger.LogError("Lease {Lease} disappeared while this instance led", _lock.Describe());
            return false;
        }
        if (record.HolderIdentity != Identity)
        {
            _logger.LogError("Lease {Lease} is now held by {Holder}", _lock.Describe(), record.HolderIdentity);
            return false;
        }
        if (Expired(record, now))
        {
            _logger.LogError("Lease {Lease} expired before it was renewed (last renewed {RenewTime}): another instance may have taken over",
                _lock.Describe(), record.RenewTime);
            return false;
        }
        return await _lock.UpdateAsync(record with { RenewTime = now, LeaseDurationSeconds = LeaseSeconds }, cancellationToken);
    }

    private async Task ReleaseAsync()
    {
        using var timer = new CancellationTokenSource(_options.RenewalTimeout, _time);
        var record = await _lock.GetAsync(timer.Token).WaitAsync(timer.Token);
        if (record?.HolderIdentity != Identity)
        {
            _logger.LogInformation("Lease {Lease} isn't held by this instance (holder {Holder}): nothing to release",
                _lock.Describe(), record?.HolderIdentity);
            return;
        }
        // The times go into the past, so the next instance takes over at its next cycle instead of waiting it out.
        var past = _time.GetUtcNow() - _options.LeaseDuration * 2;
        var released = await _lock.UpdateAsync(record with { HolderIdentity = null, AcquireTime = past, RenewTime = past }, timer.Token)
            .WaitAsync(timer.Token);
        if (released)
            _logger.LogInformation("Lease {Lease} released", _lock.Describe());
        else
            _logger.LogWarning("Lease {Lease} changed while being released", _lock.Describe());
    }

    private async Task AcquireInternalAsync()
    {
        var journal = new LeaderElectionJournal(_time);
        _logger.LogInformation("Won Lease {Lease}: running OnLeadAcquire", _lock.Describe());
        var handlers = Task.Run(() => InvokeAll(OnLeadAcquire, new LeaderElectionEventArgs
        {
            CancellationToken = _handlerTimeout.Token,
            IsLeader = true,
            Journal = journal,
        }));
        try
        {
            await handlers.WaitAsync(_options.LeaderAcquisitionHandlerTimeout, _time);
            _isLeader = true;
            _logger.LogInformation("This instance ({Identity}) is the leader; OnLeadAcquire took {Elapsed}",
                Identity, _time.GetUtcNow() - journal.Started);
        }
        catch (TimeoutException)
        {
            _logger.LogError("OnLeadAcquire didn't finish within {Timeout}: the application's state is unknown, stopping it",
                _options.LeaderAcquisitionHandlerTimeout);
            _indeterminate = true;
            await _handlerTimeout.CancelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnLeadAcquire failed: giving up the leadership");
            await DemoteInternalAsync(stopping: false);
        }
        finally
        {
            Publish(journal, isAcquire: true);
        }
    }

    private async Task DemoteInternalAsync(bool stopping)
    {
        var journal = new LeaderElectionJournal(_time);
        _logger.LogInformation("Stepping down: running OnLeadRelease");
        var handlers = Task.Run(() => InvokeAll(OnLeadRelease, new LeaderElectionEventArgs
        {
            CancellationToken = _handlerTimeout.Token,
            IsLeader = false,
            IsLeaderElectionStopping = stopping,
            Journal = journal,
        }));
        try
        {
            try
            {
                await handlers.WaitAsync(_options.LeaderDemotionHandlerTimeout, _time);
                _logger.LogInformation("OnLeadRelease finished");
            }
            catch (TimeoutException)
            {
                _logger.LogError("OnLeadRelease didn't finish within {Timeout} (stopping: {Stopping})", _options.LeaderDemotionHandlerTimeout, stopping);
                if (!stopping)
                    await _handlerTimeout.CancelAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OnLeadRelease failed (stopping: {Stopping})", stopping);
                if (!stopping)
                    await _handlerTimeout.CancelAsync();
            }

            // Released after OnLeadRelease, the reverse of an acquisition. A failed release isn't fatal: the work has
            // stopped, and the Lease expires by itself.
            try
            {
                await ReleaseAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Releasing Lease {Lease} failed; it expires within {LeaseDuration}", _lock.Describe(), _options.LeaseDuration);
            }
        }
        finally
        {
            _isLeader = false;
            _indeterminate = false;
            Publish(journal, isAcquire: false);
        }
    }

    private static Task InvokeAll(Func<LeaderElectionEventArgs, Task>? handler, LeaderElectionEventArgs args)
    {
        if (handler is null)
            return Task.CompletedTask;
        var tasks = new List<Task>();
        foreach (var each in handler.GetInvocationList().Cast<Func<LeaderElectionEventArgs, Task>>())
        {
            try
            {
                tasks.Add(each(args));
            }
            catch (Exception ex)
            {
                tasks.Add(Task.FromException(ex));
            }
        }
        return Task.WhenAll(tasks);
    }

    private bool Expired(LeaderElectionRecord record, DateTimeOffset now)
    {
        // The record's own duration when it has one (its holder's settings), else ours. A holder without a renewal
        // time counts as expired (DraftKings treated it as never expiring).
        if (record.RenewTime is null)
            return true;
        var duration = record.LeaseDurationSeconds > 0 ? TimeSpan.FromSeconds(record.LeaseDurationSeconds) : _options.LeaseDuration;
        return now - record.RenewTime.Value > duration;
    }

    private int LeaseSeconds => (int)Math.Ceiling(_options.LeaseDuration.TotalSeconds);

    private void Publish(LeaderElectionJournal journal, bool isAcquire)
    {
        var text = journal.Format(isAcquire);
        if (text.Length > 0)
            _logger.LogInformation("{Journal}", text);
    }

    // Registered as LeaderElector and ILeaderElection, so the container disposes it twice.
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        _handlerTimeout.Dispose();
        _stopping.Dispose();
        _cycle.Dispose();
    }
}
