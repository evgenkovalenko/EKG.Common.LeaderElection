using System.Text.Json;
using EKG.Common.Cache.Redis;
using StackExchange.Redis;

namespace EKG.Common.LeaderElection;

internal class LeaderElectionRedisClient(IConnectionMultiplexer connection) : RedisClientBase(connection), ILeaderElectionRedisClient
{
    public Task<LeaderElectionState?> GetStateAsync(string key)
        => GetAsync<LeaderElectionState>(key);

    public Task SetStateAsync(string key, LeaderElectionState state, TimeSpan ttl)
        => SetAsync(key, state, ttl);

    public async Task<bool> TryAcquireAsync(string key, LeaderElectionState state, TimeSpan ttl)
    {
        var json = JsonSerializer.Serialize(state);
        return await Db().StringSetAsync(key, json, ttl, When.NotExists);
    }

    public Task ReleaseAsync(string key)
        => RemoveAsync(key);
}
