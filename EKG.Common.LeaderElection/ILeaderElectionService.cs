namespace EKG.Common.LeaderElection;

public interface ILeaderElectionService
{
    bool IsLeader { get; }
    Guid InstanceId { get; }

    event EventHandler<LeadershipAcquiredEventArgs> LeadershipAcquired;
    event EventHandler<LeadershipReleasedEventArgs> LeadershipReleased;

    Task DemoteLeadershipAsync();
}
