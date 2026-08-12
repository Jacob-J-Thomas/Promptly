using System.Diagnostics;
using System.Text.RegularExpressions;
using Promptly.Application.Interfaces;

namespace Promptly.Application.Services;

public sealed class BoundedRegexMatcher : IBoundedRegexMatcher
{
    public const int MaxPatternLength = 512;
    public const int MaxInputLength = 65_536;
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private readonly IRegexEvaluationBudgetFactory _budgetFactory;

    public BoundedRegexMatcher()
        : this(new StopwatchRegexEvaluationBudgetFactory())
    {
    }

    public BoundedRegexMatcher(IRegexEvaluationBudgetFactory budgetFactory)
    {
        _budgetFactory = budgetFactory ?? throw new ArgumentNullException(nameof(budgetFactory));
    }

    public BoundedRegexMatchResult FindMatches(
        string pattern,
        string input,
        bool caseInsensitive = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(input);
        var validationFailure = Validate(pattern, input.Length);
        if (validationFailure != null)
        {
            return new BoundedRegexMatchResult(
                validationFailure.Status,
                [],
                validationFailure.ErrorMessage);
        }

        try
        {
            var regex = CreateRegex(pattern, caseInsensitive);
            var matches = new List<BoundedRegexCapture>();
            foreach (Match match in regex.Matches(input))
            {
                cancellationToken.ThrowIfCancellationRequested();
                matches.Add(new BoundedRegexCapture(match.Value, match.Index));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new BoundedRegexMatchResult(BoundedRegexStatus.Completed, matches);
        }
        catch (RegexMatchTimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new BoundedRegexMatchResult(
                BoundedRegexStatus.TimedOut,
                [],
                TimeoutMessage());
        }
        catch (NotSupportedException ex)
        {
            return new BoundedRegexMatchResult(
                BoundedRegexStatus.UnsupportedPattern,
                [],
                UnsupportedMessage(ex));
        }
        catch (ArgumentException ex)
        {
            return new BoundedRegexMatchResult(
                BoundedRegexStatus.InvalidPattern,
                [],
                InvalidMessage(ex));
        }
    }

    public BoundedRegexCandidateResult FindMatchingCandidates(
        string pattern,
        IReadOnlyList<string> candidates,
        bool caseInsensitive = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(candidates);
        var totalInputLength = 0L;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(candidate);
            totalInputLength += candidate.Length;
            if (totalInputLength > MaxInputLength)
            {
                return CandidateFailure(
                    BoundedRegexStatus.InputTooLong,
                    $"Evaluated text exceeds the {MaxInputLength} character safety limit");
            }
        }

        var validationFailure = Validate(pattern, (int)totalInputLength);
        if (validationFailure != null)
        {
            return CandidateFailure(validationFailure.Status, validationFailure.ErrorMessage);
        }

        try
        {
            _ = CreateRegex(pattern, caseInsensitive);
            var budget = _budgetFactory.Start(MatchTimeout);
            var matches = new List<string>();
            Regex? regex = null;
            var regexTimeout = TimeSpan.Zero;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = budget.Remaining;
                var allowedMatchTimeout = GetAllowedMatchTimeout(remaining);
                if (allowedMatchTimeout <= TimeSpan.Zero)
                {
                    return CandidateFailure(BoundedRegexStatus.TimedOut, TimeoutMessage());
                }

                if (regex == null || regexTimeout > allowedMatchTimeout)
                {
                    regex = CreateRegex(pattern, caseInsensitive, allowedMatchTimeout);
                    regexTimeout = allowedMatchTimeout;
                }

                if (regex.IsMatch(candidate))
                {
                    matches.Add(candidate);
                }

                if (budget.Remaining <= TimeSpan.Zero)
                {
                    return CandidateFailure(BoundedRegexStatus.TimedOut, TimeoutMessage());
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new BoundedRegexCandidateResult(BoundedRegexStatus.Completed, matches);
        }
        catch (RegexMatchTimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CandidateFailure(BoundedRegexStatus.TimedOut, TimeoutMessage());
        }
        catch (NotSupportedException ex)
        {
            return CandidateFailure(BoundedRegexStatus.UnsupportedPattern, UnsupportedMessage(ex));
        }
        catch (ArgumentException ex)
        {
            return CandidateFailure(BoundedRegexStatus.InvalidPattern, InvalidMessage(ex));
        }
    }

    private static BoundedRegexMatchResult? Validate(string pattern, int inputLength)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        if (pattern.Length > MaxPatternLength)
        {
            return Failure(
                BoundedRegexStatus.PatternTooLong,
                $"Regex pattern exceeds the {MaxPatternLength} character safety limit");
        }

        if (inputLength > MaxInputLength)
        {
            return Failure(
                BoundedRegexStatus.InputTooLong,
                $"Evaluated text exceeds the {MaxInputLength} character safety limit");
        }

        return null;
    }

    private static Regex CreateRegex(
        string pattern,
        bool caseInsensitive,
        TimeSpan? timeout = null)
    {
        var options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        if (caseInsensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new Regex(pattern, options, timeout ?? MatchTimeout);
    }

    private static TimeSpan GetAllowedMatchTimeout(TimeSpan remaining)
    {
        const double schedulingMarginMilliseconds = 1;
        const double timeoutBucketMilliseconds = 10;

        var availableMilliseconds = remaining.TotalMilliseconds - schedulingMarginMilliseconds;
        if (availableMilliseconds <= 0)
        {
            return TimeSpan.Zero;
        }

        var bucketedMilliseconds =
            Math.Floor(availableMilliseconds / timeoutBucketMilliseconds) * timeoutBucketMilliseconds;
        if (bucketedMilliseconds < schedulingMarginMilliseconds)
        {
            bucketedMilliseconds = schedulingMarginMilliseconds;
        }

        return TimeSpan.FromMilliseconds(
            Math.Min(bucketedMilliseconds, MatchTimeout.TotalMilliseconds));
    }

    private static BoundedRegexMatchResult Failure(
        BoundedRegexStatus status,
        string errorMessage)
    {
        return new BoundedRegexMatchResult(status, [], errorMessage);
    }

    private static BoundedRegexCandidateResult CandidateFailure(
        BoundedRegexStatus status,
        string? errorMessage)
    {
        return new BoundedRegexCandidateResult(status, [], errorMessage);
    }

    private static string TimeoutMessage() =>
        $"Regex evaluation exceeded the {MatchTimeout.TotalMilliseconds:0} ms safety limit";

    private static string UnsupportedMessage(NotSupportedException exception) =>
        $"Regex pattern uses a construct that the safe non-backtracking engine does not support: {exception.Message}";

    private static string InvalidMessage(ArgumentException exception) =>
        $"Invalid regex pattern: {exception.Message}";

    private sealed class StopwatchRegexEvaluationBudgetFactory : IRegexEvaluationBudgetFactory
    {
        public IRegexEvaluationBudget Start(TimeSpan timeout) =>
            new StopwatchRegexEvaluationBudget(timeout);
    }

    private sealed class StopwatchRegexEvaluationBudget(TimeSpan timeout) : IRegexEvaluationBudget
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public TimeSpan Remaining
        {
            get
            {
                var remaining = timeout - _stopwatch.Elapsed;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }
}
