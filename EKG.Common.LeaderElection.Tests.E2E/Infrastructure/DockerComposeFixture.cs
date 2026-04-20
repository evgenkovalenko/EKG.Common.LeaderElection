using System.Diagnostics;
using Polly;

namespace EKG.Common.LeaderElection.Tests.E2E.Infrastructure;

public class DockerComposeFixture : IAsyncLifetime
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private readonly HttpClient _probe = new() { Timeout = TimeSpan.FromSeconds(3) };

    public async Task InitializeAsync()
    {
        RunDockerCompose("up --build -d");
        await WaitForServicesAsync();
    }

    public Task DisposeAsync()
    {
        RunDockerCompose("down -v");
        _probe.Dispose();
        return Task.CompletedTask;
    }

    public void StopInstance(string serviceName)
        => RunDockerCompose($"stop {serviceName}");

    private void RunDockerCompose(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"compose {args}",
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start docker compose process.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"docker compose {args} failed:\n{stderr.Result}");
    }

    private async Task WaitForServicesAsync()
    {
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new Polly.Retry.RetryStrategyOptions
            {
                MaxRetryAttempts = 60,
                Delay = TimeSpan.FromSeconds(2),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(),
            })
            .Build();

        await pipeline.ExecuteAsync(async ct =>
        {
            var r1 = await _probe.GetAsync("http://localhost:5020/health", ct);
            r1.EnsureSuccessStatusCode();
            var r2 = await _probe.GetAsync("http://localhost:5021/health", ct);
            r2.EnsureSuccessStatusCode();
        });
    }
}
