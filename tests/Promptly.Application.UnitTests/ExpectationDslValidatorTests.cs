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

    [Theory]
    [InlineData("", "invalid_expectations_json")]
    [InlineData("{}", "invalid_expectations_json")]
    [InlineData("[1]", "invalid_expectation")]
    [InlineData("[\"text\"]", "invalid_expectation")]
    [InlineData("[", "invalid_expectations_json")]
    public void ValidateExpectationsJson_rejects_non_array_and_non_object_documents(
        string json,
        string expectedCode)
    {
        var result = new ExpectationDslValidator().ValidateExpectationsJson(json);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == expectedCode);
    }

    [Theory]
    [InlineData("required_expectation_field", "sequence")]
    [InlineData("invalid_tool_sequence", "sequence")]
    public void ValidateExpectation_rejects_missing_or_empty_native_sequences(
        string expectedCode,
        string expectedPath)
    {
        var expectation = Expectation("tool_sequence");
        if (expectedCode == "invalid_tool_sequence")
        {
            expectation["sequence"] = new List<string> { " " };
        }

        var result = new ExpectationDslValidator().ValidateExpectation(expectation);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue =>
            issue.Code == expectedCode && issue.Path == expectedPath);
    }

    [Fact]
    public void ValidateExpectation_rejects_json_sequences_with_non_string_items()
    {
        using var document = JsonDocument.Parse(
            """{"type":"tool_sequence","sequence":["search",42]}""");
        var expectation = JsonSerializer.Deserialize<Dictionary<string, object>>(
            document.RootElement.GetRawText())!;

        var result = new ExpectationDslValidator().ValidateExpectation(expectation);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "invalid_tool_sequence");
    }

    public static IEnumerable<object[]> NativeScoreValues()
    {
        yield return [0.5d];
        yield return [0.5f];
        yield return [0.5m];
        yield return [1];
    }

    [Theory]
    [MemberData(nameof(NativeScoreValues))]
    public void ValidateExpectation_accepts_supported_native_score_values(object score)
    {
        var result = new ExpectationDslValidator().ValidateExpectation(
            Expectation("groundedness", ("min_score", score)));

        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void ValidateExpectation_rejects_native_scores_outside_the_range(double score)
    {
        var result = new ExpectationDslValidator().ValidateExpectation(
            Expectation("groundedness", ("min_score", score)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "invalid_score");
    }

    [Fact]
    public void ValidateExpectation_rejects_unsupported_native_score_values()
    {
        var result = new ExpectationDslValidator().ValidateExpectation(
            Expectation("groundedness", ("min_score", new object())));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "invalid_score");
    }

    [Fact]
    public void ValidateExpectation_checks_optional_text_and_boolean_types()
    {
        var result = new ExpectationDslValidator().ValidateExpectation(
            Expectation(
                "llm_judge",
                ("rubric", "helpful"),
                ("model", 42),
                ("provider", ""),
                ("min_score", 0.5)));

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Issues.Count(issue => issue.Code == "invalid_expectation_field"));
        Assert.Contains(result.Issues, issue => issue.Path == "model");
        Assert.Contains(result.Issues, issue => issue.Path == "provider");
    }

    [Fact]
    public void ValidateExpectation_rejects_an_invalid_optional_boolean()
    {
        var result = new ExpectationDslValidator().ValidateExpectation(
            Expectation(
                "contains_text",
                ("text", "hello"),
                ("case_insensitive", "yes")));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue =>
            issue.Code == "invalid_expectation_field" && issue.Path == "case_insensitive");
    }

    [Fact]
    public void ValidateExpectation_allows_null_optional_text_values()
    {
        var result = new ExpectationDslValidator().ValidateExpectation(
            Expectation(
                "llm_judge",
                ("rubric", "helpful"),
                ("model", null!),
                ("provider", null!)));

        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
    }

    [Fact]
    public void ExpectationValidationResult_valid_singleton_is_an_empty_contract_result()
    {
        Assert.True(ExpectationValidationResult.Valid.IsValid);
        Assert.Empty(ExpectationValidationResult.Valid.Issues);
    }

    public static IEnumerable<object[]> RegexFailureStatuses()
    {
        yield return [BoundedRegexStatus.InvalidPattern, "invalid_regex_pattern"];
        yield return [BoundedRegexStatus.UnsupportedPattern, "unsupported_regex_construct"];
        yield return [BoundedRegexStatus.PatternTooLong, "regex_pattern_too_long"];
        yield return [BoundedRegexStatus.InputTooLong, "regex_input_too_long"];
        yield return [BoundedRegexStatus.TimedOut, "regex_timeout"];
        yield return [(BoundedRegexStatus)999, "regex_evaluation_error"];
    }

    [Theory]
    [MemberData(nameof(RegexFailureStatuses))]
    public void ValidatePattern_maps_each_regex_failure_code(
        BoundedRegexStatus status,
        string expectedCode)
    {
        var validator = new ExpectationDslValidator(
            new StubRegexMatcher(new BoundedRegexMatchResult(status, [], null)));

        var result = validator.ValidateExpectation(
            Expectation("regex_match", ("pattern", "candidate")));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == expectedCode);
        Assert.Contains(result.Issues, issue => issue.Path == "pattern");
    }

    private static Dictionary<string, object> Expectation(
        string type,
        params (string Key, object Value)[] values)
    {
        var expectation = new Dictionary<string, object> { ["type"] = type };
        foreach (var (key, value) in values)
        {
            expectation[key] = value;
        }

        return expectation;
    }

    private sealed class StubRegexMatcher(BoundedRegexMatchResult result) : IBoundedRegexMatcher
    {
        public BoundedRegexMatchResult FindMatches(
            string pattern,
            string input,
            bool caseInsensitive = false,
            CancellationToken cancellationToken = default) => result;

        public BoundedRegexCandidateResult FindMatchingCandidates(
            string pattern,
            IReadOnlyList<string> candidates,
            bool caseInsensitive = false,
            CancellationToken cancellationToken = default) =>
            new(result.Status, [], result.ErrorMessage);
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
            trace,
            TestContext.Current.CancellationToken);
        var subsequence = await evaluator.EvaluateAsync(
            new Dictionary<string, object>
            {
                ["type"] = "tool_sequence",
                ["sequence"] = new List<string> { "search", "summarize" },
                ["exact_sequence"] = false
            },
            trace,
            TestContext.Current.CancellationToken);
        var exact = await evaluator.EvaluateAsync(
            new Dictionary<string, object>
            {
                ["type"] = "tool_sequence",
                ["sequence"] = new List<string> { "search", "summarize" }
            },
            trace,
            TestContext.Current.CancellationToken);

        Assert.True(contains.Passed);
        Assert.True(subsequence.Passed);
        Assert.False(exact.Passed);
        Assert.Equal("Pass", contains.Status);
        Assert.Equal("Fail", exact.Status);
    }
}
