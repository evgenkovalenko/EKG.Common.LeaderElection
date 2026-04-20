namespace EKG.Common.LeaderElection;

public class LeadershipAcquiredEventArgs : EventArgs
{
    public Guid InstanceId { get; init; }
    public string AppName { get; init; } = string.Empty;
    public int TransitionCount { get; init; }
}
