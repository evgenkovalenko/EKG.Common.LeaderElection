using System.Text.Json;

namespace EKG.Common.LeaderElection.Tests.E2E.Infrastructure;

public class SampleClient(string baseUrl)
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(baseUrl) };

    public async Task<LeaderStatusResponse> GetLeaderStatusAsync()
    {
        var response = await _http.GetAsync("/leader-status");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<LeaderStatusResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;
    }
}

public record LeaderStatusResponse(bool IsLeader, Guid InstanceId, string AppName);
