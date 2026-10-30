# EKG.Common.LeaderElection

Leader election on a Kubernetes Lease for EKG services. Among the replicas of a service, at most one is the leader; it
runs the leader-only work and hands it over when it stops, turns unhealthy or is demoted.

Version 2.0 (PRM-78) follows the design of DraftKings.Feeds.LeaderElection, adapted to EKG: .NET 10, Microsoft.Extensions
DI and hosting, no metrics. **1.0 (Redis) is still published** and used by Bonus and Jpe, which don't run on Kubernetes;
see [the migration guide](Docs/Migration-1.x-to-2.0.md) and the [CHANGELOG](CHANGELOG.md).

## How it works

Every instance runs an election cycle every `RenewalPeriod` (2 s) against one `coordination.k8s.io/v1` Lease:

1. **Health**: it asks its `ILeaderHealth` (within `HealthCheckTimeout`). An unhealthy instance doesn't compete, and an
   unhealthy leader steps down.
2. **The leader renews**: if the Lease is still its own and not expired, it writes a new `renewTime` (within
   `RenewalTimeout`, Kubernetes calls retried). A failed or late renewal makes it step down.
3. **A follower tries to acquire**: no Lease → create it; a Lease without a holder, or whose holder hasn't renewed for
   the Lease's duration → take it over (`leaseTransitions + 1`). A live Lease is left alone.
4. **Becoming the leader**: `OnLeadAcquire` handlers run (within `LeaderAcquisitionHandlerTimeout`), and only then does
   `IsLeader` turn true.
5. **Stepping down** (unhealthy, failed renewal, `DemoteAsync`, shutdown): `OnLeadRelease` handlers run (within
   `LeaderDemotionHandlerTimeout`), then the Lease is released — its holder cleared and its times moved into the past, so
   the next instance takes over at its next cycle instead of waiting for expiry.

Writes are optimistic: a replace carries the `resourceVersion` it read, so when two instances race, Kubernetes accepts one
and rejects the other with 409.

**The lease duration** is computed so the Lease can't expire while its holder is still renewing or releasing (split-brain):
`max(RenewalPeriod × 2.5, (RenewalPeriod + HealthCheckTimeout + RenewalTimeout + LeaderDemotionHandlerTimeout) × 1.5)`,
36 s with the defaults. A leader that is killed without releasing is replaced once its Lease expires.

**Handler failures**: an `OnLeadAcquire` that throws demotes the instance (and `OnLeadRelease` runs to undo partial work).
An `OnLeadAcquire` that times out — or, outside a shutdown, an `OnLeadRelease` that throws or times out — leaves the
application in an unknown state: `LeaderHandlerTimeoutToken` is cancelled and the host stops the application.

## Package

```xml
<PackageReference Include="EKG.Common.LeaderElection" Version="2.0.*" />
```

## Registration

```csharp
builder.Services.AddLeaderElection(builder.Configuration);                 // every instance healthy
builder.Services.AddLeaderElection<MyLeaderHealth>(builder.Configuration); // or: your health rule
```

The host starts the election on `ApplicationStarted` (every singleton that subscribes is built by then) and stops it on
`ApplicationStopping` — before the other hosted services stop — so a leader releases the Lease on a graceful shutdown.
Invalid options fail the host's start.

## Usage

```csharp
public sealed class MyWorker(ILeaderElection leader) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (leader.IsLeader)
                await DoLeaderOnlyWorkAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
```

Or react to the transitions (optional; both handlers may be left out):

```csharp
leader.OnLeadAcquire += async e =>
{
    e.Journal.Add("starting the consumer");
    await consumer.StartAsync(e.CancellationToken);
};
leader.OnLeadRelease += async e =>
{
    await consumer.StopAsync();
};
```

| Member | |
|---|---|
| `IsLeader` | True once `OnLeadAcquire` completed, until the step-down starts. |
| `IsHealthy` | The last `ILeaderHealth` answer. |
| `Identity` | The holder identity written to the Lease: the pod name (`HOSTNAME`). |
| `LeaderHandlerTimeoutToken` | Cancelled when a handler failed fatally (see above). |
| `DemoteAsync()` | Step down now; the instance keeps competing. |
| `LeaderElectionEventArgs.Journal` | Notes logged as one entry after the handlers. |

## Configuration

```json
{
  "LeaderElection": {
    "LockName": "prediction-tradeapi-funds"
  }
}
```

| Key | Default | |
|---|---|---|
| `LockName` | *(required)* | The Lease's name; lowercased, must be a DNS-1123 subdomain. |
| `Namespace` | the pod's namespace | |
| `Enabled` | `true` | `false`: no Kubernetes; the Lease lives in the process, so a single instance always leads (while healthy). For docker-compose and local runs. |
| `KubeContext` | — | Outside a cluster, the kubeconfig context to use (e.g. `kind-leader-election`). **Required there**: the current context is never used implicitly, so a local run can't take a Lease in a shared cluster. |
| `RenewalPeriod` | `00:00:02` | Time between cycles. |
| `RenewalTimeout` | `00:00:10` | Bound on one acquire / renew, retries included. |
| `HealthCheckTimeout` | `00:00:02` | |
| `LeaderAcquisitionHandlerTimeout` | `00:00:10` | |
| `LeaderDemotionHandlerTimeout` | `00:00:10` | |
| `KubernetesApiRetryDelay` / `KubernetesApiMaxRetryDelay` | `00:00:00.100` / `00:00:02` | Exponential backoff. Transient failures (network, timeouts, 408, 5xx) are retried as many times as fit in `RenewalTimeout` minus 1 s (7 with the defaults). |

The 1.0 keys `AppName` and `TtlSeconds` are ignored (a warning is logged when present).

## Kubernetes RBAC

The pod's ServiceAccount needs `get`, `create` and `update` on Leases in its namespace:

```yaml
apiVersion: v1
kind: ServiceAccount
metadata:
  name: my-service
---
apiVersion: rbac.authorization.k8s.io/v1
kind: Role
metadata:
  name: my-service-leases
rules:
  - apiGroups: ['coordination.k8s.io']
    resources: ['leases']
    verbs: ['get', 'create', 'update']
---
apiVersion: rbac.authorization.k8s.io/v1
kind: RoleBinding
metadata:
  name: my-service-leases
subjects:
  - kind: ServiceAccount
    name: my-service
roleRef:
  kind: Role
  name: my-service-leases
  apiGroup: rbac.authorization.k8s.io
```

…and the Deployment sets `serviceAccountName: my-service`. Without it every cycle logs "Forbidden to get Lease …". Check
who leads with `kubectl get lease <LockName> -o yaml` (`spec.holderIdentity` is the pod).

## Running tests

**Unit tests** (no dependencies; two electors on an in-memory Lease, fake time):

```bash
dotnet test EKG.Common.LeaderElection.Tests.Unit
```

**E2E tests** (the sample, two replicas, on a local kind cluster; requires Docker, kind and Tilt):

```bash
kind create cluster --name leader-election
tilt up --context kind-leader-election
dotnet test EKG.Common.LeaderElection.Tests.E2E   # LE_E2E_CONTEXT, default kind-leader-election
tilt down
```

The Tiltfile and the tests refuse any context that isn't `kind-*`: they create Leases and delete pods. They check one
leader that holds the Lease, handover on `/demote`, an unhealthy leader stepping down, a graceful pod delete handing over
before expiry, and a killed leader replaced once its Lease expires.

**The sample locally** (`dotnet run --project EKG.Common.LeaderElection.Sample`) runs with `Enabled=false`
(launchSettings): `GET /leader-status`, `POST /demote`, `POST /health/false|true`.

## Publishing

Every push to `main` publishes the package to GitHub Packages as `{major}.{minor}.{run_number}` (from `<Version>` in the
csproj: `2.0.N`). Pull requests build and run the unit tests (`pr.yml`). A fix to 1.0 goes on a `release/1.x` branch cut
from the last 1.0 commit.
