using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EKG.Common.LeaderElection;

public class LeaderElectionService : BackgroundService, ILeaderElectionService
{
    private readonly ILeaderElectionRedisClient _redis;
    private readonly LeaderElectionOptions _options;
    private readonly ILogger<LeaderElectionService> _logger;

    private volatile bool _isLeader;

    public bool IsLeader => _isLeader;
    public Guid InstanceId { get; } = Guid.NewGuid();

    private string Key => $"leader-election:{_options.AppName}";

    internal LeaderElectionService(
        ILeaderElectionRedisClient redis,
        IOptions<LeaderElectionOptions> options,
        ILogger<LeaderElectionService> logger,
        IHostApplicationLifetime lifetime)
    {
        _redis = redis;
        _options = options.Value;
        _logger = logger;

        lifetime.ApplicationStopping.Register(OnStopping);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await TryElectAsync();
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task TryElectAsync()
    {
        try
        {
            var ttl = TimeSpan.FromSeconds(_options.TtlSeconds);
            var current = await _redis.GetStateAsync(Key);

            if (current?.HolderId == InstanceId)
            {
                current.RenewTime = DateTime.UtcNow;
                await _redis.SetStateAsync(Key, current, ttl);
                _isLeader = true;
                return;
            }

            var newState = new LeaderElectionState
            {
                HolderId = InstanceId,
                TransitionCount = (current?.TransitionCount ?? -1) + 1,
                AcquireTime = DateTime.UtcNow,
                RenewTime = DateTime.UtcNow,
            };

            var acquired = await _redis.TryAcquireAsync(Key, newState, ttl);
            if (acquired)
            {
                _isLeader = true;
                _logger.LogInformation(
                    "LeaderElection: instance {InstanceId} acquired leadership for {AppName}. TransitionCount={TransitionCount}",
                    InstanceId, _options.AppName, newState.TransitionCount);
            }
            else
            {
                _isLeader = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LeaderElection: Redis error during election loop for {AppName}", _options.AppName);
        }
    }

    private void OnStopping()
    {
        if (!_isLeader)
            return;

        try
        {
            _redis.ReleaseAsync(Key).GetAwaiter().GetResult();
            _isLeader = false;
            _logger.LogInformation(
                "LeaderElection: instance {InstanceId} released leadership for {AppName} on shutdown",
                InstanceId, _options.AppName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LeaderElection: failed to release lock on shutdown for {AppName}", _options.AppName);
        }
    }
}
