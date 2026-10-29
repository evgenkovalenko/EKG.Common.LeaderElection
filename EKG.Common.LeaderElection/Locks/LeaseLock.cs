using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace EKG.Common.LeaderElection.Locks;

// PRM-78 plan D5: the record is a coordination.k8s.io/v1 Lease. Replace sends the resourceVersion last seen, so of two
// instances racing for the Lease, Kubernetes rejects one with 409 Conflict. Transient failures throw (RetryingLock
// retries them); 404 is "no Lease yet", 409 is "lost the race".
internal sealed class LeaseLock : ILock
{
    private readonly Lazy<IKubernetes> _client;
    private readonly string _namespace;
    private readonly string _name;
    private readonly ILogger<LeaseLock> _logger;
    private V1Lease? _lastSeen;

    public LeaseLock(Func<IKubernetes> client, string @namespace, string name, string identity, ILogger<LeaseLock> logger)
    {
        _client = new Lazy<IKubernetes>(client);
        _namespace = @namespace;
        _name = name;
        Identity = identity;
        _logger = logger;
    }

    public string Identity { get; }

    public string Describe() => $"{_namespace}/{_name}";

    public async Task<LeaderElectionRecord?> GetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var lease = await _client.Value.CoordinationV1.ReadNamespacedLeaseAsync(_name, _namespace, cancellationToken: cancellationToken);
            Interlocked.Exchange(ref _lastSeen, lease);
            return ToRecord(lease);
        }
        catch (HttpOperationException e) when (e.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            Interlocked.Exchange(ref _lastSeen, null);
            return null;
        }
        catch (HttpOperationException e) when (e.Response?.StatusCode == HttpStatusCode.Forbidden)
        {
            throw Forbidden(e, "get");
        }
    }

    public async Task<bool> CreateAsync(LeaderElectionRecord record, CancellationToken cancellationToken)
    {
        var lease = new V1Lease
        {
            ApiVersion = "coordination.k8s.io/v1",
            Kind = "Lease",
            Metadata = new V1ObjectMeta { Name = _name, NamespaceProperty = _namespace },
            Spec = ToSpec(record),
        };
        try
        {
            var created = await _client.Value.CoordinationV1.CreateNamespacedLeaseAsync(lease, _namespace, cancellationToken: cancellationToken);
            Interlocked.Exchange(ref _lastSeen, created);
            return true;
        }
        catch (HttpOperationException e) when (e.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            _logger.LogDebug("Lease {Lease} was created by another instance first", Describe());
            return false;
        }
        catch (HttpOperationException e) when (e.Response?.StatusCode == HttpStatusCode.Forbidden)
        {
            throw Forbidden(e, "create");
        }
    }

    public async Task<bool> UpdateAsync(LeaderElectionRecord record, CancellationToken cancellationToken)
    {
        var lease = Volatile.Read(ref _lastSeen)
                    ?? throw new InvalidOperationException("The Lease must be read or created before it's updated");
        var replacement = new V1Lease
        {
            ApiVersion = lease.ApiVersion,
            Kind = lease.Kind,
            Metadata = lease.Metadata, // carries the resourceVersion: the optimistic check
            Spec = ToSpec(record),
        };
        try
        {
            var replaced = await _client.Value.CoordinationV1.ReplaceNamespacedLeaseAsync(replacement, _name, _namespace, cancellationToken: cancellationToken);
            Interlocked.Exchange(ref _lastSeen, replaced);
            return true;
        }
        catch (HttpOperationException e) when (e.Response?.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            _logger.LogDebug("Lease {Lease} changed since it was read ({Status})", Describe(), e.Response?.StatusCode);
            return false;
        }
        catch (HttpOperationException e) when (e.Response?.StatusCode == HttpStatusCode.Forbidden)
        {
            throw Forbidden(e, "update");
        }
    }

    private InvalidOperationException Forbidden(HttpOperationException e, string verb) =>
        new($"Forbidden to {verb} Lease {Describe()}: the pod's ServiceAccount needs a Role with get, create and update on " +
            "coordination.k8s.io leases in this namespace (see EKG.Common.LeaderElection README, \"Kubernetes RBAC\")", e);

    private static LeaderElectionRecord ToRecord(V1Lease lease) => new()
    {
        HolderIdentity = string.IsNullOrEmpty(lease.Spec?.HolderIdentity) ? null : lease.Spec.HolderIdentity,
        LeaseDurationSeconds = lease.Spec?.LeaseDurationSeconds ?? 0,
        AcquireTime = ToOffset(lease.Spec?.AcquireTime),
        RenewTime = ToOffset(lease.Spec?.RenewTime),
        LeaderTransitions = lease.Spec?.LeaseTransitions ?? 0,
    };

    private static V1LeaseSpec ToSpec(LeaderElectionRecord record) => new()
    {
        HolderIdentity = record.HolderIdentity,
        LeaseDurationSeconds = record.LeaseDurationSeconds,
        AcquireTime = record.AcquireTime?.UtcDateTime,
        RenewTime = record.RenewTime?.UtcDateTime,
        LeaseTransitions = record.LeaderTransitions,
    };

    private static DateTimeOffset? ToOffset(DateTime? value) =>
        value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value.ToUniversalTime(), DateTimeKind.Utc));
}
