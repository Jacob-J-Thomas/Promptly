namespace Promptly.Domain.ValueObjects;

public record MappingSpecDefinition
{
    public int? Version { get; init; }
    public MessagesMapping? Messages { get; init; }
    public ToolCallsMapping? ToolCalls { get; init; }
    public UsageMapping? Usage { get; init; }
    public RetrievedDocsMapping? RetrievedDocs { get; init; }
    public FallbackMapping? Fallback { get; init; }
}

public record MessagesMapping
{
    public string? ItemsPath { get; init; }
    public string RolePath { get; init; } = "$.role";
    public string ContentPath { get; init; } = "$.content";
}

public record ToolCallsMapping
{
    public string? ItemsPath { get; init; }
    public string NamePath { get; init; } = "$.name";
    public string ArgumentsPath { get; init; } = "$.arguments";
}

public record UsageMapping
{
    public string? ObjectPath { get; init; }
    public string? PromptTokensPath { get; init; }
    public string? CompletionTokensPath { get; init; }
    public string? TotalTokensPath { get; init; }
    public string? CostPath { get; init; }
    public string? LatencyMsPath { get; init; }
}

public record RetrievedDocsMapping
{
    public string? ItemsPath { get; init; }
    public string? IdPath { get; init; }
    public string? TitlePath { get; init; }
    public string? ContentPath { get; init; }
    public string? MetadataPath { get; init; }
}

public record FallbackMapping
{
    public string? SingleAssistantContentPath { get; init; }
}
