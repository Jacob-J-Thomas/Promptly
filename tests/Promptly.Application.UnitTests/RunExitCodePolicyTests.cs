using Promptly.Cli;
using Promptly.Sdk.DotNet;

namespace Promptly.Application.UnitTests;

public sealed class RunExitCodePolicyTests
{
    [Fact]
    public void All_pass_results_return_success()
    {
        var exitCode = RunExitCodePolicy.GetExitCode(
            [Result("Pass")],
            CompletedRun());

        Assert.Equal(RunExitCodePolicy.Success, exitCode);
    }

    [Fact]
    public void Assertion_failures_return_one()
    {
        var exitCode = RunExitCodePolicy.GetExitCode(
            [Result("Pass"), Result("Fail")],
            CompletedRun());

        Assert.Equal(RunExitCodePolicy.AssertionFailure, exitCode);
    }

    [Fact]
    public void Result_errors_return_infrastructure_error_two()
    {
        var exitCode = RunExitCodePolicy.GetExitCode(
            [Result("Error")],
            CompletedRun());

        Assert.Equal(RunExitCodePolicy.InfrastructureError, exitCode);
    }

    [Fact]
    public void A_completed_run_with_zero_results_is_an_infrastructure_error()
    {
        var exitCode = RunExitCodePolicy.GetExitCode([], CompletedRun());

        Assert.Equal(RunExitCodePolicy.InfrastructureError, exitCode);
    }

    [Fact]
    public void A_completed_run_with_missing_results_is_an_infrastructure_error()
    {
        var exitCode = RunExitCodePolicy.GetExitCode(null, CompletedRun());

        Assert.Equal(RunExitCodePolicy.InfrastructureError, exitCode);
    }

    [Fact]
    public void An_unknown_result_status_is_an_infrastructure_error()
    {
        var exitCode = RunExitCodePolicy.GetExitCode(
            [Result("Unexpected")],
            CompletedRun());

        Assert.Equal(RunExitCodePolicy.InfrastructureError, exitCode);
    }

    [Theory]
    [InlineData("Failed", null)]
    [InlineData("Completed", "run result unavailable")]
    [InlineData("Queued", null)]
    [InlineData("Unknown", null)]
    public void Run_level_errors_return_infrastructure_error_two(
        string status,
        string? errorMessage)
    {
        var run = new TestRunResponse
        {
            Status = status,
            CreatedByUserId = "runner",
            ErrorMessage = errorMessage
        };

        var exitCode = RunExitCodePolicy.GetExitCode([], run);

        Assert.Equal(RunExitCodePolicy.InfrastructureError, exitCode);
    }

    private static TestRunResponse CompletedRun() => new()
    {
        Status = "Completed",
        CreatedByUserId = "runner"
    };

    private static TestRunResultResponse Result(string status) => new()
    {
        Status = status
    };
}
