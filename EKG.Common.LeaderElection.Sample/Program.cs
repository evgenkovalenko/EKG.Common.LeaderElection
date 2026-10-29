using EKG.Common.LeaderElection;
using EKG.Common.LeaderElection.Sample;

// The library's sample and E2E target (PRM-78 plan D9): two replicas compete for Lease "le-sample" (k8s/). The E2E
// tests read /leader-status, demote the leader and flip its health.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<SampleHealth>();
builder.Services.AddLeaderElection<SampleHealth>(builder.Configuration);

var app = builder.Build();

var election = app.Services.GetRequiredService<ILeaderElection>();
var logger = app.Services.GetRequiredService<ILogger<Program>>();
election.OnLeadAcquire += e =>
{
    e.Journal.Add("sample: leader-only work started");
    logger.LogInformation("Leading as {Identity}", election.Identity);
    return Task.CompletedTask;
};
election.OnLeadRelease += e =>
{
    e.Journal.Add("sample: leader-only work stopped");
    logger.LogInformation("No longer leading (stopping: {Stopping})", e.IsLeaderElectionStopping);
    return Task.CompletedTask;
};

app.MapGet("/leader-status", (ILeaderElection le) => new
{
    isLeader = le.IsLeader,
    isHealthy = le.IsHealthy,
    identity = le.Identity,
    lockName = builder.Configuration["LeaderElection:LockName"],
});

app.MapPost("/demote", async (ILeaderElection le) =>
{
    await le.DemoteAsync();
    return Results.NoContent();
});

app.MapPost("/health/{healthy:bool}", (bool healthy, SampleHealth health) =>
{
    health.Healthy = healthy;
    return Results.NoContent();
});

app.MapGet("/health", () => Results.Ok("healthy"));

app.Run();
