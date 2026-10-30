using System.Net;
using System.Net.Sockets;
using EKG.Common.LeaderElection.Locks;
using k8s.Autorest;
using Microsoft.Extensions.Logging.Abstractions;

namespace EKG.Common.LeaderElection.Tests.Unit;

// PRM-78 plan D3 / D5: the computed timings, the validation rules and what is retried.
public class OptionsAndRetryTests
{
    private static LeaderElectionOptionsValidator Validator(bool inCluster = true) => new(() => inCluster);

    [Fact]
    public void The_defaults_give_a_36_second_lease_and_7_retries()
    {
        var o = new LeaderElectionOptions();

        Assert.Equal(TimeSpan.FromSeconds(36), o.LeaseDuration);
        Assert.Equal(7, o.KubernetesApiMaxRetryAttempts); // 100+200+400+800+1600+2000+2000 ms fit in 9 s
    }

    [Fact]
    public void The_lease_is_at_least_two_and_a_half_renewal_periods()
    {
        var o = new LeaderElectionOptions
        {
            RenewalPeriod = TimeSpan.FromSeconds(20),
            HealthCheckTimeout = TimeSpan.FromSeconds(1),
            RenewalTimeout = TimeSpan.FromSeconds(1),
            LeaderDemotionHandlerTimeout = TimeSpan.FromSeconds(1),
        };

        Assert.Equal(TimeSpan.FromSeconds(50), o.LeaseDuration);
    }

    [Fact]
    public void No_retries_when_the_renewal_timeout_leaves_no_room()
    {
        Assert.Equal(0, new LeaderElectionOptions { RenewalTimeout = TimeSpan.FromSeconds(1) }.KubernetesApiMaxRetryAttempts);
        Assert.Equal(3, new LeaderElectionOptions { RenewalTimeout = TimeSpan.FromSeconds(2) }.KubernetesApiMaxRetryAttempts); // 100+200+400
    }

    [Fact]
    public void The_lock_name_is_lowercased()
    {
        Assert.Equal("prediction-tradeapi-funds", new LeaderElectionOptions { LockName = "Prediction-TradeApi-Funds" }.LockName);
    }

    [Fact]
    public void Valid_options_pass()
    {
        Assert.True(Validator().Validate(null, new LeaderElectionOptions { LockName = "prediction-tradeapi-funds" }).Succeeded);
    }

    [Theory]
    [InlineData("", "LockName is required")]
    [InlineData("has space", "isn't a valid Kubernetes name")]
    [InlineData("-leading-dash", "isn't a valid Kubernetes name")]
    [InlineData("under_score", "isn't a valid Kubernetes name")]
    public void An_invalid_lock_name_fails(string name, string message)
    {
        var result = Validator().Validate(null, new LeaderElectionOptions { LockName = name });

        Assert.True(result.Failed);
        Assert.Contains(message, result.FailureMessage);
    }

    [Fact]
    public void Every_error_is_listed()
    {
        var result = Validator().Validate(null, new LeaderElectionOptions
        {
            LockName = "ok",
            RenewalPeriod = TimeSpan.Zero,
            HealthCheckTimeout = TimeSpan.FromSeconds(-1),
            KubernetesApiRetryDelay = TimeSpan.FromSeconds(3),
            KubernetesApiMaxRetryDelay = TimeSpan.FromSeconds(2),
        });

        Assert.Contains("RenewalPeriod", result.FailureMessage);
        Assert.Contains("HealthCheckTimeout", result.FailureMessage);
        Assert.Contains("must be at least KubernetesApiRetryDelay", result.FailureMessage);
    }

    [Fact]
    public void Outside_a_cluster_the_kubeconfig_context_must_be_named()
    {
        var result = Validator(inCluster: false).Validate(null, new LeaderElectionOptions { LockName = "ok" });

        Assert.True(result.Failed);
        Assert.Contains("Not running in a Kubernetes cluster", result.FailureMessage);
        Assert.True(Validator(inCluster: false).Validate(null, new LeaderElectionOptions { LockName = "ok", KubeContext = "kind-ekg" }).Succeeded);
        Assert.True(Validator(inCluster: false).Validate(null, new LeaderElectionOptions { LockName = "ok", Enabled = false }).Succeeded);
    }

    public static TheoryData<Exception, bool> Failures => new()
    {
        { new HttpRequestException("refused"), true },
        { new SocketException(), true },
        { new TimeoutException(), true },
        { Http(HttpStatusCode.InternalServerError), true },
        { Http(HttpStatusCode.ServiceUnavailable), true },
        { Http(HttpStatusCode.RequestTimeout), true },
        { new HttpOperationException("no response"), true },
        { Http(HttpStatusCode.Forbidden), false },
        { Http(HttpStatusCode.Conflict), false },
        { new InvalidOperationException("RBAC"), false },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void Only_transient_failures_are_retried(Exception failure, bool transient)
    {
        Assert.Equal(transient, RetryingLock.IsTransient(failure));
    }

    [Fact]
    public async Task A_transient_failure_is_retried_until_it_succeeds()
    {
        var server = new FakeLeaseServer();
        var failures = 2;
        var inner = new FakeLock(server, "pod-a") { FailWith = () => failures-- > 0 ? new HttpRequestException("blip") : null };
        var retrying = new RetryingLock(inner, new LeaderElectionOptions
        {
            KubernetesApiRetryDelay = TimeSpan.FromMilliseconds(1),
            KubernetesApiMaxRetryDelay = TimeSpan.FromMilliseconds(2),
        }, NullLogger<RetryingLock>.Instance);

        var record = await retrying.GetAsync(default);

        Assert.Null(record);
        Assert.Equal(-1, failures);
    }

    [Fact]
    public async Task A_non_transient_failure_is_not_retried()
    {
        var calls = 0;
        var inner = new FakeLock(new FakeLeaseServer(), "pod-a") { FailWith = () => { calls++; return new InvalidOperationException("RBAC"); } };
        var retrying = new RetryingLock(inner, new LeaderElectionOptions { KubernetesApiRetryDelay = TimeSpan.FromMilliseconds(1) },
            NullLogger<RetryingLock>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => retrying.GetAsync(default));
        Assert.Equal(1, calls);
    }

    private static HttpOperationException Http(HttpStatusCode status) => new($"HTTP {(int)status}")
    {
        Response = new HttpResponseMessageWrapper(new HttpResponseMessage(status), string.Empty),
    };
}
