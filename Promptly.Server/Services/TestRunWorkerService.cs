using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Promptly.Application.Interfaces;

namespace Promptly.Server.Services;

public class TestRunWorkerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TestRunWorkerService> _logger;
    private readonly int _pollingIntervalSeconds;
    private readonly int _maxConcurrentRuns;
    private readonly SemaphoreSlim _semaphore;
    private readonly object _processingTasksLock = new();
    private readonly HashSet<Task> _processingTasks = [];

    public TestRunWorkerService(
        IServiceProvider serviceProvider,
        ILogger<TestRunWorkerService> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;

        // Configuration
        _pollingIntervalSeconds = configuration.GetValue<int>("TestRunner:PollingIntervalSeconds", 5);
        _maxConcurrentRuns = configuration.GetValue<int>("TestRunner:MaxConcurrentRuns", 2);
        _semaphore = new SemaphoreSlim(_maxConcurrentRuns, _maxConcurrentRuns);

        _logger.LogInformation(
            "TestRunWorkerService initialized: Polling interval = {PollingInterval}s, Max concurrent = {MaxConcurrent}",
            _pollingIntervalSeconds, _maxConcurrentRuns);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TestRunWorkerService starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _semaphore.WaitAsync(stoppingToken);

                var processingTask = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessNextRunAsync(stoppingToken);
                    }
                    finally
                    {
                        _semaphore.Release();
                    }
                });
                TrackProcessingTask(processingTask);

                // Wait before checking for next run
                await Task.Delay(TimeSpan.FromSeconds(_pollingIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("TestRunWorkerService stopping due to cancellation");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in TestRunWorkerService main loop");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        _logger.LogInformation("TestRunWorkerService stopped");
    }

    private async Task ProcessNextRunAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var processingScope = _serviceProvider.CreateScope();
            var testRunProcessor = processingScope.ServiceProvider
                .GetRequiredService<ITestRunProcessor>();

            Guid? runId;
            using (var claimScope = _serviceProvider.CreateScope())
            {
                var testRunWorkerStore = claimScope.ServiceProvider
                    .GetRequiredService<ITestRunWorkerStore>();
                runId = await testRunWorkerStore.ClaimNextQueuedRunAsync(stoppingToken);
            }

            if (!runId.HasValue)
            {
                // No queued runs, return silently
                return;
            }

            _logger.LogInformation("Processing run {RunId}", runId.Value);
            await testRunProcessor.ProcessRunAsync(runId.Value, stoppingToken);

            _logger.LogInformation("Completed processing run {RunId}", runId.Value);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Run processing stopped due to cancellation");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing run");
        }
    }

    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TestRunWorkerService is stopping");
        await base.StopAsync(stoppingToken);

        Task[] processingTasks;
        lock (_processingTasksLock)
        {
            processingTasks = [.. _processingTasks];
        }

        if (processingTasks.Length > 0)
        {
            _logger.LogInformation(
                "Waiting for {RunCount} in-flight run(s) to observe cancellation",
                processingTasks.Length);
            await Task.WhenAll(processingTasks).WaitAsync(stoppingToken);
        }
    }

    private void TrackProcessingTask(Task processingTask)
    {
        lock (_processingTasksLock)
        {
            _processingTasks.Add(processingTask);
        }

        _ = processingTask.ContinueWith(
            completedTask =>
            {
                lock (_processingTasksLock)
                {
                    _processingTasks.Remove(completedTask);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
