using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Promptly.IntegrationTests;

internal sealed class PromptlyApiClient : IDisposable
{
    private readonly HttpClient _client;

    private PromptlyApiClient(HttpClient client, RegisteredUser user)
    {
        _client = client;
        User = user;
    }

    public RegisteredUser User { get; }

    public static async Task<PromptlyApiClient> RegisterAsync(
        PromptlyWebApplicationFactory factory,
        string? email = null,
        string password = "Integration1")
    {
        var client = factory.CreateClient(new()
        {
            AllowAutoRedirect = false
        });
        email ??= $"integration-{Guid.NewGuid():N}@example.test";

        using var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password,
            name = "Integration User"
        });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = document.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Registration response did not contain a token");
        var userId = document.RootElement.GetProperty("user").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Registration response did not contain a user ID");

        return new PromptlyApiClient(client, new(email, userId, token));
    }

    public static PromptlyApiClient FromToken(
        PromptlyWebApplicationFactory factory,
        RegisteredUser user)
    {
        var client = factory.CreateClient(new()
        {
            AllowAutoRedirect = false
        });
        return new PromptlyApiClient(client, user);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", User.Token);
        return await _client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> GetAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        return await SendAsync(request);
    }

    public async Task<HttpResponseMessage> PostJsonAsync(string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        return await SendAsync(request);
    }

    public async Task<Guid> CreateProjectAsync(string? name = null)
    {
        using var response = await PostJsonAsync("/api/projects", new
        {
            name = name ?? $"Integration Project {Guid.NewGuid():N}",
            description = "Created by the integration harness"
        });
        response.EnsureSuccessStatusCode();
        return await ReadIdAsync(response);
    }

    public async Task<Guid> CreateEnvironmentAsync(Guid projectId)
    {
        using var response = await PostJsonAsync($"/api/projects/{projectId}/environments", new
        {
            name = "Integration",
            baseUrl = "https://integration.example.test",
            headers = new Dictionary<string, string>
            {
                ["X-Promptly-Test"] = "integration"
            }
        });
        response.EnsureSuccessStatusCode();
        return await ReadIdAsync(response);
    }

    public async Task<Guid> CreateEndpointAsync(Guid environmentId)
    {
        using var response = await PostJsonAsync($"/api/environments/{environmentId}/endpoints", new
        {
            name = "Integration endpoint",
            path = "/chat",
            httpMethod = "POST",
            timeoutSeconds = 10
        });
        response.EnsureSuccessStatusCode();
        return await ReadIdAsync(response);
    }

    public async Task<Guid> CreateSuiteAsync(Guid projectId)
    {
        using var response = await PostJsonAsync($"/api/suites?projectId={projectId}", new
        {
            name = $"Integration suite {Guid.NewGuid():N}",
            description = "YAML import persistence integration smoke"
        });
        response.EnsureSuccessStatusCode();
        return await ReadIdAsync(response);
    }

    public void Dispose() => _client.Dispose();

    private static async Task<Guid> ReadIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }
}

internal sealed record RegisteredUser(string Email, string Id, string Token);
