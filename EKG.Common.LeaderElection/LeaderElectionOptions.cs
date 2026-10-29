namespace EKG.Common.LeaderElection;

/// <summary>
/// Configuration section <c>LeaderElection</c> (PRM-78 plan D3). Only <see cref="LockName"/> is required; the timings
/// default to DraftKings.Feeds.LeaderElection's, and the Lease duration and the retry count are computed from them.
/// </summary>
public sealed class LeaderElectionOptions
{
    public const string Section = "LeaderElection";

    private string _lockName = string.Empty;

    /// <summary>The Lease's name; lowercased (Kubernetes names are DNS-1123 subdomains).</summary>
    public string LockName
    {
        get => _lockName;
        set => _lockName = (value ?? string.Empty).ToLowerInvariant();
    }

    /// <summary>The Lease's namespace. Default: the pod's own namespace, else <c>default</c>.</summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// False: no Kubernetes calls, the Lease lives in this process -- one instance, always the leader (while healthy).
    /// For docker-compose and local runs.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Outside a cluster, the kubeconfig context to use (e.g. <c>kind-ekg</c>). Required there: the current context is
    /// never used implicitly, so a local run can't take a Lease in a shared cluster by accident.
    /// </summary>
    public string? KubeContext { get; set; }

    /// <summary>Time between election cycles.</summary>
    public TimeSpan RenewalPeriod { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Bound on one acquire or renew, retries included.</summary>
    public TimeSpan RenewalTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>An <see cref="ILeaderHealth"/> slower than this counts as unhealthy.</summary>
    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan LeaderAcquisitionHandlerTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan LeaderDemotionHandlerTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The first backoff between retried Kubernetes calls; doubled per retry up to the maximum.</summary>
    public TimeSpan KubernetesApiRetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan KubernetesApiMaxRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a Lease stays valid without a renewal. Long enough for a full cycle plus the bounded release handler, so
    /// the Lease can't expire while its holder is still finishing (split-brain):
    /// max(RenewalPeriod × 2.5, (RenewalPeriod + HealthCheckTimeout + RenewalTimeout + LeaderDemotionHandlerTimeout) × 1.5).
    /// 36 s with the defaults.
    /// </summary>
    public TimeSpan LeaseDuration => TimeSpan.FromMilliseconds(Math.Max(
        RenewalPeriod.TotalMilliseconds * 2.5,
        (RenewalPeriod + HealthCheckTimeout + RenewalTimeout + LeaderDemotionHandlerTimeout).TotalMilliseconds * 1.5));

    /// <summary>
    /// The most retries whose backoff fits in <see cref="RenewalTimeout"/> after a 1 s budget for the calls themselves
    /// (10 × a 100 ms mean call). 7 with the defaults; 0 when the timeout is 1 s or less.
    /// </summary>
    public int KubernetesApiMaxRetryAttempts
    {
        get
        {
            var available = RenewalTimeout - TimeSpan.FromSeconds(1);
            if (available <= TimeSpan.Zero || KubernetesApiRetryDelay <= TimeSpan.Zero)
                return 0;
            var total = TimeSpan.Zero;
            var delay = KubernetesApiRetryDelay;
            for (var attempts = 0; attempts < 100; attempts++)
            {
                total += delay;
                if (total > available)
                    return attempts;
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, Math.Max(KubernetesApiMaxRetryDelay.Ticks, 1)));
            }
            return 100;
        }
    }
}
