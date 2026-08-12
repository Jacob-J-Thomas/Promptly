namespace Promptly.Application.Interfaces;

public interface ITestRunProcessor
{
    /// <summary>
    /// Process a test run end-to-end: execute tests, evaluate expectations, generate results
    /// </summary>
    Task ProcessRunAsync(Guid runId, CancellationToken cancellationToken = default);
}
