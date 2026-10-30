using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EKG.Common.LeaderElection;

// PRM-78 plan D6: the host drives the election.
//   - started on ApplicationStarted, once every singleton that subscribes to the handlers has been built;
//   - stopped on ApplicationStopping, before the other hosted services stop: a leader runs OnLeadRelease and releases
//     the Lease, so the next pod takes over at its next cycle;
//   - LeaderHandlerTimeoutToken -> StopApplication (unless the host is already stopping).
internal sealed class LeaderElectionHostedService(
    LeaderElector elector,
    IHostApplicationLifetime lifetime,
    IConfiguration configuration,
    ILogger<LeaderElectionHostedService> logger) : IHostedService
{
    private volatile bool _stopping;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var section = configuration.GetSection(LeaderElectionOptions.Section);
        if (section["AppName"] is not null || section["TtlSeconds"] is not null)
            logger.LogWarning("LeaderElection:AppName and LeaderElection:TtlSeconds are 1.0 (Redis) settings and are ignored in 2.0: " +
                              "the Lease is LeaderElection:LockName and its duration is computed");

        lifetime.ApplicationStarted.Register(() =>
        {
            try
            {
                elector.Start();
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Leader election failed to start: stopping the application");
                lifetime.StopApplication();
            }
        });

        lifetime.ApplicationStopping.Register(() =>
        {
            _stopping = true;
            logger.LogInformation("Application stopping: stopping the leader election");
            elector.StopAsync().GetAwaiter().GetResult();
        });

        // Off the elector's thread: stopping waits for the election loop, which is the thread cancelling the token.
        elector.LeaderHandlerTimeoutToken.Register(() =>
        {
            if (_stopping)
                return;
            logger.LogCritical("A leader election handler failed or timed out: stopping the application");
            _ = Task.Run(lifetime.StopApplication);
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => elector.StopAsync();
}
