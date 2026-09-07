using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Promptly.Application.Data;
using Promptly.Application.Interfaces;
using Promptly.Domain.Entities;
using PromptlyEnvironment = Promptly.Domain.Entities.Environment;

namespace Promptly.IntegrationTests;

public sealed class EndpointTargetSecurityTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task EndpointApi_RejectsUnsafeTargetsWithoutCreatingOrMutatingState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var user = await PromptlyApiClient.RegisterAsync(fixture.PrimaryHost.Factory);
        var projectId = await user.CreateProjectAsync();
        var environmentId = await user.CreateEnvironmentAsync(projectId);
        var unsafePaths = new[]
        {
            "https://attacker.example.test/capture",
            "//attacker.example.test/capture",
            @"\attacker.example.test\capture"
        };

        foreach (var unsafePath in unsafePaths)
        {
            using var create = await user.PostJsonAsync(
                $"/api/environments/{environmentId}/endpoints",
                new
                {
                    name = "unsafe create",
                    path = unsafePath,
                    httpMethod = "POST",
                    timeoutSeconds = 10
                });

            Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        }

        var endpointId = await user.CreateEndpointAsync(environmentId);

        foreach (var request in unsafePaths.Select(unsafePath => new HttpRequestMessage(
                     HttpMethod.Put,
                     $"/api/endpoints/{endpointId}")
        {
            Content = JsonContent.Create(new
            {
                name = "unsafe update",
                path = unsafePath,
                httpMethod = "DELETE",
                timeoutSeconds = 299
            })
        }))
        {
            using (request)
            using (var update = await user.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
            }
        }

        await using var scope = fixture.PrimaryHost.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PromptlyDbContext>();
        var persistedEndpoints = await dbContext.Endpoints
            .AsNoTracking()
            .Where(endpoint => endpoint.EnvironmentId == environmentId)
            .ToListAsync(cancellationToken);
        var persisted = Assert.Single(persistedEndpoints);

        Assert.Equal(endpointId, persisted.Id);
        Assert.Equal("Integration endpoint", persisted.Name);
        Assert.Equal("/chat", persisted.Path);
        Assert.Equal("POST", persisted.HttpMethod);
        Assert.Equal(10, persisted.TimeoutSeconds);
    }

    [Fact]
    public async Task EndpointExecutor_DoesNotForwardEncryptedCredentialsAcrossRedirect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var redirectListener = new TcpListener(IPAddress.Loopback, 0);
        using var captureListener = new TcpListener(IPAddress.Loopback, 0);
        redirectListener.Start();
        captureListener.Start();
        using var redirectCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        using var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        redirectCancellation.CancelAfter(TimeSpan.FromSeconds(10));
        Task<string>? redirectRequestTask = null;
        Task<string>? captureRequestTask = null;

        try
        {
            var redirectPort = ((IPEndPoint)redirectListener.LocalEndpoint).Port;
            var capturePort = ((IPEndPoint)captureListener.LocalEndpoint).Port;
            var captureUri = new Uri($"http://127.0.0.1:{capturePort}/capture");
            redirectRequestTask = ServeRedirectAsync(
                redirectListener,
                captureUri,
                redirectCancellation.Token);
            captureRequestTask = ServeSuccessAsync(
                captureListener,
                captureCancellation.Token);

            await using var scope = fixture.PrimaryHost.Factory.Services.CreateAsyncScope();
            var encryptionService = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
            var endpointExecutor = scope.ServiceProvider.GetRequiredService<IEndpointExecutor>();
            var secret = $"redirect-secret-{Guid.NewGuid():N}";
            var environment = new PromptlyEnvironment
            {
                Id = Guid.NewGuid(),
                ProjectId = Guid.NewGuid(),
                Name = "redirect security proof",
                BaseUrl = $"http://127.0.0.1:{redirectPort}",
                DefaultHeadersEncryptedJson = encryptionService.Encrypt(
                    JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["X-Promptly-Secret"] = secret
                    }))
            };
            var endpoint = new Endpoint
            {
                Id = Guid.NewGuid(),
                EnvironmentId = environment.Id,
                Name = "redirect",
                Path = "/redirect",
                HttpMethod = "POST",
                TimeoutSeconds = 5
            };
            var testCase = new TestCase
            {
                Id = Guid.NewGuid(),
                SuiteId = Guid.NewGuid(),
                ExternalId = "redirect-security-proof",
                Name = "redirect security proof",
                InputSpecJson = "{\"messages\":[]}",
                ExpectationsJson = "[]"
            };

            var result = await endpointExecutor.ExecuteAsync(
                endpoint,
                environment,
                testCase,
                cancellationToken);
            var redirectRequest = await redirectRequestTask;

            Assert.False(result.Success);
            Assert.Equal((int)HttpStatusCode.Redirect, result.StatusCode);
            Assert.Contains("POST /redirect HTTP/1.1", redirectRequest, StringComparison.Ordinal);
            Assert.Contains(
                $"X-Promptly-Secret: {secret}",
                redirectRequest,
                StringComparison.OrdinalIgnoreCase);

            var captureObservation = await Task.WhenAny(
                captureRequestTask,
                Task.Delay(TimeSpan.FromSeconds(1), cancellationToken));
            Assert.NotSame(captureRequestTask, captureObservation);

            await captureCancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => captureRequestTask!);
        }
        finally
        {
            await redirectCancellation.CancelAsync();
            await captureCancellation.CancelAsync();
            redirectListener.Stop();
            captureListener.Stop();
            await ObserveExpectedShutdownAsync(redirectRequestTask);
            await ObserveExpectedShutdownAsync(captureRequestTask);
        }
    }

    private static async Task<string> ServeRedirectAsync(
        TcpListener listener,
        Uri redirectTarget,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        var request = await ReadRequestAsync(stream, cancellationToken);
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 302 Found\r\n" +
            $"Location: {redirectTarget.AbsoluteUri}\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return request;
    }

    private static async Task<string> ServeSuccessAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        var request = await ReadRequestAsync(stream, cancellationToken);
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/json\r\n" +
            "Content-Length: 2\r\n" +
            "Connection: close\r\n\r\n{}");
        await stream.WriteAsync(response, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return request;
    }

    private static async Task<string> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        var requestLines = new List<string>();
        var contentLength = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line && line.Length > 0)
        {
            requestLines.Add(line);
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
            }
        }

        if (contentLength > 0)
        {
            var requestBody = new char[contentLength];
            _ = await reader.ReadBlockAsync(requestBody, cancellationToken);
        }

        return string.Join("\r\n", requestLines);
    }

    private static async Task ObserveExpectedShutdownAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        var exception = await Record.ExceptionAsync(() => task);
        Assert.True(
            exception is null or OperationCanceledException or SocketException or ObjectDisposedException,
            $"Unexpected listener shutdown exception: {exception}");
    }
}
