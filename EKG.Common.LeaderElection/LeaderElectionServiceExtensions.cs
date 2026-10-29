using EKG.Common.LeaderElection.Locks;
using k8s;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EKG.Common.LeaderElection;

public static class LeaderElectionServiceExtensions
{
    /// <summary>Leader election on a Kubernetes Lease (section <c>LeaderElection</c>); every instance is healthy.</summary>
    public static IServiceCollection AddLeaderElection(this IServiceCollection services, IConfiguration configuration) =>
        services.AddLeaderElection<AlwaysHealthy>(configuration);

    /// <summary>
    /// Leader election on a Kubernetes Lease (section <c>LeaderElection</c>), with <typeparamref name="THealth"/> deciding
    /// whether this instance may lead. Inject <see cref="ILeaderElection"/>; the host starts and stops it.
    /// </summary>
    public static IServiceCollection AddLeaderElection<THealth>(this IServiceCollection services, IConfiguration configuration)
        where THealth : class, ILeaderHealth
    {
        services.AddOptions<LeaderElectionOptions>()
            .Bind(configuration.GetSection(LeaderElectionOptions.Section))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<LeaderElectionOptions>, LeaderElectionOptionsValidator>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ILeaderHealth, THealth>();
        services.AddSingleton<ILock>(CreateLock);
        services.AddSingleton(sp => new LeaderElector(
            sp.GetRequiredService<IOptions<LeaderElectionOptions>>(),
            sp.GetRequiredService<ILock>(),
            sp.GetRequiredService<ILeaderHealth>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<LeaderElector>>()));
        services.AddSingleton<ILeaderElection>(sp => sp.GetRequiredService<LeaderElector>());
        services.AddHostedService(sp => new LeaderElectionHostedService(
            sp.GetRequiredService<LeaderElector>(),
            sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<LeaderElectionHostedService>>()));
        return services;
    }

    // In a pod, HOSTNAME is the pod's name: the identity `kubectl get lease` shows.
    internal static string DefaultIdentity() =>
        Environment.GetEnvironmentVariable("HOSTNAME") is { Length: > 0 } host ? host : Environment.MachineName;

    private static ILock CreateLock(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<LeaderElectionOptions>>().Value;
        var identity = DefaultIdentity();
        if (!options.Enabled)
            return new LocalLock(identity);

        // In a cluster: the pod's service account and namespace. Outside: only an explicitly named kubeconfig context
        // (the validator requires KubeContext there), never the current one.
        var config = string.IsNullOrWhiteSpace(options.KubeContext)
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile(currentContext: options.KubeContext);
        var @namespace = string.IsNullOrWhiteSpace(options.Namespace) ? config.Namespace ?? "default" : options.Namespace;
        var lease = new LeaseLock(() => new Kubernetes(config), @namespace, options.LockName, identity,
            sp.GetRequiredService<ILogger<LeaseLock>>());
        return new RetryingLock(lease, options, sp.GetRequiredService<ILogger<RetryingLock>>());
    }
}
