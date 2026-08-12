namespace Promptly.IntegrationTests;

public sealed class CleanupRunnerTests
{
    [Fact]
    public async Task RunAsync_AttemptsEveryStep_AggregatesFailuresAndPreservesPrimary()
    {
        var primary = new InvalidOperationException("primary failure");
        var calls = new List<string>();
        var steps = new[]
        {
            new CleanupStep(
                "throws",
                TimeSpan.FromSeconds(1),
                _ =>
                {
                    calls.Add("throws");
                    throw new IOException("cleanup failure");
                }),
            new CleanupStep(
                "times out",
                TimeSpan.FromMilliseconds(50),
                async cancellationToken =>
                {
                    calls.Add("times out");
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }),
            new CleanupStep(
                "succeeds",
                TimeSpan.FromSeconds(1),
                _ =>
                {
                    calls.Add("succeeds");
                    return Task.CompletedTask;
                })
        };

        var result = await CleanupRunner.RunAsync(primary, steps);

        var aggregate = Assert.IsType<AggregateException>(result);
        Assert.Same(primary, aggregate.InnerExceptions[0]);
        Assert.IsType<InvalidOperationException>(aggregate.InnerExceptions[1]);
        Assert.IsType<TimeoutException>(aggregate.InnerExceptions[2]);
        Assert.Equal(["throws", "times out", "succeeds"], calls);

        var preserved = await CleanupRunner.RunAsync(
            primary,
            [new("succeeds", TimeSpan.FromSeconds(1), _ => Task.CompletedTask)]);
        Assert.Same(primary, preserved);
    }

    [Fact]
    public async Task RunAsync_BoundsSynchronousDelegateExecution_AndContinuesCleanup()
    {
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var finalStepRan = false;

        var result = await CleanupRunner.RunAsync(
            primaryException: null,
            [
                new(
                    "synchronously blocks",
                    TimeSpan.FromMilliseconds(50),
                    _ =>
                    {
                        started.Set();
                        release.Wait();
                        return Task.CompletedTask;
                    }),
                new(
                    "still runs",
                    TimeSpan.FromSeconds(1),
                    _ =>
                    {
                        finalStepRan = true;
                        return Task.CompletedTask;
                    })
            ]);

        Assert.True(started.IsSet);
        Assert.True(finalStepRan);
        var aggregate = Assert.IsType<AggregateException>(result);
        Assert.IsType<TimeoutException>(Assert.Single(aggregate.InnerExceptions));
        release.Set();
    }
}
