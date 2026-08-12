using System.Net;
using System.Text.Json;

namespace Promptly.IntegrationTests;

public sealed class ProjectIsolationTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task ProjectQueries_IsolateTwoAuthenticatedUsers()
    {
        using var firstUser = await PromptlyApiClient.RegisterAsync(fixture.PrimaryHost.Factory);
        using var secondUser = await PromptlyApiClient.RegisterAsync(fixture.PrimaryHost.Factory);
        var firstProject = await firstUser.CreateProjectAsync("First user's project");
        var secondProject = await secondUser.CreateProjectAsync("Second user's project");

        await AssertProjectListAsync(firstUser, expected: firstProject, excluded: secondProject);
        await AssertProjectListAsync(secondUser, expected: secondProject, excluded: firstProject);

        using var firstReadsSecond = await firstUser.GetAsync($"/api/projects/{secondProject}");
        using var secondReadsFirst = await secondUser.GetAsync($"/api/projects/{firstProject}");
        Assert.Equal(HttpStatusCode.NotFound, firstReadsSecond.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, secondReadsFirst.StatusCode);
    }

    private static async Task AssertProjectListAsync(
        PromptlyApiClient client,
        Guid expected,
        Guid excluded)
    {
        using var response = await client.GetAsync("/api/projects");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var projectIds = document.RootElement
            .EnumerateArray()
            .Select(project => project.GetProperty("id").GetGuid())
            .ToArray();
        Assert.Contains(expected, projectIds);
        Assert.DoesNotContain(excluded, projectIds);
    }
}
