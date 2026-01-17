namespace Promptly.Domain.ValueObjects;

public record CanonicalTrace
{
    public List<Message> Messages { get; init; } = new();
    public List<ToolCall> ToolCalls { get; init; } = new();
    public Usage? Usage { get; init; }
    public List<RetrievedDoc> RetrievedDocs { get; init; } = new();
    public string? RawResponse { get; init; }
}

public record Message
{
    public required string Role { get; init; }
    public required string Content { get; init; }
}

public record ToolCall
{
    public required string Name { get; init; }
    public required string ArgumentsJson { get; init; }
}

public record Usage
{
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public int? TotalTokens { get; init; }
    public decimal? Cost { get; init; }
    public long? LatencyMs { get; init; }
}

public record RetrievedDoc
{
    public string? Id { get; init; }
    public string? Title { get; init; }
    public required string Content { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}
