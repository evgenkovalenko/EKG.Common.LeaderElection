namespace EKG.Common.LeaderElection;

public interface ILeaderElectionService
{
    bool IsLeader { get; }
    Guid InstanceId { get; }
}
