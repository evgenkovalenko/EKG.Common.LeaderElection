using System.Net;
using System.Net.Sockets;
using k8s.Autorest;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace EKG.Common.LeaderElection.Locks;

// PRM-78 plan D5: transient Kubernetes API failures (network, timeouts, 408, 5xx) are retried with exponential backoff,
// at most LeaderElectionOptions.KubernetesApiMaxRetryAttempts times -- the count whose backoff fits in RenewalTimeout.
// The election bounds the whole call by RenewalTimeout anyway. 404 / 409 aren't failures (LeaseLock answers them) and
// 403 is a configuration error: neither is retried.
internal sealed class RetryingLock : ILock
{
    private readonly ILock _inner;
    private readonly ResiliencePipeline _pipeline;

    public RetryingLock(ILock inner, LeaderElectionOptions options, ILogger<RetryingLock> logger)
    {
        _inner = inner;
        var attempts = options.KubernetesApiMaxRetryAttempts;
        _pipeline = attempts == 0
            ? ResiliencePipeline.Empty
            : new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransient),
                    MaxRetryAttempts = attempts,
                    Delay = options.KubernetesApiRetryDelay,
                    MaxDelay = options.KubernetesApiMaxRetryDelay,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = false,
                    OnRetry = args =>
                    {
                        logger.LogWarning("Kubernetes API call for Lease {Lease} failed (attempt {Attempt} of {Max}): {Error}; retrying in {Delay} ms",
                            inner.Describe(), args.AttemptNumber + 1, attempts + 1, args.Outcome.Exception?.Message, args.RetryDelay.TotalMilliseconds);
                        return ValueTask.CompletedTask;
                    },
                })
                .Build();
    }

    public string Identity => _inner.Identity;

    public string Describe() => _inner.Describe();

    public Task<LeaderElectionRecord?> GetAsync(CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(async ct => await _inner.GetAsync(ct), cancellationToken).AsTask();

    public Task<bool> CreateAsync(LeaderElectionRecord record, CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(async ct => await _inner.CreateAsync(record, ct), cancellationToken).AsTask();

    public Task<bool> UpdateAsync(LeaderElectionRecord record, CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(async ct => await _inner.UpdateAsync(record, ct), cancellationToken).AsTask();

    internal static bool IsTransient(Exception e) => e switch
    {
        HttpOperationException { Response: null } => true,
        HttpOperationException h => (int)h.Response.StatusCode >= 500 || h.Response.StatusCode == HttpStatusCode.RequestTimeout,
        HttpRequestException or SocketException or TimeoutException => true,
        _ => false,
    };
}
