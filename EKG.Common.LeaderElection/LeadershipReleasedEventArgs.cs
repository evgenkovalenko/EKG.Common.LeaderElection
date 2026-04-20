namespace EKG.Common.LeaderElection;

public class LeadershipReleasedEventArgs : EventArgs
{
    public Guid InstanceId { get; init; }
    public string AppName { get; init; } = string.Empty;
    public LeadershipReleasedReason Reason { get; init; }
}
