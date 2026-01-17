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

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessNextRunAsync(stoppingToken);
                    }
                    finally
                    {
                        _semaphore.Release();
                    }
                }, stoppingToken);

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
        using var scope = _serviceProvider.CreateScope();
        var testRunService = scope.ServiceProvider.GetRequiredService<ITestRunService>();
        var testRunProcessor = scope.ServiceProvider.GetRequiredService<ITestRunProcessor>();

        try
        {
            // Claim next queued run
            var run = await testRunService.ClaimNextQueuedRunAsync();

            if (run == null)
            {
                // No queued runs, return silently
                return;
            }

            _logger.LogInformation("Processing run {RunId}", run.Id);

            // Process the run
            await testRunProcessor.ProcessRunAsync(run.Id);

            _logger.LogInformation("Completed processing run {RunId}", run.Id);
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
    }
}
