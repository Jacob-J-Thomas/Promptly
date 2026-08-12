using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Interfaces;
using Promptly.Application.Services;
using Promptly.Domain.ValueObjects;

namespace Promptly.Application.UnitTests;

public sealed class ExpectationEvaluatorTests
{
    private readonly TestExpectationEvaluator _evaluator = new();

    [Fact]
    public async Task EvaluateAsync_rejects_an_expectation_without_a_type()
    {
        var result = await _evaluator.EvaluateAsync(new Dictionary<string, object>(), Trace("hello"));

        Assert.False(result.Passed);
        Assert.Equal("unknown", result.ExpectationType);
        Assert.Equal(0.0, result.Score);
        Assert.Contains("missing 'type'", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_rejects_a_null_expectation()
    {
        var result = await _evaluator.EvaluateAsync(null!, Trace("hello"));

        Assert.False(result.Passed);
        Assert.Equal("unknown", result.ExpectationType);
    }

    [Fact]
    public async Task EvaluateAsync_rejects_an_unknown_expectation_type()
    {
        var result = await _evaluator.EvaluateAsync(
            Expectation("not_real"),
            Trace("hello"));

        Assert.False(result.Passed);
        Assert.Equal("not_real", result.ExpectationType);
        Assert.Contains("Unknown expectation type", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_treats_a_null_type_as_unknown()
    {
        var result = await _evaluator.EvaluateAsync(
            new Dictionary<string, object> { ["type"] = null! },
            Trace("hello"));

        Assert.False(result.Passed);
        Assert.Equal("unknown", result.ExpectationType);
    }

    [Fact]
    public async Task EvaluateAsync_accepts_a_json_element_expectation()
    {
        using var document = JsonDocument.Parse(
            """
            { "type": "contains_text", "text": "HELLO", "case_insensitive": true }
            """);

        var result = await _evaluator.EvaluateAsync(document.RootElement, Trace("hello there"));

        Assert.True(result.Passed);
        Assert.Equal(1.0, result.Score);
    }

    [Fact]
    public async Task Json_non_boolean_case_insensitive_value_is_not_treated_as_true()
    {
        using var document = JsonDocument.Parse(
            """
            { "type": "contains_text", "text": 42, "case_insensitive": "true" }
            """);

        var result = await _evaluator.EvaluateAsync(document.RootElement, Trace("Value 42"));

        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData("hello", false, "hello there", true)]
    [InlineData("HELLO", false, "hello there", false)]
    [InlineData("HELLO", true, "hello there", true)]
    [InlineData("system-only", true, "assistant response", false)]
    public async Task Contains_text_evaluates_only_assistant_content(
        string text,
        bool caseInsensitive,
        string assistantContent,
        bool expected)
    {
        var trace = Trace(assistantContent);
        trace.Messages.Insert(0, new Message { Role = "system", Content = "system-only" });
        var expectation = Expectation(
            "contains_text",
            ("text", text),
            ("case_insensitive", caseInsensitive));

        var result = await _evaluator.EvaluateAsync(expectation, trace);

        Assert.Equal(expected, result.Passed);
        Assert.Equal(expected ? 1.0 : 0.0, result.Score);
        Assert.Equal("contains_text", result.ExpectationType);
    }

    [Theory]
    [InlineData("secret", false, "safe response", true)]
    [InlineData("secret", false, "contains secret", false)]
    [InlineData("SECRET", true, "contains secret", false)]
    public async Task Banned_text_passes_only_when_text_is_absent(
        string text,
        bool caseInsensitive,
        string assistantContent,
        bool expected)
    {
        var result = await _evaluator.EvaluateAsync(
            Expectation(
                "banned_text",
                ("text", text),
                ("case_insensitive", caseInsensitive)),
            Trace(assistantContent));

        Assert.Equal(expected, result.Passed);
        Assert.Equal(expected ? 1.0 : 0.0, result.Score);
        Assert.Equal("banned_text", result.ExpectationType);
    }

    [Fact]
    public async Task Regex_match_reports_match_details()
    {
        var result = await _evaluator.EvaluateAsync(
            Expectation(
                "regex_match",
                ("pattern", "order-[0-9]+"),
                ("case_insensitive", false)),
            Trace("Created order-42"));

        Assert.True(result.Passed);
        Assert.Null(result.ErrorCode);
        Assert.NotNull(result.Metrics);
        Assert.Equal("order-42", result.Metrics["matched_text"]);
        Assert.Equal(8, result.Metrics["match_index"]);
    }

    [Fact]
    public async Task Regex_match_preserves_message_boundaries_and_ignores_non_assistant_content()
    {
        var trace = Trace("first", "second");
        trace.Messages.Insert(1, new Message { Role = "system", Content = "ignored" });

        var result = await _evaluator.EvaluateAsync(
            Expectation("regex_match", ("pattern", "first\\nsecond")),
            trace);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task Regex_match_returns_a_normal_failed_assertion_when_it_does_not_match()
    {
        var result = await _evaluator.EvaluateAsync(
            Expectation("regex_match", ("pattern", "order-[0-9]+")),
            Trace("No identifier"));

        Assert.False(result.Passed);
        Assert.Null(result.ErrorCode);
        Assert.Null(result.Metrics);
        Assert.Contains("did not match", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[", "invalid_regex_pattern")]
    [InlineData("(?=unsafe)", "unsupported_regex_construct")]
    public async Task Regex_match_returns_structured_pattern_errors(string pattern, string errorCode)
    {
        var result = await _evaluator.EvaluateAsync(
            Expectation("regex_match", ("pattern", pattern)),
            Trace("unsafe"));

        Assert.False(result.Passed);
        Assert.Equal(errorCode, result.ErrorCode);
    }

    [Fact]
    public async Task Regex_match_preserves_an_explicit_empty_pattern()
    {
        var result = await _evaluator.EvaluateAsync(
            Expectation("regex_match", ("pattern", "")),
            Trace("assistant response"));

        Assert.True(result.Passed);
        Assert.Null(result.ErrorCode);
    }

    [Theory]
    [InlineData("regex_match")]
    [InlineData("link_pattern")]
    public async Task Regex_expectations_reject_a_missing_pattern(string expectationType)
    {
        var result = await _evaluator.EvaluateAsync(
            Expectation(expectationType),
            Trace("https://example.com"));

        Assert.Equal("invalid_regex_pattern", result.ErrorCode);
        Assert.Contains("required", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Regex_match_rejects_patterns_over_the_safety_limit()
    {
        var result = await _evaluator.EvaluateAsync(
            Expectation("regex_match", ("pattern", new string('a', 513))),
            Trace("a"));

        Assert.False(result.Passed);
        Assert.Equal("regex_pattern_too_long", result.ErrorCode);
        Assert.Contains("512", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Regex_match_rejects_input_over_the_safety_limit()
    {
        var result = await _evaluator.EvaluateAsync(
            Expectation("regex_match", ("pattern", "a+")),
            Trace(new string('a', 65_537)));

        Assert.False(result.Passed);
        Assert.Equal("regex_input_too_long", result.ErrorCode);
        Assert.Contains("65536", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Catastrophic_backtracking_pattern_completes_within_a_bounded_time()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = await _evaluator.EvaluateAsync(
            Expectation("regex_match", ("pattern", "(a+)+$")),
            Trace(new string('a', 60_000) + "!"));

        stopwatch.Stop();
        Assert.False(result.Passed);
        Assert.True(
            result.ErrorCode is null or "regex_timeout",
            $"Unexpected error code: {result.ErrorCode}");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Safe regex evaluation took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Regex_match_maps_a_matcher_timeout_to_a_structured_error()
    {
        var matcher = new StubRegexMatcher(
            new BoundedRegexMatchResult(
                BoundedRegexStatus.TimedOut,
                [],
                "Regex evaluation timed out"));
        var evaluator = new ExpectationEvaluator(
            NullLogger<ExpectationEvaluator>.Instance,
            matcher);

        var result = await evaluator.EvaluateAsync(
            Expectation("regex_match", ("pattern", "a+")),
            Trace("aaa"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Equal("regex_timeout", result.ErrorCode);
        Assert.Equal("Regex evaluation timed out", result.Reason);
        Assert.Equal(1, matcher.CallCount);
    }

    [Fact]
    public async Task Regex_match_maps_an_unknown_matcher_failure_defensively()
    {
        var matcher = new StubRegexMatcher(
            new BoundedRegexMatchResult((BoundedRegexStatus)999, [], null));
        var evaluator = new ExpectationEvaluator(
            NullLogger<ExpectationEvaluator>.Instance,
            matcher);

        var result = await evaluator.EvaluateAsync(
            Expectation("regex_match", ("pattern", "a+")),
            Trace("aaa"),
            TestContext.Current.CancellationToken);

        Assert.Equal("regex_evaluation_error", result.ErrorCode);
        Assert.Equal("Regex evaluation failed", result.Reason);
    }

    [Fact]
    public async Task Link_pattern_fails_when_no_urls_exist()
    {
        var result = await _evaluator.EvaluateAsync(
            Expectation("link_pattern", ("pattern", "example\\.com")),
            Trace("No links here"));

        Assert.False(result.Passed);
        Assert.Null(result.ErrorCode);
        Assert.Equal("No URLs found in assistant messages", result.Reason);
    }

    [Theory]
    [InlineData("[", "invalid_regex_pattern")]
    [InlineData("(?=example)", "unsupported_regex_construct")]
    public async Task Link_pattern_validates_the_pattern_even_when_no_urls_exist(
        string pattern,
        string errorCode)
    {
        var result = await _evaluator.EvaluateAsync(
            Expectation("link_pattern", ("pattern", pattern)),
            Trace("No links here"));

        Assert.False(result.Passed);
        Assert.Equal(errorCode, result.ErrorCode);
    }

    [Fact]
    public async Task Link_pattern_maps_url_extraction_failures()
    {
        var matcher = new StubRegexMatcher(
            new BoundedRegexMatchResult(
                BoundedRegexStatus.TimedOut,
                [],
                "URL extraction timed out"));
        var evaluator = new ExpectationEvaluator(
            NullLogger<ExpectationEvaluator>.Instance,
            matcher);

        var result = await evaluator.EvaluateAsync(
            Expectation("link_pattern", ("pattern", "example")),
            Trace("https://example.com"),
            TestContext.Current.CancellationToken);

        Assert.Equal("regex_timeout", result.ErrorCode);
        Assert.Equal("URL extraction timed out", result.Reason);
        Assert.Equal(1, matcher.CallCount);
    }

    [Fact]
    public async Task Link_pattern_reports_all_matching_urls()
    {
        var result = await _evaluator.EvaluateAsync(
            Expectation("link_pattern", ("pattern", "example\\.com")),
            Trace("See https://example.com/a and https://example.com/b"));

        Assert.True(result.Passed);
        Assert.NotNull(result.Metrics);
        var matched = Assert.IsType<List<string>>(result.Metrics["matched_urls"]);
        Assert.Equal(2, matched.Count);
        Assert.Equal(2, result.Metrics["total_urls"]);
    }

    [Fact]
    public async Task Link_pattern_reports_discovered_nonmatching_urls()
    {
        var result = await _evaluator.EvaluateAsync(
            Expectation("link_pattern", ("pattern", "allowed\\.example")),
            Trace("See https://blocked.example/path"));

        Assert.False(result.Passed);
        Assert.Null(result.ErrorCode);
        Assert.NotNull(result.Metrics);
        var found = Assert.IsType<List<string>>(result.Metrics["found_urls"]);
        Assert.Single(found);
    }

    [Theory]
    [InlineData("[", "invalid_regex_pattern")]
    [InlineData("(?=example)", "unsupported_regex_construct")]
    public async Task Link_pattern_returns_structured_pattern_errors(string pattern, string errorCode)
    {
        var result = await _evaluator.EvaluateAsync(
            Expectation("link_pattern", ("pattern", pattern)),
            Trace("https://example.com"));

        Assert.False(result.Passed);
        Assert.Equal(errorCode, result.ErrorCode);
    }

    [Theory]
    [InlineData("search", true)]
    [InlineData("missing", false)]
    public async Task Tool_called_reports_called_tools(string expectedTool, bool expected)
    {
        var trace = Trace("done");
        trace.ToolCalls.Add(new ToolCall { Name = "search", ArgumentsJson = "{}" });

        var result = await _evaluator.EvaluateAsync(
            Expectation("tool_called", ("tool_name", expectedTool)),
            trace);

        Assert.Equal(expected, result.Passed);
        Assert.NotNull(result.Metrics);
        var called = Assert.IsType<List<string>>(result.Metrics["called_tools"]);
        Assert.Equal(["search"], called);
    }

    [Fact]
    public async Task Tool_sequence_passes_for_the_expected_order()
    {
        var result = await _evaluator.EvaluateAsync(
            Expectation("tool_sequence", ("sequence", new List<string> { "search", "summarize" })),
            TraceWithTools("search", "summarize"));

        Assert.True(result.Passed);
        Assert.Equal(1.0, result.Score);
    }

    [Fact]
    public async Task Tool_sequence_fails_when_actual_sequence_is_shorter()
    {
        var result = await _evaluator.EvaluateAsync(
            Expectation("tool_sequence", ("sequence", new List<string> { "search", "summarize" })),
            TraceWithTools("search"));

        Assert.False(result.Passed);
        Assert.Contains("mismatch", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_sequence_fails_on_the_first_mismatched_position()
    {
        var result = await _evaluator.EvaluateAsync(
            Expectation("tool_sequence", ("sequence", new List<string> { "search", "summarize" })),
            TraceWithTools("search", "publish"));

        Assert.False(result.Passed);
        Assert.NotNull(result.Metrics);
    }

    [Fact]
    public async Task Tool_sequence_accepts_a_json_array()
    {
        using var document = JsonDocument.Parse(
            """
            { "type": "tool_sequence", "sequence": ["search", "summarize"] }
            """);

        var result = await _evaluator.EvaluateAsync(
            document.RootElement,
            TraceWithTools("search", "summarize"));

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task EvaluateAsync_propagates_cancellation_before_work_starts()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _evaluator.EvaluateWithCancellationAsync(
                Expectation("regex_match", ("pattern", "a+")),
                Trace("aaa"),
                cancellation.Token));
    }

    [Fact]
    public async Task EvaluateAsync_rejects_a_null_trace()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _evaluator.EvaluateAsync(Expectation("contains_text", ("text", "hello")), null!));
    }

    [Fact]
    public async Task EvaluateAsync_converts_unexpected_failures_to_an_error_result()
    {
        var result = await _evaluator.EvaluateAsync(
            new Dictionary<string, object> { ["type"] = new ThrowingValue() },
            Trace("hello"));

        Assert.False(result.Passed);
        Assert.Equal("error", result.ExpectationType);
        Assert.Contains("Evaluation error", result.Reason, StringComparison.Ordinal);
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

    private static CanonicalTrace Trace(params string[] assistantMessages)
    {
        return new CanonicalTrace
        {
            Messages = assistantMessages
                .Select(content => new Message { Role = "assistant", Content = content })
                .ToList()
        };
    }

    private static CanonicalTrace TraceWithTools(params string[] tools)
    {
        return new CanonicalTrace
        {
            ToolCalls = tools
                .Select(name => new ToolCall { Name = name, ArgumentsJson = "{}" })
                .ToList()
        };
    }

    private sealed class ThrowingValue
    {
        public override string ToString() => throw new InvalidOperationException("boom");
    }

    private sealed class StubRegexMatcher(BoundedRegexMatchResult result) : IBoundedRegexMatcher
    {
        public int CallCount { get; private set; }

        public BoundedRegexMatchResult FindMatches(
            string pattern,
            string input,
            bool caseInsensitive = false,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return result;
        }

        public BoundedRegexCandidateResult FindMatchingCandidates(
            string pattern,
            IReadOnlyList<string> candidates,
            bool caseInsensitive = false,
            CancellationToken cancellationToken = default)
        {
            return new BoundedRegexCandidateResult(result.Status, [], result.ErrorMessage);
        }
    }

    private sealed class TestExpectationEvaluator
    {
        private readonly ExpectationEvaluator _inner = new(
            NullLogger<ExpectationEvaluator>.Instance,
            new BoundedRegexMatcher());

        public Task<ExpectationResult> EvaluateAsync(object expectation, CanonicalTrace trace)
        {
            return _inner.EvaluateAsync(
                expectation,
                trace,
                TestContext.Current.CancellationToken);
        }

        public Task<ExpectationResult> EvaluateWithCancellationAsync(
            object expectation,
            CanonicalTrace trace,
            CancellationToken cancellationToken)
        {
            return _inner.EvaluateAsync(expectation, trace, cancellationToken);
        }
    }
}
