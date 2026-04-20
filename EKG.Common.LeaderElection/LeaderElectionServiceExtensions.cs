using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EKG.Common.LeaderElection;

public static class LeaderElectionServiceExtensions
{
    public static IServiceCollection AddLeaderElection(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LeaderElectionOptions>(configuration.GetSection("LeaderElection"));
        services.AddSingleton<LeaderElectionRedisClient>();
        services.AddSingleton<ILeaderElectionRedisClient>(sp => sp.GetRequiredService<LeaderElectionRedisClient>());
        services.AddSingleton<LeaderElectionService>();
        services.AddSingleton<ILeaderElectionService>(sp => sp.GetRequiredService<LeaderElectionService>());
        services.AddHostedService(sp => sp.GetRequiredService<LeaderElectionService>());
        return services;
    }
}
