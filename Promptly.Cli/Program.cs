using System.CommandLine;
using System.Text.Json;
using Promptly.Sdk.DotNet;

var rootCommand = new RootCommand("Promptly CLI - LLM Black-Box Test Harness");

// Trigger command
var triggerCommand = new Command("trigger", "Trigger a new test run");

var baseUrlOption = new Option<string>(
    name: "--base-url",
    description: "Base URL of the Promptly API")
{
    IsRequired = true
};

var apiKeyOption = new Option<string>(
    name: "--api-key",
    description: "API key for authentication")
{
    IsRequired = true
};

var suiteOption = new Option<Guid>(
    name: "--suite",
    description: "Test suite ID")
{
    IsRequired = true
};

var envOption = new Option<Guid>(
    name: "--env",
    description: "Environment ID")
{
    IsRequired = true
};

var endpointOption = new Option<Guid>(
    name: "--endpoint",
    description: "Endpoint ID")
{
    IsRequired = true
};

var mappingOption = new Option<Guid>(
    name: "--mapping",
    description: "Mapping spec ID")
{
    IsRequired = true
};

var commitOption = new Option<string?>(
    name: "--commit",
    description: "Git commit hash (optional)");

var configOption = new Option<string?>(
    name: "--config",
    description: "Config snapshot JSON (optional)");

var waitOption = new Option<bool>(
    name: "--wait",
    description: "Wait for run to complete",
    getDefaultValue: () => false);

var timeoutOption = new Option<int>(
    name: "--timeout",
    description: "Timeout in seconds for waiting (default: 600)",
    getDefaultValue: () => 600);

triggerCommand.AddOption(baseUrlOption);
triggerCommand.AddOption(apiKeyOption);
triggerCommand.AddOption(suiteOption);
triggerCommand.AddOption(envOption);
triggerCommand.AddOption(endpointOption);
triggerCommand.AddOption(mappingOption);
triggerCommand.AddOption(commitOption);
triggerCommand.AddOption(configOption);
triggerCommand.AddOption(waitOption);
triggerCommand.AddOption(timeoutOption);

triggerCommand.SetHandler(async (context) =>
{
    var baseUrl = context.ParseResult.GetValueForOption(baseUrlOption)!;
    var apiKey = context.ParseResult.GetValueForOption(apiKeyOption)!;
    var suite = context.ParseResult.GetValueForOption(suiteOption);
    var env = context.ParseResult.GetValueForOption(envOption);
    var endpoint = context.ParseResult.GetValueForOption(endpointOption);
    var mapping = context.ParseResult.GetValueForOption(mappingOption);
    var commit = context.ParseResult.GetValueForOption(commitOption);
    var config = context.ParseResult.GetValueForOption(configOption);
    var wait = context.ParseResult.GetValueForOption(waitOption);
    var timeout = context.ParseResult.GetValueForOption(timeoutOption);

    try
    {
        using var client = new PromptlyClient(baseUrl, apiKey);

        Console.WriteLine("Triggering test run...");
        var run = await client.TriggerRunAsync(
            suite,
            env,
            endpoint,
            mapping,
            commit,
            config);

        Console.WriteLine($"Test run triggered successfully!");
        Console.WriteLine($"Run ID: {run.Id}");
        Console.WriteLine($"Status: {run.Status}");

        if (wait)
        {
            Console.WriteLine($"Waiting for completion (timeout: {timeout}s)...");
            var completedRun = await client.WaitForCompletionAsync(
                run.Id,
                TimeSpan.FromSeconds(timeout));

            Console.WriteLine($"Run completed with status: {completedRun.Status}");

            if (!string.IsNullOrWhiteSpace(completedRun.SummaryJson))
            {
                var summary = JsonDocument.Parse(completedRun.SummaryJson);
                Console.WriteLine("\nSummary:");
                Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            }

            // Get results
            var results = await client.GetRunResultsAsync(completedRun.Id);
            var failedCount = results.Count(r => r.Status == "Fail");
            var passedCount = results.Count(r => r.Status == "Pass");
            var errorCount = results.Count(r => r.Status == "Error");

            Console.WriteLine($"\nResults: {passedCount} passed, {failedCount} failed, {errorCount} errors");

            if (failedCount > 0 || errorCount > 0)
            {
                Console.WriteLine("\nFailed/Error tests:");
                foreach (var result in results.Where(r => r.Status != "Pass"))
                {
                    Console.WriteLine($"  - {result.TestCaseExternalId} ({result.TestCaseName}): {result.Status}");
                }
                context.ExitCode = 1;
                return;
            }

            context.ExitCode = 0;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        context.ExitCode = 1;
    }
});

// Wait command
var waitCommand = new Command("wait", "Wait for a test run to complete");

var waitBaseUrlOption = new Option<string>(
    name: "--base-url",
    description: "Base URL of the Promptly API")
{
    IsRequired = true
};

var waitApiKeyOption = new Option<string>(
    name: "--api-key",
    description: "API key for authentication")
{
    IsRequired = true
};

var runIdOption = new Option<Guid>(
    name: "--run-id",
    description: "Run ID to wait for")
{
    IsRequired = true
};

var waitTimeoutOption = new Option<int>(
    name: "--timeout",
    description: "Timeout in seconds (default: 600)",
    getDefaultValue: () => 600);

waitCommand.AddOption(waitBaseUrlOption);
waitCommand.AddOption(waitApiKeyOption);
waitCommand.AddOption(runIdOption);
waitCommand.AddOption(waitTimeoutOption);

waitCommand.SetHandler(async (context) =>
{
    var baseUrl = context.ParseResult.GetValueForOption(waitBaseUrlOption)!;
    var apiKey = context.ParseResult.GetValueForOption(waitApiKeyOption)!;
    var runId = context.ParseResult.GetValueForOption(runIdOption);
    var timeout = context.ParseResult.GetValueForOption(waitTimeoutOption);

    try
    {
        using var client = new PromptlyClient(baseUrl, apiKey);

        Console.WriteLine($"Waiting for run {runId} to complete (timeout: {timeout}s)...");
        var run = await client.WaitForCompletionAsync(
            runId,
            TimeSpan.FromSeconds(timeout));

        Console.WriteLine($"Run completed with status: {run.Status}");

        if (!string.IsNullOrWhiteSpace(run.SummaryJson))
        {
            var summary = JsonDocument.Parse(run.SummaryJson);
            Console.WriteLine("\nSummary:");
            Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        }

        // Get results
        var results = await client.GetRunResultsAsync(runId);
        var failedCount = results.Count(r => r.Status == "Fail");
        var passedCount = results.Count(r => r.Status == "Pass");
        var errorCount = results.Count(r => r.Status == "Error");

        Console.WriteLine($"\nResults: {passedCount} passed, {failedCount} failed, {errorCount} errors");

        if (failedCount > 0 || errorCount > 0)
        {
            Console.WriteLine("\nFailed/Error tests:");
            foreach (var result in results.Where(r => r.Status != "Pass"))
            {
                Console.WriteLine($"  - {result.TestCaseExternalId} ({result.TestCaseName}): {result.Status}");
            }
            context.ExitCode = 1;
            return;
        }

        context.ExitCode = 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        context.ExitCode = 1;
    }
});

rootCommand.AddCommand(triggerCommand);
rootCommand.AddCommand(waitCommand);

return await rootCommand.InvokeAsync(args);
