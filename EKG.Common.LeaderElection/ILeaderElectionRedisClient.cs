namespace EKG.Common.LeaderElection;

internal interface ILeaderElectionRedisClient
{
    Task<LeaderElectionState?> GetStateAsync(string key);
    Task SetStateAsync(string key, LeaderElectionState state, TimeSpan ttl);
    Task<bool> TryAcquireAsync(string key, LeaderElectionState state, TimeSpan ttl);
    Task ReleaseAsync(string key);
}
