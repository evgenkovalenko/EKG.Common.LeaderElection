# Migrating from 1.x (Redis) to 2.0 (Kubernetes Lease)

2.0 needs Kubernetes: the service's pods must run in a cluster (or with `LeaderElection:Enabled=false` for a single
local instance). Services that only run on docker-compose stay on `1.0.*`.

## 1. Package

```xml
<PackageReference Include="EKG.Common.LeaderElection" Version="2.0.*" />
```

Keep `AddRedisCache` only if the service uses Redis for something else.

## 2. Registration

```csharp
builder.Services.AddLeaderElection(builder.Configuration);                 // unchanged call
builder.Services.AddLeaderElection<MyLeaderHealth>(builder.Configuration); // optional: a health rule
```

## 3. API map

| 1.x | 2.0 |
|---|---|
| `ILeaderElectionService` | `ILeaderElection` |
| `IsLeader` | `IsLeader` (true only after `OnLeadAcquire` completed) |
| `InstanceId` (Guid) | `Identity` (string, the pod name) |
| `event EventHandler<LeadershipAcquiredEventArgs> LeadershipAcquired` | `event Func<LeaderElectionEventArgs, Task> OnLeadAcquire` (bounded; may be omitted) |
| `event EventHandler<LeadershipReleasedEventArgs> LeadershipReleased` (`Reason`) | `event Func<LeaderElectionEventArgs, Task> OnLeadRelease` (`IsLeaderElectionStopping`) |
| `DemoteLeadershipAsync()` | `DemoteAsync()` |
| — | `IsHealthy`, `ILeaderHealth`, `LeaderHandlerTimeoutToken`, `LeaderElectionEventArgs.Journal` |

A service that only polls `IsLeader` changes the interface name and `InstanceId` → `Identity`. A test fake implements
`ILeaderElection` (the events can stay unused).

## 4. Configuration

```json
"LeaderElection": { "LockName": "my-service-leader" }
```

`AppName` and `TtlSeconds` are gone (`LockName` must be lowercase-DNS-safe; the duration is computed). For
docker-compose / local runs add `LeaderElection__Enabled=false`.

## 5. Kubernetes

Give the pods a ServiceAccount with `get`, `create`, `update` on `coordination.k8s.io` `leases` and set
`serviceAccountName` on the Deployment (snippet in the README, "Kubernetes RBAC").
