using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Services;
using Promptly.Domain.ValueObjects;

namespace Promptly.Application.UnitTests;

public sealed class ExpectationDslValidatorTests
{
    public static IEnumerable<object[]> ValidExpectations()
    {
        yield return ["{\"type\":\"contains_text\",\"text\":\"hello\"}"];
        yield return ["{\"type\":\"banned_text\",\"text\":\"secret\"}"];
        yield return ["{\"type\":\"regex_match\",\"pattern\":\"hello.*\"}"];
        yield return ["{\"type\":\"link_pattern\",\"pattern\":\"example.com\"}"];
        yield return ["{\"type\":\"tool_called\",\"tool_name\":\"search\"}"];
        yield return ["{\"type\":\"tool_sequence\",\"sequence\":[\"search\"]}"];
        yield return ["{\"type\":\"llm_judge\",\"rubric\":\"helpful\"}"];
        yield return ["{\"type\":\"groundedness\"}"];
    }

    [Theory]
    [MemberData(nameof(ValidExpectations))]
    public void ValidateExpectation_accepts_the_minimum_payload_for_each_type(string json)
    {
        using var document = JsonDocument.Parse(json);
        var dictionary = JsonSerializer.Deserialize<Dictionary<string, object>>(
            document.RootElement.GetRawText())!;
        var validator = new ExpectationDslValidator();

        var result = validator.ValidateExpectation(dictionary);

        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
    }

    [Theory]
    [InlineData("{\"type\":\"contains_text\"}", "required_expectation_field")]
    [InlineData("{\"type\":\"tool_called\",\"tool_name\":\"\"}", "required_expectation_field")]
    [InlineData("{\"type\":\"tool_sequence\",\"sequence\":[]}", "invalid_tool_sequence")]
    [InlineData("{\"type\":\"llm_judge\",\"rubric\":\"x\",\"min_score\":1.1}", "invalid_score")]
    [InlineData("{\"type\":\"future\"}", "unsupported_expectation_type")]
    public void ValidateExpectationsJson_rejects_malformed_payloads(
        string json,
        string expectedCode)
    {
        var result = new ExpectationDslValidator().ValidateExpectationsJson($"[{json}]");

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == expectedCode);
    }

    [Fact]
    public void ValidateExpectationsJson_rejects_empty_arrays_and_invalid_objects()
    {
        var validator = new ExpectationDslValidator();

        Assert.False(validator.ValidateExpectationsJson("[]").IsValid);
        Assert.False(validator.ValidateExpectationsJson("[{\"type\":\"contains_text\"}]").IsValid);
    }

    [Theory]
    [InlineData("{\"type\":\"llm_judge\",\"rubric\":\"helpful\",\"min_score\":0}")]
    [InlineData("{\"type\":\"groundedness\",\"min_score\":1}")]
    public void ValidateExpectationsJson_accepts_score_boundaries(
        string json)
    {
        var result = new ExpectationDslValidator().ValidateExpectationsJson($"[{json}]");

        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
    }

    [Fact]
    public void ValidateExpectationsJson_rejects_unknown_fields()
    {
        var result = new ExpectationDslValidator().ValidateExpectationsJson(
            "[{\"type\":\"contains_text\",\"text\":\"hello\",\"min_score\":0}]");

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "unknown_expectation_field");
    }

    [Fact]
    public async Task Evaluator_uses_domain_defaults_and_sequence_modes()
    {
        var evaluator = new ExpectationEvaluator(
            NullLogger<ExpectationEvaluator>.Instance,
            new BoundedRegexMatcher());
        var trace = new CanonicalTrace
        {
            Messages = [new Message { Role = "assistant", Content = "HELLO" }],
            ToolCalls =
            [
                new ToolCall { Name = "first", ArgumentsJson = "{}" },
                new ToolCall { Name = "search", ArgumentsJson = "{}" },
                new ToolCall { Name = "summarize", ArgumentsJson = "{}" },
                new ToolCall { Name = "last", ArgumentsJson = "{}" }
            ]
        };

        var contains = await evaluator.EvaluateAsync(
            new Dictionary<string, object> { ["type"] = "contains_text", ["text"] = "hello" },
            trace);
        var subsequence = await evaluator.EvaluateAsync(
            new Dictionary<string, object>
            {
                ["type"] = "tool_sequence",
                ["sequence"] = new List<string> { "search", "summarize" },
                ["exact_sequence"] = false
            },
            trace);
        var exact = await evaluator.EvaluateAsync(
            new Dictionary<string, object>
            {
                ["type"] = "tool_sequence",
                ["sequence"] = new List<string> { "search", "summarize" }
            },
            trace);

        Assert.True(contains.Passed);
        Assert.True(subsequence.Passed);
        Assert.False(exact.Passed);
        Assert.Equal("Pass", contains.Status);
        Assert.Equal("Fail", exact.Status);
    }
}
