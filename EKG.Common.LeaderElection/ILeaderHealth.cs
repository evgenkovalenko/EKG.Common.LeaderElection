namespace EKG.Common.LeaderElection;

/// <summary>
/// Whether this instance may lead. Asked at the start of every election cycle, within
/// <see cref="LeaderElectionOptions.HealthCheckTimeout"/> (slower counts as unhealthy). An unhealthy leader steps down; an
/// unhealthy follower doesn't compete. Register one with <c>AddLeaderElection&lt;THealth&gt;</c>; the default is always
/// healthy.
/// </summary>
public interface ILeaderHealth
{
    Task<bool> IsHealthy();
}

internal sealed class AlwaysHealthy : ILeaderHealth
{
    public Task<bool> IsHealthy() => Task.FromResult(true);
}
