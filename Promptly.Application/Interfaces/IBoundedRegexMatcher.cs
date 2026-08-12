namespace Promptly.Application.Interfaces;

/// <summary>
/// Executes user-supplied regular expressions under a fixed safety policy.
/// </summary>
public interface IBoundedRegexMatcher
{
    BoundedRegexMatchResult FindMatches(
        string pattern,
        string input,
        bool caseInsensitive = false,
        CancellationToken cancellationToken = default);

    BoundedRegexCandidateResult FindMatchingCandidates(
        string pattern,
        IReadOnlyList<string> candidates,
        bool caseInsensitive = false,
        CancellationToken cancellationToken = default);
}

public interface IRegexEvaluationBudget
{
    TimeSpan Remaining { get; }
}

public interface IRegexEvaluationBudgetFactory
{
    IRegexEvaluationBudget Start(TimeSpan timeout);
}

public enum BoundedRegexStatus
{
    Completed,
    InvalidPattern,
    UnsupportedPattern,
    PatternTooLong,
    InputTooLong,
    TimedOut
}

public sealed record BoundedRegexCapture(string Value, int Index);

public sealed record BoundedRegexMatchResult(
    BoundedRegexStatus Status,
    IReadOnlyList<BoundedRegexCapture> Matches,
    string? ErrorMessage = null)
{
    public bool Succeeded => Status == BoundedRegexStatus.Completed;
}

public sealed record BoundedRegexCandidateResult(
    BoundedRegexStatus Status,
    IReadOnlyList<string> Matches,
    string? ErrorMessage = null)
{
    public bool Succeeded => Status == BoundedRegexStatus.Completed;
}
