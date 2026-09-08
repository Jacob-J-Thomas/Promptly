namespace Promptly.Application.Interfaces;

/// <summary>
/// Validates the persisted expectation DSL at both API intake and execution.
/// The validator deliberately does not rewrite the stored JSON; callers use
/// the domain defaults when an optional property is omitted.
/// </summary>
public interface IExpectationValidator
{
    ExpectationValidationResult ValidateExpectationsJson(string expectationsJson);

    ExpectationValidationResult ValidateExpectation(
        IReadOnlyDictionary<string, object> expectation);
}

public sealed record ExpectationValidationIssue(
    string Code,
    string Path,
    string Message);

public sealed record ExpectationValidationResult(
    IReadOnlyList<ExpectationValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;

    public static ExpectationValidationResult Valid { get; } =
        new(Array.Empty<ExpectationValidationIssue>());
}
