namespace Promptly.Domain.ValueObjects;

public abstract record Expectation
{
    public required string Type { get; init; }
}

public record ContainsTextExpectation : Expectation
{
    public required string Text { get; init; }
    public bool CaseInsensitive { get; init; } = true;
}

public record BannedTextExpectation : Expectation
{
    public required string Text { get; init; }
    public bool CaseInsensitive { get; init; } = true;
}

public record RegexMatchExpectation : Expectation
{
    public required string Pattern { get; init; }
}

public record LinkPatternExpectation : Expectation
{
    public required string Pattern { get; init; }
}

public record ToolCalledExpectation : Expectation
{
    public required string ToolName { get; init; }
}

public record ToolSequenceExpectation : Expectation
{
    public required List<string> Sequence { get; init; }
    public bool ExactSequence { get; init; } = true;
}

public record LlmJudgeExpectation : Expectation
{
    public required string Rubric { get; init; }
    public double MinScore { get; init; } = 0.8;
    public string? Model { get; init; }
    public string? Provider { get; init; }
}

public record GroundednessExpectation : Expectation
{
    public double MinScore { get; init; } = 0.8;
    public string? Model { get; init; }
    public string? Provider { get; init; }
}

public record ExpectationResult
{
    public required string ExpectationType { get; init; }
    public bool Passed { get; init; }
    public double Score { get; init; }
    public required string Reason { get; init; }
    public Dictionary<string, object>? Metrics { get; init; }
}
