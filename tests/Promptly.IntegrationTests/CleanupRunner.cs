using System.Runtime.ExceptionServices;

namespace Promptly.IntegrationTests;

internal sealed record CleanupStep(
    string Name,
    TimeSpan Timeout,
    Func<CancellationToken, Task> Action);

internal static class CleanupRunner
{
    public static async Task<Exception?> RunAsync(
        Exception? primaryException,
        IEnumerable<CleanupStep> steps)
    {
        var failures = new List<Exception>();

        foreach (var step in steps)
        {
            using var timeout = new CancellationTokenSource(step.Timeout);
            Task? cleanupTask = null;
            try
            {
                cleanupTask = Task.Run(
                    async () => await step.Action(timeout.Token),
                    CancellationToken.None);
                await cleanupTask.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
            {
                ObserveLateFailure(cleanupTask);
                failures.Add(new TimeoutException(
                    $"Cleanup step '{step.Name}' exceeded {step.Timeout}",
                    exception));
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException(
                    $"Cleanup step '{step.Name}' failed",
                    exception));
            }
        }

        if (failures.Count == 0)
        {
            return primaryException;
        }

        if (primaryException is not null)
        {
            failures.Insert(0, primaryException);
        }

        return new AggregateException(
            primaryException is null
                ? "One or more resource cleanup steps failed"
                : "The primary operation failed and one or more resource cleanup steps also failed",
            failures);
    }

    public static void Throw(Exception exception)
    {
        ExceptionDispatchInfo.Capture(exception).Throw();
    }

    private static void ObserveLateFailure(Task? cleanupTask)
    {
        if (cleanupTask is null || cleanupTask.IsCompleted)
        {
            return;
        }

        _ = cleanupTask.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
