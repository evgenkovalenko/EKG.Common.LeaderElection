namespace EKG.Common.LeaderElection;

/// <summary>
/// Leader election among the replicas of a service, on a Kubernetes Lease (PRM-78). At most one instance is the leader;
/// it renews the Lease every <see cref="LeaderElectionOptions.RenewalPeriod"/> and loses it when it stops renewing, turns
/// unhealthy (<see cref="ILeaderHealth"/>) or is demoted. The host starts and stops the election
/// (<c>AddLeaderElection</c>); services observe it.
/// </summary>
public interface ILeaderElection
{
    /// <summary>True once <see cref="OnLeadAcquire"/> has completed, until the demotion starts.</summary>
    bool IsLeader { get; }

    /// <summary>The last <see cref="ILeaderHealth"/> answer; an unhealthy instance neither leads nor competes.</summary>
    bool IsHealthy { get; }

    /// <summary>The holder identity written to the Lease: the pod name.</summary>
    string Identity { get; }

    /// <summary>
    /// Cancelled when an acquire or release handler fails or overruns its timeout outside a shutdown: the application's
    /// state is then unknown, and the host stops the application.
    /// </summary>
    CancellationToken LeaderHandlerTimeoutToken { get; }

    /// <summary>
    /// Raised when this instance won the Lease, before <see cref="IsLeader"/> turns true. Must complete within
    /// <see cref="LeaderElectionOptions.LeaderAcquisitionHandlerTimeout"/>. A handler that throws demotes the instance
    /// (and <see cref="OnLeadRelease"/> runs to undo partial work); one that times out stops the application. Optional.
    /// </summary>
    event Func<LeaderElectionEventArgs, Task>? OnLeadAcquire;

    /// <summary>
    /// Raised when this instance stops leading -- unhealthy, a failed renewal, <see cref="DemoteAsync"/> or shutdown --
    /// before the Lease is released. Must complete within <see cref="LeaderElectionOptions.LeaderDemotionHandlerTimeout"/>;
    /// outside a shutdown, a handler that throws or times out stops the application. Optional.
    /// </summary>
    event Func<LeaderElectionEventArgs, Task>? OnLeadRelease;

    /// <summary>
    /// Steps down: <see cref="OnLeadRelease"/>, then the Lease is released. The instance keeps competing and can lead
    /// again. No effect when not the leader.
    /// </summary>
    Task DemoteAsync();
}
