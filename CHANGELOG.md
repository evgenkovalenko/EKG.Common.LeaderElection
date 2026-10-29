# Changelog

## 2.0.0 (PRM-78)

Breaking: a new implementation on a Kubernetes Lease, following DraftKings.Feeds.LeaderElection. See
[the migration guide](Docs/Migration-1.x-to-2.0.md).

- **Lock**: a `coordination.k8s.io/v1` Lease (optimistic replace by `resourceVersion`) instead of the Redis key;
  `EKG.Common.Cache.Redis` is no longer needed. `Enabled: false` keeps the Lease in-process for single-instance runs.
- **API**: `ILeaderElection` replaces `ILeaderElectionService`:
  - async `OnLeadAcquire` / `OnLeadRelease` handlers, each bounded by a timeout; a fatal handler failure cancels
    `LeaderHandlerTimeoutToken` and stops the application;
  - `IsLeader` turns true only after `OnLeadAcquire` completed;
  - `ILeaderHealth`: an unhealthy instance doesn't lead;
  - `Identity` (the pod name) replaces `InstanceId` (a GUID); `DemoteAsync` replaces `DemoteLeadershipAsync`;
  - `LeaderElectionJournal` on the handler args.
- **Options**: `LockName` replaces `AppName`; the lease duration is computed from the timings (`TtlSeconds` is gone);
  Kubernetes calls are retried within `RenewalTimeout`. Invalid options fail the start. Outside a cluster, a kubeconfig
  context must be named (`KubeContext`).
- **Hosting**: started on `ApplicationStarted`, stopped on `ApplicationStopping` (a leader releases the Lease).
- Differences from DraftKings: one package; no metrics; handlers optional; configuration errors fail the start; a Lease
  without `renewTime` counts as expired; the Lease's own duration decides its expiry; a failed release isn't fatal (the
  Lease expires by itself).

## 1.0.x

Redis-backed election (`SET NX` on `leader-election:{AppName}`, 15 s TTL, 5 s loop). Still published; used by Bonus and
Jpe.
