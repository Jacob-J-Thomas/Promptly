using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Promptly.IntegrationTests;

internal sealed class ProviderStubProcess : IAsyncDisposable
{
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(5);
    private readonly Process _process;
    private readonly Task<string> _stderr;
    private readonly string _artifactDirectory;
    private readonly string _evidencePath;
    private Task<string>? _stdout;
    private string _stdoutPrefix = string.Empty;
    private string _capturedStdout = string.Empty;
    private string _capturedStderr = string.Empty;

    private ProviderStubProcess(Process process, string artifactDirectory, string evidencePath)
    {
        _process = process;
        _artifactDirectory = artifactDirectory;
        _evidencePath = evidencePath;
        _stderr = process.StandardError.ReadToEndAsync();
    }

    public ushort Port { get; private set; }

    public static async Task<ProviderStubProcess> StartAsync(
        string repositoryRoot,
        string artifactDirectory,
        CancellationToken cancellationToken)
    {
        var evidencePath = Path.Join(artifactDirectory, "provider-requests.jsonl");
        File.WriteAllText(evidencePath, string.Empty);
        var python = OperatingSystem.IsWindows() ? "python" : "python3";
        var providerScriptPath = Path.Join(
            repositoryRoot,
            "Promptly.Worker",
            "scripts",
            "provider_stub.py");

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = artifactDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(providerScriptPath);
        startInfo.ArgumentList.Add("--ephemeral-port");
        startInfo.ArgumentList.Add("--record-evidence");

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the deterministic provider stub");
        var stub = new ProviderStubProcess(process, artifactDirectory, evidencePath);

        try
        {
            await stub.ReadPortHandoffAsync(cancellationToken);
            await stub.WaitUntilReadyAsync(cancellationToken);
            return stub;
        }
        catch (Exception primaryException)
        {
            Exception? failure;
            try
            {
                await stub.DisposeAsync();
                failure = primaryException;
            }
            catch (Exception cleanupException)
            {
                failure = new AggregateException(
                    "Provider startup failed and provider cleanup also failed",
                    primaryException,
                    cleanupException);
            }

            CleanupRunner.Throw(failure);
            throw new UnreachableException();
        }
    }

    public async Task<IReadOnlyList<ProviderRequestEvidence>> ReadEvidenceAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_evidencePath))
        {
            return [];
        }

        await using var stream = new FileStream(
            _evidencePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);
        using var reader = new StreamReader(stream);
        var records = new List<ProviderRequestEvidence>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            records.Add(JsonSerializer.Deserialize<ProviderRequestEvidence>(line)
                ?? throw new JsonException("Provider evidence record was null"));
        }

        return records;
    }

    public async Task<ProviderRequestEvidence> WaitForChatCompletionAsync(
        string correlationId,
        int afterSequence,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evidence = await ReadEvidenceAsync(cancellationToken);
            var match = evidence.SingleOrDefault(record =>
                record.Sequence > afterSequence
                && record.Kind == "chat_completion"
                && record.CorrelationId == correlationId);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        throw new TimeoutException(
            $"Provider did not record chat completion correlation '{correlationId}' within 10 seconds");
    }

    public async ValueTask DisposeAsync()
    {
        var steps = new[]
        {
            new CleanupStep(
                "terminate provider process",
                TimeSpan.FromSeconds(2),
                _ =>
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                    }

                    return Task.CompletedTask;
                }),
            new CleanupStep(
                "wait for provider process exit",
                ProcessExitTimeout,
                cancellationToken => _process.HasExited
                    ? Task.CompletedTask
                    : _process.WaitForExitAsync(cancellationToken)),
            new CleanupStep(
                "drain provider stdout",
                StreamDrainTimeout,
                async cancellationToken =>
                {
                    _stdout ??= _process.StandardOutput.ReadToEndAsync();
                    _capturedStdout = _stdoutPrefix + await _stdout.WaitAsync(cancellationToken);
                }),
            new CleanupStep(
                "drain provider stderr",
                StreamDrainTimeout,
                async cancellationToken =>
                {
                    _capturedStderr = await _stderr.WaitAsync(cancellationToken);
                }),
            new CleanupStep(
                "write provider stdout artifact",
                TimeSpan.FromSeconds(2),
                cancellationToken => File.WriteAllTextAsync(
                    Path.Join(_artifactDirectory, "provider.stdout.log"),
                    _capturedStdout,
                    cancellationToken)),
            new CleanupStep(
                "write provider stderr artifact",
                TimeSpan.FromSeconds(2),
                cancellationToken => File.WriteAllTextAsync(
                    Path.Join(_artifactDirectory, "provider.stderr.log"),
                    _capturedStderr,
                    cancellationToken)),
            new CleanupStep(
                "dispose provider process",
                TimeSpan.FromSeconds(2),
                _ => Task.Run(_process.Dispose))
        };

        var failure = await CleanupRunner.RunAsync(primaryException: null, steps);
        if (failure is not null)
        {
            CleanupRunner.Throw(failure);
        }
    }

    private async Task ReadPortHandoffAsync(CancellationToken cancellationToken)
    {
        using var handoffTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handoffTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        var line = await _process.StandardOutput.ReadLineAsync(handoffTimeout.Token);
        if (line is null)
        {
            throw new InvalidOperationException(
                $"Provider exited before reporting its bound port; exitCode={GetExitCode()}");
        }

        _stdoutPrefix = line + Environment.NewLine;
        _stdout = _process.StandardOutput.ReadToEndAsync();
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.GetProperty("event").GetString() != "provider_listening"
            || !root.GetProperty("port").TryGetUInt16(out var port)
            || port == 0)
        {
            throw new InvalidOperationException($"Provider emitted an invalid port handoff: {line}");
        }

        Port = port;
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(1)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "verification-only");
        var uri = new Uri($"http://127.0.0.1:{Port}/v1/models");
        var deadline = Stopwatch.StartNew();

        Exception? lastConnectionError = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Provider stub exited before becoming ready with code {_process.ExitCode}");
            }

            try
            {
                using var response = await client.GetAsync(uri, cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException exception)
            {
                lastConnectionError = exception;
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastConnectionError = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        throw new TimeoutException(
            "Provider stub did not become ready within 15 seconds",
            lastConnectionError);
    }

    private string GetExitCode() => _process.HasExited ? _process.ExitCode.ToString() : "running";
}

internal sealed record ProviderRequestEvidence
{
    [JsonPropertyName("sequence")]
    public int Sequence { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("authorized")]
    public bool Authorized { get; init; }

    [JsonPropertyName("valid_json")]
    public bool? ValidJson { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("message_count")]
    public int? MessageCount { get; init; }

    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; init; }
}
