using System.Text.RegularExpressions;
using k8s;
using Microsoft.Extensions.Options;

namespace EKG.Common.LeaderElection;

// PRM-78 plan D3: checked when the host starts (ValidateOnStart). Unlike DraftKings, invalid options fail the start: a
// service whose leader-only work silently never runs is worse than one that doesn't start.
internal sealed partial class LeaderElectionOptionsValidator : IValidateOptions<LeaderElectionOptions>
{
    private readonly Func<bool> _isInCluster;

    public LeaderElectionOptionsValidator() : this(KubernetesClientConfiguration.IsInCluster)
    {
    }

    internal LeaderElectionOptionsValidator(Func<bool> isInCluster) => _isInCluster = isInCluster;

    public ValidateOptionsResult Validate(string? name, LeaderElectionOptions o)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(o.LockName))
            errors.Add("LeaderElection:LockName is required (the Lease's name)");
        else if (o.LockName.Length > 253 || !DnsSubdomain().IsMatch(o.LockName))
            errors.Add($"LeaderElection:LockName '{o.LockName}' isn't a valid Kubernetes name (DNS-1123 subdomain: a-z, 0-9, '-', '.', at most 253)");

        Positive(errors, nameof(o.RenewalPeriod), o.RenewalPeriod);
        Positive(errors, nameof(o.RenewalTimeout), o.RenewalTimeout);
        Positive(errors, nameof(o.HealthCheckTimeout), o.HealthCheckTimeout);
        Positive(errors, nameof(o.LeaderAcquisitionHandlerTimeout), o.LeaderAcquisitionHandlerTimeout);
        Positive(errors, nameof(o.LeaderDemotionHandlerTimeout), o.LeaderDemotionHandlerTimeout);
        Positive(errors, nameof(o.KubernetesApiRetryDelay), o.KubernetesApiRetryDelay);
        Positive(errors, nameof(o.KubernetesApiMaxRetryDelay), o.KubernetesApiMaxRetryDelay);
        if (o.KubernetesApiMaxRetryDelay < o.KubernetesApiRetryDelay)
            errors.Add($"LeaderElection:KubernetesApiMaxRetryDelay ({o.KubernetesApiMaxRetryDelay}) must be at least KubernetesApiRetryDelay ({o.KubernetesApiRetryDelay})");

        if (o.Enabled && string.IsNullOrWhiteSpace(o.KubeContext) && !_isInCluster())
            errors.Add("Not running in a Kubernetes cluster: set LeaderElection:Enabled=false for a local single-instance run, " +
                       "or LeaderElection:KubeContext to the kubeconfig context to use (the current context is never used implicitly)");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void Positive(List<string> errors, string name, TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
            errors.Add($"LeaderElection:{name} ({value}) must be greater than zero");
    }

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?(\\.[a-z0-9]([-a-z0-9]*[a-z0-9])?)*$")]
    private static partial Regex DnsSubdomain();
}
