using EKG.Common.LeaderElection;

namespace EKG.Common.LeaderElection.Sample;

// Healthy until POST /health/false: lets the E2E tests make the leader step down.
public sealed class SampleHealth : ILeaderHealth
{
    public volatile bool Healthy = true;

    public Task<bool> IsHealthy() => Task.FromResult(Healthy);
}
