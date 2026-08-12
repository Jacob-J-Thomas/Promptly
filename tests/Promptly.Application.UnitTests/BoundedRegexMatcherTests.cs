using System.Diagnostics;
using Promptly.Application.Interfaces;
using Promptly.Application.Services;

namespace Promptly.Application.UnitTests;

public sealed class BoundedRegexMatcherTests
{
    private readonly TestBoundedRegexMatcher _matcher = new();

    [Theory]
    [InlineData("order-[0-9]+", "Created order-42", false, 1, "order-42", 8)]
    [InlineData("HELLO", "hello", false, 0, null, null)]
    [InlineData("HELLO", "hello", true, 1, "hello", 0)]
    public void FindMatches_reports_completed_results(
        string pattern,
        string input,
        bool caseInsensitive,
        int expectedCount,
        string? expectedValue,
        int? expectedIndex)
    {
        var result = _matcher.FindMatches(pattern, input, caseInsensitive);

        Assert.True(result.Succeeded);
        Assert.Equal(BoundedRegexStatus.Completed, result.Status);
        Assert.Equal(expectedCount, result.Matches.Count);
        Assert.Null(result.ErrorMessage);
        if (expectedCount > 0)
        {
            Assert.Equal(expectedValue, result.Matches[0].Value);
            Assert.Equal(expectedIndex, result.Matches[0].Index);
        }
    }

    [Fact]
    public void FindMatches_returns_every_nonoverlapping_match()
    {
        var result = _matcher.FindMatches("a+", "a bb aaa");

        Assert.Equal(2, result.Matches.Count);
        Assert.Collection(
            result.Matches,
            match => Assert.Equal(new BoundedRegexCapture("a", 0), match),
            match => Assert.Equal(new BoundedRegexCapture("aaa", 5), match));
    }

    [Theory]
    [InlineData("[")]
    public void FindMatches_rejects_invalid_patterns(string pattern)
    {
        var result = _matcher.FindMatches(pattern, "input");

        Assert.False(result.Succeeded);
        Assert.Equal(BoundedRegexStatus.InvalidPattern, result.Status);
        Assert.Empty(result.Matches);
        Assert.NotNull(result.ErrorMessage);
        Assert.NotEmpty(result.ErrorMessage);
    }

    [Fact]
    public void FindMatches_preserves_valid_empty_patterns()
    {
        var result = _matcher.FindMatches("", "input");

        Assert.True(result.Succeeded);
        Assert.Equal("", result.Matches[0].Value);
        Assert.Equal(0, result.Matches[0].Index);
    }

    [Fact]
    public void FindMatches_preserves_valid_whitespace_patterns()
    {
        var result = _matcher.FindMatches(" ", "hello world");

        Assert.True(result.Succeeded);
        Assert.Single(result.Matches);
        Assert.Equal(" ", result.Matches[0].Value);
    }

    [Theory]
    [InlineData("(?=unsafe)")]
    [InlineData("(a)\\1")]
    public void FindMatches_rejects_constructs_unsupported_by_the_safe_engine(string pattern)
    {
        var result = _matcher.FindMatches(pattern, "unsafe");

        Assert.False(result.Succeeded);
        Assert.Equal(BoundedRegexStatus.UnsupportedPattern, result.Status);
        Assert.Contains("non-backtracking", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindMatches_accepts_the_exact_pattern_limit()
    {
        var result = _matcher.FindMatches(new string('a', BoundedRegexMatcher.MaxPatternLength), "a");

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void FindMatches_rejects_one_character_over_the_pattern_limit()
    {
        var result = _matcher.FindMatches(
            new string('a', BoundedRegexMatcher.MaxPatternLength + 1),
            "a");

        Assert.Equal(BoundedRegexStatus.PatternTooLong, result.Status);
        Assert.Contains(
            BoundedRegexMatcher.MaxPatternLength.ToString(),
            result.ErrorMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FindMatches_accepts_the_exact_input_limit()
    {
        var result = _matcher.FindMatches(
            "z$",
            new string('a', BoundedRegexMatcher.MaxInputLength - 1) + "z");

        Assert.True(result.Succeeded);
        Assert.Single(result.Matches);
    }

    [Fact]
    public void FindMatches_rejects_one_character_over_the_input_limit()
    {
        var result = _matcher.FindMatches(
            "a",
            new string('a', BoundedRegexMatcher.MaxInputLength + 1));

        Assert.Equal(BoundedRegexStatus.InputTooLong, result.Status);
        Assert.Contains(
            BoundedRegexMatcher.MaxInputLength.ToString(),
            result.ErrorMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FindMatches_propagates_preexisting_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            _matcher.FindMatchesWithCancellation("[", "aaa", cancellation.Token));
    }

    [Fact]
    public void FindMatches_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => _matcher.FindMatches(null!, "input"));
        Assert.Throws<ArgumentNullException>(() => _matcher.FindMatches("pattern", null!));
    }

    [Fact]
    public void FindMatches_bounds_a_classic_catastrophic_backtracking_pattern()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = _matcher.FindMatches(
            "(a+)+$",
            new string('a', 60_000) + "!");

        stopwatch.Stop();
        Assert.True(
            result.Status is BoundedRegexStatus.Completed or BoundedRegexStatus.TimedOut,
            $"Unexpected matcher status: {result.Status}");
        if (result.Succeeded)
        {
            Assert.Empty(result.Matches);
        }
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Safe regex evaluation took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void FindMatchingCandidates_returns_matching_values_for_many_candidates()
    {
        var candidates = Enumerable.Range(0, 7_000)
            .Select(index => $"u{index:0000}")
            .ToList();

        var result = _matcher.FindMatchingCandidates("u(?:0001|6999)", candidates);

        Assert.True(result.Succeeded);
        Assert.Equal(["u0001", "u6999"], result.Matches);
    }

    [Fact]
    public void FindMatchingCandidates_rejects_total_input_over_the_limit()
    {
        var result = _matcher.FindMatchingCandidates(
            "a",
            [new string('a', BoundedRegexMatcher.MaxInputLength), "a"]);

        Assert.Equal(BoundedRegexStatus.InputTooLong, result.Status);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void FindMatchingCandidates_rejects_patterns_over_the_limit()
    {
        var result = _matcher.FindMatchingCandidates(
            new string('a', BoundedRegexMatcher.MaxPatternLength + 1),
            ["a"]);

        Assert.Equal(BoundedRegexStatus.PatternTooLong, result.Status);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void FindMatchingCandidates_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _matcher.FindMatchingCandidates("a", null!));
        Assert.Throws<ArgumentNullException>(() =>
            _matcher.FindMatchingCandidates("a", [null!]));
    }

    [Fact]
    public void FindMatchingCandidates_propagates_preexisting_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            _matcher.FindMatchingCandidatesWithCancellation(
                "a",
                [new string('a', BoundedRegexMatcher.MaxInputLength + 1)],
                cancellation.Token));
    }

    [Fact]
    public void FindMatchingCandidates_returns_timeout_when_the_overall_budget_expires()
    {
        var matcher = new TestBoundedRegexMatcher(new ExpiredBudgetFactory());

        var result = matcher.FindMatchingCandidates("a", ["a"]);

        Assert.Equal(BoundedRegexStatus.TimedOut, result.Status);
        Assert.Contains("100", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void FindMatchingCandidates_checks_the_budget_after_the_final_match()
    {
        var matcher = new TestBoundedRegexMatcher(new ExpireAfterFirstCheckBudgetFactory());

        var result = matcher.FindMatchingCandidates("a", ["a"]);

        Assert.Equal(BoundedRegexStatus.TimedOut, result.Status);
    }

    [Fact]
    public void FindMatchingCandidates_limits_each_match_to_the_remaining_budget()
    {
        var matcher = new TestBoundedRegexMatcher(new TinyRemainingBudgetFactory());

        var result = matcher.FindMatchingCandidates(
            ".*z",
            [new string('a', BoundedRegexMatcher.MaxInputLength)]);

        Assert.Equal(BoundedRegexStatus.TimedOut, result.Status);
    }

    private sealed class TestBoundedRegexMatcher
    {
        private readonly BoundedRegexMatcher _inner;

        public TestBoundedRegexMatcher()
            : this(new BoundedRegexMatcher())
        {
        }

        public TestBoundedRegexMatcher(IRegexEvaluationBudgetFactory budgetFactory)
            : this(new BoundedRegexMatcher(budgetFactory))
        {
        }

        private TestBoundedRegexMatcher(BoundedRegexMatcher inner)
        {
            _inner = inner;
        }

        public BoundedRegexMatchResult FindMatches(
            string pattern,
            string input,
            bool caseInsensitive = false)
        {
            return _inner.FindMatches(
                pattern,
                input,
                caseInsensitive,
                TestContext.Current.CancellationToken);
        }

        public BoundedRegexMatchResult FindMatchesWithCancellation(
            string pattern,
            string input,
            CancellationToken cancellationToken)
        {
            return _inner.FindMatches(pattern, input, cancellationToken: cancellationToken);
        }

        public BoundedRegexCandidateResult FindMatchingCandidates(
            string pattern,
            IReadOnlyList<string> candidates,
            bool caseInsensitive = false)
        {
            return _inner.FindMatchingCandidates(
                pattern,
                candidates,
                caseInsensitive,
                TestContext.Current.CancellationToken);
        }

        public BoundedRegexCandidateResult FindMatchingCandidatesWithCancellation(
            string pattern,
            IReadOnlyList<string> candidates,
            CancellationToken cancellationToken)
        {
            return _inner.FindMatchingCandidates(
                pattern,
                candidates,
                cancellationToken: cancellationToken);
        }
    }

    private sealed class ExpiredBudgetFactory : IRegexEvaluationBudgetFactory
    {
        public IRegexEvaluationBudget Start(TimeSpan timeout) => new ExpiredBudget();
    }

    private sealed class ExpiredBudget : IRegexEvaluationBudget
    {
        public TimeSpan Remaining => TimeSpan.Zero;
    }

    private sealed class ExpireAfterFirstCheckBudgetFactory : IRegexEvaluationBudgetFactory
    {
        public IRegexEvaluationBudget Start(TimeSpan timeout) => new ExpireAfterFirstCheckBudget();
    }

    private sealed class ExpireAfterFirstCheckBudget : IRegexEvaluationBudget
    {
        private int _checkCount;

        public TimeSpan Remaining => Interlocked.Increment(ref _checkCount) > 1
            ? TimeSpan.Zero
            : BoundedRegexMatcher.MatchTimeout;
    }

    private sealed class TinyRemainingBudgetFactory : IRegexEvaluationBudgetFactory
    {
        public IRegexEvaluationBudget Start(TimeSpan timeout) => new TinyRemainingBudget();
    }

    private sealed class TinyRemainingBudget : IRegexEvaluationBudget
    {
        public TimeSpan Remaining => TimeSpan.FromTicks(1);
    }
}
