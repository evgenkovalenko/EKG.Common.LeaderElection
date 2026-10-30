using k8s;
using k8s.Models;

namespace EKG.Common.LeaderElection.Tests.E2E;

[CollectionDefinition("Sample", DisableParallelization = true)]
public sealed class SampleCollection : ICollectionFixture<SampleCluster>;

// PRM-78 plan D9, against the kind bench (Tiltfile): two sample pods, one Lease. The tests change who leads, so they run
// one after another and each starts from a settled state.
[Collection("Sample")]
public class LeaderElectionTests(SampleCluster cluster)
{
    [Fact]
    public async Task Exactly_one_pod_leads_and_holds_the_Lease()
    {
        var (leader, follower) = await cluster.SettledAsync();

        var lease = await cluster.LeaseAsync();
        Assert.NotNull(lease);
        Assert.Equal(leader.Metadata.Name, lease.Spec.HolderIdentity);
        Assert.Equal(leader.Metadata.Name, (await cluster.StatusAsync(leader)).Identity);
        Assert.False((await cluster.StatusAsync(follower)).IsLeader);
        Assert.Equal(11, lease.Spec.LeaseDurationSeconds); // ceil(10.5)
    }

    [Fact]
    public async Task Demote_hands_the_Lease_over()
    {
        var (leader, _) = await cluster.SettledAsync();
        var transitions = (await cluster.LeaseAsync())!.Spec.LeaseTransitions ?? 0;

        await cluster.PostAsync(leader, "demote");
        var (next, took) = await cluster.NewLeaderAsync(leader.Metadata.Name, TimeSpan.FromSeconds(15));

        var lease = await cluster.LeaseAsync();
        Assert.Equal(next.Metadata.Name, lease!.Spec.HolderIdentity);
        Assert.True(lease.Spec.LeaseTransitions > transitions);
        Assert.True(took < TimeSpan.FromSeconds(5), $"handover took {took}"); // released, not expired
    }

    [Fact]
    public async Task An_unhealthy_leader_steps_down()
    {
        var (leader, _) = await cluster.SettledAsync();

        await cluster.PostAsync(leader, "health/false");
        try
        {
            await cluster.NewLeaderAsync(leader.Metadata.Name, TimeSpan.FromSeconds(15));
            var status = await cluster.StatusAsync(leader);
            Assert.False(status.IsLeader);
            Assert.False(status.IsHealthy);
        }
        finally
        {
            await cluster.PostAsync(leader, "health/true");
        }
    }

    [Fact]
    public async Task Deleting_the_leader_pod_gracefully_hands_over_without_waiting_for_expiry()
    {
        var (leader, _) = await cluster.SettledAsync();

        await cluster.Client.CoreV1.DeleteNamespacedPodAsync(leader.Metadata.Name, SampleCluster.Namespace);
        var (_, took) = await cluster.NewLeaderAsync(leader.Metadata.Name, TimeSpan.FromSeconds(30));

        Assert.True(took < SampleCluster.LeaseDuration, $"handover took {took}: the Lease wasn't released on shutdown");
    }

    [Fact]
    public async Task A_killed_leader_is_replaced_once_its_Lease_expires()
    {
        var (leader, _) = await cluster.SettledAsync();

        await cluster.Client.CoreV1.DeleteNamespacedPodAsync(leader.Metadata.Name, SampleCluster.Namespace,
            new V1DeleteOptions { GracePeriodSeconds = 0 });
        var (_, took) = await cluster.NewLeaderAsync(leader.Metadata.Name, SampleCluster.LeaseDuration + TimeSpan.FromSeconds(20));

        Assert.True(took < SampleCluster.LeaseDuration + TimeSpan.FromSeconds(10), $"handover took {took}");
    }
}
