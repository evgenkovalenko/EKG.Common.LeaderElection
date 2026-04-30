using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EKG.Common.LeaderElection;

public static class LeaderElectionServiceExtensions
{
    public static IServiceCollection AddLeaderElection(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LeaderElectionOptions>(configuration.GetSection("LeaderElection"));
        services.AddSingleton<LeaderElectionRedisClient>();
        services.AddSingleton<ILeaderElectionRedisClient>(sp => sp.GetRequiredService<LeaderElectionRedisClient>());
        services.AddSingleton<LeaderElectionService>(sp => new LeaderElectionService(
            sp.GetRequiredService<ILeaderElectionRedisClient>(),
            sp.GetRequiredService<IOptions<LeaderElectionOptions>>(),
            sp.GetRequiredService<ILogger<LeaderElectionService>>(),
            sp.GetRequiredService<IHostApplicationLifetime>()));
        services.AddSingleton<ILeaderElectionService>(sp => sp.GetRequiredService<LeaderElectionService>());
        services.AddHostedService(sp => sp.GetRequiredService<LeaderElectionService>());
        return services;
    }
}
