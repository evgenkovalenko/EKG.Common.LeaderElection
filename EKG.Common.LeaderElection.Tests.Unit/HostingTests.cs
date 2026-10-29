using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EKG.Common.LeaderElection.Tests.Unit;

// PRM-78 plan D6: AddLeaderElection in a real generic host, with Enabled=false (the in-process lock).
public class HostingTests
{
    private static IHost BuildHost(Dictionary<string, string?> settings, Action<IServiceCollection>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddLeaderElection(builder.Configuration);
        configure?.Invoke(builder.Services);
        return builder.Build();
    }

    private static Dictionary<string, string?> Local(params (string Key, string? Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["LeaderElection:LockName"] = "sample",
            ["LeaderElection:Enabled"] = "false",
            ["LeaderElection:RenewalPeriod"] = "00:00:00.200",
        };
        foreach (var (key, value) in extra)
            settings[key] = value;
        return settings;
    }

    [Fact]
    public async Task The_host_starts_the_election_and_a_single_local_instance_leads()
    {
        using var host = BuildHost(Local());
        var election = host.Services.GetRequiredService<ILeaderElection>();
        var released = new TaskCompletionSource<LeaderElectionEventArgs>();
        election.OnLeadRelease += e =>
        {
            released.TrySetResult(e);
            return Task.CompletedTask;
        };

        await host.StartAsync();
        await Drive.UntilAsync(() => election.IsLeader);
        await host.StopAsync();

        var args = await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(args.IsLeaderElectionStopping);
        Assert.False(election.IsLeader);
    }

    [Fact]
    public async Task Invalid_options_fail_the_host_start()
    {
        using var host = BuildHost(Local(("LeaderElection:LockName", "")));

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains("LockName is required", error.Message);
    }

    [Fact]
    public async Task An_acquire_handler_that_times_out_stops_the_application()
    {
        using var host = BuildHost(Local(("LeaderElection:LeaderAcquisitionHandlerTimeout", "00:00:00.300")));
        var election = host.Services.GetRequiredService<ILeaderElection>();
        election.OnLeadAcquire += _ => new TaskCompletionSource().Task;
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        await host.StartAsync();

        await lifetime.ApplicationStopping.WaitHandle.WaitOneAsync(TimeSpan.FromSeconds(5));
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.True(election.LeaderHandlerTimeoutToken.IsCancellationRequested);
        await host.StopAsync();
    }

    [Fact]
    public async Task A_custom_health_is_used()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(Local());
        builder.Services.AddLeaderElection<NeverHealthy>(builder.Configuration);
        using var host = builder.Build();
        var election = host.Services.GetRequiredService<ILeaderElection>();

        await host.StartAsync();
        await Task.Delay(600);

        Assert.False(election.IsLeader);
        Assert.False(election.IsHealthy);
        await host.StopAsync();
    }

    private sealed class NeverHealthy : ILeaderHealth
    {
        public Task<bool> IsHealthy() => Task.FromResult(false);
    }
}

internal static class WaitHandleExtensions
{
    public static Task WaitOneAsync(this WaitHandle handle, TimeSpan timeout) =>
        Task.Run(() => handle.WaitOne(timeout));
}
