using EKG.Common.Cache.Redis;
using EKG.Common.LeaderElection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRedisCache(builder.Configuration);
builder.Services.AddLeaderElection(builder.Configuration);

var app = builder.Build();

app.MapGet("/leader-status", (ILeaderElectionService leaderElection) => new
{
    isLeader = leaderElection.IsLeader,
    instanceId = leaderElection.InstanceId,
    appName = builder.Configuration["LeaderElection:AppName"],
});

app.MapGet("/health", () => Results.Ok("healthy"));

app.Run();
