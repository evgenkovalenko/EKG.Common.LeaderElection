namespace EKG.Common.LeaderElection;

public class LeaderElectionState
{
    public Guid HolderId { get; set; }
    public int TransitionCount { get; set; }
    public DateTime RenewTime { get; set; }
    public DateTime AcquireTime { get; set; }
}
