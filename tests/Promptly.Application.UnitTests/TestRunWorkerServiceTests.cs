using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Promptly.Application.Interfaces;
using Promptly.Domain.Enums;
using Promptly.Server.Services;

namespace Promptly.Application.UnitTests;

public sealed class TestRunWorkerServiceTests
{
    [Fact]
    public async Task Worker_uses_default_configuration_and_returns_silently_when_no_run_is_queued()
    {
        var claimEntered = NewCompletion();
        var store = new StubWorkerStore(cancellationToken =>
        {
            claimEntered.TrySetResult();
            return Task.FromResult<Guid?>(null);
        });
        var processor = new StubRunProcessor((_, _) =>
            throw new InvalidOperationException("The processor must not be resolved."));
        var scopeFactory = new RecordingScopeFactory(store, processor);
        var logger = new RecordingLogger<TestRunWorkerService>();
        using var worker = CreateWorker(scopeFactory, logger);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await claimEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await scopeFactory.WaitForScopeDisposalAsync(1, TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, store.ClaimCount);
        Assert.Equal(0, processor.ProcessCount);
        Assert.Equal(1, scopeFactory.ScopeCount);
        Assert.Contains(
            logger.Messages,
            message => message.Contains(
                "Polling interval = 5s, Max concurrent = 2",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Messages,
            message => message.Contains("Processing run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Worker_claims_and_processes_in_distinct_ordered_scopes()
    {
        var runId = Guid.NewGuid();
        var processCompleted = NewCompletion();
        var store = new StubWorkerStore(_ => Task.FromResult<Guid?>(runId));
        var processor = new StubRunProcessor((observedRunId, cancellationToken) =>
        {
            Assert.Equal(runId, observedRunId);
            Assert.True(cancellationToken.CanBeCanceled);
            processCompleted.TrySetResult();
            return Task.CompletedTask;
        });
        var scopeFactory = new RecordingScopeFactory(store, processor);
        var logger = new RecordingLogger<TestRunWorkerService>();
        using var worker = CreateWorker(scopeFactory, logger);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await processCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await scopeFactory.WaitForScopeDisposalAsync(2, TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, store.ClaimCount);
        Assert.Equal(1, processor.ProcessCount);
        Assert.Equal(
            [
                "scope:1:created",
                "scope:1:resolve:ITestRunWorkerStore",
                "scope:1:disposed",
                "scope:2:created",
                "scope:2:resolve:ITestRunProcessor",
                "scope:2:disposed"
            ],
            scopeFactory.Events);
        Assert.Contains(
            logger.Messages,
            message => message.Contains($"Processing run {runId}", StringComparison.Ordinal));
        Assert.Contains(
            logger.Messages,
            message => message.Contains(
                $"Completed processing run {runId}",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_contains_claim_and_processor_failures(bool failProcessor)
    {
        var runId = Guid.NewGuid();
        var expectedException = new InvalidOperationException(
            failProcessor ? "processor failure" : "claim failure");
        var store = new StubWorkerStore(_ => failProcessor
            ? Task.FromResult<Guid?>(runId)
            : Task.FromException<Guid?>(expectedException));
        var processor = new StubRunProcessor((_, _) =>
            Task.FromException(expectedException));
        var scopeFactory = new RecordingScopeFactory(store, processor);
        var logger = new RecordingLogger<TestRunWorkerService>();
        var errorLogged = logger.WaitForMessageAsync(
            "Error processing run",
            TestContext.Current.CancellationToken);
        using var worker = CreateWorker(scopeFactory, logger);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await errorLogged;
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, store.ClaimCount);
        Assert.Equal(failProcessor ? 1 : 0, processor.ProcessCount);
        Assert.Equal(failProcessor ? 2 : 1, scopeFactory.ScopeCount);
        var log = Assert.Single(
            logger.Entries,
            entry => entry.Message.Contains("Error processing run", StringComparison.Ordinal));
        Assert.Same(expectedException, log.Exception);
        Assert.All(
            Enumerable.Range(1, scopeFactory.ScopeCount),
            scopeId => Assert.Contains($"scope:{scopeId}:disposed", scopeFactory.Events));
    }

    [Fact]
    public async Task Worker_treats_uncorrelated_operation_cancellation_as_a_processing_error()
    {
        var syntheticCancellation = new OperationCanceledException("not the stopping token");
        var store = new StubWorkerStore(_ =>
            Task.FromException<Guid?>(syntheticCancellation));
        var scopeFactory = new RecordingScopeFactory(store, StubRunProcessor.Unused());
        var logger = new RecordingLogger<TestRunWorkerService>();
        var errorLogged = logger.WaitForMessageAsync(
            "Error processing run",
            TestContext.Current.CancellationToken);
        using var worker = CreateWorker(scopeFactory, logger);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await errorLogged;
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.Contains(
            logger.Entries,
            entry => ReferenceEquals(entry.Exception, syntheticCancellation));
        Assert.DoesNotContain(
            logger.Messages,
            message => message.Contains(
                "Run processing stopped due to cancellation",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Worker_observes_cancellation_during_claim_and_disposes_the_scope()
    {
        var claimEntered = NewCompletion();
        var store = new StubWorkerStore(async cancellationToken =>
        {
            claimEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        });
        var scopeFactory = new RecordingScopeFactory(store, StubRunProcessor.Unused());
        var logger = new RecordingLogger<TestRunWorkerService>();
        var cancellationLogged = logger.WaitForMessageAsync(
            "Run processing stopped due to cancellation",
            TestContext.Current.CancellationToken);
        using var worker = CreateWorker(scopeFactory, logger);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await claimEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);
        await cancellationLogged;

        Assert.Contains("scope:1:disposed", scopeFactory.Events);
        Assert.Contains(
            logger.Messages,
            message => message.Contains(
                "TestRunWorkerService stopping due to cancellation",
                StringComparison.Ordinal));
        Assert.Contains(
            logger.Messages,
            message => message.Contains("TestRunWorkerService stopped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Worker_honors_configured_concurrency_and_stop_waits_for_in_flight_processing()
    {
        var firstRunId = Guid.NewGuid();
        var secondRunId = Guid.NewGuid();
        var firstEntered = NewCompletion();
        var secondEntered = NewCompletion();
        var releaseFirst = NewCompletion();
        var releaseSecond = NewCompletion();
        var claimIndex = 0;
        var activeProcessors = 0;
        var maximumActiveProcessors = 0;
        var store = new StubWorkerStore(async cancellationToken =>
        {
            var index = Interlocked.Increment(ref claimIndex);
            return index switch
            {
                1 => firstRunId,
                2 => secondRunId,
                _ => await WaitForCancellationAsync(cancellationToken)
            };
        });
        var processor = new StubRunProcessor(async (runId, _) =>
        {
            var active = Interlocked.Increment(ref activeProcessors);
            UpdateMaximum(ref maximumActiveProcessors, active);
            try
            {
                if (runId == firstRunId)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task;
                }
                else
                {
                    Assert.Equal(secondRunId, runId);
                    secondEntered.TrySetResult();
                    await releaseSecond.Task;
                }
            }
            finally
            {
                Interlocked.Decrement(ref activeProcessors);
            }
        });
        var scopeFactory = new RecordingScopeFactory(store, processor);
        var logger = new RecordingLogger<TestRunWorkerService>();
        var configuration = BuildConfiguration(
            ("TestRunner:PollingIntervalSeconds", "0"),
            ("TestRunner:MaxConcurrentRuns", "1"));
        using var worker = CreateWorker(scopeFactory, logger, configuration);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await firstEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, store.ClaimCount);

        releaseFirst.TrySetResult();
        await secondEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, maximumActiveProcessors);

        var waitingLogged = logger.WaitForMessageAsync(
            "Waiting for 1 in-flight run(s)",
            TestContext.Current.CancellationToken);
        var stopTask = worker.StopAsync(TestContext.Current.CancellationToken);
        await waitingLogged;
        Assert.False(stopTask.IsCompleted);

        releaseSecond.TrySetResult();
        await stopTask;
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, processor.ProcessCount);
        Assert.Equal(1, maximumActiveProcessors);
        Assert.Contains(
            logger.Messages,
            message => message.Contains(
                "Polling interval = 0s, Max concurrent = 1",
                StringComparison.Ordinal));
        Assert.Equal(
            [
                "scope:1:created",
                "scope:1:resolve:ITestRunWorkerStore",
                "scope:1:disposed",
                "scope:2:created",
                "scope:2:resolve:ITestRunProcessor",
                "scope:2:disposed",
                "scope:3:created",
                "scope:3:resolve:ITestRunWorkerStore",
                "scope:3:disposed",
                "scope:4:created",
                "scope:4:resolve:ITestRunProcessor",
                "scope:4:disposed"
            ],
            scopeFactory.Events);
    }

    [Fact]
    public async Task Worker_shutdown_does_not_cancel_a_tracked_task_before_its_delegate_runs()
    {
        var store = new StubWorkerStore(_ => Task.FromResult<Guid?>(null));
        var scopeFactory = new RecordingScopeFactory(store, StubRunProcessor.Unused());
        var logger = new RecordingLogger<TestRunWorkerService>();
        var mainLoopErrorLogged = logger.WaitForMessageAsync(
            "Error in TestRunWorkerService main loop",
            TestContext.Current.CancellationToken);
        var configuration = BuildConfiguration(
            ("TestRunner:PollingIntervalSeconds", int.MaxValue.ToString()),
            ("TestRunner:MaxConcurrentRuns", "1"));
        using var worker = CreateWorker(scopeFactory, logger, configuration);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await mainLoopErrorLogged;
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, store.ClaimCount);
        Assert.Contains("scope:1:disposed", scopeFactory.Events);
        var error = Assert.Single(
            logger.Entries,
            entry => entry.Message.Contains(
                "Error in TestRunWorkerService main loop",
                StringComparison.Ordinal));
        Assert.IsType<ArgumentOutOfRangeException>(error.Exception);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Worker_rejects_non_positive_max_concurrency(string configuredValue)
    {
        var configuration = BuildConfiguration(
            ("TestRunner:MaxConcurrentRuns", configuredValue));
        var scopeFactory = new RecordingScopeFactory(
            StubWorkerStore.Unused(),
            StubRunProcessor.Unused());
        var logger = new RecordingLogger<TestRunWorkerService>();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateWorker(scopeFactory, logger, configuration));
    }

    private static TestRunWorkerService CreateWorker(
        RecordingScopeFactory scopeFactory,
        RecordingLogger<TestRunWorkerService> logger,
        IConfiguration? configuration = null) =>
        new(
            new RootServiceProvider(scopeFactory),
            logger,
            configuration ?? BuildConfiguration());

    private static IConfiguration BuildConfiguration(
        params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(
                pair => pair.Key,
                pair => (string?)pair.Value,
                StringComparer.Ordinal))
            .Build();

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<Guid?> WaitForCancellationAsync(
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return null;
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        var observed = Volatile.Read(ref maximum);
        while (candidate > observed)
        {
            var prior = Interlocked.CompareExchange(ref maximum, candidate, observed);
            if (prior == observed)
            {
                return;
            }

            observed = prior;
        }
    }

    private sealed class StubWorkerStore(
        Func<CancellationToken, Task<Guid?>> claim) : ITestRunWorkerStore
    {
        private int _claimCount;

        public int ClaimCount => Volatile.Read(ref _claimCount);

        public Task<Guid?> ClaimNextQueuedRunAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _claimCount);
            return claim(cancellationToken);
        }

        public Task<WorkerRunLoadResult> LoadRunForProcessingAsync(
            Guid runId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateRunStatusAsync(
            Guid runId,
            TestRunStatus status,
            string? summaryJson = null,
            string? errorMessage = null) =>
            throw new NotSupportedException();

        public static StubWorkerStore Unused() =>
            new(_ => throw new InvalidOperationException("The store must not be used."));
    }

    private sealed class StubRunProcessor(
        Func<Guid, CancellationToken, Task> process) : ITestRunProcessor
    {
        private int _processCount;

        public int ProcessCount => Volatile.Read(ref _processCount);

        public Task ProcessRunAsync(
            Guid runId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _processCount);
            return process(runId, cancellationToken);
        }

        public static StubRunProcessor Unused() =>
            new((_, _) => throw new InvalidOperationException(
                "The processor must not be used."));
    }

    private sealed class RootServiceProvider(
        IServiceScopeFactory scopeFactory) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceScopeFactory) ? scopeFactory : null;
    }

    private sealed class RecordingScopeFactory(
        ITestRunWorkerStore store,
        ITestRunProcessor processor) : IServiceScopeFactory
    {
        private readonly ConcurrentQueue<string> _events = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _disposals = new();
        private int _scopeCount;

        public IReadOnlyList<string> Events => [.. _events];

        public int ScopeCount => Volatile.Read(ref _scopeCount);

        public IServiceScope CreateScope()
        {
            var scopeId = Interlocked.Increment(ref _scopeCount);
            var disposal = NewCompletion();
            Assert.True(_disposals.TryAdd(scopeId, disposal));
            _events.Enqueue($"scope:{scopeId}:created");
            return new RecordingScope(
                new ScopeServiceProvider(scopeId, store, processor, _events),
                () =>
                {
                    _events.Enqueue($"scope:{scopeId}:disposed");
                    disposal.TrySetResult();
                });
        }

        public async Task WaitForScopeDisposalAsync(
            int scopeId,
            CancellationToken cancellationToken)
        {
            TaskCompletionSource? disposal = null;
            while (!_disposals.TryGetValue(scopeId, out disposal))
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
            }

            await disposal.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ScopeServiceProvider(
        int scopeId,
        ITestRunWorkerStore store,
        ITestRunProcessor processor,
        ConcurrentQueue<string> events) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            events.Enqueue($"scope:{scopeId}:resolve:{serviceType.Name}");
            if (serviceType == typeof(ITestRunWorkerStore))
            {
                return store;
            }

            if (serviceType == typeof(ITestRunProcessor))
            {
                return processor;
            }

            return null;
        }
    }

    private sealed class RecordingScope(
        IServiceProvider serviceProvider,
        Action dispose) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;

        public void Dispose() => dispose();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly object _sync = new();
        private readonly List<LogEntry> _entries = [];
        private readonly List<(string Fragment, TaskCompletionSource Completion)> _waiters = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_sync)
                {
                    return [.. _entries];
                }
            }
        }

        public IReadOnlyList<string> Messages =>
            Entries.Select(entry => entry.Message).ToArray();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            NoopDisposable.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (_sync)
            {
                _entries.Add(new LogEntry(logLevel, message, exception));
                foreach (var waiter in _waiters.Where(waiter =>
                    message.Contains(waiter.Fragment, StringComparison.Ordinal)))
                {
                    waiter.Completion.TrySetResult();
                }
            }
        }

        public Task WaitForMessageAsync(
            string fragment,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (_entries.Any(entry =>
                    entry.Message.Contains(fragment, StringComparison.Ordinal)))
                {
                    return Task.CompletedTask;
                }

                var completion = NewCompletion();
                _waiters.Add((fragment, completion));
                return completion.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        Exception? Exception);

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
