using System.Net.Http.Json;
using System.Text.Json;
using k8s;
using k8s.Models;

namespace EKG.Common.LeaderElection.Tests.E2E;

// The bench the Tiltfile deploys (k8s/sample.yaml): two le-sample pods and Lease "le-sample" in leader-election-e2e.
// Pods are reached through the API server's pod proxy, so a test can talk to each pod (and to a replacement) without
// port-forwards. Only a kind context is accepted (LE_E2E_CONTEXT, default kind-leader-election): the tests delete pods.
public sealed class SampleCluster : IDisposable
{
    public const string Namespace = "leader-election-e2e";
    public const string LeaseName = "le-sample";
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(10.5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public SampleCluster()
    {
        var context = Environment.GetEnvironmentVariable("LE_E2E_CONTEXT") ?? "kind-leader-election";
        if (!context.StartsWith("kind-", StringComparison.Ordinal))
            throw new InvalidOperationException($"The E2E tests run on kind only (LE_E2E_CONTEXT={context})");
        Client = new Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(currentContext: context));
    }

    public Kubernetes Client { get; }

    public async Task<IReadOnlyList<V1Pod>> ReadyPodsAsync()
    {
        var pods = await Client.CoreV1.ListNamespacedPodAsync(Namespace, labelSelector: "app=le-sample");
        return pods.Items
            .Where(p => p.Metadata.DeletionTimestamp is null
                        && p.Status?.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") == true)
            .ToList();
    }

    public async Task<LeaderStatus> StatusAsync(V1Pod pod)
    {
        using var response = await Client.CoreV1.ConnectGetNamespacedPodProxyWithPathWithHttpMessagesAsync(
            $"{pod.Metadata.Name}:8080", Namespace, "leader-status");
        return (await JsonSerializer.DeserializeAsync<LeaderStatus>(response.Body, Json))!;
    }

    public async Task PostAsync(V1Pod pod, string path)
    {
        using var response = await Client.CoreV1.ConnectPostNamespacedPodProxyWithPathWithHttpMessagesAsync(
            $"{pod.Metadata.Name}:8080", Namespace, path);
    }

    public async Task<V1Lease?> LeaseAsync()
    {
        try
        {
            return await Client.CoordinationV1.ReadNamespacedLeaseAsync(LeaseName, Namespace);
        }
        catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // Every ready pod's status; waits until there are two ready pods and exactly one leader.
    public async Task<(V1Pod Leader, V1Pod Follower)> SettledAsync(TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(90));
        while (true)
        {
            var pods = await ReadyPodsAsync();
            if (pods.Count == 2)
            {
                var statuses = await Task.WhenAll(pods.Select(async p => (Pod: p, Status: await TryStatusAsync(p))));
                var leaders = statuses.Where(s => s.Status?.IsLeader == true).ToList();
                if (leaders.Count == 1 && statuses.All(s => s.Status is not null))
                    return (leaders[0].Pod, statuses.Single(s => s.Pod != leaders[0].Pod).Pod);
            }
            if (DateTime.UtcNow > until)
                throw new TimeoutException("The sample didn't settle on two ready pods with one leader");
            await Task.Delay(500);
        }
    }

    // Waits until a pod other than `previous` leads; returns it and how long it took.
    public async Task<(V1Pod Leader, TimeSpan Took)> NewLeaderAsync(string previous, TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < timeout)
        {
            foreach (var pod in await ReadyPodsAsync())
                if (pod.Metadata.Name != previous && (await TryStatusAsync(pod))?.IsLeader == true)
                    return (pod, DateTime.UtcNow - started);
            await Task.Delay(250);
        }
        throw new TimeoutException($"No pod other than {previous} took over within {timeout}");
    }

    private async Task<LeaderStatus?> TryStatusAsync(V1Pod pod)
    {
        try
        {
            return await StatusAsync(pod);
        }
        catch (Exception)
        {
            return null; // starting or stopping
        }
    }

    public void Dispose() => Client.Dispose();
}

public sealed record LeaderStatus(bool IsLeader, bool IsHealthy, string Identity, string? LockName);
