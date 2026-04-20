namespace EKG.Common.LeaderElection;

public class LeaderElectionOptions
{
    public int TtlSeconds { get; set; } = 15;
    public string AppName { get; set; } = string.Empty;
}
