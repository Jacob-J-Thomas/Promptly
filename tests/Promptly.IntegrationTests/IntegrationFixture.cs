using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Promptly.Application.Data;
using Promptly.Server.Services;
using Testcontainers.PostgreSql;

namespace Promptly.IntegrationTests;

public sealed class IntegrationFixture : IAsyncLifetime
{
    private const string PostgreSqlImage =
        "postgres:18.4-alpine3.23@sha256:996d0920e4ff9df1fc19dacb904492f3c1ec0ec1cc338f0ad7123be7731c5f5e";
    private readonly string _temporaryRoot = Path.Join(
        Path.GetTempPath(),
        "promptly-integration-tests",
        Guid.NewGuid().ToString("N"));
    private PostgreSqlContainer? _postgres;
    private ProviderStubProcess? _provider;
    private IFutureDockerImage? _workerImage;
    private IContainer? _worker;
    private IntegrationTestHost? _primaryHost;
    private int _cleanupStarted;
    private int _hostNumber;

    public string RepositoryRoot { get; } = FindRepositoryRoot();

    public string ArtifactDirectory { get; private set; } = string.Empty;

    internal IntegrationTestHost PrimaryHost => _primaryHost
        ?? throw new InvalidOperationException("The primary integration host has not started");

    public IReadOnlyList<string> AppliedMigrations { get; private set; } = [];

    public int InitialUserCount { get; private set; }

    public int InitialProjectCount { get; private set; }

    public bool BackgroundRunnerRegistered { get; private set; }

    public string DefaultWorkerBaseUrl { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        ArtifactDirectory = Path.Join(
            RepositoryRoot,
            "artifacts",
            "test-results",
            "integration");
        Directory.CreateDirectory(ArtifactDirectory);
        Directory.CreateDirectory(_temporaryRoot);
        File.WriteAllText(
            Path.Join(ArtifactDirectory, "harness.log"),
            $"Promptly integration harness started {DateTimeOffset.UtcNow:O}{Environment.NewLine}");
        foreach (var name in new[]
                 {
                     "postgres.stdout.log",
                     "postgres.stderr.log",
                     "worker.stdout.log",
                     "worker.stderr.log",
                     "provider.stdout.log",
                     "provider.stderr.log"
                 })
        {
            File.WriteAllText(Path.Join(ArtifactDirectory, name), string.Empty);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var databasePassword = $"Promptly-{Guid.NewGuid():N}";
            _postgres = new PostgreSqlBuilder(PostgreSqlImage)
                .WithDatabase("promptly_integration")
                .WithUsername("promptly")
                .WithPassword(databasePassword)
                .Build();
            await _postgres.StartAsync(timeout.Token);
            AppendHarnessLog($"PostgreSQL started from {PostgreSqlImage}");

            _provider = await ProviderStubProcess.StartAsync(
                RepositoryRoot,
                ArtifactDirectory,
                timeout.Token);
            await TestcontainersSettings.ExposeHostPortsAsync(_provider.Port, timeout.Token);
            AppendHarnessLog($"Deterministic provider stub started on host port {_provider.Port}");

            ContainerBuilder workerBuilder;
            var prebuiltImage = Environment.GetEnvironmentVariable("PROMPTLY_INTEGRATION_WORKER_IMAGE");
            if (string.IsNullOrWhiteSpace(prebuiltImage))
            {
                var imageName = $"localhost/promptly-worker-integration:{Guid.NewGuid():N}";
                _workerImage = new ImageFromDockerfileBuilder()
                    .WithContextDirectory(RepositoryRoot)
                    .WithDockerfileDirectory(RepositoryRoot)
                    .WithDockerfile("Promptly.Worker/Dockerfile")
                    .WithName(imageName)
                    .Build();
                await _workerImage.CreateAsync(timeout.Token);
                workerBuilder = new ContainerBuilder(_workerImage);
                AppendHarnessLog($"Built worker image {imageName} from Promptly.Worker/Dockerfile");
            }
            else
            {
                workerBuilder = new ContainerBuilder(prebuiltImage);
                AppendHarnessLog($"Using prebuilt worker image {prebuiltImage}");
            }

            _worker = workerBuilder
                .WithEnvironment("PROMPTLY_LLM_PROVIDER", "openai")
                .WithEnvironment("PROMPTLY_LLM_API_KEY", "verification-only")
                .WithEnvironment("PROMPTLY_LLM_MODEL_DEFAULT", "verification-model")
                .WithEnvironment(
                    "PROMPTLY_LLM_BASE_URL",
                    $"http://host.testcontainers.internal:{_provider.Port}/v1")
                .WithEnvironment("PROMPTLY_LLM_TIMEOUT_SECONDS", "10")
                .WithPortBinding(8000, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                    request => request.ForPort(8000).ForPath("/health/ready"),
                    strategy => strategy.WithTimeout(TimeSpan.FromMinutes(2))))
                .Build();
            await _worker.StartAsync(timeout.Token);

            var mappedWorkerPort = _worker.GetMappedPublicPort(8000);
            DefaultWorkerBaseUrl = new UriBuilder(
                Uri.UriSchemeHttp,
                _worker.Hostname,
                mappedWorkerPort).Uri.GetLeftPart(UriPartial.Authority);
            AppendHarnessLog($"FastAPI worker ready at {DefaultWorkerBaseUrl}");

            using (var workerClient = new HttpClient())
            using (var liveness = await workerClient.GetAsync(
                       new Uri($"{DefaultWorkerBaseUrl}/health/live"),
                       timeout.Token))
            {
                liveness.EnsureSuccessStatusCode();
                await File.WriteAllTextAsync(
                    Path.Join(ArtifactDirectory, "worker-liveness.json"),
                    await liveness.Content.ReadAsStringAsync(timeout.Token),
                    timeout.Token);
            }

            _primaryHost = await CreateHostAsync(DefaultWorkerBaseUrl);
            using var health = await _primaryHost.Client.GetAsync("/health", timeout.Token);
            health.EnsureSuccessStatusCode();

            await using var scope = _primaryHost.Factory.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<PromptlyDbContext>();
            AppliedMigrations = [.. await dbContext.Database.GetAppliedMigrationsAsync(timeout.Token)];
            InitialUserCount = await dbContext.Users.CountAsync(timeout.Token);
            InitialProjectCount = await dbContext.Projects.CountAsync(timeout.Token);
            BackgroundRunnerRegistered = PrimaryHost.Factory.Services
                .GetServices<IHostedService>()
                .Any(service => service is TestRunWorkerService);
            AppendHarnessLog(
                $"Server startup applied {AppliedMigrations.Count} migration(s); " +
                $"initial users={InitialUserCount}; initial projects={InitialProjectCount}; " +
                $"background runner registered={BackgroundRunnerRegistered}");
        }
        catch (Exception primaryException)
        {
            var failure = await DisposeResourcesAsync(primaryException);
            CleanupRunner.Throw(failure ?? primaryException);
            throw new InvalidOperationException("Unreachable after rethrowing integration startup failure");
        }
    }

    public async ValueTask DisposeAsync()
    {
        var failure = await DisposeResourcesAsync(primaryException: null);
        if (failure is not null)
        {
            CleanupRunner.Throw(failure);
        }
    }

    internal Task<IReadOnlyList<ProviderRequestEvidence>> ReadProviderEvidenceAsync(
        CancellationToken cancellationToken)
    {
        var provider = _provider
            ?? throw new InvalidOperationException("The provider stub has not started");
        return provider.ReadEvidenceAsync(cancellationToken);
    }

    internal Task<ProviderRequestEvidence> WaitForProviderChatCompletionAsync(
        string correlationId,
        int afterSequence,
        CancellationToken cancellationToken)
    {
        var provider = _provider
            ?? throw new InvalidOperationException("The provider stub has not started");
        return provider.WaitForChatCompletionAsync(correlationId, afterSequence, cancellationToken);
    }

    internal async Task RunWithHostAsync(
        string workerBaseUrl,
        Func<IntegrationTestHost, Task> operation)
    {
        var host = await CreateHostAsync(workerBaseUrl);
        await host.RunAndDisposeAsync(operation);
    }

    private async Task<IntegrationTestHost> CreateHostAsync(string workerBaseUrl)
    {
        if (_postgres is null)
        {
            throw new InvalidOperationException("The PostgreSQL integration container has not started");
        }

        var hostNumber = Interlocked.Increment(ref _hostNumber);
        var hostRoot = Path.Join(_temporaryRoot, $"host-{hostNumber}");
        var dataProtectionPath = Path.Join(hostRoot, "dataprotection-keys");
        Directory.CreateDirectory(dataProtectionPath);
        var logName = hostNumber == 1 ? "server.log" : $"server-{hostNumber}.log";
        var factory = new PromptlyWebApplicationFactory(
            _postgres.GetConnectionString(),
            workerBaseUrl,
            dataProtectionPath,
            Path.Join(ArtifactDirectory, logName));
        return await IntegrationTestHost.CreateAsync(factory);
    }

    private async Task<Exception?> DisposeResourcesAsync(Exception? primaryException)
    {
        if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0)
        {
            return primaryException;
        }

        var primaryHost = _primaryHost;
        _primaryHost = null;
        var worker = _worker;
        _worker = null;
        var workerImage = _workerImage;
        _workerImage = null;
        var provider = _provider;
        _provider = null;
        var postgres = _postgres;
        _postgres = null;
        var steps = new List<CleanupStep>();

        if (primaryHost is not null)
        {
            steps.Add(new(
                "dispose primary ASP.NET Core test host",
                TimeSpan.FromSeconds(25),
                async _ =>
                {
                    var failure = await primaryHost.DisposeResourcesAsync(primaryException: null);
                    if (failure is not null)
                    {
                        CleanupRunner.Throw(failure);
                    }
                }));
        }

        if (worker is not null)
        {
            steps.Add(new(
                "capture FastAPI worker logs",
                TimeSpan.FromSeconds(10),
                cancellationToken => CaptureContainerLogsAsync(worker, "worker", cancellationToken)));
            steps.Add(new(
                "dispose FastAPI worker container",
                TimeSpan.FromSeconds(30),
                _ => worker.DisposeAsync().AsTask()));
        }

        if (workerImage is not null)
        {
            steps.Add(new(
                "dispose locally built FastAPI worker image",
                TimeSpan.FromSeconds(30),
                _ => workerImage.DisposeAsync().AsTask()));
        }

        if (provider is not null)
        {
            steps.Add(new(
                "dispose deterministic provider process",
                TimeSpan.FromSeconds(30),
                _ => provider.DisposeAsync().AsTask()));
        }

        if (postgres is not null)
        {
            steps.Add(new(
                "capture PostgreSQL logs",
                TimeSpan.FromSeconds(10),
                cancellationToken => CaptureContainerLogsAsync(postgres, "postgres", cancellationToken)));
            steps.Add(new(
                "dispose PostgreSQL container",
                TimeSpan.FromSeconds(30),
                _ => postgres.DisposeAsync().AsTask()));
        }

        steps.Add(new(
            "record harness shutdown",
            TimeSpan.FromSeconds(2),
            _ => Task.Run(() => AppendHarnessLog(
                $"Promptly integration harness cleanup attempted {DateTimeOffset.UtcNow:O}"))));
        steps.Add(new(
            "delete temporary integration host files",
            TimeSpan.FromSeconds(5),
            _ => Task.Run(() =>
            {
                if (Directory.Exists(_temporaryRoot))
                {
                    Directory.Delete(_temporaryRoot, recursive: true);
                }
            })));

        return await CleanupRunner.RunAsync(primaryException, steps);
    }

    private async Task CaptureContainerLogsAsync(
        IContainer container,
        string prefix,
        CancellationToken cancellationToken)
    {
        var (stdout, stderr) = await container.GetLogsAsync(
            DateTime.MinValue,
            DateTime.MaxValue,
            timestampsEnabled: false,
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Join(ArtifactDirectory, $"{prefix}.stdout.log"),
            stdout,
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Join(ArtifactDirectory, $"{prefix}.stderr.log"),
            stderr,
            cancellationToken);
    }

    private void AppendHarnessLog(string message)
    {
        File.AppendAllText(
            Path.Join(ArtifactDirectory, "harness.log"),
            $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "Promptly.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find Promptly.slnx above {AppContext.BaseDirectory}");
    }
}
