using Promptly.Sdk.DotNet;

namespace Promptly.Cli;

/// <summary>
/// Maps a completed run to the exit code used by CI and local shell callers.
/// </summary>
public static class RunExitCodePolicy
{
    public const int Success = 0;
    public const int AssertionFailure = 1;
    public const int InfrastructureError = 2;

    public static int GetExitCode(
        IReadOnlyCollection<TestRunResultResponse>? results,
        TestRunResponse run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.Status == "Failed" || !string.IsNullOrWhiteSpace(run.ErrorMessage))
        {
            return InfrastructureError;
        }

        // A completed run with no result payload is not a failed assertion. It
        // is a transport or API contract failure and must be visible to CI.
        if (results is null)
        {
            return InfrastructureError;
        }

        if (results.Any(result =>
                result.Status == "Error"
                || (result.Status != "Pass" && result.Status != "Fail")))
        {
            return InfrastructureError;
        }

        return results.Any(result => result.Status == "Fail")
            ? AssertionFailure
            : Success;
    }
}
