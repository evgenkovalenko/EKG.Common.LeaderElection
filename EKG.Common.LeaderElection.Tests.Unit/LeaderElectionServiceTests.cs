using EKG.Common.LeaderElection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace EKG.Common.LeaderElection.Tests.Unit;

public class LeaderElectionServiceTests
{
    private static LeaderElectionService CreateService(
        ILeaderElectionRedisClient redis,
        string appName = "TestApp",
        int ttlSeconds = 15,
        IHostApplicationLifetime? lifetime = null)
    {
        lifetime ??= CreateLifetime();
        var options = Options.Create(new LeaderElectionOptions { AppName = appName, TtlSeconds = ttlSeconds });
        return new LeaderElectionService(redis, options, NullLogger<LeaderElectionService>.Instance, lifetime);
    }

    private static IHostApplicationLifetime CreateLifetime()
    {
        var lt = Substitute.For<IHostApplicationLifetime>();
        lt.ApplicationStopping.Returns(CancellationToken.None);
        return lt;
    }

    [Fact]
    public void IsLeader_False_BeforeStart()
    {
        var redis = Substitute.For<ILeaderElectionRedisClient>();
        var svc = CreateService(redis);

        Assert.False(svc.IsLeader);
    }

    [Fact]
    public async Task AcquiresLeadership_WhenKeyAbsent()
    {
        var redis = Substitute.For<ILeaderElectionRedisClient>();
        redis.GetStateAsync(Arg.Any<string>()).Returns((LeaderElectionState?)null);
        redis.TryAcquireAsync(Arg.Any<string>(), Arg.Any<LeaderElectionState>(), Arg.Any<TimeSpan>())
             .Returns(true);

        var svc = CreateService(redis);
        await RunOneLoopAsync(svc);

        Assert.True(svc.IsLeader);
    }

    [Fact]
    public async Task RenewsLease_WhenAlreadyLeader()
    {
        var redis = Substitute.For<ILeaderElectionRedisClient>();
        var svc = CreateService(redis);

        var existingState = new LeaderElectionState
        {
            HolderId = svc.InstanceId,
            TransitionCount = 0,
            AcquireTime = DateTime.UtcNow.AddSeconds(-10),
            RenewTime = DateTime.UtcNow.AddSeconds(-5),
        };

        redis.GetStateAsync(Arg.Any<string>()).Returns(existingState);

        await RunOneLoopAsync(svc);

        await redis.Received(1).SetStateAsync(
            Arg.Any<string>(),
            Arg.Is<LeaderElectionState>(s => s.HolderId == svc.InstanceId),
            Arg.Any<TimeSpan>());
        await redis.DidNotReceive().TryAcquireAsync(Arg.Any<string>(), Arg.Any<LeaderElectionState>(), Arg.Any<TimeSpan>());
        Assert.True(svc.IsLeader);
    }

    [Fact]
    public async Task YieldsLeadership_WhenKeyTakenByOther()
    {
        var redis = Substitute.For<ILeaderElectionRedisClient>();
        var otherId = Guid.NewGuid();
        redis.GetStateAsync(Arg.Any<string>()).Returns(new LeaderElectionState
        {
            HolderId = otherId,
            TransitionCount = 1,
            AcquireTime = DateTime.UtcNow,
            RenewTime = DateTime.UtcNow,
        });
        redis.TryAcquireAsync(Arg.Any<string>(), Arg.Any<LeaderElectionState>(), Arg.Any<TimeSpan>())
             .Returns(false);

        var svc = CreateService(redis);
        await RunOneLoopAsync(svc);

        Assert.False(svc.IsLeader);
    }

    [Fact]
    public async Task ReleasesLock_OnShutdown_WhenLeader()
    {
        var redis = Substitute.For<ILeaderElectionRedisClient>();
        redis.GetStateAsync(Arg.Any<string>()).Returns((LeaderElectionState?)null);
        redis.TryAcquireAsync(Arg.Any<string>(), Arg.Any<LeaderElectionState>(), Arg.Any<TimeSpan>())
             .Returns(true);

        var cts = new CancellationTokenSource();
        var lt = Substitute.For<IHostApplicationLifetime>();
        lt.ApplicationStopping.Returns(cts.Token);

        var svc = CreateService(redis, lifetime: lt);
        await RunOneLoopAsync(svc);
        Assert.True(svc.IsLeader);

        cts.Cancel();
        await Task.Delay(50);

        await redis.Received(1).ReleaseAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task DoesNotReleaseLock_OnShutdown_WhenNotLeader()
    {
        var redis = Substitute.For<ILeaderElectionRedisClient>();
        redis.GetStateAsync(Arg.Any<string>()).Returns(new LeaderElectionState
        {
            HolderId = Guid.NewGuid(),
            TransitionCount = 0,
            AcquireTime = DateTime.UtcNow,
            RenewTime = DateTime.UtcNow,
        });
        redis.TryAcquireAsync(Arg.Any<string>(), Arg.Any<LeaderElectionState>(), Arg.Any<TimeSpan>())
             .Returns(false);

        var cts = new CancellationTokenSource();
        var lt = Substitute.For<IHostApplicationLifetime>();
        lt.ApplicationStopping.Returns(cts.Token);

        var svc = CreateService(redis, lifetime: lt);
        await RunOneLoopAsync(svc);
        Assert.False(svc.IsLeader);

        cts.Cancel();
        await Task.Delay(50);

        await redis.DidNotReceive().ReleaseAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task TransitionCount_IncrementedOnNewLeader()
    {
        var redis = Substitute.For<ILeaderElectionRedisClient>();
        redis.GetStateAsync(Arg.Any<string>()).Returns(new LeaderElectionState
        {
            HolderId = Guid.NewGuid(),
            TransitionCount = 3,
            AcquireTime = DateTime.UtcNow,
            RenewTime = DateTime.UtcNow,
        });
        redis.TryAcquireAsync(Arg.Any<string>(), Arg.Any<LeaderElectionState>(), Arg.Any<TimeSpan>())
             .Returns(true);

        var svc = CreateService(redis);
        await RunOneLoopAsync(svc);

        await redis.Received(1).TryAcquireAsync(
            Arg.Any<string>(),
            Arg.Is<LeaderElectionState>(s => s.TransitionCount == 4),
            Arg.Any<TimeSpan>());
    }

    private static async Task RunOneLoopAsync(LeaderElectionService svc)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        // Start + immediately cancel so loop runs once then exits
        var executeMethod = typeof(LeaderElectionService)
            .GetMethod("TryElectAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)executeMethod.Invoke(svc, null)!;
    }
}
