using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Interfaces;
using Promptly.Domain.ValueObjects;
using Promptly.Infrastructure.Clients;

namespace Promptly.Application.UnitTests;

public sealed class PythonWorkerLiveContractTests
{
    private const string OptInVariable = "PROMPTLY_RUN_WORKER_CONTRACT_TESTS";
    private const string VerificationKey = "verification-only";

    public static bool LiveContractTestsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(OptInVariable), "1", StringComparison.Ordinal);

    [Fact(
        Skip = $"Set {OptInVariable}=1 to run the live Python worker contract",
        SkipUnless = nameof(LiveContractTestsEnabled),
        Timeout = 60_000)]
    public async Task PythonEvalClient_round_trips_through_real_FastAPI_and_local_provider()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workerRoot = Path.Combine(repositoryRoot, "Promptly.Worker");
        var providerPort = AllocateLoopbackPort();
        await using var provider = StartProcess(
            workerRoot,
            ResolveUvExecutable(),
            new Dictionary<string, string>
            {
                ["PROMPTLY_PROVIDER_STUB_HOST"] = "127.0.0.1",
                ["PROMPTLY_PROVIDER_STUB_PORT"] = providerPort.ToString()
            },
            "run",
            "--frozen",
            "--no-dev",
            "python",
            "scripts/provider_stub.py");
        await WaitForHttpAsync(
            $"http://127.0.0.1:{providerPort}/v1/models",
            new Dictionary<string, string> { ["Authorization"] = $"Bearer {VerificationKey}" },
            TestContext.Current.CancellationToken);

        var providerConfigurations = new[]
        {
            (
                Name: "azureopenai",
                Environment: new Dictionary<string, string>
                {
                    ["PROMPTLY_LLM_PROVIDER"] = "azureopenai",
                    ["PROMPTLY_LLM_AZURE_ENDPOINT"] = $"http://127.0.0.1:{providerPort}"
                },
                VerifyProviderStop: false),
            (
                Name: "openai",
                Environment: new Dictionary<string, string>
                {
                    ["PROMPTLY_LLM_PROVIDER"] = "openai",
                    ["PROMPTLY_LLM_BASE_URL"] = $"http://127.0.0.1:{providerPort}/v1"
                },
                VerifyProviderStop: true)
        };

        foreach (var providerConfiguration in providerConfigurations)
        {
            var workerPort = AllocateLoopbackPort();
            while (workerPort == providerPort)
            {
                workerPort = AllocateLoopbackPort();
            }

            await using var worker = StartProcess(
                workerRoot,
                ResolveUvExecutable(),
                CreateWorkerEnvironment(providerConfiguration.Environment),
                "run",
                "--frozen",
                "--no-dev",
                "uvicorn",
                "main:app",
                "--host",
                "127.0.0.1",
                "--port",
                workerPort.ToString(),
                "--no-access-log");
            var readiness = await WaitForHttpAsync(
                $"http://127.0.0.1:{workerPort}/health/ready",
                headers: null,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                providerConfiguration.Name,
                readiness.GetProperty("llm_provider").GetString());

            using var workerHttpClient = new HttpClient();
            var client = CreatePythonClient(workerPort, workerHttpClient);
            var trace = CreateTrace();
            await AssertSuccessfulRoundTripAsync(
                client,
                trace,
                TestContext.Current.CancellationToken);

            if (providerConfiguration.VerifyProviderStop)
            {
                await provider.StopAsync();
                var unavailable = await client.EvaluateLlmJudgeAsync(
                    "Be accurate",
                    0.8,
                    trace,
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.False(unavailable.Success);
                Assert.Equal(502, unavailable.WorkerStatusCode);
                Assert.Equal(PythonWorkerErrorCodes.UpstreamFailure, unavailable.ErrorCode);
            }
        }
    }

    [Fact(
        Skip = $"Set {OptInVariable}=1 to run live provider-readiness contracts",
        SkipUnless = nameof(LiveContractTestsEnabled),
        Timeout = 60_000)]
    public async Task Health_ready_supports_OpenAI_and_AzureOpenAI_wire_contracts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workerRoot = Path.Combine(repositoryRoot, "Promptly.Worker");
        var providerPort = AllocateLoopbackPort();
        await using var provider = StartProcess(
            workerRoot,
            ResolveUvExecutable(),
            new Dictionary<string, string>
            {
                ["PROMPTLY_PROVIDER_STUB_HOST"] = "127.0.0.1",
                ["PROMPTLY_PROVIDER_STUB_PORT"] = providerPort.ToString()
            },
            "run",
            "--frozen",
            "--no-dev",
            "python",
            "scripts/provider_stub.py");
        await WaitForHttpAsync(
            $"http://127.0.0.1:{providerPort}/v1/models",
            new Dictionary<string, string> { ["Authorization"] = $"Bearer {VerificationKey}" },
            TestContext.Current.CancellationToken);

        var openAiReady = await RunReadinessProbeAsync(
            workerRoot,
            providerPort,
            new Dictionary<string, string>
            {
                ["PROMPTLY_LLM_PROVIDER"] = "openai",
                ["PROMPTLY_LLM_BASE_URL"] = $"http://127.0.0.1:{providerPort}/v1"
            },
            TestContext.Current.CancellationToken);
        Assert.Equal("openai", openAiReady.GetProperty("llm_provider").GetString());

        var azureReady = await RunReadinessProbeAsync(
            workerRoot,
            providerPort,
            new Dictionary<string, string>
            {
                ["PROMPTLY_LLM_PROVIDER"] = "azureopenai",
                ["PROMPTLY_LLM_AZURE_ENDPOINT"] = $"http://127.0.0.1:{providerPort}"
            },
            TestContext.Current.CancellationToken);
        Assert.Equal("azureopenai", azureReady.GetProperty("llm_provider").GetString());
    }

    private static async Task<JsonElement> RunReadinessProbeAsync(
        string workerRoot,
        int providerPort,
        Dictionary<string, string> providerEnvironment,
        CancellationToken cancellationToken)
    {
        var workerPort = AllocateLoopbackPort();
        while (workerPort == providerPort)
        {
            workerPort = AllocateLoopbackPort();
        }
        await using var worker = StartProcess(
            workerRoot,
            ResolveUvExecutable(),
            CreateWorkerEnvironment(providerEnvironment),
            "run",
            "--frozen",
            "--no-dev",
            "uvicorn",
            "main:app",
            "--host",
            "127.0.0.1",
            "--port",
            workerPort.ToString(),
            "--no-access-log");
        return await WaitForHttpAsync(
            $"http://127.0.0.1:{workerPort}/health/ready",
            headers: null,
            cancellationToken);
    }

    private static Dictionary<string, string> CreateWorkerEnvironment(
        Dictionary<string, string> providerEnvironment) =>
        new(providerEnvironment)
        {
            ["PROMPTLY_LLM_API_KEY"] = VerificationKey,
            ["PROMPTLY_LLM_MODEL_DEFAULT"] = "verification-model",
            ["PROMPTLY_LLM_API_VERSION"] = "2024-08-01-preview",
            ["PROMPTLY_LLM_TIMEOUT_SECONDS"] = "5"
        };

    private static async Task AssertSuccessfulRoundTripAsync(
        PythonEvalClient client,
        CanonicalTrace trace,
        CancellationToken cancellationToken)
    {
        var mapping = await client.ProposeMappingAsync(
            "{\"answer\":\"hello\"}",
            "{\"prompt\":\"hi\"}",
            cancellationToken: cancellationToken);
        Assert.True(mapping.Success, mapping.ErrorMessage);
        using (var mappingSpec = JsonDocument.Parse(Assert.IsType<string>(mapping.MappingSpecJson)))
        {
            Assert.Equal(
                "$.answer",
                mappingSpec.RootElement
                    .GetProperty("fallback")
                    .GetProperty("singleAssistantContentPath")
                    .GetString());
        }

        var judge = await client.EvaluateLlmJudgeAsync(
            "Be accurate",
            0.8,
            trace,
            cancellationToken: cancellationToken);
        Assert.True(judge.Success, judge.ErrorMessage);
        Assert.Equal(0.85, judge.Score);
        Assert.Equal("Deterministically accurate", judge.Reason);

        var groundedness = await client.EvaluateGroundednessAsync(
            0.7,
            trace,
            trace.RetrievedDocs,
            cancellationToken: cancellationToken);
        Assert.True(groundedness.Success, groundedness.ErrorMessage);
        Assert.Equal(0.75, groundedness.Score);
        Assert.Equal("Deterministically grounded", groundedness.Reason);
    }

    private static PythonEvalClient CreatePythonClient(int workerPort, HttpClient httpClient)
    {
        var configuration = new ConfigurationManager
        {
            ["PROMPTLY_EVAL_BASE_URL"] = $"http://127.0.0.1:{workerPort}"
        };
        return new PythonEvalClient(
            httpClient,
            configuration,
            NullLogger<PythonEvalClient>.Instance);
    }

    private static CanonicalTrace CreateTrace() => new()
    {
        Messages = [new Message { Role = "assistant", Content = "The answer is supported." }],
        RetrievedDocs = [new RetrievedDoc { Id = "doc-1", Content = "Supporting fact." }]
    };

    private static async Task<JsonElement> WaitForHttpAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = Stopwatch.StartNew();
        Exception? lastError = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (headers != null)
                {
                    foreach (var (name, value) in headers)
                    {
                        request.Headers.TryAddWithoutValidation(name, value);
                    }
                }
                using var response = await client.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(body);
                    return document.RootElement.Clone();
                }
                lastError = new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException)
            {
                lastError = exception;
            }
            await Task.Delay(100, cancellationToken);
        }
        throw new TimeoutException($"Endpoint did not become ready: {url}", lastError);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Promptly.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Promptly repository root");
    }

    private static string ResolveUvExecutable() =>
        Environment.GetEnvironmentVariable("PROMPTLY_UV_EXECUTABLE") is { Length: > 0 } configured
            ? configured
            : "uv";

    private static int AllocateLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static LiveProcess StartProcess(
        string workingDirectory,
        string executable,
        IReadOnlyDictionary<string, string> environment,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {executable}");
        return new LiveProcess(process);
    }

    private sealed class LiveProcess(Process process) : IAsyncDisposable
    {
        private bool stopped;

        public async Task StopAsync()
        {
            if (stopped)
            {
                return;
            }
            stopped = true;
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync();
            }
            finally
            {
                if (process.ExitCode != 0)
                {
                    var standardOutput = await process.StandardOutput.ReadToEndAsync();
                    var standardError = await process.StandardError.ReadToEndAsync();
                    TestContext.Current.SendDiagnosticMessage(
                        $"Process exited with {process.ExitCode}. stdout: {standardOutput}; stderr: {standardError}");
                }
                process.Dispose();
            }
        }
    }
}
